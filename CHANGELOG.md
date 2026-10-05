# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- `AddWaybillDispatcher<TContext>()` and `AddWaybillRetention<TContext>()`: take the connection string from the registered `DbContext`; an explicit `ConnectionString` still wins.
- Runnable sample (`samples/`): a billing API and a receipts consumer on plain RabbitMQ.Client with the inbox, `docker compose up --wait`, tests with the fakes, and broker outage scripts (bash and PowerShell), all run in CI.
- Retention (`AddWaybillRetention`, `WaybillRetentionOptions`): hosted service that deletes published outbox rows past `OutboxRetention` (7 days, counted from publication) and inbox rows past `InboxRetention` (30 days, counted from processing), in batches with `FOR UPDATE SKIP LOCKED`; pending, claimed and dead-lettered rows are never deleted. Any number of instances may run it. New migration: index `ix_inbox_processed_at`. ADR 0004.
- Gauge `waybill.outbox.oldest_pending.age` (meter `Waybill`, seconds), sampled in the background every `WaybillDispatcherOptions.MetricsInterval` (15 s) on the database clock; a failed sample keeps the last value.
- Dispatcher health check (`AddHealthChecks().AddWaybillDispatcherCheck()`): `Degraded` while the broker is unreachable, `Unhealthy` when the loop is not running or stalled or the database failed three cycles in a row. `Waybill.EntityFrameworkCore.PostgreSql` now depends on `Microsoft.Extensions.Diagnostics.HealthChecks`.
- `docs/OPERATIONS.md`: defaults, what to monitor, autovacuum settings and how to size the inbox retention.

- Inbox (`AddWaybillInbox<TContext>`, `IInbox<TContext>.ProcessAsync`): records `(handler, message_id)` with `INSERT … ON CONFLICT DO NOTHING` and runs the handler with the same context in one `READ COMMITTED` transaction, so the effect applies once per handler; a duplicate returns `InboxResult.Duplicate` without running it, and a handler failure rolls back everything (including events it enqueued) and clears the change tracker. A `SaveChanges` through another instance of the context while a handler runs throws instead of committing outside the inbox transaction.
- `GetWaybillMessageId()` on RabbitMQ `IReadOnlyBasicProperties`, for consumers written with plain RabbitMQ.Client.
- `FakeInbox<TContext>` and `AddFakeWaybillInbox<TContext>()` in `Waybill.Testing`, checked for equivalence against the real inbox.

- Failure classification (ADR 0003): `PublishResult` carries a `TransportFailure` (`Connection`, `ConfirmTimeout`, `Nacked`). Connection or channel failures open a per-dispatcher circuit breaker (no claims while open, a one-message probe when half-open, open period doubling up to 30 s); confirmation timeouts and nacks halve the batch without opening it. When the broker closes the channel with 406 mid-batch, the RabbitMQ transport republishes the unconfirmed messages one by one on a fresh channel and only the one that closes it alone goes to the DLQ.

- RabbitMQ transport (`AddWaybillRabbitMQ`): one configured exchange with the registered message name as routing key, publisher confirms tracked per message, `mandatory`, persistent delivery, AMQP properties (`message_id`, `type`, `content_type`, `timestamp`, `correlation_id`) and headers (`traceparent`, `tracestate`, `tenant_id`, `waybill-key`). `basic.return` maps to `Returned`; nacks and closed connections or channels to `Retry`; a missing exchange is reported as a configuration error.

- Outbox dispatcher (`AddWaybillDispatcher`): hosted service that claims rows (`FOR UPDATE SKIP LOCKED`, lease = `PublishTimeout` + `LeaseMargin`, fencing by owner and fence), publishes through `ITransport` outside any transaction, hands batches back on transport failure without spending attempts, sends size defects and exhausted `basic.return` budgets to the outbox DLQ with a reason, and hands back what it holds on graceful shutdown.
- `ITransport` contract with per-message results (`Confirmed`, `Retry`, `Returned`, `Defect`).

- `IOutbox<TContext>.Enqueue(...)`: writes messages to the outbox in the same `SaveChanges` and transaction as the application's data. The message id (UUIDv7), type registration, serialization and size are fixed and checked at enqueue time.
- Message registry with stable names and source-generated `JsonTypeInfo` (`AddMessage("billing.invoice-paid.v1", ...)`); mandatory `MaxPayloadBytes`.
- Package-owned `waybill` schema (`outbox`, `inbox`) shipped as EF Core migrations and applied by `WaybillSchema.MigrateAsync`; the application maps the outbox with `modelBuilder.MapWaybillOutbox()`. Outbox records join the `DbContext` change tracker at enqueue time, so the EF unit of work is the single source of truth.
- `Waybill.Testing` package: `FakeOutbox<TContext>` with assertions, checked for equivalence against the real outbox.
- ADR 0002: enqueue API, package-owned schema, `key_hash` instead of a stored partition, type registry.
- Project skeleton: `Waybill`, `Waybill.EntityFrameworkCore.PostgreSql` and `Waybill.RabbitMQ` packages targeting .NET 10, with unit, integration and chaos test projects.
- CI on pull requests (PostgreSQL 15 and 18), scheduled workflow for long scenarios, and tag-based release pipeline.
- ADR 0001: row claim with `FOR UPDATE SKIP LOCKED`, per-row lease and fencing token, with the stage 0 spike results.

### Changed

- Registration ergonomics (ADR 0005), before the first release:
  - `AddHealthChecks().AddWaybillDispatcher()` is now `AddWaybillDispatcherCheck()`.
  - `modelBuilder.AddWaybillOutbox()` is now `modelBuilder.MapWaybillOutbox()`.
  - The `AddWaybill*` registration methods live in `Microsoft.Extensions.DependencyInjection`, and `MapWaybillOutbox` in `Microsoft.EntityFrameworkCore`, so setup needs no `using Waybill...`.
- `FakeOutbox<TContext>` follows the context's change tracker like the real outbox: a message discarded by `ChangeTracker.Clear()` is neither pending nor saved (it used to stay pending and fail the test at dispose).
- A half-open probe that times out or is nacked reopens the circuit breaker for twice as long, as ADR 0003 describes; it used to close the breaker, so a silent outage cycled the backlog through claims at the base period.
