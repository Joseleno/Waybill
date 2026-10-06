# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Changed

- A message returned as unroutable (`basic.return`) now waits a growing interval before it is published again: `ReturnBackoff` (1 minute by default) after the first return, doubling with each return up to `MaxReturnBackoff` (10 minutes). With the defaults it reaches the outbox DLQ after 15 minutes of waiting instead of about 5 seconds, time to create the missing binding. Set `ReturnBackoff` to zero for the 0.1 behavior (ADR 0006).
- The `waybill` schema gains the `outbox.next_attempt_at` column. Run `WaybillSchema.MigrateAsync` before upgrading the dispatcher; the migration only adds the column, and pending rows stay claimable.

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
