# Etapa 4 — Inbox (G3)

Derivado de `docs/plano.md`, seção "Etapa 4", e do Contrato técnico de `docs/escopo-e-fronteiras.md` (linha "Inbox").

## Objetivo

Entregar G3: **o efeito gravado no banco do consumidor é aplicado uma vez, mesmo com entregas repetidas.** O inbox envolve o handler da aplicação, qualquer que seja o cliente de broker; o consumo (loop, roteamento, ack) continua da aplicação.

## Decisões (Oct 4, 2026)

| Tema | Decisão |
| --- | --- |
| API | Delegate com o contexto: `inbox.ProcessAsync(handler, messageId, async (db, ct) => { ... }, ct)` devolve `Processed` ou `Duplicate`. Sem interface de handler nem roteamento (o escopo deixa isso com a aplicação) |
| Limpeza do inbox | Vai para a etapa 5, junto da limpeza do outbox |

## API

```csharp
services.AddWaybillInbox<AppDbContext>();

// no consumidor (RabbitMQ.Client, Kafka, ...)
var result = await inbox.ProcessAsync("billing.mark-invoice-paid", messageId, async (db, ct) =>
{
    var invoice = await db.Invoices.FindAsync([id], ct);
    invoice!.MarkPaid();
    outbox.Enqueue(new ReceiptIssued(invoice.Id));   // consumidor que também produz: mesma transação
}, ct);
await channel.BasicAckAsync(deliveryTag, false);       // Processed ou Duplicate: ack sempre depois do commit
```

## Mecanismo

1. Recusa se o contexto já tem transação: o inbox é dono dela.
2. Abre a transação em `READ COMMITTED` no `DbContext` do escopo (dentro da estratégia de execução, se houver retry).
3. `INSERT INTO waybill.inbox (handler, message_id) … ON CONFLICT DO NOTHING`. Zero linhas: duplicata, rollback, devolve `Duplicate` sem chamar o handler. Uma entrega concorrente da mesma mensagem espera a primeira transação terminar.
4. Chama o handler com o mesmo contexto, faz `SaveChanges` e commit. Mensagens enfileiradas no outbox pelo handler entram no mesmo commit.
5. Exceção no handler: rollback, o change tracker é limpo (nada do que o handler preparou sobra) e a exceção sobe. A mensagem volta pelo broker e é reprocessada.
6. Guarda: com um inbox em andamento, um `SaveChanges` de outra instância do mesmo `TContext` fora da transação do inbox falha com mensagem explícita.

Nome do handler: obrigatório, estável, único por handler, até 255 bytes; é parte da chave `(handler, message_id)`, então dois handlers do mesmo evento aplicam cada um.

## Fake

`FakeInbox<TContext>` no `Waybill.Testing`: lembra em memória o que foi processado, chama o handler e o `SaveChanges` do contexto do teste, e oferece asserções. Teste de equivalência com o real.

## Fora do escopo

Limpeza por retenção (etapa 5); verificação de sequência por chave (v0.2); recusa de reprocessamento mais antigo que a retenção (API de operação, v1.0).
