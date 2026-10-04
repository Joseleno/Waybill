# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- `IOutbox<TContext>.Enqueue(...)`: writes messages to the outbox in the same `SaveChanges` and transaction as the application's data. The message id (UUIDv7), type registration, serialization and size are fixed and checked at enqueue time.
- Message registry with stable names and source-generated `JsonTypeInfo` (`AddMessage("billing.invoice-paid.v1", ...)`); mandatory `MaxPayloadBytes`.
- Package-owned `waybill` schema (`outbox`, `inbox`) shipped as EF Core migrations and applied by `WaybillSchema.MigrateAsync`; the application maps the outbox with `AddWaybillOutbox()` and `UseWaybill()`.
- `Waybill.Testing` package: `FakeOutbox<TContext>` with assertions, checked for equivalence against the real outbox.
- ADR 0002: enqueue API, package-owned schema, `key_hash` instead of a stored partition, type registry.
- Project skeleton: `Waybill`, `Waybill.EntityFrameworkCore.PostgreSql` and `Waybill.RabbitMQ` packages targeting .NET 10, with unit, integration and chaos test projects.
- CI on pull requests (PostgreSQL 15 and 18), scheduled workflow for long scenarios, and tag-based release pipeline.
- ADR 0001: row claim with `FOR UPDATE SKIP LOCKED`, per-row lease and fencing token, with the stage 0 spike results.
