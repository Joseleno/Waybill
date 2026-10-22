# ADR 0011 — OpenTelemetry: propagação, métricas e spans

- Status: aceito (parte 1: propagação e métricas; a parte 2, dos spans, entra com a implementação deles)
- Data: 2026-10-21
- Etapa: 11 (v0.2)

## Contexto

O envelope leva o `traceparent` e o `tracestate` do código que enfileirou desde a v0.1, e o transporte RabbitMQ os manda como cabeçalhos. O escopo promete, para a v0.2, um único trace da requisição até o handler e métricas de atraso, de chaves bloqueadas e de DLQ. O atraso (`waybill.outbox.oldest_pending.age`) e as chaves bloqueadas (`waybill.outbox.blocked_keys`) já existiam. A DLQ só aparecia em SQL, e as regressões do inbox (etapa 10) só no log.

A etapa achou um defeito da v0.1. O RabbitMQ.Client 7, com a fonte `RabbitMQ.Client.Publisher` ouvida, abre um span de publicação com pai no `Activity.Current` e escreve o contexto desse span sobre o `traceparent` da mensagem, sem condição. O dispatcher roda num serviço hospedado, sem `Current`. Cada mensagem chegava então ao consumidor num trace novo, sem relação com a requisição. Sem ouvinte nessa fonte, ou com o `Sample` dizendo `None`, o cliente não injeta nada, e o cabeçalho do Waybill passa.

O SPEC e o PLAN passaram por duas revisões adversariais e por um time de quatro revisores (diagnóstico .NET e OpenTelemetry, RabbitMQ.Client, PostgreSQL e EF, testes e API), com experimentos: o RabbitMQ.Client 7.2.2 descompilado e contra `rabbitmq:4-alpine`, o SDK OpenTelemetry 1.18.0, o EF Core e o Npgsql 10.0.3, `postgres:15` e `postgres:18`.

## Decisão — parte 1

1. **O transporte põe o contexto da mensagem no `Activity.Current` enquanto o cliente publica.** Uma `Activity` sem fonte, em W3C, filha do `traceparent` gravado (lido com `isRemote: true`), aberta e fechada em volta do `BasicPublishAsync`, dentro do método assíncrono de cada mensagem: o `Current` não vaza para o fluxo do dispatcher. Com isso o span do cliente e o contexto que ele injeta ficam no trace da mensagem. Duzentas publicações concorrentes no mesmo canal, cada uma com seu `Current`, não trocaram trace nenhum.
2. **Só quando a fonte do cliente tem ouvinte.** Uma `ActivitySource` com o nome da fonte do cliente responde a `HasListeners()` como a dele, porque os ouvintes escolhem fontes pelo nome; nada é aberto nela. Sem ouvinte, nenhuma alocação por mensagem.
3. **Gauge `waybill.outbox.dlq`.** Mensagens em `dlq`, com a ordenação ligada ou desligada, amostradas com os outros gauges, as três leituras num `SELECT` só. Antes da primeira amostra, nada é reportado: um 0 depois de um reinício durante uma queda pareceria uma DLQ vazia. Índice parcial `ix_outbox_dlq (id) WHERE status = 'dlq'` na `SchemaV0_2`, ainda sem tag: sem ele a contagem varre as linhas retidas (67 a 110 ms com um milhão; com ele, 0,03 a 0,05 ms). No modelo do EF, o índice é declarado com nome no `HasIndex`: sem nome, ele reconfiguraria o `ix_outbox_claimable`, o outro índice em `Id`, e o snapshot o perderia.
4. **Agregação por máximo.** Toda instância do dispatcher lê a mesma tabela e reporta o mesmo valor dos gauges da outbox; somar multiplica pelo número de instâncias. Vale para `oldest_pending.age`, `blocked_keys` e `dlq`.
5. **Contador `waybill.inbox.regressions`,** com o handler e o motivo (`lower`, `reused`), onde o inbox já loga o EventId 50. O inbox registra os próprios instrumentos, uma vez por contêiner: um serviço só consumidor não tem dispatcher.
6. **Um meter só.** `WaybillDiagnostics.MeterName` é público; todos os pacotes montam o meter pelo mesmo caminho, e um `IMeterFactory` devolve o mesmo meter a todos.

## Alternativas descartadas — parte 1

| Alternativa | Por que não |
| --- | --- |
| Propagador do cliente por conexão (`ConnectionFactory.TracingOptions`) | Só existe a partir do RabbitMQ.Client 7.3, ainda não publicado; o 7.2.2 tem só a configuração estática, do processo inteiro, e uma biblioteca não deve mexer nela |
| Documentar e não corrigir | O trace partido aparece justamente para quem configurou telemetria, sem aviso |
| Contar a DLQ sem índice novo | Os índices parciais de hoje só têm linhas com `sequence`; a contagem varreria a tabela a cada amostra, em cada instância |
| `UpDownCounter` para a DLQ | Sugere soma entre instâncias, e o valor não é somável |

## Consequências — parte 1

- O consumidor fica no trace da requisição também quando só a fonte do cliente é ouvida. A `Activity` de propagação não é exportada: no trace, o span do cliente fica pendurado num span id que nenhum backend recebeu. O `OPERATIONS.md` diz isso.
- Bancos de desenvolvimento que aplicaram a `SchemaV0_2` antes desta etapa não têm o índice e precisam ser recriados, como na etapa 10.

## Testes que provam — parte 1

- `Rastro_RabbitMq_SoFonteDoClienteOuvida_ConsumidorNoTrace`, visto falhando antes da correção: três mensagens, três traces novos.
- `Rastro_RabbitMq_NinguemOuve_TraceparentDaRequisicao`.
- `Metrica_DlqEChavesBloqueadas_SobemEVoltamAZero` e `Metrica_DlqSemChave_SoODlq_TambemComOrdenacaoDesligada`.
- `Metrica_Regressoes_ServicoSemDispatcher_ContaPorHandlerEMotivo`.
- `Schema_ModeloSemMudancaPendente`, `Schema_IndiceDaDlq_UsadoNaContagem`, `Schema_DownSemIndiceDaDlq_NaoFalha` e os `Schema_*` ampliados.

Mutações vistas morrendo:
- o índice declarado sem nome (`Schema_ModeloSemMudancaPendente`);
- o registro do contador do inbox com `AddSingleton` (`Metrica_Regressoes_ServicoSemDispatcher`).
