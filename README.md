# Waybill

Transactional outbox and inbox for .NET, PostgreSQL-first and framework-free.

The goal: an event written in the same transaction as your data reaches the broker, and the effect a consumer writes
to its own database is applied once, even when the message is delivered more than once. You keep consuming with
whatever broker client you already use.

## Status

**Experimental, not usable end to end yet.** Messages can be written to the outbox, but there is no dispatcher, so
nothing reaches a broker. Packages built from this repository before `v0.1.0-alpha` are pipeline tests, not releases.

This README describes only what exists. Each guarantee will be written down only after a concurrency test proves it;
until then, nothing here is a promise.

## What exists today

- Design documents (in Portuguese) in [`docs/`](docs): business analysis, scope and boundaries, development plan, and review notes.
- [ADR 0001](docs/adr/0001-claim-por-linha-skip-locked-e-fencing.md): how the dispatcher claims rows (`FOR UPDATE SKIP LOCKED`, per-row lease, fencing token), with the [stage 0 spike](spike/RESULTADOS.md) behind it (archived code and raw numbers, outside the solution and CI).
- [ADR 0002](docs/adr/0002-enfileiramento-schema-e-registro-de-tipos.md): the enqueue API, the package-owned `waybill` schema and the message type registry.
- `Waybill` and `Waybill.EntityFrameworkCore.PostgreSql`: `IOutbox<TContext>.Enqueue(...)` writes messages to `waybill.outbox` in the same `SaveChanges` and transaction as your data, and `WaybillSchema.MigrateAsync` creates the tables. No dispatcher yet.
- `Waybill.Testing`: an in-memory `FakeOutbox<TContext>` with assertions, to test code that enqueues messages without PostgreSQL.
- `Waybill.RabbitMQ`: empty for now.

## Building

Requires the .NET 10 SDK and Docker (integration tests use Testcontainers).

```
dotnet build
dotnet test --project tests/Waybill.Tests.Unit
dotnet test --project tests/Waybill.Tests.Integration
dotnet pack -c Release -o artifacts
```

Integration tests run against `postgres:18-alpine` by default; set `WAYBILL_POSTGRES_IMAGE` (for example
`postgres:15-alpine`) to test another supported version.

## License

[Apache-2.0](LICENSE)
