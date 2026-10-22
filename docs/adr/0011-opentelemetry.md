# ADR 0011 — OpenTelemetry: propagação, métricas e spans

- Status: aceito (parte 1: propagação e métricas; parte 2: spans)
- Data: 2026-10-21
- Etapa: 11 (v0.2)

## Contexto

O envelope leva o `traceparent` e o `tracestate` do código que enfileirou desde a v0.1, e o transporte RabbitMQ os manda como cabeçalhos. O escopo promete, para a v0.2, um único trace da requisição até o handler e métricas de atraso, de chaves bloqueadas e de DLQ. O atraso (`waybill.outbox.oldest_pending.age`) e as chaves bloqueadas (`waybill.outbox.blocked_keys`) já existiam. A DLQ só aparecia em SQL, e as regressões do inbox (etapa 10) só no log.

A etapa achou um defeito da v0.1. O RabbitMQ.Client 7, com a fonte `RabbitMQ.Client.Publisher` ouvida, abre um span de publicação com pai no `Activity.Current` e escreve o contexto desse span sobre o `traceparent` da mensagem, sem condição. O dispatcher roda num serviço hospedado, sem `Current`. Cada mensagem chegava então ao consumidor num trace novo, sem relação com a requisição. Sem ouvinte nessa fonte, ou com o `Sample` dizendo `None`, o cliente não injeta nada, e o cabeçalho do Waybill passa.

O SPEC e o PLAN passaram por duas revisões adversariais e por um time de quatro revisores (diagnóstico .NET e OpenTelemetry, RabbitMQ.Client, PostgreSQL e EF, testes e API), com experimentos: o RabbitMQ.Client 7.2.2 descompilado e contra `rabbitmq:4-alpine`, o SDK OpenTelemetry 1.18.0, o EF Core e o Npgsql 10.0.3, `postgres:15` e `postgres:18`.

## Decisão — parte 1

1. **O transporte põe o contexto da mensagem no `Activity.Current` enquanto o cliente publica.** Uma `Activity` sem fonte, em W3C, filha do `traceparent` gravado (lido com `isRemote: true`), aberta e fechada em volta do `BasicPublishAsync`, dentro do método assíncrono de cada mensagem: o `Current` não vaza para o fluxo do dispatcher. Com isso o span do cliente e o contexto que ele injeta ficam no trace da mensagem. Duzentas publicações concorrentes no mesmo canal, cada uma com seu `Current`, não trocaram trace nenhum. Desde a parte 2, a `Activity` sem fonte é só a reserva, para quando não há span `send` (ninguém ouve a fonte `Waybill`); com ele, o transporte põe o próprio `send` no `Current` (parte 2, item 3), e o item 2 abaixo vale só para a reserva.
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

## Decisão — parte 2: spans

Convenções seguidas: as de mensageria do OpenTelemetry 1.44.0, ainda em desenvolvimento.

1. **Três spans, cada um com um dono.**
   - `create`: aberto no `Enqueue`, kind Producer, filho do `Current`, depois de toda validação. O `traceparent` gravado na linha passa a ser o dele.
   - `send {destino}` (só `send` num transporte de terceiros, que não informa o destino): aberto pelo dispatcher, kind Producer, um por mensagem entregue ao transporte ou julgada na rodada (payload acima do limite), por claim. Fecha depois da marcação no banco, com `waybill.send.outcome` (`published`, `retry`, `returned`, `dlq`, `fenced`, `unknown`).
   - `process`: aberto no inbox, kind Internal, um por chamada, fora da estratégia de execução; cada execução dela é um evento.
2. **O pai do `send` é o contexto gravado, além do link** que as convenções pedem. Toda tentativa, também depois de um lease vencido, horas mais tarde, cai no trace da requisição. É o que o escopo promete.
3. **O dispatcher é o dono do `send`, não o transporte.** Vários desfechos se decidem fora do transporte: canal indisponível, timeout do lote, `MaxReturns` esgotado, fence perdido, marcação que falha. O `FinishResult` devolve os ids aplicados com o status final, e o desfecho sai do que o banco gravou. O span chega ao transporte de dois jeitos:
   - no cabeçalho `traceparent`, que qualquer transporte propaga;
   - numa propriedade interna da mensagem, que o transporte do Waybill põe no `Current` em volta do `BasicPublishAsync`.

   Ela é interna porque um transporte de terceiros poderia fechar o span antes do desfecho. O destino e os atributos do sistema vêm do transporte por uma interface interna; o `ITransport` não muda.
4. **Abrir e fechar sem mexer no `Current` de quem chama.** Salvar e restaurar o `Current` à mão falha com um `Current` parado (o setter o ignora) e com um ouvinte que lança. Os spans do dispatcher abrem e fecham dentro de um método assíncrono que termina de forma síncrona: ele devolve o contexto de execução do chamador em qualquer caso. O laço do dispatcher zera o `Current` ao começar: ele herda o contexto de execução de quem iniciou o host, talvez dentro de um span já encerrado.
5. **Um ouvinte que lança custa o span, nunca a publicação.** O dispatcher segue sem o span da mensagem.
6. **Nenhum span de ciclo.** Claim, manutenção das partições, varredura, amostragem e retenção seriam um trace por ciclo de polling, sem relação com as mensagens.
7. **`process` é Internal.** O consumo é da aplicação. Com a instrumentação do cliente, o `deliver` dele já é o span Consumer e o pai; um segundo Consumer contaria o processamento duas vezes.
8. **Ids em W3C sempre,** mesmo com outro formato padrão no processo: o `traceparent` gravado e o propagado precisam ser legíveis pelo consumidor.
9. **Chave e tenant fora dos spans por padrão** (decisão do autor), ligáveis por `WaybillOptions.RecordKeyAndTenantInTraces`. O `process` só conhece a chave da ordem; o tenant, não.

**Desvios das convenções:**
- o `send` tem o contexto de criação como pai, além do link (item 2);
- o `process` é Internal, não Consumer (item 7);
- `create`, `process` e o `send` de um transporte de terceiros saem sem `messaging.system`, que é obrigatório: o núcleo e o inbox não conhecem o broker.

## Alternativas descartadas — parte 2

| Alternativa | Por que não |
| --- | --- |
| Um span por lote no dispatcher | Atravessaria traces, e, como `Current`, poria todas as mensagens do lote no trace do lote: o cliente injetaria esse contexto em cada uma |
| O span de envio no transporte | Os desfechos decididos fora dele (canal indisponível, timeout do lote, `MaxReturns`, fence) ficariam sem span, e o status do span poderia contradizer o banco |
| `SendActivity` público | Daria a transportes de terceiros um objeto mutável que eles poderiam fechar antes do desfecho; eles já recebem o contexto pelo `traceparent` |
| `send` só com link, como as convenções pedem | A republicação sairia do trace da requisição |
| Um span de `process` por tentativa da estratégia | Um commit ambíguo apareceria como dois processamentos |
| Salvar e restaurar o `Current` à mão | Falha com um `Current` parado e com um ouvinte que lança (verificado) |

## Consequências — parte 2

- Sem ouvinte da fonte `Waybill`, nada é aberto nem alocado. Com ouvinte, cerca de 1,7 KB por mensagem no `send`.
- Com o amostrador padrão, uma requisição não amostrada leva junto todo o trace da mensagem. O `OPERATIONS.md` manda ouvir também a instrumentação de entrada.
- Uma republicação horas depois pode cair num trace que um coletor com *tail sampling* já fechou.
- O `FakeOutbox` usa o mesmo `EnvelopeFactory` e emite `create`; o `FakeInbox` não emite `process`.

## Testes que provam — parte 2

- `Rastro_PontaAPonta_RequisicaoAteHandler`: um só trace em `create`, `send`, `publish` e `deliver` do cliente e `process`, cada um filho do anterior.
- `Rastro_Send_*`: pai, link e cabeçalho; os desfechos; as linhas não enviadas; lease vencido; lote misto; `traceparent` malformado; sem ouvinte; chave e tenant.
- `Rastro_Ciclo_SqlForaDosSpansDeMensagem`, `Rastro_Ciclo_OuvinteQueLanca_PublicaMesmoAssim`, `Rastro_SemSpanDeCiclo`.
- `Rastro_RabbitMq_DuasFontes_ClienteDentroDoSend`, `Rastro_RabbitMq_Isolamento406_EventoNoSend`.
- `Rastro_Process_*`; os `Rastro_Create_*` e `Rastro_StartIsolated_*` (unitários).

As mutações estão listadas no `TASKS.md` da etapa 11; todas morreram.
