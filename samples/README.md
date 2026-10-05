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
  message never issues a second receipt.
- The `audit` queue receives every event, so you can see both.

## Run it

Requires Docker. Ports 8080, 8081 and 15672 must be free.

```
cd samples
docker compose up --build --wait
```

The first build takes a minute or two. `--wait` returns once both services are healthy.

## Publish the first event

Bash:

```
id=$(curl -s -X POST localhost:8080/invoices -H 'Content-Type: application/json' \
  -d '{"number":"INV-1","amount":42.50}' | sed -E 's/.*"id":"([^"]+)".*/\1/')
curl -i -X POST localhost:8080/invoices/$id/payments
curl -s "localhost:8081/receipts?invoiceId=$id"
```

PowerShell:

```
$invoice = Invoke-RestMethod -Method Post http://localhost:8080/invoices -ContentType 'application/json' -Body '{"number":"INV-1","amount":42.50}'
Invoke-RestMethod -Method Post "http://localhost:8080/invoices/$($invoice.id)/payments"
Invoke-RestMethod "http://localhost:8081/receipts?invoiceId=$($invoice.id)"
```

The receipt shows up within a second or two. Paying the same invoice again returns `409` and publishes nothing.

What to look at:

- **The events:** the RabbitMQ UI at http://localhost:15672 (user `waybill`, password `waybill`), queue `audit`,
  "Get messages". Each message carries `message_id` (the id the inbox deduplicates on), `type` and the
  `waybill-key` header.
- **The outbox table:**
  ```
  docker compose exec postgres psql -U postgres -d billing -c "SELECT id, type, status, published_at FROM waybill.outbox"
  ```
- **The inbox table:**
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

| File | What it shows |
| --- | --- |
| [`Billing/Program.cs`](Billing/Program.cs) | The whole Waybill setup: message registry, outbox, RabbitMQ transport, dispatcher, retention, health check |
| [`Billing/BillingMessages.cs`](Billing/BillingMessages.cs) | Message registration with a stable, versioned name and a source-generated `JsonTypeInfo` |
| [`Billing/BillingDbContext.cs`](Billing/BillingDbContext.cs) | `modelBuilder.AddWaybillOutbox()`: maps the outbox so `Enqueue` joins this context's `SaveChanges` |
| [`Billing/InvoicePayments.cs`](Billing/InvoicePayments.cs) | `outbox.Enqueue(...)` next to the state change, one `SaveChanges` |
| [`Receipts/InvoicePaidConsumer.cs`](Receipts/InvoicePaidConsumer.cs) | A plain RabbitMQ.Client consumer: read the Waybill message id, run the handler, ack after the commit |
| [`Receipts/InvoicePaidHandler.cs`](Receipts/InvoicePaidHandler.cs) | `inbox.ProcessAsync(...)` around the handler, which also enqueues an event |
| [`*.Tests`](Billing.Tests/InvoicePaymentsTests.cs) | Tests with `FakeOutbox` and `FakeInbox` over an in-memory `DbContext`: no PostgreSQL, no broker |

Run the tests from the repository root:

```
dotnet test --project samples/Billing.Tests
dotnet test --project samples/Receipts.Tests
```

## Why the API does not migrate at startup

The `billing-migrate` and `receipts-migrate` services run each image once with `migrate`. That step applies Waybill's
schema (`WaybillSchema.MigrateAsync`) and the application's EF migrations, then exits. The services start only after
it succeeds. Migrating from every API instance at startup would make instances race each other on the same DDL. It
would also need a database user with DDL rights in the running service, which it should not have. In production, run
the same step from your deployment pipeline or an init container.

## The topology

Waybill publishes to one exchange you name. It does not create exchanges, queues or bindings. Here RabbitMQ loads them
at boot from [`rabbitmq/definitions.json`](rabbitmq/definitions.json): the `events` exchange, the
`receipts.invoice-paid` queue bound to `billing.invoice-paid.v1`, and the `audit` queue bound to `#`.

## Clean up

```
docker compose down -v
```
