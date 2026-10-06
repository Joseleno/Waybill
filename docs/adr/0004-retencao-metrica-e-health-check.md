# ADR 0004 — Retenção, métrica de atraso e health check

- Status: aceito
- Data: 2026-10-04
- Etapa: 5 (limpeza e observabilidade)

## Contexto

Sem limpeza, `waybill.outbox` e `waybill.inbox` crescem para sempre. Uma limpeza que apague mensagem ainda não entregue viola G2. O inbox limpo esquece o que já processou, e isso toca a fronteira de G3. O operador também precisa enxergar duas coisas sem depender de OpenTelemetry: atraso na publicação e broker fora do ar.

## Decisão

**Limpeza em serviço próprio.** `AddWaybillRetention` registra um `BackgroundService` separado do dispatcher, com data source próprio. Um consumidor que só usa o inbox não sobe dispatcher. A limpeza roda em lotes (`BatchSize`), cada um com `DELETE … WHERE id IN (SELECT … LIMIT … FOR UPDATE SKIP LOCKED)` numa transação curta, repetido enquanto o lote vier cheio. Várias instâncias limpam ao mesmo tempo sem esperar umas pelas outras.

| Tabela | Apaga | Nunca apaga |
| --- | --- | --- |
| `waybill.outbox` | `status = 'published'` com `published_at` além de `OutboxRetention` (7 dias) | `pending`, `claimed`, `dlq` |
| `waybill.inbox` | `processed_at` além de `InboxRetention` (30 dias) | — |

**Filtro por `id` no outbox.** O filtro do outbox soma `id < uuid_do_corte`, para a busca andar pela chave primária sem índice novo. O critério continua sendo o `published_at`. O UUIDv7 é gerado no cliente antes do commit, e o `published_at` é posterior ao commit. Com o relógio do cliente adiantado, a linha só fica mais tempo. Com o relógio atrasado, ela passa pelo filtro de `id` antes, mas o `published_at` ainda barra. Isso não contradiz o achado A5 do spike (ordem nunca por timestamp): aqui o `id` só estreita uma busca, não decide ordem.

**Índice no inbox.** O `message_id` do inbox pode vir de outro sistema e não ser UUIDv7, então o mesmo truque não serve. Uma migration nova do pacote cria `ix_inbox_processed_at`. O inbox só recebe INSERT; o custo é uma entrada de índice por linha.

**`outbox_keys` e `inbox_keys`.** Só existem na v0.2. A limpeza toca apenas `waybill.outbox` e `waybill.inbox`, por construção, e o teste de que as chaves nunca são limpas entra junto com elas.

**Fronteira de G3: `ProcessAsync` não recusa por idade.** A condição de G3 no Escopo põe a recusa por retenção na API de operação da DLQ (v1.0) e declara não coberto o replay fora da janela. Recusar no `ProcessAsync` pelo timestamp do UUIDv7 foi considerado e rejeitado. O id nasce no `Enqueue`. Com o broker fora por mais tempo que a retenção do inbox, a primeira entrega já chegaria "velha" e seria recusada: o efeito nunca seria aplicado. Seria trocar uma duplicata possível por uma perda certa. Fica documentado como dimensionar: a retenção do inbox precisa ser maior que o maior atraso entre o `Enqueue` e a última reentrega (backlog do outbox + fila do broker + janela de reprocessamento da DLQ). Não há piso obrigatório, porque os testes usam retenção de segundos.

**Métrica.** `ObservableGauge<double>` `waybill.outbox.oldest_pending.age`, unidade `s`, no Meter `Waybill`, criado pelo `IMeterFactory` quando houver um. Um amostrador registrado por `AddWaybillDispatcher` consulta a cada `MetricsInterval` (15 s) a idade da linha `pending` ou `claimed` mais antiga na ordem do claim (`ORDER BY id LIMIT 1` no índice parcial `ix_outbox_claimable`), medida pelo relógio do banco. O callback do gauge só lê o valor guardado. Ele roda no ritmo do exportador e é síncrono; consultar o banco ali bloquearia a coleta. Sem pendentes, o valor é 0. Se a consulta falha, gera log e o gauge mantém o último valor. O menor `created_at` foi descartado porque, com o broker fora e um backlog grande, varreria todas as pendentes a cada amostra.

**Health check.** Fica em `Waybill.EntityFrameworkCore.PostgreSql`, onde estão o dispatcher e o breaker. O pacote passa a depender de `Microsoft.Extensions.Diagnostics.HealthChecks`, e não só do `.Abstractions`, porque é ali que está o `IHealthChecksBuilder` usado por `AddHealthChecks().AddWaybillDispatcher()`. O health check do próprio EF Core segue o mesmo caminho. O núcleo não muda. Regras, na ordem:

| Estado | Quando |
| --- | --- |
| `Unhealthy` | Laço do dispatcher fora de execução, ou último ciclo concluído há mais de `Lease + max(MaxBackoff, PollingInterval) + 30 s` (a maior espera legítima entre ciclos mais folga para o próprio ciclo; 90 s com os padrões) |
| `Unhealthy` | Três ou mais falhas de banco seguidas |
| `Degraded` | Breaker aberto ou último ciclo com falha de conexão: o broker está fora, mas o outbox continua aceitando eventos |
| `Healthy` | Nenhuma das anteriores |

## Consequências

- Depois de uma pane longa do broker, o filtro por `id` deixa de estreitar: o backlog tem `id` antigo e `published_at` recente, e cada passada percorre essas linhas sem apagá-las até passarem da retenção. O custo é proporcional ao backlog, uma vez por `Interval`. Fica documentado no `OPERATIONS.md`; um índice parcial por `published_at` só entra se isso aparecer em medição (achado da revisão).
- Broker fora por mais tempo que a retenção: nada pendente é apagado, a tabela cresce e a métrica mostra o atraso. Quando o broker volta, a fila drena, e as linhas publicadas só saem depois de mais uma retenção.
- Reentrega depois da limpeza do inbox processa de novo. É a fronteira declarada de G3, caracterizada por teste e documentada no `OPERATIONS.md`; não é garantia.
- A métrica tem a resolução do `MetricsInterval`, e o valor pode ter até esse atraso.
- O cenário de carga longa roda 5 h no runner do GitHub, com a duração configurável. O limite de 6 h vale por job, então ele tem um job próprio no `scheduled.yml`, separado dos outros longos. O que ele prova é a estabilização da latência do claim depois que a transação longa fecha, não a duração em si.

## Testes que provam

`G2_BrokerParadoAlemDaRetencao_NenhumaPendenteApagada`, `Retencao_*`, `Inbox_ReentregaDepoisDaLimpeza_ProcessaDeNovo`, `Metrica_*`, `HealthCheck_*` (integração); `BrokerParadoEReligado_MetricaEHealthCheckAcompanham` (caos); `CargaLonga_TransacaoLongaAberta_LatenciaDoClaimEstabiliza` (agendado, 5 h).

## Revisão (2026-10-04)

A revisão de código independente desta etapa encontrou e motivou:

- **Laço travado com `PollingInterval` longo.** O limiar `Lease + MaxBackoff` marcava `Unhealthy` um dispatcher ocioso com intervalo de polling maior que 60 s. Passou a `Lease + max(MaxBackoff, PollingInterval) + 30 s`.
- **Carga longa que podia passar sem provar nada.** O teste passou a exigir que os dead tuples com a transação aberta passem do dobro dos da linha de base, e a janela final só conta depois do primeiro autovacuum posterior ao fechamento.
- **Uma tabela que falha travava a outra.** A limpeza do outbox e a do inbox passaram a ser independentes: a falha numa gera log e a outra segue.
- **Intervalos sem teto.** `Task.Delay` lança acima de ~49 dias e derrubaria o host; `PollingInterval`, `MetricsInterval` e `Interval` passaram a aceitar no máximo um dia.
- **CI que tolerava zero testes.** `--ignore-exit-code 8` saiu dos passos que agora sempre têm testes.
- **"Not running" logo depois da partida.** No .NET 10 o `BackgroundService` roda o `ExecuteAsync` inteiro numa tarefa à parte, então um health check logo depois do `StartAsync` lia `Unhealthy`. O status passou a ser marcado no `StartAsync` (e a parada também no `StopAsync`). Achado ao investigar um teste instável.

## Achado da carga longa (2026-10-05)

O primeiro run agendado de 5 h falhou no critério de latência: depois da transação longa, o p95 do claim voltava à linha de base, mas um terço dos minutos tinha rajadas de ~8 ms até o fim, horas depois. Autovacuum, dead tuples e tamanho da tabela estavam normais. O autor decidiu investigar a causa em vez de afrouxar o critério.

Dois pares de runs, sem e com `REINDEX INDEX CONCURRENTLY waybill.ix_outbox_claimable` depois do primeiro autovacuum pós-fechamento, mostraram a causa:

- **Sem reindex (5 h):** o índice parcial do claim ficou em ~85 MB (contra menos de 1 MB na linha de base) até o fim. 28 dos 106 minutos da recuperação tiveram p95 acima de 5 ms. Nos minutos lentos, a varredura leu as mesmas ~29 tuplas que nos normais, mas 2,4 vezes mais páginas do índice (40,8 contra 16,8 por varredura).
- **Com reindex (5 h):** o índice voltou a 16 KB, e só os 2 minutos antes da reconstrução passaram de 5 ms.

O autovacuum limpa as entradas mortas, mas não devolve as páginas do índice. Depois de um episódio de transação longa, o claim percorre páginas quase vazias. Nada no pacote muda:
- o `OPERATIONS.md` passa a mandar reconstruir o índice depois de um episódio desses, com o comando e como medir;
- o cenário da carga longa faz o mesmo e continua sujeito ao mesmo critério de latência (`WAYBILL_LONG_REINDEX=0` desliga a reconstrução, para reproduzir as rajadas).

Fazer o próprio pacote detectar o inchaço e reindexar ficou fora: exige privilégio de dono do índice e decisão de janela, que são do operador.
