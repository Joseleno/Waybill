# Waybill

Transactional outbox and inbox for .NET, on EF Core, PostgreSQL and RabbitMQ.

An event you enqueue in the same transaction as your data reaches the broker. The effect a consumer writes to its own
database is applied once, even when the message is delivered more than once. Waybill is not a messaging framework:
you keep consuming with whatever broker client you already use.

**Status: alpha (0.1.0-alpha).** The API can still change before 1.0. Every promise below has a concurrency test
behind it, listed in [GUARANTEES.md](https://github.com/Joseleno/Waybill/blob/main/GUARANTEES.md); anything not listed
there is not promised.

## Install

```
dotnet add package Waybill.EntityFrameworkCore.PostgreSql --prerelease
dotnet add package Waybill.RabbitMQ --prerelease
dotnet add package Waybill.Testing --prerelease   # in test projects
```

Requires .NET 10, EF Core 10 with Npgsql, PostgreSQL 15 or later, and RabbitMQ (RabbitMQ.Client 7).

## The API in ten lines

<!-- api -->
```csharp
// Once, as a deployment step: creates the waybill schema (outbox and inbox tables).
await WaybillSchema.MigrateAsync(connectionString);

// Startup: the messages you publish, the outbox on your DbContext, the transport and the dispatcher.
services.AddWaybill(o =>
{
    o.MaxPayloadBytes = 16 * 1024;
    o.AddMessage("billing.invoice-paid.v1", AppJson.Default.InvoicePaid);
});
services.AddWaybillOutbox<AppDbContext>();
services.AddWaybillRabbitMQ(o => { o.Uri = rabbitUri; o.Exchange = "events"; });
services.AddWaybillDispatcher<AppDbContext>();

// In AppDbContext.OnModelCreating: map the outbox table.
modelBuilder.MapWaybillOutbox();

// Wherever you change data: the event commits with it, or not at all.
outbox.Enqueue(new InvoicePaid(invoice.Id, invoice.Amount), key: invoice.Id.ToString());
await db.SaveChangesAsync(ct);
```

`outbox` is the `IOutbox<AppDbContext>` from DI. A test runs these exact lines against PostgreSQL on every pull request.

- **Messages are registered by a stable name** (`billing.invoice-paid.v1`), not by class name, with a `JsonTypeInfo`
  (source generation recommended). `Enqueue` refuses unregistered types, payloads that fail to serialize and payloads above
  `MaxPayloadBytes`, before anything is written.
- **The dispatcher** runs as a hosted service in your API or in a worker. It claims rows with
  `FOR UPDATE SKIP LOCKED`, publishes outside the transaction with publisher confirms, and hands messages back without
  spending attempts when the broker or the network fails (G2).
- **Waybill does not create topology.** Declare the exchange and queues yourself; every message is published to the
  configured exchange with its registered name as routing key.

The [sample](https://github.com/Joseleno/Waybill/tree/main/samples) runs the whole flow with
`docker compose up --wait`: a billing API that publishes, a receipts consumer that uses the inbox and publishes in
turn, and a script that stops the broker to show the backlog drain when it comes back.

## Consuming with the inbox

Wrap your handler, inside whatever consumer you already have, in
`IInbox<TContext>.ProcessAsync(handlerName, messageId, (db, ct) => ...)` (registered by
`AddWaybillInbox<TContext>()`). Waybill records `(handler, message_id)` and runs your handler in the same
`READ COMMITTED` transaction, on the same context. A repeated delivery returns `InboxResult.Duplicate` without running
the handler, and you acknowledge the message after `ProcessAsync` returns. Events the handler enqueues commit together
with its effect. With plain RabbitMQ.Client, `GetWaybillMessageId()` reads the message id from the delivery
properties.

## Testing your code

`Waybill.Testing` has in-memory `FakeOutbox<TContext>` and `FakeInbox<TContext>`, checked for equivalence against the
real ones, so you can test code that enqueues or consumes messages without PostgreSQL: `ShouldContain<T>()`,
`ShouldBeEmpty()` and `ShouldHaveNoPending()`, which fails the test for an event enqueued and never saved.

## Guarantees

In short, under the conditions written in [GUARANTEES.md](https://github.com/Joseleno/Waybill/blob/main/GUARANTEES.md),
each linked to the test that proves it:

1. **G1.** An event exists if, and only if, the transaction that enqueued it commits.
2. **G2.** Every persisted event is published at least once, or goes to the outbox DLQ with a recorded reason; none
   is dropped silently.
3. **G3.** The effect a consumer writes to its own database is applied once, even when the message is delivered more
   than once.

Publication is at least once: consumers see duplicates, and the inbox is how their effect is applied once. Version 0.1
makes no ordering promise; ordering by aggregate key is planned for 0.2.

## When not to use Waybill

- **You want a messaging framework:** handlers, routing, retries on the consuming side, sagas, scheduling. Use
  Wolverine, MassTransit, NServiceBus or CAP. Waybill only publishes and deduplicates.
- **Your database is not PostgreSQL, or you do not write through EF Core.** Writes through `ExecuteUpdate` or raw SQL do not
  produce events.
- **Your broker is not RabbitMQ.** Kafka is planned for 0.2.
- **You need ordering per aggregate today.** It is planned for 0.2.
- **The effect you need once is outside your database** (an e-mail, a call to a payment gateway). The inbox protects
  your database, not the calls that leave it.

## Comparison

<!-- comparison: pending measured data -->

## What Waybill does not solve

These remain with Waybill configured correctly, because they are outside the reach of any outbox:

| Problem | Why it remains | Way out |
| --- | --- | --- |
| Business consistency across services | Waybill delivers the event; it does not make the other service accept the decision | A saga with explicit compensation |
| An event without its data after a PostgreSQL failover | With asynchronous replication or `synchronous_commit = off`, a failover can lose a commit whose event was already published | Synchronous replication where the business requires it |
| An event with wrong content | A bug in the event is published faithfully | A compensating event and contract tests |
| A contract change that breaks consumers | Waybill moves bytes; it does not know the schema | Versioned message names and contract tests |
| A broker down for hours | The outbox grows and delivery waits; nothing goes to the DLQ, but the disk grows | Alert on the age of the oldest pending message |
| A slow consumer | Waybill measures the delay; it does not scale the consumer | More consumers, partitioning |
| Writes outside the database (Redis, another database, files) | Atomicity covers only the `DbContext`'s transaction | Bring the write into the same database, or accept the dual write there |

## Operating Waybill

[OPERATIONS.md](https://github.com/Joseleno/Waybill/blob/main/docs/OPERATIONS.md) covers the defaults, retention
(`AddWaybillRetention`), the gauge `waybill.outbox.oldest_pending.age` (meter `Waybill`), the dispatcher health check
(`AddHealthChecks().AddWaybillDispatcherCheck()`), autovacuum settings and how to size the inbox retention.

## Design documents

The design is documented in Portuguese in [`docs/`](https://github.com/Joseleno/Waybill/tree/main/docs): scope and
boundaries, the development plan and five architecture decision records, from the row claim
([ADR 0001](https://github.com/Joseleno/Waybill/blob/main/docs/adr/0001-claim-por-linha-skip-locked-e-fencing.md)) to
the registration API ([ADR 0005](https://github.com/Joseleno/Waybill/blob/main/docs/adr/0005-ergonomia-do-registro.md)).

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

[Apache-2.0](https://github.com/Joseleno/Waybill/blob/main/LICENSE)
