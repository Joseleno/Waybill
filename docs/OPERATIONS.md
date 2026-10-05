# Operating Waybill

What to configure, what to watch, and what the defaults assume. The reasoning behind each choice is in
[ADR 0004](adr/0004-retencao-metrica-e-health-check.md) (in Portuguese).

## Setup

```csharp
services.AddWaybillDispatcher(o => o.ConnectionString = "...");   // also samples the pending-age gauge
services.AddWaybillRetention(o => o.ConnectionString = "...");    // deletes delivered and expired rows
services.AddHealthChecks().AddWaybillDispatcher();                // entry "waybill-dispatcher"
```

Retention is a hosted service of its own, so a consumer that only uses the inbox can run it without a dispatcher.
Any number of instances may run either service against the same database.

## Defaults

| Option | Default | Meaning |
| --- | --- | --- |
| `WaybillRetentionOptions.OutboxRetention` | 7 days | A **published** outbox row is deleted this long after its publication. Pending, claimed and dead-lettered rows are never deleted, however old. |
| `WaybillRetentionOptions.InboxRetention` | 30 days | An inbox row is deleted this long after the message was processed. See [Sizing the inbox retention](#sizing-the-inbox-retention). |
| `WaybillRetentionOptions.Interval` | 5 minutes | Wait between cleanup passes. A failed pass is logged and retried at the next interval. |
| `WaybillRetentionOptions.BatchSize` | 1000 | Rows deleted per statement; a pass repeats until a batch comes back short. |
| `WaybillDispatcherOptions.MetricsInterval` | 15 seconds | How often the pending-age gauge is sampled. |

All options are validated at startup; a zero or negative retention fails the host instead of deleting everything.

## What to monitor

**`waybill.outbox.oldest_pending.age`** (meter `Waybill`, unit `s`): the age of the oldest outbox message not yet
published, measured on the database clock. It is 0 when nothing is waiting. A value that keeps growing means events
are not leaving: broker down, dispatcher stopped, or a dispatcher that cannot keep up. Alert on it staying above what
your consumers can tolerate. If a sample fails (database unreachable), the gauge keeps its last value and an error is
logged; it never drops to 0 because of a failure. The meter comes from the host's `IMeterFactory` when one is
registered, so OpenTelemetry or any `MeterListener` picks it up; Waybill does not depend on OpenTelemetry.

**Health check `waybill-dispatcher`:**

| Status | When | Suggested use |
| --- | --- | --- |
| `Healthy` | The dispatcher loop runs and the broker is reachable. | — |
| `Degraded` | The broker is unreachable (circuit breaker open or a connection failure). The outbox keeps accepting events; they are published when the broker is back. | Alert. Do not restart the process: it would not help. |
| `Unhealthy` | The dispatcher loop is not running, has not finished a cycle for longer than the lease plus the longest wait between cycles (30 s, or `PollingInterval` if longer) plus 30 s — 90 s with the defaults — or the database failed three cycles in a row. | Liveness / restart. |

The entry's data carries `oldest_pending_age_seconds`.

**The DLQ.** Dead-lettered messages stay in `waybill.outbox` with `status = 'dlq'` and a `dlq_reason`, and retention
never deletes them. Watch their count:

```sql
SELECT count(*) FROM waybill.outbox WHERE status = 'dlq';
```

Reprocessing them is a manual operation until the operations API (planned for v1.0).

## Sizing the inbox retention

The inbox applies a consumer's effect once per handler only while it remembers the message. After retention deletes
the row, a redelivery of that message is processed again. `ProcessAsync` does not refuse old messages: a message can
legitimately arrive late (it waited in the outbox while the broker was down), and refusing it would lose it.

Keep `InboxRetention` above the longest delay between enqueueing a message and its last possible redelivery:

- the longest the outbox may hold a backlog (broker outages you plan to survive);
- the longest a message may wait in the broker queue (consumer down, queue TTL);
- the window in which you might reprocess dead-lettered messages or replay a queue.

Replaying a queue or an offset older than the inbox retention is not covered.

## PostgreSQL: autovacuum and long transactions

Every claim changes `status`, which is part of the predicate of the partial index the claim walks. That rules out HOT
updates, so each message leaves dead tuples and dead index entries behind. Vacuum them often:

```sql
ALTER TABLE waybill.outbox SET (autovacuum_vacuum_scale_factor = 0.01, autovacuum_vacuum_threshold = 1000);
```

Do not set a `fillfactor` on the outbox: it only helps HOT updates, and they cannot happen here.

A long-running transaction anywhere in the database (a report, an idle-in-transaction session, a standby with
`hot_standby_feedback` running a long query) holds back the vacuum horizon. While it is open, dead tuples pile up and
the claim can get slower, even if the table size looks stable. The table file does not shrink after such an episode
(plain `VACUUM` makes the space reusable, it does not return it). Watch for long transactions with
`pg_stat_activity` (`xact_start`, `state = 'idle in transaction'`) and set `idle_in_transaction_session_timeout`.

The long load scenario (`CargaLonga_TransacaoLongaAberta_LatenciaDoClaimEstabiliza`) holds such a transaction open
under constant load with the autovacuum settings above. It checks that dead tuples pile up while it is open and that,
once it closes and autovacuum runs, the claim latency comes back near the baseline and the table stops growing. It
runs for 5 hours in the scheduled CI workflow and writes one CSV line per minute (claim p95, table size, dead tuples).

## Retention after a long broker outage

To find what to delete without an extra index, retention walks the outbox primary key below the UUIDv7 of the cutoff
time and checks `status` and `published_at` row by row. In steady state that range holds only rows due for deletion.
After a long outage it also holds the whole backlog: messages created before the outage but published only when the
broker came back. Until they pass `OutboxRetention` (counted from publication), every pass walks over them without
deleting them, a cost proportional to that backlog once per `Interval`. If that shows up in your database, raise
`Interval` while it lasts.
