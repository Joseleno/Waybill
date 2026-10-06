# Waybill sample: billing → receipts

Two small services that show Waybill end to end, with PostgreSQL and RabbitMQ in Docker:

```
POST /invoices/{id}/payments
        │
   ┌────▼─────┐  same commit   ┌───────────────┐  dispatcher   ┌──────────────────┐
   │ Billing  ├───────────────►│ waybill.outbox├──────────────►│ exchange "events"│
   └──────────┘  invoice paid  └───────────────┘               └───┬──────────┬───┘
                                                                   │          │
                                      queue receipts.invoice-paid  │          │ queue audit (#)
                                                                   ▼          ▼
   ┌──────────┐  inbox + receipt + receipt-issued, one commit
   │ Receipts │◄── plain RabbitMQ.Client consumer, ack after the commit
   └──────────┘
```

- **Billing** pays an invoice and enqueues `billing.invoice-paid.v1` in the same `SaveChanges`: the event exists if
  and only if the payment commits. Its dispatcher publishes it to RabbitMQ.
- **Receipts** consumes it with RabbitMQ.Client directly (no messaging framework). Waybill's inbox wraps the handler:
  the receipt, the inbox record and a second event, `receipts.receipt-issued.v1`, commit together, so a redelivered
  message never issues a second receipt. Receipts has its own database, with its own outbox for that second event.
- The `audit` queue receives every event, so you can see both.

## Prerequisites

- Docker with Compose v2 (`docker compose up --wait`). Ports 8080, 8081 and 15672 must be free; if one is
  taken, set `BILLING_PORT`, `RECEIPTS_PORT` or `RABBITMQ_UI_PORT` before `docker compose up` (the scripts read
  `BILLING` and `RECEIPTS` for the URLs).
- To run the scripts: Bash (on Windows, Git Bash or WSL) or PowerShell 7 (`pwsh`).
- To run the sample's tests: the .NET 10 SDK.

## Run it

From the repository root:

```
cd samples
docker compose up --build --wait
```

The first build takes a minute or two. `--wait` returns once both services are healthy. Every command below runs from
`samples/`. In the logs, the migrate steps print one `fail:` line about `__EFMigrationsHistory` on a fresh database:
that is EF Core checking for its history table before creating it, not an error.

## Publish the first event

Bash:

```
id=$(curl -fsS -X POST localhost:8080/invoices -H 'Content-Type: application/json' \
  -d "{\"number\":\"INV-$RANDOM\",\"amount\":42.50}" | sed -E 's/.*"id":"([^"]+)".*/\1/')
curl -fsS -X POST localhost:8080/invoices/$id/payments; echo
for i in 1 2 3 4 5 6 7 8 9 10; do r=$(curl -fsS "localhost:8081/receipts?invoiceId=$id"); [ "$r" != "[]" ] && break; sleep 1; done; echo "$r"
```

PowerShell:

```
$invoice = Invoke-RestMethod -Method Post http://localhost:8080/invoices -ContentType 'application/json' -Body (@{ number = "INV-$(Get-Random)"; amount = 42.50 } | ConvertTo-Json)
Invoke-RestMethod -Method Post "http://localhost:8080/invoices/$($invoice.id)/payments"
foreach ($i in 1..10) { $receipt = Invoke-RestMethod "http://localhost:8081/receipts?invoiceId=$($invoice.id)"; if ($receipt) { break }; Start-Sleep 1 }; $receipt
```

The payment returns the invoice with `"status":"Paid"`. The receipt is issued asynchronously, usually within a second: the
event goes from the outbox to the broker to the consumer, so the snippet polls for it. Paying the same invoice
again, even concurrently, returns `409` and publishes nothing; so does creating an invoice with a number that already exists.

What to look at:

- **The events:** the RabbitMQ UI at http://localhost:15672 (user `waybill`, password `waybill`), queue `audit`,
  "Get messages". Waybill sets `message_id` (the id the inbox deduplicates on), `type` (the registered name),
  `content_type`, `timestamp`, and the headers `waybill-key` and, when the publishing code runs inside a trace,
  `traceparent`. `x-dotnet-pub-seq-no` comes from the RabbitMQ client; the broker adds
  `x-acquired-count` once a message has been fetched.
- **The outboxes** (one per service):
  ```
  docker compose exec postgres psql -U postgres -d billing -c "SELECT id, type, status, published_at FROM waybill.outbox"
  docker compose exec postgres psql -U postgres -d receipts -c "SELECT id, type, status, published_at FROM waybill.outbox"
  ```
- **The inbox:**
  ```
  docker compose exec postgres psql -U postgres -d receipts -c "SELECT * FROM waybill.inbox"
  ```
- **Health:** http://localhost:8080/health and http://localhost:8081/health.

## Stop the broker and watch nothing get lost

```
./scripts/broker-outage.sh        # or: pwsh ./scripts/broker-outage.ps1
```

The script:

1. stops RabbitMQ;
2. pays five invoices (the API keeps answering);
3. shows the events waiting in `waybill.outbox` while `/health` reports `Degraded`;
4. starts RabbitMQ again and waits until:
   - the backlog drains;
   - the five receipts are issued;
   - `/health` is back to `Healthy`.

`scripts/smoke.sh` runs the happy path the same way. CI runs both scripts.

## Where Waybill is in the code

Producer side (Billing):

| File | What it shows |
| --- | --- |
| [`Billing/Program.cs`](Billing/Program.cs) | The setup: `AddWaybill` (the message registry), `AddWaybillOutbox<T>`, the RabbitMQ transport, `AddWaybillDispatcher<T>` and `AddWaybillRetention<T>` (they use the context's database), the health check |
| [`Billing/BillingMessages.cs`](Billing/BillingMessages.cs) | Each message registered with a stable, versioned name and a source-generated `JsonTypeInfo`; `MaxPayloadBytes` is required |
| [`Billing/BillingDbContext.cs`](Billing/BillingDbContext.cs) | `modelBuilder.MapWaybillOutbox()`: maps the outbox so `Enqueue` joins this context's `SaveChanges` |
| [`Billing/InvoicePayments.cs`](Billing/InvoicePayments.cs) | `outbox.Enqueue(...)` next to the state change, one `SaveChanges` |

Consumer side (Receipts):

| File | What it shows |
| --- | --- |
| [`Receipts/Program.cs`](Receipts/Program.cs) | `AddWaybillInbox<T>` next to the outbox setup (the consumer also publishes). The inbox needs no model mapping |
| [`Receipts/ReceiptsDbContext.cs`](Receipts/ReceiptsDbContext.cs) | `MapWaybillOutbox()` for the event it publishes |
| [`Receipts/ReceiptsMessages.cs`](Receipts/ReceiptsMessages.cs) | The consumer's own copy of the `InvoicePaid` contract, and the message it publishes |
| [`Receipts/InvoicePaidConsumer.cs`](Receipts/InvoicePaidConsumer.cs) | A plain RabbitMQ.Client consumer: read the Waybill message id, run the handler, ack after the commit |
| [`Receipts/InvoicePaidHandler.cs`](Receipts/InvoicePaidHandler.cs) | `inbox.ProcessAsync(...)` around the handler, which also enqueues an event |

Things worth knowing:

- **`Enqueue` does not send anything.** It adds the message to the context's pending changes; the next `SaveChanges`
  writes it in the same transaction as your data, and the dispatcher publishes it afterwards.
- **Both registrations are needed.** `services.AddWaybillOutbox<T>()` provides `IOutbox<T>`;
  `modelBuilder.MapWaybillOutbox()` maps the table into `T`'s model. Forget the first and `IOutbox<T>` cannot be
  resolved; forget the second and the first `Enqueue` throws, saying to call `MapWaybillOutbox()`. Forget to register
  a message type and `Enqueue` throws, naming the type and `AddMessage`.
- **`key` is optional.** It names the entity whose messages belong together (here, the invoice, also for the receipt
  issued for it) and travels as the `waybill-key` header. In this version it does not change delivery: per-key ordering
  is planned for v0.2.
- **Inside `ProcessAsync`, do not call `SaveChanges`.** The `db` your handler receives is the scope's context, the same
  instance the injected `IOutbox<T>` enqueues into. When the handler returns, the inbox saves and commits once: the
  effect, the inbox record and any enqueued event together. If the handler throws, nothing commits and the broker
  delivers the message again, after a growing delay (up to 30 s); past the queue's `x-delivery-limit` (20) it is
  dead-lettered to `receipts.invoice-paid.dead`, which someone has to look at.
- **Retention** deletes published outbox rows after 7 days and inbox rows after 30. It never deletes a message that
  was not published.

## Tests

The tests use `FakeOutbox` and `FakeInbox` from the `Waybill.Testing` package over an EF Core in-memory `DbContext`: no
PostgreSQL, no broker. See [`Billing.Tests`](Billing.Tests/InvoicePaymentsTests.cs) and
[`Receipts.Tests`](Receipts.Tests/InvoicePaidHandlerTests.cs). The fakes treat a successful `SaveChanges` as the commit
and have no transactions, so rollback paths need a test against real PostgreSQL.

```
dotnet test --project Billing.Tests
dotnet test --project Receipts.Tests
```

## Why the API does not migrate at startup

The `billing-migrate` and `receipts-migrate` services run each image once with `migrate`. That step applies Waybill's
schema (`WaybillSchema.MigrateAsync`) and the application's EF migrations, then exits. The services start only after
it succeeds. Migrating from every API instance at startup would make instances race each other on the same DDL. It
would also need a database user with DDL rights in the running service, which it should not have. In production, run
the same step from your deployment pipeline or an init container.

## The topology

Waybill publishes to one exchange you name. It does not create exchanges, queues or bindings. Here RabbitMQ loads them
at boot from [`rabbitmq/definitions.json`](rabbitmq/definitions.json):

- the `events` exchange;
- the `receipts.invoice-paid` queue bound to `billing.invoice-paid.v1`, with its dead-letter queue;
- the `audit` queue bound to `#`.

## Clean up

```
docker compose down -v --rmi local
```

This removes the sample's containers, volumes and images; the base images (postgres, rabbitmq, .NET) stay.
