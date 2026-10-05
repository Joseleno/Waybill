# Etapa 5 — Limpeza e observabilidade

Derivado de `docs/plano.md`, seção "Etapa 5", e das linhas G3 e "Operação da DLQ" de `docs/escopo-e-fronteiras.md`.

## Objetivo

Manter o pacote utilizável depois do primeiro mês em produção: as tabelas não crescem sem limite, nenhuma mensagem pendente é apagada pela limpeza, e o operador enxerga atraso e queda do broker sem depender de OpenTelemetry.

## Decisões (Oct 4, 2026)

| Tema | Decisão |
| --- | --- |
| Cenário de carga longa | 5 h no runner do GitHub, com a duração como input do workflow (default 5 h). O que o cenário prova é que a latência do claim estabiliza depois que a transação longa fecha, não as 24 h. `docs/plano.md` passa a dizer "carga longa" |
| Métrica | `ObservableGauge<double>` `waybill.outbox.oldest_pending.age`, unidade `s`, no Meter `Waybill`. Um laço em background consulta a idade da mais antiga ainda não publicada (`pending` ou `claimed`) num intervalo configurável e guarda o valor; o callback do gauge só lê o valor guardado e nunca toca o banco. Sem pendentes, o valor é 0 |
| Retenção do inbox e G3 | `ProcessAsync` não recusa mensagem antiga. Recusar pelo timestamp do UUIDv7 transformaria em perda a primeira entrega de uma mensagem que esperou no outbox com o broker fora por mais tempo que a retenção. A recusa por retenção continua na API de operação da DLQ (v1.0), como no Escopo; replay de offset ou de fila fora da janela continua não coberto. A etapa documenta como dimensionar a retenção do inbox |
| Health check | Em `Waybill.EntityFrameworkCore.PostgreSql`, onde já estão o dispatcher e o `CircuitBreaker`. O pacote passa a depender de `Microsoft.Extensions.Diagnostics.HealthChecks` (onde está o `IHealthChecksBuilder`; ver ADR 0004). O núcleo não muda |

As quatro decisões vão para o ADR 0004.

## API

```csharp
services.AddWaybillRetention(options =>
{
    options.ConnectionString = "...";
    options.OutboxRetention = TimeSpan.FromDays(7);   // só linhas published, contadas de published_at
    options.InboxRetention = TimeSpan.FromDays(30);   // contada de processed_at
});

services.AddWaybillDispatcher(options => { ... });   // passa a publicar a métrica de idade
services.AddHealthChecks().AddWaybillDispatcher();
```

A limpeza é um serviço próprio, separado do dispatcher: um consumidor que só usa o inbox não sobe dispatcher. Os nomes e os defaults acima são propostas e fecham no PLAN.

## Mecanismo

1. **Limpeza.** `DELETE` em lotes pequenos (cada lote em transação curta), repetido até não sobrar linha elegível. No outbox, apaga só `status = 'published'` com `published_at` mais antigo que a retenção. Linhas `pending`, `claimed` e `dlq` nunca são apagadas. No inbox, apaga as linhas com `processed_at` mais antigo que a retenção. Vários processos podem limpar ao mesmo tempo sem conflito.
2. **Chaves.** `outbox_keys` e `inbox_keys` só existem na v0.2. Por construção, a limpeza toca só `waybill.outbox` e `waybill.inbox`. O teste de que as chaves nunca são limpas entra com elas.
3. **Métrica.** A consulta usa o índice parcial `ix_outbox_claimable`. Uma falha da consulta gera log e mantém o último valor; não derruba o dispatcher.
4. **Health check.**
   - `Healthy`: o laço do dispatcher está vivo e o breaker está fechado.
   - `Degraded`: o breaker está aberto (broker fora). O outbox continua aceitando eventos.
   - `Unhealthy`: o laço parou.
   - A descrição traz o motivo e, quando houver, a idade da pendente mais antiga.
5. **Documento de operação** (`docs/OPERATIONS.md`, em inglês por ser voltado ao usuário). Traz:
   - os defaults;
   - o que monitorar: a idade da pendente, o tamanho da DLQ e o health check;
   - o autovacuum: `autovacuum_vacuum_scale_factor` baixo, sem `fillfactor`, porque HOT é impossível com `status` no predicado do índice parcial;
   - o dimensionamento da retenção do inbox: maior que o maior atraso possível entre o enfileiramento e a última reentrega, ou seja, backlog do outbox + fila do broker + janela de reprocessamento da DLQ.

## Cenários de aceite

| Cenário | Onde roda |
| --- | --- |
| Broker parado por mais tempo que a retenção: nenhuma pendente é apagada | PR, com retenção de segundos |
| Broker parado e religado: a métrica cresce e volta a zero; o health check vai a degradado e volta | PR |
| Carga longa com uma transação longa aberta: a latência do claim estabiliza depois que ela fecha | Job agendado, 5 h, `Category=Long`. Mede tempo, não só bytes |

Os cenários de limpeza do inbox e de limpeza concorrente entram no PLAN.

## Fora do escopo

- Recusa por retenção no reprocessamento (API de DLQ, v1.0).
- Limpeza e teste de `outbox_keys` e `inbox_keys` (v0.2).
- Métricas além da idade da pendente: vazão, tamanho da DLQ como métrica, histogramas de latência.
- Integração com OpenTelemetry além do Meter.
