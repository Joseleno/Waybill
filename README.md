# Waybill

Transactional outbox and inbox for .NET, PostgreSQL-first and framework-free.

The goal: an event written in the same transaction as your data reaches the broker, and the effect a consumer writes
to its own database is applied once, even when the message is delivered more than once. You keep consuming with
whatever broker client you already use.

## Status

**Experimental.** The write side (outbox), the dispatcher and the RabbitMQ transport work end to end, with failure
classification, a circuit breaker and chaos tests. The inbox applies a consumer's effect once per handler. Retention cleanup, a pending-age gauge and a dispatcher health check keep it operable over time. Packages built from this repository before `v0.1.0-alpha` are pipeline tests, not releases.

This README describes only what exists. Each guarantee will be written down only after a concurrency test proves it;
until then, nothing here is a promise.

## What exists today

- Design documents (in Portuguese) in [`docs/`](docs): business analysis, scope and boundaries, development plan, and review notes.
- [ADR 0001](docs/adr/0001-claim-por-linha-skip-locked-e-fencing.md): how the dispatcher claims rows (`FOR UPDATE SKIP LOCKED`, per-row lease, fencing token), with the [stage 0 spike](spike/RESULTADOS.md) behind it (archived code and raw numbers, outside the solution and CI).
- [ADR 0002](docs/adr/0002-enfileiramento-schema-e-registro-de-tipos.md): the enqueue API, the package-owned `waybill` schema and the message type registry.
- [ADR 0003](docs/adr/0003-classificacao-de-falhas-do-dispatcher.md): how the dispatcher classifies failures (DLQ, retry, circuit breaker, batch reduction).
- [ADR 0004](docs/adr/0004-retencao-metrica-e-health-check.md): retention, the pending-age metric and the dispatcher health check; [OPERATIONS.md](docs/OPERATIONS.md) says what to configure and watch.
- [ADR 0005](docs/adr/0005-ergonomia-do-registro.md): registration names and namespaces, settled with a newcomer running the sample.
- [`samples/`](samples): a billing API and a receipts consumer, runnable with `docker compose up --wait`. Start there to see the whole flow, including a broker outage.
- `Waybill` and `Waybill.EntityFrameworkCore.PostgreSql`: `IOutbox<TContext>.Enqueue(...)` writes messages to `waybill.outbox` in the same `SaveChanges` and transaction as your data, and `WaybillSchema.MigrateAsync` creates the tables. `AddWaybillDispatcher<TContext>()` runs the dispatcher as a hosted service on the context's database: per-row claim with lease and fencing token, publish outside the transaction, hand-back on transport failure, DLQ as a status.
- `IInbox<TContext>.ProcessAsync(handler, messageId, handle)` (`AddWaybillInbox<TContext>()`): records `(handler, message_id)` in `waybill.inbox` and runs your handler in the same `READ COMMITTED` transaction, on the same context, so the effect commits once however many times the message arrives; a repeat returns `Duplicate` without running it. Events the handler enqueues on the outbox join that commit. Writing through another instance of the context while the handler runs fails loudly. It wraps the handler inside whatever consumer you already have; `GetWaybillMessageId()` reads the id from RabbitMQ properties.
- `Waybill.Testing`: in-memory `FakeOutbox<TContext>` and `FakeInbox<TContext>` with assertions, to test code that enqueues or consumes messages without PostgreSQL, each checked for equivalence against the real one.
- `Waybill.RabbitMQ`: `AddWaybillRabbitMQ(...)` publishes to one exchange you declare, with the registered message name as routing key, publisher confirms, `mandatory` and persistent delivery. Waybill does not create topology.
- Retention (`AddWaybillRetention(...)`): a hosted service that deletes, in small batches, published outbox rows past `OutboxRetention` and inbox rows past `InboxRetention`. Pending, claimed and dead-lettered rows are never deleted, also while the broker is down for longer than the retention. A message redelivered after its inbox row was deleted is processed again; [OPERATIONS.md](docs/OPERATIONS.md#sizing-the-inbox-retention) explains how to size the inbox retention.
- Observability without OpenTelemetry: the gauge `waybill.outbox.oldest_pending.age` (meter `Waybill`, seconds) and `AddHealthChecks().AddWaybillDispatcherCheck()`, which reports `Degraded` while the broker is unreachable and `Unhealthy` when the dispatcher loop stops or the database keeps failing.

## Building

Requires the .NET 10 SDK and Docker (integration tests use Testcontainers).

```
dotnet build
dotnet test --project tests/Waybill.Tests.Unit
dotnet test --project tests/Waybill.Tests.Integration --filter-not-trait "Category=Long"
dotnet pack -c Release -o artifacts
```

Integration tests run against `postgres:18-alpine` by default; set `WAYBILL_POSTGRES_IMAGE` (for example
`postgres:15-alpine`) to test another supported version.

## License

[Apache-2.0](LICENSE)
