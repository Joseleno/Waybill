# Etapa 11 — Plano e critérios de aceite

Deriva do `SPEC.md` desta pasta, com as decisões do autor de Oct 21, 2026. Revisto depois de uma revisão adversarial com experimentos de `Activity` e a descompilação do RabbitMQ.Client 7.2.2. Convenções de mensageria seguidas: **OpenTelemetry semantic conventions 1.44.0** (Aug 2026), parte de mensageria em desenvolvimento.

## Comportamentos do `Activity` que o desenho respeita (verificados)

- `StartActivity` com pai explícito **põe o span no `Activity.Current`** mesmo assim. Com pai `default`, ele herda o `Current`.
- `Stop` devolve ao `Current` a atividade que era corrente no `Start`, mesmo quando o span parado não é o corrente.
- O setter do `Current` ignora uma atividade parada, e uma atividade parada não serve de pai.
- `Sample` de uma raiz vê um `TraceId` recém-gerado; `ActivityStarted` e `ActivityStopped` chegam a todo ouvinte da fonte, inclusive ao que disse `None`; com vários ouvintes, vale a amostragem mais permissiva.
- RabbitMQ.Client 7.2.2: o span de publicação tem pai no `Current` e sobrescreve o `traceparent` sem condição; com `UseRoutingKeyAsOperationName` (padrão), o nome é `publish {routing key}`; o span `deliver` usa o contexto extraído como pai (`UsePublisherAsParent`, padrão) e envolve o `HandleBasicDeliverAsync`, e por isso o `ReceivedAsync` do Receipts roda sob ele; o span de publicação fecha antes da espera da confirmação.

## Desenho

| Tema | Decisão |
| --- | --- |
| Constantes públicas | `Waybill.WaybillDiagnostics` no núcleo, com `ActivitySourceName = "Waybill"` e `MeterName = "Waybill"`. O `OutboxMetrics.MeterName` passa a apontar para ela |
| Fonte | Um `ActivitySource` interno e estático no núcleo (`WaybillActivity.Source`, com a versão do assembly). O `Waybill.csproj` ganha `InternalsVisibleTo` para `Waybill.RabbitMQ` (hoje só o pacote do EF tem) |
| Abrir sem mexer no `Current` | `WaybillActivity.StartDetached(nome, kind, pai, tags, links)` guarda o `Current`, zera, abre o span e devolve o `Current` guardado; `StopDetached` faz o mesmo em volta do `Stop`. Todo span aberto fora do fluxo da mensagem (os `send` no dispatcher) usa os dois. Com pai `default`, o `Current` zerado faz do span uma raiz própria, nunca filho de outra mensagem |
| Opção nova | `WaybillOptions.RecordKeyAndTenantInTraces` (`false`). Ligada, os spans levam `waybill.key` e `waybill.tenant_id` |
| `create` | No `EnvelopeFactory.Create`, depois de toda validação e da serialização: `create`, kind Producer, filho do `Current` (aqui é o fluxo da requisição, e o span fecha antes do `return`, o que devolve o `Current`). Atributos: `messaging.operation.type = create`, `messaging.operation.name = create`, `messaging.message.id`, `waybill.message.name`, e chave e tenant se a opção pedir. Sem `messaging.system` nem destino: o núcleo não conhece o transporte (desvio, no ADR). O `Headers` lê o contexto desse span quando ele existe; senão o `Current`, como hoje |
| `send`: quando abre | Na rodada da linha, logo antes de entregá-la ao transporte, ou ao julgá-la localmente (payload acima do limite). Linha que a rodada não envia não abre span |
| `send`: como abre | Por `StartDetached`, kind Client, com pai e link = o contexto lido dos cabeçalhos `traceparent` e `tracestate` da linha por `ActivityContext.TryParse`. Linha sem `traceparent`, ou com um malformado: span raiz, sem link. Nome `send {exchange}:{routing key}`, que o transporte RabbitMQ informa por um método interno; um transporte de terceiros, que não o informa, dá só `send` (o `ITransport` não muda). Atributos: `messaging.operation.type = send`, `messaging.operation.name = send`, `messaging.message.id`, `waybill.sequence` quando há, e, do RabbitMQ, `messaging.system = rabbitmq`, `messaging.destination.name = {exchange}:{routing key}`, `messaging.rabbitmq.destination.routing_key` |
| `send`: propagação | A mensagem entregue ao transporte é refeita com `Headers["traceparent"]` = o id do span e a propriedade interna `SendActivity` = o span, só quando há span. A linha no banco não muda: um reenvio parte de novo do contexto de criação, e cai no mesmo trace com o mesmo pai |
| `send`: desfecho | Mapeado do resultado da mensagem e do que o `FinishAsync` aplicou (o `FinishResult` passa a devolver os ids aplicados com o status final, no lugar da contagem de fenced): não aplicado → `fenced`; `Defect` → `dlq`; `Returned` → `dlq` se voltou como `dlq`, senão `returned`; `Retry` → `retry`; `Confirmed` → `published`. `error.type`, de lista fechada: `connection`, `confirm_timeout`, `nacked`, `unspecified` (os de `TransportFailure`), `returned`, `defect`, `fenced`, ou o nome do tipo da exceção do `FinishAsync`. Status `Error` fora de `published` |
| `send`: fim | Num `finally` depois do `FinishAsync`, com `StopDetached`, uma vez. O timeout do lote (`WaitAsync`) não fecha span |
| Transporte | Em `PublishOneAsync` e `PublishAloneAsync`, dentro do método assíncrono, antes do primeiro `await`: os atributos do RabbitMQ no `SendActivity`, e `Activity.Current = SendActivity is { IsStopped: false } ? SendActivity : Propagation(message)` em volta do `BasicPublishAsync`, restaurado num `finally`. Um `SendActivity` já parado (tarefa abandonada pelo timeout do lote) cai na `Propagation`. `Propagation`: `ActivityContext.TryParse` dos cabeçalhos, `new Activity("Waybill.Propagation").SetParentId(traceId, spanId, flags)`, `TraceStateString`, `Start` e `Stop` em volta da chamada; nada sem `traceparent` válido. O isolamento depois de um 406 acrescenta o evento `waybill.isolated` ao `SendActivity` |
| `process` | No `DbContextInbox.ProcessCoreAsync`, fora do `ExecuteAsync`: `process`, kind Internal, filho do `Current` (o fluxo é o da aplicação). Atributos: `messaging.operation.type = process`, `messaging.operation.name = process`, `messaging.message.id`, `waybill.inbox.handler`, `waybill.sequence` quando há ordem, chave se a opção pedir. Cada execução da estratégia acrescenta o evento `waybill.attempt` com o número. No fim, `waybill.inbox.result`; `regressed` fecha com `error.type = regressed`; exceção fecha com `error.type` = tipo da exceção, e a exceção segue. Kind Internal: desvio de Consumer, no ADR |
| Gauge da DLQ | `waybill.outbox.dlq`, `{message}`, lido pelo `OldestPendingSampler` com `SELECT count(*) FROM waybill.outbox WHERE status = 'dlq'`, com a ordenação ligada ou desligada |
| Índice | `ix_outbox_dlq ON waybill.outbox (id) WHERE status = 'dlq'`: `HasIndex(...).HasFilter(...)` no `WaybillSchemaContext`, no Designer e no snapshot da `SchemaV0_2`, como o `ix_outbox_blocked_keys`; criado `CONCURRENTLY` como os outros parciais dela; `Down` correspondente |
| Contador de regressões | `waybill.inbox.regressions`, `{message}`, tags `waybill.inbox.handler` e `waybill.regression` (`lower`, `reused`). `InboxMetrics` singleton registrado pelo `AddWaybillInbox` com o `IMeterFactory` quando há, como o `OutboxMetrics`; o `DbContextInbox` conta onde hoje loga o EventId 50. O XML doc do `handler` pede valor constante, porque ele vira tag |
| Exemplo | Billing e Receipts: `OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Instrumentation.AspNetCore`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`, com `AddSource("Waybill", "RabbitMQ.Client.Publisher", "RabbitMQ.Client.Subscriber")` e `AddMeter("Waybill")`. Compose: `mcr.microsoft.com/aspire/dashboard` com tag fixa, `ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS=true`, UI na 18888 (porta do host configurável), OTLP gRPC na 18889 dentro da rede. Pacotes só nos `samples/`, em `Directory.Packages.props`, auditados |
| Documentos | `OPERATIONS.md`: rastro (as fontes a ouvir e por que as duas; o elo não exportado; o que não abre span; *tail sampling* e republicação tardia) e métricas novas, com a agregação por máximo do `dlq` e do `blocked_keys`. README: um parágrafo e as fontes. CHANGELOG: o defeito da v0.1, as fontes, as métricas, as convenções em desenvolvimento. ADR 0011 |

## Testes que provam

**Isolamento.** O `TraceRecorder` de cada teste fixa um `TraceId` antes de tudo: a raiz do teste é uma `Activity` sem fonte (como `EnvelopeTests.cs` já faz) ou um `ActivityContext` gerado antes. O `Sample` dele devolve `AllDataAndRecorded` só para esse `TraceId`, e o `ActivityStarted`/`ActivityStopped` dele também filtra por ele, porque recebe spans de qualquer teste. O dispatcher roda com `Activity.Current = null`, como no hosted service.

| Teste | Cenário | Resultado esperado | Onde |
| --- | --- | --- | --- |
| `Rastro_Create_ComOuvinte_GravaOContextoDoSpan` | `Enqueue` sob uma raiz ouvida | Um `create`, filho da raiz; o `traceparent` do envelope é o do `create`; o `Current` volta à raiz | Unit |
| `Rastro_Create_SemOuvinte_TraceparentDaV01` | `Enqueue` sob uma raiz não ouvida pela fonte `Waybill` | Nenhum span; o `traceparent` é o da raiz | Unit |
| `Rastro_Create_EnqueueRecusado_SemSpan` | Tipo não registrado, payload grande, string longa | Exceção; nenhum span | Unit |
| `Rastro_ChaveETenant_SoComAOpcao` | `create` com e sem a opção | Os dois atributos só com ela | Unit |
| `Rastro_Send_FilhoDoContextoGravado_ComLink` | Uma mensagem, `FakeTransport` | Um `send` Client, pai e link = contexto gravado; atributos; o transporte recebe o `traceparent` do `send` | Integração |
| `Rastro_Send_Desfechos` (teoria) | Confirmada, retry por conexão, timeout de confirmação, nack, returned com orçamento, `MaxReturns` esgotado, defect, payload acima do limite, fence perdido, timeout do lote | `waybill.outcome` e `error.type` pelo mapeamento; coerentes com o status gravado; cada span parado uma vez (contado no `ActivityStopped`) | Integração |
| `Rastro_Send_LinhaNaoEnviada_SemSpan` | Chave parada na rodada 1; rodada cortada pelo prazo | Nenhum `send` para as linhas não enviadas | Integração |
| `Rastro_Send_LeaseVencido_MesmoTraceMesmoPai` | O `FakeTransport` vence o lease por SQL durante a primeira tentativa; outro dispatcher reivindica | Dois `send` no mesmo trace, com o mesmo pai | Integração |
| `Rastro_Send_LoteMisto_NaoCruza` | Três mensagens de três raízes e uma sem `traceparent` no mesmo lote | Cada `send` no seu trace; o sem `traceparent` é raiz própria | Integração |
| `Rastro_Ciclo_SqlForaDosSpansDeMensagem` | Ouvinte também da fonte do Npgsql, no trace de uma mensagem | Nenhum comando do `FinishAsync` tem um `send` como ancestral; o `Current` é nulo depois do ciclo | Integração |
| `Rastro_Send_SemOuvinte_CabecalhoIntacto` | Nenhum ouvinte da fonte `Waybill` | Nenhum span; o transporte recebe o `traceparent` gravado | Integração |
| `Rastro_RabbitMq_SoFonteDoClienteOuvida_ConsumidorNoTrace` | Só o publisher do cliente ouvido, amostrado pelo nome `publish {routing key única do teste}`; dispatcher com `Current` nulo; broker real | O `traceparent` recebido pelo consumidor tem o `TraceId` do contexto gravado. Visto falhando antes da correção (o cliente cria uma raiz nova e a injeta) | Integração |
| `Rastro_RabbitMq_NinguemOuve_TraceparentDaRequisicao` | Nenhuma fonte ouvida; broker real | O consumidor recebe o `traceparent` gravado | Integração |
| `Rastro_RabbitMq_DuasFontes_ClienteDentroDoSend` | `Waybill` e o publisher do cliente ouvidos | O span do cliente é filho do `send`; o consumidor recebe o contexto do span do cliente | Integração |
| `Rastro_RabbitMq_Isolamento406_EventoNoSend` | O cenário de `G2_MensagemAcimaDoMaxMessageSizeDoBroker_IsoladaUmAUm`, com o publisher do cliente ouvido | Um `send` por mensagem; a culpada com `dlq`; as inocentes `published`, com o evento `waybill.isolated`; os spans do cliente no isolamento são filhos do `send` da mensagem | Integração |
| `Rastro_PontaAPonta_RequisicaoAteHandler` | Raiz no papel da requisição; `Enqueue` e `SaveChanges`; dispatcher com `Current` nulo; consumidor com `ProcessInOrderAsync`; as três fontes ouvidas | Um só `TraceId` em `create`, `send`, `publish` e `deliver` do cliente e `process`, nessa cadeia de pais | Integração |
| `Rastro_Process_Resultados` (teoria) | `processed`, `duplicate`, `regressed` | O atributo de resultado; `regressed` com erro e `error.type` | Integração |
| `Rastro_Process_HandlerLanca_Erro` | Handler lança | `process` com erro; a exceção chega ao chamador | Integração |
| `Rastro_Process_CommitAmbiguo_UmSpanComTentativas` | `LoseFirstCommitAcknowledgement` | Um `process`, dois eventos `waybill.attempt`, resultado `duplicate` | Integração |
| `Metrica_DlqEChavesBloqueadas_SobemEVoltamAZero` | Mensagem com chave à DLQ (defeito), depois liberada pelo SQL do `OPERATIONS.md`; e uma sem chave | `dlq` e `blocked_keys` vão a 1 e voltam a 0; sem chave, só `dlq`; com a ordenação desligada, `dlq` também | Integração |
| `Metrica_Regressoes_ServicoSemDispatcher` | Contêiner só com `AddWaybillInbox`, `MeterListener` no meter desse contêiner | Uma regressão conta 1, com handler e motivo | Integração |
| `Schema_IndiceDaDlq_UsadoNaContagem` | Milhares de linhas, poucas em `dlq`, `VACUUM ANALYZE`, `EXPLAIN` da contagem (como `G4_Ponteiro_PlanoGenerico_UsaOIndice`) | Usa `ix_outbox_dlq` | Integração |
| `Schema_DownSemOrdenacaoUsada_VoltaAV01EVolta` (ampliado) | `Down` e `Up` | `ix_outbox_dlq` sai e volta | Integração |
| `Nucleo_DependeSoDeSystemEMicrosoftExtensions` | Inalterado | Passa | Unit |

O exemplo: o `smoke.sh` confere que o dashboard responde e que Billing e Receipts sobem com o exportador ligado; o README do exemplo mostra a tela do trace de um pagamento.

## Mutações

Cada uma deve derrubar o teste indicado:

- **`Propagation` desligado:** `SoFonteDoClienteOuvida`.
- **`StartDetached` sem devolver o `Current`:** `SqlForaDosSpansDeMensagem`.
- **Pai `default` sem zerar o `Current`:** `LoteMisto` (a linha sem `traceparent` cai no trace de outra).
- **`Current` não posto no `PublishAloneAsync`:** `Isolamento406` (o span do cliente no isolamento sai do trace).
- **Pai = `Current` do dispatcher no lugar do contexto gravado:** `LeaseVencido` e `PontaAPonta`.
- **Desfecho só pelo resultado do transporte:** as linhas `fence perdido` e `MaxReturns esgotado` de `Desfechos`.
- **`SendActivity` parado aceito no `Current`:** a linha `timeout do lote` de `Desfechos`, com o publisher do cliente ouvido (o span do cliente sai do trace).
- **`create` antes da validação:** `EnqueueRecusado_SemSpan`.
- **Span de `process` dentro da estratégia:** `CommitAmbiguo`.
- **Contador do inbox registrado só no dispatcher:** `ServicoSemDispatcher`.

## Armadilhas

- **`StartActivity` mexe no `Current`, com pai explícito ou não.** Fora do fluxo da mensagem, sempre `StartDetached` e `StopDetached`.
- **Ouvinte é global.** Filtrar por `TraceId` no `Sample` e também nos callbacks; a raiz do teste não pode vir de fonte própria, porque o `Sample` dela vê um `TraceId` novo.
- **`StartActivity` devolve nulo sem ouvinte, e quando o `Sample` diz `None`.** Todo código que usa o span aceita nulo.
- **Não há `SetParentId(traceparent, tracestate)`.** `ActivityContext.TryParse` e `SetParentId(traceId, spanId, flags)`; `SetParentId(string)` com valor malformado gera um id fora do W3C.
- **`IMeterFactory` cria um meter por contêiner.** O `MeterListener` filtra pelo meter do contêiner do teste.
- **`EXPLAIN` em tabela pequena escolhe `Seq Scan`.** Carregar linhas e rodar `VACUUM ANALYZE` antes.
- **A imagem do dashboard é `mcr.microsoft.com/aspire/dashboard`,** não a antiga `dotnet/aspire-dashboard`; tag fixa.

## Fora do PLAN

O mesmo do SPEC. Um histograma de duração do `send` sai de graça dos spans, num coletor que derive métricas deles; não entra como métrica do Waybill.
