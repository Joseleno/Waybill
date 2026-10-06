# Etapa 3 — Dispatcher e RabbitMQ (G2)

Derivado de `docs/plano.md`, seção "Etapa 3", do Contrato técnico de `docs/escopo-e-fronteiras.md` e do ADR 0001.

## Objetivo

Entregar G2: **todo evento persistido é publicado pelo menos uma vez, ou vai para a DLQ do outbox por um defeito da lista fechada, com o motivo registrado; nunca é descartado em silêncio.**

## Divisão em três PRs (decisão do autor, Oct 4, 2026)

| PR | Conteúdo | Cenários do plano |
| --- | --- | --- |
| **3a** | Dispatcher e claim com transporte falso: contrato `ITransport`, claim por linha, lease com margem, fencing por `owner` e `fence`, devolução sem gastar tentativa, DLQ como status, shutdown gracioso | `kill -9` do dispatcher; mesma instância reivindica de novo; mensagem gravada sob limite maior que o atual num lote de cem; oito dispatchers concorrentes |
| **3b** | Transporte RabbitMQ: publisher confirms, mensagem persistente, `mandatory`, roteamento, propriedades AMQP | Publicação real e `basic.return` |
| **3c** | Classificação de falhas completa, circuit breaker, redução de lote, republicação um a um em canal novo, testes de caos com Toxiproxy, ADR da classificação | Broker parado por uma hora; latência alta; limite acima do `max_message_size`; Toxiproxy derruba a conexão |

## Decisões de desenho

| Tema | Decisão |
| --- | --- |
| Roteamento (RabbitMQ, 3b) | Uma exchange configurada (topic, criada pelo time) e routing key = nome registrado da mensagem. O consumidor faz bind pelo nome ou por padrão (`billing.#`). Rota por mensagem pode vir depois sem quebrar |
| Contrato de transporte | `ITransport.PublishAsync(lote)` devolve um resultado por mensagem, na ordem: `Confirmed`, `Retry` (falha de transporte: devolve sem gastar tentativa), `Returned` (sem rota: gasta do orçamento próprio) ou `Defect` (DLQ com motivo). Exceção ou cancelamento no meio do lote = `Retry` para o lote inteiro |
| Lease | `Lease = PublishTimeout + LeaseMargin`, então "lease ≥ timeout de confirmação + margem" vale por construção. A espera pela publicação é cancelada no `PublishTimeout`; resultado desconhecido vira devolução (pode duplicar, nunca perde) |
| Tempo | O lease vem do relógio do banco (`clock_timestamp()`); o timeout de publicação, do relógio local. A margem absorve a diferença |
| Owner | Único por encarnação de processo: `{máquina}/{pid}/{aleatório}` |
| Acesso ao banco | O dispatcher usa Npgsql direto com SQL cru (o mesmo do ADR 0001), num `NpgsqlDataSource` próprio; não passa pelo `DbContext` da aplicação |
| Hospedagem | `BackgroundService`, na API ou num worker. Um lote em voo por instância. Lote cheio: busca o próximo na hora; lote parcial ou vazio: espera o `PollingInterval` |
| Shutdown | Para de reivindicar, termina o lote em voo (limitado pelo `PublishTimeout`) e devolve o que ainda estiver reivindicado por este `owner` |
| Tamanho na publicação | Linha com payload acima do `MaxPayloadBytes` atual (gravada sob um limite maior) vai para a DLQ sem tocar o broker |

## API da 3a

```csharp
services.AddWaybillDispatcher(o =>
{
    o.ConnectionString = cs;                       // obrigatório
    o.BatchSize = 100;
    o.PollingInterval = TimeSpan.FromSeconds(1);
    o.PublishTimeout = TimeSpan.FromSeconds(20);
    o.LeaseMargin = TimeSpan.FromSeconds(10);      // lease = 30 s
    o.MaxReturns = 5;                              // orçamento de basic.return antes da DLQ
});
services.AddSingleton<ITransport, MeuTransporte>(); // na 3b: AddWaybillRabbitMQ(...)
```

## Fora da 3a

Transporte RabbitMQ (3b); circuit breaker, redução de lote e republicação um a um (3c); métricas e health check (etapa 5).
