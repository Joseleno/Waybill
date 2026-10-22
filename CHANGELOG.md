# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- Ordering by key (v0.2): `WaybillDispatcherOptions.OrderByKey` (off by default) publishes the messages of each aggregate key in commit order, one per key at a time. Keys map to `key_hash % Partitions`; each partition is held by one dispatcher at a time, shared evenly among live dispatchers, renewed every cycle and handed back on shutdown, and the claim checks ownership in the same statement. `Partitions` and `PartitionLease` are stored in the database by the first dispatcher that orders and checked by every other one; a mismatch stops the dispatcher with a critical log (ADR 0007). While ordering is on, the database numbers every keyed message as it is written, in commit order, and the claim takes only the first message of each key not yet published; a message in the DLQ stops its key until it is requeued or released (ADR 0008).
- `OutgoingMessage.Sequence`, the position of an ordered message in its key; the RabbitMQ transport sends it in the `waybill-sequence` header.
- Sequence verification in the inbox (v0.2): `IInbox.ProcessInOrderAsync` never applies, for a key, a message whose sequence is not after the last one the handler applied for it. It deduplicates first, then checks the sequence, and returns `OrderedInboxResult.Regressed` for a regression: nothing applied, nothing recorded, a warning (EventId 50) with the reason, and the application sends the message to the dead-letter exchange. It detects; it never reorders, and a gap is not an error. `MessageOrder` carries a message's key and sequence; `GetWaybillOrder` reads them from the RabbitMQ headers. The broker conditions it relies on (one active consumer per queue, at-least-once dead-lettering) are in `OPERATIONS.md`. `FakeInbox` verifies sequences the same way and counts `Regressions`.
- `waybill.outbox.blocked_keys`, the keys stopped by a message in the DLQ while ordering is on, and in `OPERATIONS.md` the SQL to find them and to release a key.
- `waybill.outbox.heads_repaired`, the messages a dispatcher found first in their key yet not marked claimable and marked, with an error log each: zero unless something changed the outbox outside Waybill. `OPERATIONS.md` has the SQL to find and mark them at once.
- `WaybillDispatcherOptions.MaxPerKey` (1 by default, up to 100, above 1 only with `OrderByKey`): a batch may carry up to that many messages of a key, published in rounds. Each call to the transport carries at most one ordered message of a key, and a key's next message goes out only after the broker confirmed its previous one. All the rounds of a batch share `PublishTimeout`; a round starts only if what is left is at least what the previous one took, and the dispatcher lowers its effective M while rounds run out of time. Any message of a round to be published again ends the batch; a stop request lets no new round start, and what the broker confirmed is recorded as published (ADR 0008, part 2; ADR 0003).
- `waybill.outbox.dlq`, the messages in the outbox DLQ, with ordering on or off, read through a new partial index; nothing is reported before a dispatcher's first sample. Every dispatcher instance reports the same value of the outbox gauges: aggregate them by max, not sum (`OPERATIONS.md`).
- `waybill.inbox.regressions`, a counter of the messages the inbox's sequence check refused, tagged with the handler and the reason (`lower`, `reused`). The inbox registers it, so a service that only consumes reports it too.
- `WaybillDiagnostics.MeterName`, the name of Waybill's meter, for `AddMeter`.
- Tracing (ADR 0011): spans on the activity source `WaybillDiagnostics.ActivitySourceName`, with no telemetry package. `create` at `Enqueue`; `send` for each message the dispatcher publishes, child of the context recorded at `Enqueue` and linked to it, so every attempt lands in the same trace, ending with `waybill.send.outcome` from what the database recorded; `process` in the inbox, with the result. With RabbitMQ.Client's sources listened too, a message's trace runs from the request that wrote it to the handler. Claims, upkeep, sampling and retention open no span. The spans follow the OpenTelemetry semantic conventions for messaging 1.44.0, still in development: attribute names may change with them. `OPERATIONS.md` lists the spans, their attributes and the values of `error.type`.
- `WaybillOptions.RecordKeyAndTenantInTraces` (off): Waybill's spans carry the message's key and tenant id.

### Changed

- A message returned as unroutable (`basic.return`) now waits a growing interval before it is published again: `ReturnBackoff` (1 minute by default) after the first return, doubling with each return up to `MaxReturnBackoff` (10 minutes). With the defaults it reaches the outbox DLQ after 15 minutes of waiting instead of about 5 seconds, time to create the missing binding. Set `ReturnBackoff` to zero for the 0.1 behavior (ADR 0006).
- The `waybill` schema gains the `outbox.next_attempt_at` column and the `settings`, `outbox_partitions` and `outbox_instances` tables. Run `WaybillSchema.MigrateAsync` before upgrading the dispatcher; the migration only adds, and pending rows stay claimable. During a rolling upgrade, a 0.1 dispatcher still running ignores the wait and may publish a returned message once more before its time, which is harmless.
- Message ids are UUIDv7 that increase in enqueue order within a process, also inside one millisecond, so the dispatcher claims messages of one process in the order they were enqueued.
- `AddWaybillOutbox` adds an interceptor to the context's options; on a context registered by hand, outside `AddDbContext`, `AddDbContextPool` or a context factory, the first message enqueued with a key logs that once, at `Information`.
- The `waybill` schema gains `outbox_keys`, the `outbox.lock_keys`, `released_at` and `released_by` columns, the `released` status, two partial indexes built concurrently, and the triggers that number keyed messages and keep `published` and `released` final. The application's INSERT now carries `lock_keys`: run `WaybillSchema.MigrateAsync` before deploying the applications that enqueue.
- With ordering on, the claim reads only the first unfinished message of each key, marked by the new `outbox.is_head` column, instead of reading past the queue behind it: a key stopped by the DLQ with 100 thousand messages behind, or one hot key with a long queue, no longer slows every claim. `ix_outbox_claimable` is rebuilt, under the same name, to hold only those messages; deploy the v0.2 dispatchers right after the migration. Turning ordering off now also marks every unfinished numbered message (`OPERATIONS.md`). A dispatcher that orders stops with a critical log if any of the ordering triggers is missing or disabled, or if its own sessions run with `session_replication_role = replica`. Releasing or deleting a message of an ordered key outside `READ COMMITTED` is refused. `waybill.outbox.oldest_pending.age` now counts only messages a claim could take.
- Two messages of one key finished at once (a zombie dispatcher and the partition's new owner, or a `DELETE` by hand) no longer leave the key without its next message marked claimable; a key's messages published in one marking promote the next one once. The repair sweep finds its place in the key order with a prepared statement too, and a failing sweep no longer stops the dispatcher's cycle. The ordered claims find their rows by primary key, avoiding a sequential scan in a generic plan, and the dispatcher's own sessions run without JIT, which compiled the claim on every run with a backlog. `SchemaV0_2` changed again: development databases that applied an earlier v0.2 build need to be recreated.
- With ordering off, writing a message with a key still runs the numbering trigger, which reads `waybill.settings` once per row. The cost is measured and published in `OPERATIONS.md` ("What it costs"); messages without a key pay nothing.
- **Breaking:** `IInbox<TContext>` gains `ProcessInOrderAsync`; an implementation outside Waybill must add it. `ProcessAsync` is unchanged.
- The `waybill` schema gains `inbox_keys`, the last sequence each handler applied per key. Retention never deletes it, and the v0.2 migration's `Down` refuses once it has rows. A database that applied the unreleased v0.2 migration before this change must be recreated.
- The samples: Billing orders by key; Receipts verifies sequences, retries a failing message in the consumer before dead-lettering it (a `basic.nack` with requeue never counts toward `x-delivery-limit`), and its queue gets single active consumer and at-least-once dead-lettering.
- The `waybill` schema gains `ix_outbox_dlq`, built concurrently. A database that applied the unreleased v0.2 migration before this change must be recreated.
- While anything listens to Waybill's activity source, the `traceparent` stored with a message is that of its `create` span, and a transport receives in `OutgoingMessage.Headers` the `traceparent` of the message's `send` span, both in the trace of the code that enqueued the message. A transport outside Waybill propagates it as before.

### Fixed

- With RabbitMQ.Client's publisher source listened to (`RabbitMQActivitySource.PublisherSourceName`), the client wrote its own span's context over each message's `traceparent`, and consumers started a new trace. The transport now makes the message's stored context current while the client publishes, so consumers stay in the trace of the code that enqueued the message (ADR 0011).

## [0.1.0-alpha] - 2026-10-06

First release. What each guarantee covers, and the test that proves it, is in [GUARANTEES.md](GUARANTEES.md).

### Added

#### Outbox (`Waybill`, `Waybill.EntityFrameworkCore.PostgreSql`)

- `IOutbox<TContext>.Enqueue(...)` (`AddWaybillOutbox<TContext>()`): writes messages to the outbox in the same `SaveChanges` and transaction as the application's data. The record joins the `DbContext` change tracker at enqueue time, so the EF unit of work is the single source of truth: `ChangeTracker.Clear()` discards it, and a repeated `SaveChanges` writes it once.
- The message id (UUIDv7), type registration, serialization and size are fixed and checked at enqueue time. Unregistered types, serialization failures, payloads above `MaxPayloadBytes` and names, correlation ids, keys or tenant ids above 255 bytes are refused before anything is written. `TransactionScope` and `AutoTransactionBehavior.Never` without a transaction are refused too.
- Message registry with stable names and `JsonTypeInfo` (`AddWaybill(o => o.AddMessage("billing.invoice-paid.v1", ...))`); `MaxPayloadBytes` is required.
- An event enqueued and never saved is logged as an error when the DI scope ends, or thrown with `ThrowOnPendingMessagesAtDispose`.
- Package-owned `waybill` schema (`outbox`, `inbox`), shipped as migrations and applied by `WaybillSchema.MigrateAsync`; the application maps the outbox with `modelBuilder.MapWaybillOutbox()`.
- Envelope with `traceparent`, `tracestate`, correlation id, optional tenant id, aggregate key and content type.

#### Dispatcher

- `AddWaybillDispatcher<TContext>()`: hosted service on the context's database (an explicit `ConnectionString` wins). Per-row claim with `FOR UPDATE SKIP LOCKED`, lease (`PublishTimeout` + `LeaseMargin`) and fencing token; publication outside any transaction; fenced marking and hand-back; graceful shutdown that hands back what it holds.
- Failure classification (ADR 0003): only message defects go to the outbox DLQ, with a reason. Connection and channel failures hand the batch back without spending attempts and open a per-dispatcher circuit breaker (no claims while open, a one-message probe when half-open, open period doubling up to 30 s). Confirmation timeouts and nacks halve the batch without opening it; repeated pressure at a batch of one opens it as a silent outage. `basic.return` has its own budget (`MaxReturns`).
- `ITransport` contract with per-message results (`Confirmed`, `Retry`, `Returned`, `Defect`).
- The dispatcher and the retention check `default_transaction_isolation` before their first cycle and stop with a critical log unless it is `read committed`, which the claim relies on.

#### RabbitMQ transport (`Waybill.RabbitMQ`)

- `AddWaybillRabbitMQ(...)`: publishes to one exchange you declare, with the registered name as routing key, publisher confirms tracked per message, `mandatory` and persistent delivery. AMQP properties (`message_id`, `type`, `content_type`, `timestamp`, `correlation_id`) and headers (`traceparent`, `tracestate`, `tenant_id`, `waybill-key`). Waybill does not create topology.
- When the broker closes the channel with 406 mid-batch, the unconfirmed messages are republished one by one on fresh channels, and only the one that closes a channel alone goes to the DLQ.
- `GetWaybillMessageId()` on `IReadOnlyBasicProperties`, for consumers written with plain RabbitMQ.Client.

#### Inbox

- `IInbox<TContext>.ProcessAsync(handler, messageId, handle)` (`AddWaybillInbox<TContext>()`): records `(handler, message_id)` with `INSERT … ON CONFLICT DO NOTHING` and runs the handler with the same context in one `READ COMMITTED` transaction, so the effect applies once per handler. A repeat returns `InboxResult.Duplicate` without running it; a failure rolls back everything, events the handler enqueued included. Writing through another instance of the context while a handler runs throws.

#### Operations

- Retention (`AddWaybillRetention<TContext>()`, `WaybillRetentionOptions`): deletes published outbox rows past `OutboxRetention` (7 days, from publication) and inbox rows past `InboxRetention` (30 days, from processing), in batches with `FOR UPDATE SKIP LOCKED`. Pending, claimed and dead-lettered rows are never deleted.
- Gauge `waybill.outbox.oldest_pending.age` (meter `Waybill`, seconds), sampled every `MetricsInterval` (15 s) on the database clock.
- Health check `AddHealthChecks().AddWaybillDispatcherCheck()`: `Degraded` while the broker is unreachable, `Unhealthy` when the loop stops or stalls, or the database fails three cycles in a row.
- [OPERATIONS.md](docs/OPERATIONS.md): defaults, shutdown, what to monitor, how to send dead-lettered messages back, isolation level, autovacuum, rebuilding the claim index after a long transaction and how to size the inbox retention.

#### Testing (`Waybill.Testing`)

- `FakeOutbox<TContext>` and `FakeInbox<TContext>`, in memory, with assertions (`ShouldContain`, `ShouldBeEmpty`, `ShouldHaveNoPending`), each checked for equivalence against the real one.

#### Repository

- Runnable sample (`samples/`): a billing API and a receipts consumer on plain RabbitMQ.Client with the inbox, `docker compose up --wait`, tests with the fakes and broker outage scripts, all run in CI.
- CI on pull requests (PostgreSQL 15 and 18, short chaos tests, the sample), a scheduled workflow for the long scenarios, and a tag-based release with Trusted Publishing that validates Source Link and symbols before publishing.
- `GUARANTEES.md`, with a test that fails the build when a cited test disappears.
- Design documents and ADRs 0001 to 0005 in `docs/` (in Portuguese).

[Unreleased]: https://github.com/Joseleno/Waybill/compare/v0.1.0-alpha...HEAD
[0.1.0-alpha]: https://github.com/Joseleno/Waybill/releases/tag/v0.1.0-alpha
