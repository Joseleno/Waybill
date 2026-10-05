# Etapa 6 — Plano e critérios de aceite

## Desenho

| Tema | Como |
| --- | --- |
| Projetos | `samples/Billing`, `samples/Receipts` (web, .NET 10, minimal API), `samples/Billing.Tests`, `samples/Receipts.Tests` (xUnit v3, só `Waybill.Testing`). Todos na solution, numa pasta `/samples/`, com `IsPackable=false` |
| Billing | Tabela `invoices` (id, number, amount, status). `POST /invoices` cria; `POST /invoices/{id}/payments` marca paga e enfileira `billing.invoice-paid.v1` (`InvoicePaid(InvoiceId, Amount, PaidAt)`) com key = id da fatura; pagar duas vezes devolve 409 sem novo evento. Dispatcher, retenção e `/health` no mesmo processo |
| Receipts | `BackgroundService` com RabbitMQ.Client: consome `receipts.invoice-paid` com prefetch 1 e ack manual. Por mensagem: `GetWaybillMessageId()`, desserializa e chama `ProcessAsync("receipts.issue-receipt", id, handler)`; o handler grava `receipts` (id, invoice_id, amount, issued_at) e enfileira `receipts.receipt-issued.v1`. Ack depois do commit, `Processed` ou `Duplicate`. Exceção: nack com requeue. Dispatcher próprio, retenção, `/health` |
| Modo migrate | `dotnet Billing.dll migrate`: `WaybillSchema.MigrateAsync` e `Database.MigrateAsync()` das migrations EF da aplicação, depois sai. O mesmo vale para o Receipts |
| Compose | `postgres:18-alpine` com dois bancos criados por script de init; `rabbitmq:4-management-alpine` com `definitions.json`; `migrate` (as duas imagens em modo migrate, em sequência); `billing` (porta 8080) e `receipts` (porta 8081), com `depends_on` em `migrate: service_completed_successfully` e healthchecks para `docker compose up --wait` |
| Dockerfiles | Multi-stage a partir da raiz do repositório (contexto `..`), imagem final `aspnet:10.0` sem SDK, usuário não-root |
| Scripts | `samples/scripts/smoke.sh`: cria e paga uma fatura, espera o recibo no banco do Receipts e as duas mensagens na fila `audit`. `broker-outage.sh` e `.ps1`: param o broker, pagam N faturas, mostram as pendentes e o `/health` `Degraded`, religam e esperam drenar e voltar a `Healthy`. Saem com código diferente de 0 se algo não acontece no prazo |

## Cenários

| Teste | Cenário | Resultado esperado | Onde roda |
| --- | --- | --- | --- |
| `Pay_OpenInvoice_EnqueuesInvoicePaid` | Caso de uso com `FakeOutbox` | Um `billing.invoice-paid.v1` com o id e o valor da fatura, key = id | Billing.Tests (PR) |
| `Pay_AlreadyPaidInvoice_DoesNotEnqueueAgain` | Segundo pagamento | 409; nenhum evento novo | Billing.Tests (PR) |
| `Enqueue_UnregisteredType_ErrorSaysWhatToDo` | Mensagem sem `AddMessage` | A exceção cita o tipo e `AddMessage` (a validação é a mesma no fake e no real: `WaybillOptions`) | Billing.Tests (PR) |
| `IssueReceipt_FirstDelivery_SavesReceiptAndEnqueues` | Handler com `FakeInbox` | Um recibo e um `receipts.receipt-issued.v1` | Receipts.Tests (PR) |
| `IssueReceipt_RepeatedDelivery_OneReceipt` | Mesma mensagem duas vezes | `Duplicate` na segunda; um recibo | Receipts.Tests (PR) |
| Smoke do compose | `docker compose up --wait` + `smoke.sh` | Recibo gravado; as duas mensagens na `audit` em até 60 s | Job `sample` (PR) |
| Falha do broker | `broker-outage.sh` no compose | Pendentes > 0 e `Degraded` com o broker parado; 0 pendentes e `Healthy` depois de religar | Job `sample` (PR) |
| Estranho com o README | Agente sem contexto recebe só `samples/README.md` | Sobe o exemplo e publica o primeiro evento; cada fricção registrada | Manual, antes do PR |

**Pronto quando**:

- os testes do exemplo e o job `sample` passam no CI;
- o agente sem contexto e o autor sobem o exemplo só com o README;
- as quatro perguntas do SPEC estão respondidas em `FRICCAO.md`, com a fricção corrigida ou registrada como decisão.
