# Operating Waybill

What to configure, what to watch, and what the defaults assume. The reasoning behind each choice is in
[ADR 0004](adr/0004-retencao-metrica-e-health-check.md) (in Portuguese).

## Setup

```csharp
services.AddWaybillDispatcher<AppDbContext>();          // the context's database; also samples the pending-age gauge
services.AddWaybillRetention<AppDbContext>();           // deletes delivered and expired rows
services.AddHealthChecks().AddWaybillDispatcherCheck(); // entry "waybill-dispatcher"
```

Both read the connection string from the registered `DbContext`; set `ConnectionString` in the options to use another
one (for example, a user with fewer rights). Set it too when the context is configured with `UseNpgsql(NpgsqlDataSource)`:
Npgsql leaves the password out of that context's connection string. Retention is a hosted service of its own, so a consumer that only uses the inbox can run it without a dispatcher.
Any number of instances may run either service against the same database.

## Defaults

| Option | Default | Meaning |
| --- | --- | --- |
| `WaybillRetentionOptions.OutboxRetention` | 7 days | A **published** outbox row is deleted this long after its publication. Pending, claimed and dead-lettered rows are never deleted, however old. |
| `WaybillRetentionOptions.InboxRetention` | 30 days | An inbox row is deleted this long after the message was processed. See [Sizing the inbox retention](#sizing-the-inbox-retention). |
| `WaybillRetentionOptions.Interval` | 5 minutes | Wait between cleanup passes. A failed pass is logged and retried at the next interval. |
| `WaybillRetentionOptions.BatchSize` | 1000 | Rows deleted per statement; a pass repeats until a batch comes back short. |
| `WaybillDispatcherOptions.BatchSize` | 100 | Most messages claimed and published per cycle. Timeouts and nacks halve the batch in use, down to 1; healthy batches double it back. |
| `WaybillDispatcherOptions.PollingInterval` | 1 second | Wait before the next cycle when the last one did not fill a batch. Also the first open period of the circuit breaker, which doubles up to 30 seconds. |
| `WaybillDispatcherOptions.PublishTimeout` | 20 seconds | How long the dispatcher waits for the broker to confirm a batch. Past it, the batch is handed back and published again: a duplicate is possible, a loss is not. |
| `WaybillDispatcherOptions.LeaseMargin` | 10 seconds | Added to `PublishTimeout` to form the claim lease (30 seconds by default), so the lease always outlives the publish wait. A crashed instance's rows become claimable again after the lease. |
| `WaybillDispatcherOptions.MaxReturns` | 5 | How many times a message may come back unroutable (`basic.return`) before it goes to the DLQ. |
| `WaybillDispatcherOptions.ReturnBackoff` | 1 minute | How long a message waits after its first return before it is published again. The wait doubles with each return, up to `MaxReturnBackoff`. With the defaults a message waits 1 + 2 + 4 + 8 = 15 minutes in all before its fifth return sends it to the DLQ: time to create the missing binding. Zero publishes it again on the next cycle, as in 0.1. |
| `WaybillDispatcherOptions.MaxReturnBackoff` | 10 minutes | The longest single wait after a return. At most one day. |
| `WaybillOptions.OrderByKey` | off | Ordering by key, being built for v0.2. See [Ordering by key](#ordering-by-key). |
| `WaybillDispatcherOptions.Partitions` | 16 | With `OrderByKey`: how many partitions (P) keys are spread over. From 1 to 1024; the same on every dispatcher. |
| `WaybillDispatcherOptions.PartitionLease` | 60 seconds | With `OrderByKey`: how long a dispatcher holds a partition without renewing it, so how long the keys of a crashed dispatcher wait. At least twice the claim lease, at most one day; the same on every dispatcher. |
| `WaybillDispatcherOptions.MetricsInterval` | 15 seconds | How often the pending-age gauge is sampled. |

All options are validated at startup; a zero or negative retention fails the host instead of deleting everything.

On shutdown, the dispatcher stops claiming, waits for the batch in flight up to `PublishTimeout`, and hands back the
rows it still holds. The host's `HostOptions.ShutdownTimeout` (30 seconds by default) bounds that wait: keep it above
`PublishTimeout`, or a message of the batch in flight may be published and handed back, and then published again.

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

A message that comes back unroutable does not reach the DLQ at once: it waits a growing interval between returns
(`ReturnBackoff`, 15 minutes in all by default), and a binding created meanwhile lets it through with no manual step.
Reprocessing dead-lettered messages is a manual operation until the operations API (planned for v1.0). Fix the cause first (for
`312 NO_ROUTE`, the missing binding), then hand the messages back to the dispatcher, which publishes them with the
same message id:

<!-- dlq-requeue -->
```sql
UPDATE waybill.outbox
SET status = 'pending', attempts = 0, dlq_reason = NULL
WHERE status = 'dlq' AND dlq_reason = '312 NO_ROUTE';
```

Do not reprocess a message older than the consumers' inbox retention: a consumer that already applied it no longer
remembers it, and would apply it again.

## Sizing the inbox retention

The inbox applies a consumer's effect once per handler only while it remembers the message. After retention deletes
the row, a redelivery of that message is processed again. `ProcessAsync` does not refuse old messages: a message can
legitimately arrive late (it waited in the outbox while the broker was down), and refusing it would lose it.

Keep `InboxRetention` above the longest delay between enqueueing a message and its last possible redelivery:

- the longest the outbox may hold a backlog (broker outages you plan to survive);
- the longest a message may wait in the broker queue (consumer down, queue TTL);
- the window in which you might reprocess dead-lettered messages or replay a queue.

Replaying a queue or an offset older than the inbox retention is not covered.

## Ordering by key

Ordering by key is being built for v0.2 and is off by default. What exists today is the partition lease it rests on;
until the rest lands, turning `OrderByKey` on spreads keys over partitions but does **not** yet guarantee order.

With `OrderByKey` on, each key belongs to a partition, `key_hash % Partitions`, and each partition is held by one
dispatcher at a time. Dispatchers share the partitions evenly (at most `ceil(Partitions / live dispatchers)` each),
renew them every cycle, and hand them back on shutdown. A crashed dispatcher's partitions move to the others after
`PartitionLease`. Messages without a key are not ordered and are published by any dispatcher. Dispatchers beyond
`Partitions` publish only messages without a key.

`Partitions` and `PartitionLease` are stored in `waybill.settings` by the first dispatcher that orders, and every
dispatcher checks them at startup and every `PartitionLease`. A dispatcher whose values differ, or one without
`OrderByKey` while the settings exist, stops with a critical log, and its health check reports `Unhealthy`.

To turn ordering on, stop every dispatcher, set `OrderByKey` everywhere, and start them. To turn it off, or to change
`Partitions` or `PartitionLease`, stop every dispatcher, clear the ordering state, and start them with the new
configuration:

<!-- ordering-reset -->
```sql
DELETE FROM waybill.outbox_partitions;
DELETE FROM waybill.outbox_instances;
DELETE FROM waybill.settings;
```

## PostgreSQL: isolation level

The claim and the retention run as single statements at the session's default isolation, and they rely on
`READ COMMITTED`, PostgreSQL's default. If `default_transaction_isolation` is set to something else for the database
or for the role Waybill connects with, the dispatcher and the retention stop before their first cycle with a critical
log, and the health check reports the dispatcher as not running. Set it back for Waybill's role:

```sql
ALTER ROLE waybill_dispatcher SET default_transaction_isolation = 'read committed';
```

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

**After such an episode, rebuild the claim index.** Autovacuum cleans the dead entries, but it does not shrink the
partial index the claim walks (`waybill.ix_outbox_claimable`), which holds only pending and claimed rows and so stays
small in steady state. Once bloated, the claim reads more of its pages for the same rows, and latency comes back in
bursts for hours. Check its size once the long transaction is gone and autovacuum has run, and rebuild it online:

```sql
SELECT pg_size_pretty(pg_relation_size('waybill.ix_outbox_claimable'));  -- a few hundred kB in steady state
REINDEX INDEX CONCURRENTLY waybill.ix_outbox_claimable;
```

`REINDEX ... CONCURRENTLY` does not block the claim. In the long load scenario below, two hours of an open
transaction left the index at about 85 MB. Without the rebuild, 28 of the following 106 minutes had a claim p95 above
5 ms, and those minutes read 2.4 times as many index pages per scan for the same rows. With it, 2 did (both before the
rebuild), and the index went back to under 1 MB.

The long load scenario (`CargaLonga_TransacaoLongaAberta_LatenciaDoClaimEstabiliza`) holds such a transaction open
under constant load with the autovacuum settings above. It checks that dead tuples pile up while it is open and that,
once it closes, autovacuum runs and the claim index is rebuilt as above, the claim latency comes back near the baseline
and the table stops growing. It runs for 5 hours in the scheduled CI workflow and writes one CSV line per minute (claim
latency, table and index sizes, dead tuples, index scans and blocks read).

## Retention after a long broker outage

To find what to delete without an extra index, retention walks the outbox primary key below the UUIDv7 of the cutoff
time and checks `status` and `published_at` row by row. In steady state that range holds only rows due for deletion.
After a long outage it also holds the whole backlog: messages created before the outage but published only when the
broker came back. Until they pass `OutboxRetention` (counted from publication), every pass walks over them without
deleting them, a cost proportional to that backlog once per `Interval`. If that shows up in your database, raise
`Interval` while it lasts.
