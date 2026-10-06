# Etapa 6 — Exemplo executável

Derivado de `docs/plano.md`, seção "Etapa 6".

## Objetivo

Usar o pacote como um estranho usaria, e corrigir a fricção antes do congelamento da API da v0.1. Pronto quando alguém que nunca viu o pacote sobe o exemplo e publica o primeiro evento só com o README do exemplo.

## Decisões (Oct 5, 2026)

| Tema | Decisão |
| --- | --- |
| Referência | `ProjectReference` para `src/`. O exemplo entra na solution e no CI, e quebra o build se a API mudar sem ele acompanhar. Os Dockerfiles buildam a partir da raiz do repositório |
| Domínio | Cobrança → Recibos, o mesmo vocabulário dos testes e do README |
| Cenário de falha | Dois scripts equivalentes, `broker-outage.sh` e `broker-outage.ps1` |
| Validação do pronto | Um agente sem contexto recebe só o README do exemplo e tenta subir e publicar o primeiro evento; depois o autor faz o mesmo antes do merge. Cada fricção vai para `FRICCAO.md` |

## O exemplo

```
samples/
  docker-compose.yml           postgres, rabbitmq (com definitions.json), migrate, billing, receipts
  rabbitmq/definitions.json    exchange "events" (topic) e filas; o Waybill não cria topologia
  Billing/                     API mínima: POST /invoices, POST /invoices/{id}/payments, GET /health
  Receipts/                    consumidor RabbitMQ.Client puro + inbox; GET /health
  Billing.Tests/, Receipts.Tests/   testes com FakeOutbox e FakeInbox, sem infraestrutura
  scripts/broker-outage.sh|.ps1
  README.md                    em inglês
```

- **Billing.**
  - `POST /invoices/{id}/payments` marca a fatura como paga e enfileira `billing.invoice-paid.v1` no mesmo `SaveChanges`.
  - Roda o dispatcher e a retenção no próprio processo.
  - `/health` usa `AddWaybillDispatcher()`.
- **Receipts.**
  - Consome `billing.invoice-paid.v1` com RabbitMQ.Client puro.
  - O handler roda dentro de `ProcessAsync`: emite o recibo e enfileira `receipts.receipt-issued.v1` na mesma transação. É o consumidor que também produz.
  - Ack só depois do commit.
  - Roda o próprio dispatcher e a retenção.
- **Bancos.** Um banco por serviço no mesmo Postgres. Cada um tem o schema `waybill` e as tabelas da aplicação.
- **Migrations.**
  - Um serviço `migrate` do compose roda cada serviço em modo `migrate`: o schema do Waybill com `WaybillSchema.MigrateAsync` e as migrations EF da aplicação.
  - A API nunca migra na partida. O README explica por quê.
  - `billing` e `receipts` dependem de `migrate` com `service_completed_successfully`.
- **Topologia.** `definitions.json`, carregado pelo próprio RabbitMQ, declara:
  - a exchange `events`;
  - a fila `receipts.invoice-paid`, com bind `billing.invoice-paid.v1`;
  - uma fila `audit`, com bind `#`, para ver o segundo evento chegar.
- **Cenário de falha.** O script faz quatro coisas:
  - para o `rabbitmq`;
  - cria cobranças;
  - mostra as pendentes acumulando (`psql` na tabela `waybill.outbox`) e o `/health` em `Degraded`;
  - religa o broker e mostra a fila drenar e o `/health` voltar a `Healthy`.

## Perguntas que a etapa responde (do plano)

| Pergunta | Como medir |
| --- | --- |
| Quantas linhas de configuração para começar? | Contar as linhas do Waybill no `Program.cs` do Billing. Acima de dez, a API está pesada e vira tarefa |
| Os fakes bastam para testar o caso de uso e o handler? | Os testes do exemplo usam só `Waybill.Testing`. Faltou asserção: ajuste nos fakes |
| Os nomes fazem sentido para quem não escreveu o pacote? | Fricção relatada pelo agente sem contexto e pelo autor |
| O que acontece quando o usuário esquece de registrar um tipo? | Teste no exemplo: a mensagem de erro diz o que fazer |

Cada fricção corrigida no pacote ganha teste. Uma mudança de API pública passa por ADR.

## CI

- O exemplo entra na solution: build e os testes `Billing.Tests` e `Receipts.Tests` rodam no job de teste.
- Um job novo, `sample`, roda `docker compose up --wait` e faz um smoke: cria uma cobrança, paga e espera o recibo e o evento na fila `audit`. Prova que o exemplo sobe, não só que compila.
- Os projetos do exemplo não são empacotados.

## Fora do escopo

Kafka, OpenTelemetry e dashboard de métricas. O exemplo mostra o health check e a contagem de pendentes por SQL; a métrica fica para quem já usa um exportador.
