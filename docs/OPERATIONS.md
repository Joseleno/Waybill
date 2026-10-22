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
| `WaybillDispatcherOptions.PublishTimeout` | 20 seconds | How long the dispatcher waits for the broker to confirm a batch. Past it, the batch is handed back and published again: a duplicate is possible, a loss is not. With `MaxPerKey` above 1 it covers all the rounds of a batch together: a round starts only if what is left is at least what the previous round took. |
| `WaybillDispatcherOptions.LeaseMargin` | 10 seconds | Added to `PublishTimeout` to form the claim lease (30 seconds by default), so the lease always outlives the publish wait. A crashed instance's rows become claimable again after the lease. |
| `WaybillDispatcherOptions.MaxReturns` | 5 | How many times a message may come back unroutable (`basic.return`) before it goes to the DLQ. |
| `WaybillDispatcherOptions.ReturnBackoff` | 1 minute | How long a message waits after its first return before it is published again. The wait doubles with each return, up to `MaxReturnBackoff`. With the defaults a message waits 1 + 2 + 4 + 8 = 15 minutes in all before its fifth return sends it to the DLQ: time to create the missing binding. Zero publishes it again on the next cycle, as in 0.1. |
| `WaybillDispatcherOptions.MaxReturnBackoff` | 10 minutes | The longest single wait after a return. At most one day. |
| `WaybillDispatcherOptions.OrderByKey` | off | Ordering by key, being built for v0.2. Set on the dispatchers only; the applications that enqueue need no setting. See [Ordering by key](#ordering-by-key). |
| `WaybillDispatcherOptions.Partitions` | 16 | With `OrderByKey`: how many partitions (P) keys are spread over. From 1 to 1024; the same on every dispatcher. |
| `WaybillDispatcherOptions.PartitionLease` | 60 seconds | With `OrderByKey`: how long a dispatcher holds a partition without renewing it, so how long the keys of a crashed dispatcher wait. At least twice the claim lease, a whole number of milliseconds, and the same on every dispatcher. Without `OrderByKey`: how often the dispatcher checks that no other one orders. Always positive and at most one day. |
| `WaybillDispatcherOptions.MaxPerKey` | 1 | With `OrderByKey`: how many messages of one key a batch may carry (M). Above 1 the batch goes out in rounds, a key's next message only after the broker confirmed its previous one; see [What it costs](#what-it-costs). From 1 to 100; above 1 only with `OrderByKey`. It may differ between dispatchers. Above `BatchSize` it has no further effect. |
| `WaybillDispatcherOptions.MetricsInterval` | 15 seconds | How often the pending-age gauge is sampled. |

All options are validated at startup; a zero or negative retention fails the host instead of deleting everything.

On shutdown, the dispatcher stops claiming, waits for the batch in flight up to `PublishTimeout` (with rounds, the round
in flight; no other round starts), records what the broker confirmed, and hands back the rows it still holds. The host's `HostOptions.ShutdownTimeout` (30 seconds by default) bounds that wait: keep it above
`PublishTimeout`, or a message of the batch in flight may be published and handed back, and then published again.

## What to monitor

**`waybill.outbox.oldest_pending.age`** (meter `Waybill`, unit `s`): the age of the oldest outbox message a claim
could take, measured on the database clock. It is 0 when nothing is waiting. With ordering by key on, a message
waiting behind an earlier one of its key does not count: a key stopped by the DLQ shows in `blocked_keys`, and a key
stopped without one in `heads_repaired`. A value that keeps growing means events
are not leaving: broker down, dispatcher stopped, or a dispatcher that cannot keep up. Alert on it staying above what
your consumers can tolerate. If a sample fails (database unreachable), the gauge keeps its last value and an error is
logged; it never drops to 0 because of a failure. The meter comes from the host's `IMeterFactory` when one is
registered, so OpenTelemetry or any `MeterListener` picks it up; Waybill does not depend on OpenTelemetry. Its name
is `WaybillDiagnostics.MeterName`.

**Aggregate the outbox gauges by max, never by sum.** Every dispatcher instance reads the same table and reports the
same value of `oldest_pending.age`, `blocked_keys` and `dlq`; summing them across instances multiplies it by the number
of instances. An instance that cannot reach the database keeps its last value, so with max an alert can outlast its
cause for as long as that instance stays cut off: the safe side.

**Health check `waybill-dispatcher`:**

| Status | When | Suggested use |
| --- | --- | --- |
| `Healthy` | The dispatcher loop runs and the broker is reachable. | — |
| `Degraded` | The broker is unreachable (circuit breaker open or a connection failure). The outbox keeps accepting events; they are published when the broker is back. | Alert. Do not restart the process: it would not help. |
| `Unhealthy` | The dispatcher loop is not running, has not finished a cycle for longer than the lease plus the longest wait between cycles (30 s, or `PollingInterval` if longer) plus 30 s — 90 s with the defaults — or the database failed three cycles in a row. | Liveness / restart. |

The entry's data carries `oldest_pending_age_seconds`.

**The DLQ.** Dead-lettered messages stay in `waybill.outbox` with `status = 'dlq'` and a `dlq_reason`, and retention
never deletes them. **`waybill.outbox.dlq`** (meter `Waybill`, unit `{message}`) reports their count, with ordering on
or off, read through a partial index; before a dispatcher's first sample it reports nothing, never a 0 that would read
as an empty DLQ. Alert on it above 0. The same count in SQL:

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

## Tracing

The message carries the W3C trace context of the code that enqueued it (`traceparent` and `tracestate`), stored with
the outbox row, and the RabbitMQ transport sends it as headers. A consumer that continues from it stays in the trace of
the request that wrote the message, however long the message waited in the outbox.

**What to listen to.** Waybill opens its spans on the activity source `WaybillDiagnostics.ActivitySourceName`
(`Waybill`) and depends on no telemetry package. With OpenTelemetry:

```csharp
tracing.AddSource(WaybillDiagnostics.ActivitySourceName,
    RabbitMQActivitySource.PublisherSourceName, RabbitMQActivitySource.SubscriberSourceName);
metrics.AddMeter(WaybillDiagnostics.MeterName);
```

Listen to your incoming requests too (ASP.NET Core's instrumentation, for one). With the default parent-based
sampler, a request nobody samples is an activity that is not recorded, and nothing under it is either: the message
gets no span at all, though its `traceparent` still carries the request's context.

**The spans**, all in W3C format whatever the process default:

| Span | Kind | Opened by | Parent | What it carries |
| --- | --- | --- | --- | --- |
| `create` | Producer | `Enqueue`, after every check: a refused message opens nothing | The current activity | `messaging.message.id`, `waybill.message.name`. Its context is what the row stores |
| `send {destination}` | Producer | The dispatcher, once per message it hands to the transport (or judges itself: a payload above the limit), per claim | The stored context, also linked | `waybill.send.outcome`, `waybill.sequence` when ordered; with RabbitMQ, `messaging.system`, `messaging.destination.name` (`{exchange}:{routing key}`) and the routing key. Ends after the marking |
| `process` | Internal | The inbox, once per `ProcessAsync` or `ProcessInOrderAsync` call | The current activity | `waybill.inbox.handler`, `waybill.inbox.result`, `waybill.sequence` when ordered; an event `waybill.attempt` per run of the execution strategy |

- **`waybill.send.outcome`:** `published`, `retry` (handed back without spending an attempt), `returned` (waiting to be
  published again), `dlq`, `fenced` (another dispatcher holds the row now), or `unknown` (the marking itself failed:
  its commit may or may not have happened).
- **`error.type`,** set on every span that ends as an error:
  - from the transport: `connection`, `confirm_timeout`, `nacked` and `unspecified`;
  - from the outcome: `returned`, `defect` and `fenced`;
  - on `process`: `regressed`;
  - otherwise, the full name of the exception's type.
- **The parent of `send`.** Every attempt hangs from the stored context, so a message published again after a lease
  expired, hours later, lands in the same trace. The OpenTelemetry messaging conventions ask only for a link to the
  creation context; Waybill adds the parent so the trace stays one (ADR 0011).
- **`process` is Internal, not Consumer.** The consumption is the application's: with RabbitMQ.Client's subscriber
  source listened, the client's `deliver` span is the Consumer span and the parent.
- **No span for the dispatcher's own work.** Claims, partition upkeep, the repair sweep, sampling and retention open
  none: they would be a trace per polling cycle, unrelated to any message. Their SQL is traced by Npgsql's own
  instrumentation, if you turn it on, and never under a message's span.
- **Key and tenant.** Spans carry `waybill.key` and `waybill.tenant_id` only with `WaybillOptions.RecordKeyAndTenantInTraces`:
  a key is often an aggregate or customer id, and traces leave the application.
- **The conventions are still in development.** Waybill follows the OpenTelemetry semantic conventions for messaging
  1.44.0, which are not stable; attribute names may change with them. `create`, `process`, and `send` through a transport
  outside Waybill carry no `messaging.system`: they do not know the broker.

**RabbitMQ.Client's own spans.** The client opens a span for each publish while your telemetry listens to its publisher
source, with the current activity as parent, and writes that span's context over the message's `traceparent`. Waybill
makes the message's `send` span current while the client publishes, so the client's span hangs from it, and the
consumer receives a context in the message's trace. Without a listener on Waybill's source there is no `send` span, and
Waybill makes a sourceless activity, child of the stored context, current instead: the consumer still stays in the
trace, but the client's span hangs from a span id no backend received. Listen to both sources. In 0.1, with only the
client's source listened, every message reached its consumers in a new trace. The client names the destination of its
span after the exchange alone (`amq.default` for the default one), where Waybill's `send` uses `{exchange}:{routing key}`.

**Consumers that set the context by hand.** Without the client's subscriber source, make the message's context current
yourself before calling the inbox, and open the consumer's channel outside any activity: the client's consumer loop
runs in the execution context of the channel's creation, and would otherwise run every delivery under that activity.

**Late republication.** A message published again long after the request (a broker outage, a requeue from the DLQ) adds
spans to a trace a collector with tail sampling may have closed already.

## Sizing the inbox retention

The inbox applies a consumer's effect once per handler only while it remembers the message. After retention deletes
the row, a redelivery of that message is processed again. `ProcessAsync` does not refuse old messages: a message can
legitimately arrive late (it waited in the outbox while the broker was down), and refusing it would lose it.

Keep `InboxRetention` above the longest delay between enqueueing a message and its last possible redelivery:

- the longest the outbox may hold a backlog (broker outages you plan to survive);
- the longest a message may wait in the broker queue (consumer down, queue TTL);
- the window in which you might reprocess dead-lettered messages or replay a queue.

Replaying a queue or an offset older than the inbox retention is not covered. With sequence verification
(`ProcessInOrderAsync`), a late copy of an ordered message past the retention is not applied again: older than the
key's last applied message, it is refused as a regression; the last applied message itself is still recognized by its
id. Messages without a sequence keep the boundary above.

## Ordering by key

Ordering by key is new in v0.2 (not released yet) and is off by default. With `OrderByKey` on, the messages of each key
are published in commit order: per key, the first copy of each message to reach the broker arrives after the first
copy of every earlier one. Later copies (a republication after a lease ran out, an attempt left unconfirmed) can arrive
at any time and carry the same message id, which the consumers' inbox recognizes. Each ordered message carries its
position in the key, `sequence`, in the `waybill-sequence` header. Messages without a key are never ordered. With
`MaxPerKey` at 1 (the default) a batch carries one message per key; above 1, up to that many, published in rounds: each
call to the broker carries at most one ordered message of a key, and a key's next message goes out only after the
broker confirmed its previous one.

With `OrderByKey` on, each key belongs to a partition, `key_hash % Partitions`, and each partition is held by one
dispatcher at a time. Dispatchers share the partitions evenly (at most `ceil(Partitions / live dispatchers)` each),
renew them every `(PartitionLease − claim lease) / 4` (7.5 seconds with the defaults), and hand them back on shutdown. With ordering on, no wait between cycles is longer than a quarter of `PartitionLease`. A crashed dispatcher's partitions move to the others after
`PartitionLease`. Messages without a key are not ordered and are published by any dispatcher. Dispatchers beyond
`Partitions` publish only messages without a key.

`Partitions` and `PartitionLease` are stored in `waybill.settings` by the first dispatcher that orders, and every
dispatcher checks them at startup and every `PartitionLease`. A dispatcher whose values differ, or one without
`OrderByKey` while the settings exist, stops with a critical log, and its health check reports `Unhealthy`.

To turn ordering on, stop every dispatcher, set `OrderByKey` on every dispatcher, and start them. While
`waybill.settings` exists, the database numbers every message with a key as it is written; transactions already open
when ordering is turned on may still write theirs unnumbered, and those are not ordered. For order from the first
message, turn ordering on with the applications that enqueue stopped.

To change `Partitions` or `PartitionLease`, stop every dispatcher, change the stored values (here to 32 partitions) and
start them with the same values. The settings row stays, so messages keep being numbered meanwhile:

<!-- ordering-change -->
```sql
BEGIN;
UPDATE waybill.settings SET partitions = 32, partition_lease = interval '60 seconds';
WITH old AS (DELETE FROM waybill.outbox_partitions RETURNING epoch)
INSERT INTO waybill.outbox_partitions (partition, epoch)
SELECT g, (SELECT coalesce(max(epoch), 0) FROM old) FROM generate_series(0, 31) AS g;
DELETE FROM waybill.outbox_instances;
COMMIT;
```

To turn ordering off, stop every dispatcher, clear the ordering state, and start them without `OrderByKey` (and with
`MaxPerKey` back at 1, which needs ordering). From then on, messages are not numbered, and those numbered before go
out without their sequence: consumers that verify sequences only deduplicate them. Never delete
`waybill.outbox_keys`: turned on again, each key continues from its last sequence, where starting again at 1 would look
like a regression to its consumers. The `UPDATE` makes every unfinished numbered message claimable without ordering:
one waiting behind its key's head is otherwise left out of the claim, and behind a dead-lettered head (which stops
nothing once ordering is off) it would never go. Before turning ordering on again, release or requeue the keys the DLQ
stops (see "A key stopped by the DLQ"): every row of such a key stays marked claimable, and while they wait the claim
reads them all on every cycle, as it did before the head pointer (measured: 2,000 stopped keys with 50 messages behind
each, about 405 thousand buffers per claim instead of 2,500).

<!-- ordering-reset -->
```sql
BEGIN;
UPDATE waybill.outbox SET is_head = true
WHERE sequence IS NOT NULL AND status IN ('pending', 'claimed', 'dlq') AND is_head IS NOT TRUE;
DELETE FROM waybill.outbox_partitions;
DELETE FROM waybill.outbox_instances;
DELETE FROM waybill.settings;
COMMIT;
```

### Before turning it on

- **Upgrade order.** Apply the v0.2 schema (`WaybillSchema.MigrateAsync`) before deploying the applications that
  enqueue with the v0.2 package: their INSERT carries a column the 0.1 schema does not have.
- **Register the context with `AddDbContext`, `AddDbContextPool` or a context factory.** `AddWaybillOutbox` then adds an
  interceptor that hands the database the keys of each `SaveChanges`, so it locks them in one order. A context built by
  hand lacks it (a warning says so once): nothing is lost or reordered, but transactions that enqueue several keys may
  deadlock and have to be retried.
- **A key's counter is locked from the INSERT to the commit.** Transactions of the same key wait for each other.
  Enqueue in the last `SaveChanges` before the commit: a transaction that enqueues a key and then writes an application
  row deadlocks with one that writes that row and then enqueues the key, and no setting prevents it. Set
  `lock_timeout` and `idle_in_transaction_session_timeout` for the applications, so a transaction left open does not
  hold a key indefinitely. To find who holds a key:

  ```sql
  SELECT pid, pg_blocking_pids(pid) AS blocked_by, wait_event, now() - xact_start AS transaction_age, state, query
  FROM pg_stat_activity WHERE cardinality(pg_blocking_pids(pid)) > 0;
  ```

- **`REPEATABLE READ` and `SERIALIZABLE`.** Two such transactions enqueuing the same key at the same time: one fails
  with 40001 (EF reports it as a transient failure). Retrying it succeeds. `READ COMMITTED`, PostgreSQL's default, just
  waits. The head pointer adds no failure there, at a price: a message written at those levels is marked claimable at
  once, so on a hot key fed at those levels the claim reads the key's whole queue again, as it did before the head
  pointer, until the key drains. Enqueue ordered keys in `READ COMMITTED`. Finishing or deleting a message of an ordered key by hand (a release, a `DELETE`) at those levels is refused:
  run it in `READ COMMITTED`.
- **Waybill's triggers must stay enabled.** `outbox_sequence` numbers keyed messages; `outbox_head_check`,
  `outbox_head_promote` and `outbox_head_promote_on_delete` keep each key's first unfinished message marked claimable.
  A dispatcher that orders stops with a critical log if any is missing or disabled, or if its own sessions run with
  `session_replication_role = replica`. Other sessions with `replica` (some restore and replication tools) skip them:
  messages they write are not numbered and not ordered, and changes they make may leave a key unmarked (see
  "Unmarked heads" below).
- **The dispatcher may wait for a producer's commit.** A producer whose message sits behind another transaction's
  unfinished one checks that one at its commit, locking it for an instant; the dispatcher publishing it at that moment
  waits for the commit to end. That is short, unless the application holds its transaction past that point:
  `SET CONSTRAINTS ALL IMMEDIATE` (the check then runs at the insert and the lock lasts the whole transaction), slow
  deferred constraints of its own, synchronous replication, or `PREPARE TRANSACTION`. Order is kept, but the
  dispatcher instance waits with its batch, for as long as that transaction stays open: meanwhile it neither claims
  nor renews its partitions, and the messages of the batch its marking already reached stay locked. Another instance
  takes over its partitions once their lease runs out; its own messages go once the transaction ends. The wait has no
  timeout, on purpose: no other claim takes a message the marking holds, so nothing is published twice while it lasts.
  Past `PartitionLease`, the messages of the batch the marking had not yet reached can be claimed and published again
  by the new owner: duplicates, never out of order.

### What it costs

With ordering off, the numbering trigger still runs on every message with a key, only to find that ordering is off:
one read of `waybill.settings` per row. Messages without a key pay nothing, and the claim is unchanged. Measured with
`pgbench`, one keyed row per transaction, 16 clients and no `fsync` (the CPU is the limit): 13% fewer transactions per
second than without the trigger, about 25 µs more per transaction. In the scheduled test (EF Core, 16 producers on a
4-vCPU CI runner, one message per transaction), the p99 of the transaction is 1% to 27% above `0.1.0-alpha`, median
13%. With `fsync` on, where the commit is the limit, the same `pgbench` showed 3% to 5% fewer transactions per second
(ADR 0008, addendum of 2026-10-21).

Measured with EF Core 10 and PostgreSQL 18, 32 producers, one machine (stage 8 prototype); compare the ratios, not the
absolute numbers:

| Case | Ordering off | Ordering on |
| --- | --- | --- |
| One new or existing key per transaction | baseline | 5% to 12% fewer transactions per second, same p99 |
| One hot key for every transaction | — | about 350 transactions per second for that key, p99 about 600 ms |
| The same, with 2 ms of work after the `SaveChanges` | — | about 170 per second: the key stays locked meanwhile |
| Three keys out of twenty per transaction | baseline | about 600 per second, no deadlock |

Publishing: with `MaxPerKey` at 1, one message per key per batch, so a key's throughput is about one message per cycle
(claim, round trip to the broker, marking). With `MaxPerKey` M, a cycle moves up to M messages of a key, one round trip
each, with one claim and one marking for them all. Measured with one key alone and a broker taking 2 ms per call: 144
messages per second with M = 1, 387 with M = 20. All the rounds of a batch share `PublishTimeout`: when they run out of
it, the dispatcher lowers M on its own (a warning says so) and raises it back to `MaxPerKey` as batches run whole. A
dispatcher that crashes, or whose marking fails, after r rounds lets up to r messages of a key be published again by
the next owner, where M = 1 lets one: later copies, which the G4 allows and the inbox absorbs.

The claim reads only each key's first unfinished message, never the queue behind it: with 100 thousand messages stopped
behind a dead-lettered one, a claim of 100 messages reads about 4,500 buffers (p50 5.6 ms), and one hot key drains at
the same cost per message whatever its queue. Publishing a message with others of its key behind it also marks the next
one claimable: one more row update. Watch `waybill.outbox.blocked_keys` and release or requeue stopped keys promptly.

`waybill.outbox_keys` keeps one row per key ever ordered, about 130 bytes each (10 million keys, about 1.3 GB). It is
never cleaned: a key starting again at 1 would look like a regression to its consumers. Its size:

```sql
SELECT pg_size_pretty(pg_total_relation_size('waybill.outbox_keys'));
```

### Upgrading and rolling back

The v0.2 migration builds its indexes `CONCURRENTLY`, outside the migration's transaction: run it over a direct
connection (not a pooler in transaction mode), and expect it to wait for transactions already open. Its short
exclusive locks on the outbox (new columns, triggers, the swap of the claim index) give up after 10 seconds instead of
holding every producer behind a long transaction. If it stops halfway, run it again: every step is idempotent and an
index left invalid is rebuilt. The claim index is rebuilt with a narrower predicate, which a 0.1 dispatcher's claim
cannot use: deploy the v0.2 dispatchers right after the migration. Afterwards, nothing should be listed here:

```sql
SELECT indexrelid::regclass FROM pg_index WHERE NOT indisvalid;
```

The schema only moves forward once ordering has numbered a key or released a message: the v0.2 migration's `Down`
refuses then. To go back to the 0.1 binaries, turn ordering off first (above) and keep the v0.2 schema.

### A key stopped by the DLQ

With ordering on, a message of a key goes out only after every earlier message of that key was published. A message
that reaches the DLQ therefore stops its key: the messages behind it wait, and the other keys keep flowing. The error
log of the dead-lettering names the key and the sequence. **`waybill.outbox.blocked_keys`** (meter `Waybill`) counts
the keys stopped this way; it is not reported while ordering is off. A stopped key does not show in
`waybill.outbox.oldest_pending.age`, which counts only messages a claim could take: alert on `blocked_keys` itself.

The keys stopped by the DLQ, the message that stops each one and how many wait behind it:

<!-- blocked-keys -->
```sql
SELECT d.key, d.sequence, d.id, d.dlq_reason,
       (SELECT count(*) FROM waybill.outbox w
        WHERE w.key = d.key AND w.sequence > d.sequence AND w.status IN ('pending', 'claimed')) AS waiting
FROM waybill.outbox d
WHERE d.status = 'dlq' AND d.sequence IS NOT NULL
ORDER BY d.key;
```

Keys held up for now by a message waiting after a `basic.return` (they move again when it goes through, or stop when
it reaches the DLQ):

<!-- waiting-heads -->
```sql
SELECT key, sequence, id, attempts, next_attempt_at
FROM waybill.outbox
WHERE status = 'pending' AND sequence IS NOT NULL AND next_attempt_at > clock_timestamp()
ORDER BY next_attempt_at;
```

Everything not yet published of one key, and the dispatcher that holds its partition:

<!-- key-rows -->
```sql
SELECT o.sequence, o.id, o.status, o.attempts, o.next_attempt_at, o.lease_until, o.owner, o.dlq_reason,
       p.owner AS partition_owner, o.is_head
FROM waybill.outbox o
LEFT JOIN waybill.settings s ON true
LEFT JOIN waybill.outbox_partitions p ON p.partition = o.key_hash % s.partitions
WHERE o.key = 'order-42' AND o.status NOT IN ('published', 'released')
ORDER BY o.sequence NULLS FIRST
LIMIT 20;
```

Two ways out. If the message can still go (for `312 NO_ROUTE`, create the binding), hand it back with the DLQ
requeue above: it goes out first, then the rest of its key, in order. If it must never go, release it: it stays in the
table as `released`, with who released it and when, and its key moves on without it. Its consumers will see a gap in
the key's sequence at that point:

<!-- key-release -->
```sql
UPDATE waybill.outbox
SET status = 'released', released_at = clock_timestamp(), released_by = session_user
WHERE key = 'order-42' AND status = 'dlq' AND sequence IS NOT NULL
RETURNING id, sequence, dlq_reason;
```

The returned sequences are the gaps; no row returned means the key was mistyped. A released message never goes back
to `pending` (the database refuses it): its successors may already be out. Releasing does not unsend: if an earlier
attempt to publish it went unconfirmed (a timeout, a lost connection), that attempt may have reached the broker, or
may still reach it after the key's later messages. Consumers then see a message older than ones they already have,
which their inbox does not mistake for a duplicate; with sequence verification it is refused as a regression (see
"Sequence verification in the inbox"). Retention keeps released messages, like the DLQ.

Never delete a message of a key to get it moving: release it. A deleted message leaves no record of the gap, and an
unconfirmed attempt to publish it may still arrive.

### Unmarked heads

The claim reads only the first unfinished message of each key, marked by `is_head` (with `is_head` false or null the
message waits behind an earlier one of its key). Waybill keeps that mark itself: when a message is written, when its
transaction commits, and when the one before it is published, released or deleted. Something outside Waybill can
leave a key's first message unmarked, and the key then stops without a DLQ message to show for it: one of Waybill's
triggers disabled, an `UPDATE` run with `session_replication_role = replica`, a restore of part of the table, a
hand-written `UPDATE` of `is_head`.

On every partition upkeep, each dispatcher that orders by key walks the next slice of 2,000 keys (of all partitions,
in key order) and marks any such message in the partitions it holds, with an error log naming the key and the
sequence. **`waybill.outbox.heads_repaired`** (meter `Waybill`) counts them, and stays at zero unless something changed
the outbox outside Waybill: alert on any increase. Each instance makes the full pass over the K keys with unfinished
messages in K / 2,000 upkeeps, one about every `(PartitionLease − Lease) / 4`; more instances do not shorten it. To
find and mark them at once:

<!-- unmarked-heads -->
```sql
SELECT o.key, o.sequence, o.id, o.status
FROM waybill.outbox o
WHERE o.sequence IS NOT NULL AND o.status IN ('pending', 'claimed', 'dlq') AND o.is_head IS NOT TRUE
  AND o.sequence = (SELECT min(h.sequence) FROM waybill.outbox h
                    WHERE h.key = o.key AND h.sequence IS NOT NULL AND h.status IN ('pending', 'claimed', 'dlq'))
ORDER BY o.key;
```

<!-- mark-heads -->
```sql
UPDATE waybill.outbox o SET is_head = true
WHERE o.sequence IS NOT NULL AND o.status IN ('pending', 'claimed', 'dlq') AND o.is_head IS NOT TRUE
  AND o.sequence = (SELECT min(h.sequence) FROM waybill.outbox h
                    WHERE h.key = o.key AND h.sequence IS NOT NULL AND h.status IN ('pending', 'claimed', 'dlq'))
RETURNING o.key, o.sequence;
```

Marking is always safe: the claim still checks that a message is its key's first, so a mark too many costs one more
look and nothing goes out of order. Both read every unfinished numbered message.

## Sequence verification in the inbox

Ordering by key guarantees the first arrival of each message, not every arrival: a late copy can come after the key's
later messages. `ProcessInOrderAsync` (new in v0.2) never applies, for a key, a message whose `sequence` is not after
the last one the handler applied for that key. It deduplicates first, as `ProcessAsync` does, then checks the sequence:

- **Processed:** in order; the handler ran and the key moved to this message. Acknowledge it.
- **Duplicate:** already applied. Also the result for the key's last applied message redelivered after inbox retention
  deleted its record: same sequence, same message id. Acknowledge it.
- **Regressed:** older than the last applied message, or the last applied sequence on another message (a producer
  restored from a backup). Nothing ran, nothing was recorded, and a redelivery is refused again. Send it to the queue's
  dead-letter exchange (`basic.nack` without requeue); never acknowledge it as processed. A warning with EventId 50
  names the handler, the key, both sequences and both message ids, and **`waybill.inbox.regressions`** (meter
  `Waybill`, a counter) adds one, tagged with `waybill.inbox.handler` and `waybill.inbox.regression` (`lower` or
  `reused`). The handler name becomes a metric tag: keep it a constant, never a value per message.

A gap is not an error, and the inbox never holds a message back waiting for one: a consumer of some of a key's message
types sees gaps, and so does one after a release. `ProcessAsync` never reads `waybill.inbox_keys`.

What it relies on:

- **The producer orders by key.** Messages without a key, written with ordering off, or published by a dispatcher that
  does not order carry no `waybill-sequence`, and are only deduplicated. Turning ordering off includes the backlog
  numbered before it: from then on nothing goes out with a sequence.
- **One producer per handler name.** Sequences are counted per key in the producer's database. Two producers behind one
  handler name would refuse each other's messages: give each its own handler name. Sequences are tracked per handler
  name, so give one name to every message type of a key whose order matters.
- **One consumer at a time, one delivery at a time.** Order of arrival is order of application only then. Declare the
  queue with `x-single-active-consumer` (a second instance waits) and handle deliveries one by one. With competing
  consumers nothing is applied out of order, but a valid message may come back `Regressed`. A single-active-consumer
  quorum queue refuses `basic.get`: the management UI's "Get messages" does not work on it.
- **Dead-lettering that never drops.** The queue needs a dead-letter exchange: `basic.nack` without requeue on a queue
  without one discards the message. Quorum queues dead-letter at most once by default; `x-dead-letter-strategy:
  at-least-once` needs `x-overflow: reject-publish` on the same queue, and without it RabbitMQ falls back to at-most-once
  and only logs it. Give the dead queue `x-overflow: reject-publish` too: with the default, a full dead queue drops its
  oldest messages. Dead-lettering is asynchronous. When the dead queue is full, a dead letter stays held in the source
  queue, counted in `messages` but in neither `messages_ready` nor `messages_unacknowledged`; RabbitMQ retries it later
  (measured on 4.3.6: not within a minute of making room). Watch the dead queue's length, and that difference.
- **Its table.** The application's role needs `SELECT`, `INSERT` and `UPDATE` on `waybill.inbox_keys`.

The samples' `rabbitmq/definitions.json` declares the queue this way. RabbitMQ keeps a queue's arguments: importing
definitions again over an existing queue changes nothing, with no error, so a queue declared before needs to be
recreated.

**Locks and timeouts.** The key's row stays locked from the check to the commit: a delivery of the same key to the same
handler waits for it, and a slow handler holds its key back. The wait counts against the command timeout (30 seconds by
default with Npgsql; past it the call throws, retried as a whole only under `EnableRetryOnFailure`, otherwise by the
consumer's own retry) and against the broker's delivery timeout (`consumer_timeout`).

**A handler that fails.** `basic.nack` with requeue does not count toward a quorum queue's `x-delivery-limit` (measured
on RabbitMQ 4.3.6): only a delivery handed back by a closed channel does, after a crash or a `consumer_timeout`. A
message that always fails and is requeued comes back for ever and, with one active consumer, holds the whole queue.
Retry in the consumer with a growing wait, long enough to ride out a database restart, then send the message to the
dead-letter exchange (the sample: ten attempts, about two and a half minutes). The key's next message is then applied
without it: a gap. Sent again later, it comes back `Regressed`.

**The consumer's dead-letter queue is evidence, not a retry queue.** A regression may be a late copy of a message the
handler already applied, once inbox retention deleted its record. Reapplying a message from it is the application's
decision, knowing that. To see where a key stands for a handler:

```sql
SELECT last_seq, last_message_id FROM waybill.inbox_keys
WHERE handler = 'receipts.issue-receipt' AND key = 'order-42';
```

**A producer that starts numbering again.** If the producer's database is rebuilt and its keys start again at 1,
everything it sends is refused as a regression. With the handler's consumers stopped, forget the handler's sequences:

<!-- inbox-forget-keys -->
```sql
DELETE FROM waybill.inbox_keys WHERE handler = 'receipts.issue-receipt';
```

Forgetting them also lets a late copy of an old message through as new: do it only when the producer really started
again. `waybill.inbox_keys` keeps one row per handler and key ever verified, and is never cleaned by retention.

## PostgreSQL: JIT

With a backlog, the claim's estimated cost passes `jit_above_cost`, and PostgreSQL would compile it on every run (about
half a second per claim with 200 thousand messages waiting). The dispatcher turns JIT off on each connection of its own
(`SET jit = off`), with no startup parameter. Behind a pooler in transaction mode (PgBouncer, RDS Proxy) a session
setting does not follow the transaction to the server: set it for the role the dispatcher connects as instead.

```sql
ALTER ROLE waybill_dispatcher SET jit = off;
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

Do not set a `fillfactor` on the outbox: it only helps HOT updates, and they cannot happen here. With ordering by
key, marking a key's next message claimable is one more such update per message published with others of its key
behind it.

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
