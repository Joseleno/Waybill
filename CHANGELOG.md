# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- RabbitMQ transport (`AddWaybillRabbitMQ`): one configured exchange with the registered message name as routing key, publisher confirms tracked per message, `mandatory`, persistent delivery, AMQP properties (`message_id`, `type`, `content_type`, `timestamp`, `correlation_id`) and headers (`traceparent`, `tracestate`, `tenant_id`, `waybill-key`). `basic.return` maps to `Returned`; nacks and closed connections or channels to `Retry`; a missing exchange is reported as a configuration error.

- Outbox dispatcher (`AddWaybillDispatcher`): hosted service that claims rows (`FOR UPDATE SKIP LOCKED`, lease = `PublishTimeout` + `LeaseMargin`, fencing by owner and fence), publishes through `ITransport` outside any transaction, hands batches back on transport failure without spending attempts, sends size defects and exhausted `basic.return` budgets to the outbox DLQ with a reason, and hands back what it holds on graceful shutdown.
- `ITransport` contract with per-message results (`Confirmed`, `Retry`, `Returned`, `Defect`).

- `IOutbox<TContext>.Enqueue(...)`: writes messages to the outbox in the same `SaveChanges` and transaction as the application's data. The message id (UUIDv7), type registration, serialization and size are fixed and checked at enqueue time.
- Message registry with stable names and source-generated `JsonTypeInfo` (`AddMessage("billing.invoice-paid.v1", ...)`); mandatory `MaxPayloadBytes`.
- Package-owned `waybill` schema (`outbox`, `inbox`) shipped as EF Core migrations and applied by `WaybillSchema.MigrateAsync`; the application maps the outbox with `AddWaybillOutbox()`. Outbox records join the `DbContext` change tracker at enqueue time, so the EF unit of work is the single source of truth.
- `Waybill.Testing` package: `FakeOutbox<TContext>` with assertions, checked for equivalence against the real outbox.
- ADR 0002: enqueue API, package-owned schema, `key_hash` instead of a stored partition, type registry.
- Project skeleton: `Waybill`, `Waybill.EntityFrameworkCore.PostgreSql` and `Waybill.RabbitMQ` packages targeting .NET 10, with unit, integration and chaos test projects.
- CI on pull requests (PostgreSQL 15 and 18), scheduled workflow for long scenarios, and tag-based release pipeline.
- ADR 0001: row claim with `FOR UPDATE SKIP LOCKED`, per-row lease and fencing token, with the stage 0 spike results.
