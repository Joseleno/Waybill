# Guarantees

Waybill 0.1 makes three promises. Each one holds under the conditions listed with it, and each condition Waybill can
test links to the test that proves it; the few it cannot test (database durability, the broker's consumer timeout) say so. A unit test ([`Garantias_CadaTesteCitadoExiste`](tests/Waybill.Tests.Unit/RastreabilidadeTests.cs))
fails the build when a test cited here is renamed, moved or deleted. Anything not written here is not promised.

The unit and integration tests run on every pull request against PostgreSQL 15 and 18, and the short
chaos tests against PostgreSQL 18, with .NET 10, EF Core 10, Npgsql 10 and RabbitMQ.Client 7. The tests marked as long (a one-hour broker outage, eight dispatchers for ten minutes) run on a
schedule.

## G1. An event exists if, and only if, the transaction that enqueued it commits

Holds when:

- The event is enqueued with `IOutbox<TContext>.Enqueue` before `SaveChanges`, on the same `DbContext` and in the
  same transaction as the data: an explicit `Database.BeginTransaction` or the transaction `SaveChanges` creates.
  A rollback leaves no event; a commit leaves one.
  [`G1_Rollback_TabelaVazia_ECommit_UmEvento`](tests/Waybill.Tests.Integration/G1/G1_Rollback_TabelaVazia.cs),
  [`G1_EnqueueDuranteSavingChanges_EntraNoMesmoSave`](tests/Waybill.Tests.Integration/G1/G1_UnidadeDeTrabalhoDoEf_FonteDeVerdade.cs)
- Several `DbContext` instances take part only when they share the connection and the transaction.
  [`G1_DoisDbContextComTransacaoCompartilhada_MesmoCommit_OuMesmoRollback`](tests/Waybill.Tests.Integration/G1/G1_DoisDbContextComTransacaoCompartilhada_MesmoCommit.cs)
- With a retrying execution strategy, the transaction runs inside `CreateExecutionStrategy().ExecuteAsync`. The
  message id is fixed when the event is enqueued, so re-running a commit that had already succeeded fails on a
  duplicate key instead of creating a second event, and a rolled-back attempt inserts the event again on the retry.
  [`G1_RetryDuranteCommit_NaoDuplica_UmEventoESegundaTentativaFalhaPorChave`](tests/Waybill.Tests.Integration/G1/G1_RetryDuranteCommit_NaoDuplica.cs),
  [`G1_SaveChangesSemAceitarRepetidoPelaEstrategia_ReinsereOEvento`](tests/Waybill.Tests.Integration/G1/G1_SaveChangesRepetidoNaMesmaTransacao_UmaLinhaPorEvento.cs)
- `SaveChanges` repeated in the same transaction writes each event once.
  [`G1_SaveChangesRepetidoNaMesmaTransacao_UmaLinhaPorEvento_AposFalhaECorrecao`](tests/Waybill.Tests.Integration/G1/G1_SaveChangesRepetidoNaMesmaTransacao_UmaLinhaPorEvento.cs)
- The EF Core unit of work is the only source of truth: `ChangeTracker.Clear()` discards a pending event together
  with the data, and a pooled context does not carry it into the next scope.
  [`G1_ChangeTrackerClearAposFalha_SemEventoFantasma`](tests/Waybill.Tests.Integration/G1/G1_UnidadeDeTrabalhoDoEf_FonteDeVerdade.cs),
  [`G1_ContextoDoPool_PendenteNaoVazaParaOProximoEscopo`](tests/Waybill.Tests.Integration/G1/G1_UnidadeDeTrabalhoDoEf_FonteDeVerdade.cs)
- The commit is durable. This is database configuration that Waybill cannot test: `synchronous_commit` at `on`
  or above and, with replicas, `synchronous_standby_names` set (`remote_apply` if consumers read from a replica).

`Enqueue` refuses the setups where the event could commit apart from the data:
[`Configuracao_TransactionScope_FalhaNoEnqueue`](tests/Waybill.Tests.Integration/SchemaTests.cs),
[`Configuracao_AutoTransactionNeverSemTransacao_FalhaNoEnqueue`](tests/Waybill.Tests.Integration/SchemaTests.cs).

An event that is enqueued and never saved is reported as an error log when the DI scope ends, or as an exception
when that option is on. An exception thrown by your code before `SaveChanges` reaches the caller unchanged. A
`DbContext` created outside DI is not covered.
[`G1_DescarteComEventoPendente_LogaErro_SemExcecao`](tests/Waybill.Tests.Integration/G1/G1_DescarteComEventoPendente_LogaErro.cs),
[`G1_DescarteComEventoPendente_ComOpcaoLigada_Lanca`](tests/Waybill.Tests.Integration/G1/G1_DescarteComEventoPendente_LogaErro.cs),
[`G1_DescarteDepoisDeSalvar_NaoReporta`](tests/Waybill.Tests.Integration/G1/G1_DescarteComEventoPendente_LogaErro.cs),
[`G1_HandlerLancaAntesDoSaveChanges_ExcecaoDoHandler_ChegaAoChamador`](tests/Waybill.Tests.Integration/G1/G1_HandlerLancaAntesDoSaveChanges_ExcecaoDoHandler.cs)

Not covered: writes that bypass the change tracker, such as `ExecuteUpdate` and raw SQL, produce no events.
[`G1_EscritaForaDoEf_NaoGeraEvento_ExecuteUpdateESqlCru`](tests/Waybill.Tests.Integration/G1/G1_EscritaForaDoEf_NaoGeraEvento.cs)

## G2. Every persisted event is published at least once, or goes to the outbox DLQ with a recorded reason; none is dropped silently

Holds when the broker confirms what it receives. The RabbitMQ transport always publishes with publisher confirms,
`mandatory` and persistent delivery. Durable or quorum queues are part of your topology, and Waybill does not create
topology.
[`G2_RabbitMq_PublicaComConfirmacaoEPropriedades_NaExchangeComONomeComoRoutingKey`](tests/Waybill.Tests.Integration/G2/RabbitMq/G2_RabbitMq_PublicaComConfirmacaoEPropriedades.cs),
[`G2_RabbitMq_LoteAcimaDoLimiteDeConfirmacoesPendentes_TodasConfirmadas`](tests/Waybill.Tests.Integration/G2/RabbitMq/G2_RabbitMq_PublicaComConfirmacaoEPropriedades.cs),
[`G2_RabbitMq_PontaAPonta_ComOHost_EventoGravadoComODadoChegaAFila`](tests/Waybill.Tests.Integration/G2/RabbitMq/G2_RabbitMq_PontaAPonta_ComOHost.cs),
[`G2_DispatcherPublicaEMarca_EmOrdemDeIdComEnvelopeCompleto`](tests/Waybill.Tests.Integration/G2/G2_DispatcherPublicaEMarca.cs)

The dispatcher's sessions run at `READ COMMITTED`, PostgreSQL's default. If the database or the role sets another
`default_transaction_isolation`, the dispatcher and the retention stop before their first cycle with a critical log
that says what to change, instead of running a claim whose concurrency rules no longer hold.
[`Configuracao_IsolamentoDiferenteDeReadCommitted_DispatcherParaComErroCritico`](tests/Waybill.Tests.Integration/Configuracao_IsolamentoTests.cs),
[`Configuracao_IsolamentoDiferenteDeReadCommitted_RetencaoParaComErroCritico`](tests/Waybill.Tests.Integration/Configuracao_IsolamentoTests.cs)

**Refused before anything is written.** `Enqueue` throws, and nothing is persisted, for:
- an unregistered message type;
- a message that fails to serialize;
- a payload above `MaxPayloadBytes`;
- a name, correlation id, key or tenant id above 255 bytes.

[`Envelope_TipoNaoRegistrado_FalhaNoEnqueue`](tests/Waybill.Tests.Unit/EnvelopeTests.cs),
[`Envelope_FalhaDeSerializacao_FalhaNoEnqueue`](tests/Waybill.Tests.Unit/EnvelopeTests.cs),
[`Envelope_PayloadAcimaDoLimite_FalhaNoEnqueue`](tests/Waybill.Tests.Unit/EnvelopeTests.cs),
[`Envelope_CorrelacaoAcimaDe255Bytes_FalhaNoEnqueue`](tests/Waybill.Tests.Unit/EnvelopeTests.cs),
[`Envelope_ChaveOuTenantAcimaDe255Bytes_FalhaNoEnqueue`](tests/Waybill.Tests.Unit/EnvelopeTests.cs),
[`Registro_NomeAcimaDe255Bytes_Falha`](tests/Waybill.Tests.Unit/EnvelopeTests.cs)

**The only ways into the DLQ.** Each one is a defect of the message, and the reason is recorded with it:
- **A row written under a larger size limit than the current one.**
  [`G2_PayloadAcimaDoLimiteAtual_SoElaVaiParaDlq_NumLoteDeCem`](tests/Waybill.Tests.Integration/G2/G2_PayloadAcimaDoLimiteAtual_SoElaVaiParaDlq.cs)
- **A message the broker refuses when it is published alone** (a 406 that closes the channel, such as a message
  above the broker's `max_message_size`). It is isolated one message at a time on a fresh channel, so the
  rest of the batch is published.
  [`G2_MensagemAcimaDoMaxMessageSizeDoBroker_IsoladaUmAUm_SoElaVaiParaDlq`](tests/Waybill.Tests.Integration/G2/RabbitMq/G2_MensagemAcimaDoMaxMessageSizeDoBroker_IsoladaUmAUm.cs)
- **A message that cannot be expressed in AMQP.**
  [`G2_RabbitMq_MensagemInexprimivelEmAmqp_VaiParaDlqEORestoPublica`](tests/Waybill.Tests.Integration/G2/RabbitMq/G2_RabbitMq_MensagemInexprimivelEmAmqp_VaiParaDlq.cs)
- **An unroutable message (`basic.return`), after its own retry budget (`MaxReturns`).**
  [`G2_Returned_OrcamentoProprioDepoisDlq_ComMotivo`](tests/Waybill.Tests.Integration/G2/G2_Returned_OrcamentoProprioDepoisDlq.cs),
  [`G2_RabbitMq_SemRota_ReturnedAteDlq_ComNoRoute`](tests/Waybill.Tests.Integration/G2/RabbitMq/G2_RabbitMq_SemRota_ReturnedAteDlq.cs)

**Transport failures never reach the DLQ and never spend an attempt.**
- **A lost connection or channel** hands the claim back and opens a circuit breaker. While the breaker is open,
  nothing is claimed. A missing exchange is retried too, without spending an attempt, until it exists.
  [`G2_FalhaDeTransporte_ReabreSemGastarTentativa_BreakerSondaEDepoisPublica`](tests/Waybill.Tests.Integration/G2/G2_FalhaDeTransporte_ReabreSemGastarTentativa.cs),
  [`G2_ConexaoDerrubadaNoMeioDaPublicacao_NadaSePerde`](tests/Waybill.Tests.Chaos/Broker/ChaosBrokerTests.cs),
  [`G2_RabbitMq_ExchangeInexistente_RetrySemGastarTentativa_DepoisPublica`](tests/Waybill.Tests.Integration/G2/RabbitMq/G2_RabbitMq_ExchangeInexistente_RetrySemGastarTentativa.cs),
  [`G2_SondaSoComDefeitoLocal_NaoFechaOBreaker`](tests/Waybill.Tests.Integration/G2/G2_BreakerReageSoAoBroker.cs)
- **A stopped broker.** Nothing goes to the DLQ, the backlog drains by itself when the broker returns, and the
  backlog does not cause a burst of claims.
  [`G2_BrokerParado_NadaNaDlqEDrenaSozinho_Curto`](tests/Waybill.Tests.Chaos/Broker/ChaosBrokerTests.cs),
  [`G2_BrokerParado_NadaNaDlqEDrenaSozinho_UmaHora`](tests/Waybill.Tests.Chaos/Broker/ChaosBrokerTests.cs),
  [`G2_BrokerForaComBacklog_SemRajadaDeClaims_RecuaEDepoisDrena`](tests/Waybill.Tests.Integration/G2/G2_BrokerForaComBacklog_SemRajadaDeClaims.cs)
- **Confirmation timeouts and nacks** halve the batch without opening the breaker. Repeated timeouts on a batch of
  one count as a silent outage and do open it. A probe that times out reopens it for twice as long.
  [`G2_TimeoutDeConfirmacaoOuNack_ReduzLoteSemAbrirBreaker`](tests/Waybill.Tests.Integration/G2/G2_FalhaDeTransporte_ReabreSemGastarTentativa.cs),
  [`G2_LatenciaAlta_BreakerFechadoLoteReduzido`](tests/Waybill.Tests.Chaos/Broker/ChaosBrokerTests.cs),
  [`G2_QuedaSilenciosa_TimeoutsComLoteDe1AbremOBreaker`](tests/Waybill.Tests.Integration/G2/G2_BreakerReageSoAoBroker.cs),
  [`G2_QuedaSilenciosa_SondaQueEstouraOTimeout_ReabrePeloDobro`](tests/Waybill.Tests.Integration/G2/G2_BreakerReageSoAoBroker.cs),
  [`G2_BuracoNegro_TimeoutsAbremOBreakerSemDlq`](tests/Waybill.Tests.Chaos/Broker/ChaosBrokerTests.cs)
- **A publication whose outcome is unknown** after the timeout is handed back before the lease expires. A
  transport result Waybill does not recognize never counts as confirmed.
  [`G2_PublicacaoAlemDoTimeout_ResultadoDesconhecidoDevolve_AntesDoLease`](tests/Waybill.Tests.Integration/G2/G2_PublicacaoAlemDoTimeout_ResultadoDesconhecidoDevolve.cs),
  [`G2_ResultadoInvalidoDoTransporte_NuncaContaComoConfirmado`](tests/Waybill.Tests.Integration/G2/G2_FalhaDeTransporte_ReabreSemGastarTentativa.cs)

**Claims with lease and fencing.**
- **Concurrent dispatchers** never hold the same row with two valid leases.
  [`G2_OitoDispatchers_NenhumaLinhaComDoisLeasesValidos_Curto`](tests/Waybill.Tests.Chaos/G2_OitoDispatchers_NenhumaLinhaComDoisLeasesValidos.cs),
  [`G2_OitoDispatchers_NenhumaLinhaComDoisLeasesValidos_DezMinutos`](tests/Waybill.Tests.Chaos/G2_OitoDispatchers_NenhumaLinhaComDoisLeasesValidos.cs)
- **A killed process.** Its rows become claimable again after the lease, never before.
  [`G2_KillDuranteAPublicacao_VoltaAposLease_NuncaAntes`](tests/Waybill.Tests.Chaos/G2_KillDuranteAPublicacao_VoltaAposLease.cs)
- **A late mark or hand-back** from an older claim is fenced out.
  [`G2_MesmaInstanciaReivindicaDeNovo_FenceBarraMarcacaoEDevolucaoAntigas`](tests/Waybill.Tests.Integration/G2/G2_MesmaInstanciaReivindicaDeNovo_FenceBarraMarcacaoAntiga.cs)
- **A graceful shutdown** finishes the batch in flight and hands back only this instance's rows, as long as the
  host's `ShutdownTimeout` (30 s by default) outlasts `PublishTimeout` (20 s by default); see OPERATIONS.md.
  [`G2_ShutdownGracioso_TerminaOLoteEmVooENadaFicaReivindicado`](tests/Waybill.Tests.Integration/G2/G2_ShutdownGracioso_DevolveOQueNaoPublicou.cs),
  [`G2_ShutdownGracioso_DevolveSoAsLinhasDesteOwnerIntactas`](tests/Waybill.Tests.Integration/G2/G2_ShutdownGracioso_DevolveOQueNaoPublicou.cs)

**Retention never deletes pending, claimed or dead-lettered rows,** also while the broker is down for longer than
the retention.
[`Retencao_Outbox_ApagaSoPublicadasAlemDaRetencao_PendenteReivindicadaEDlqFicam`](tests/Waybill.Tests.Integration/Retencao/Retencao_Outbox_ApagaSoPublicadasAlemDaRetencao.cs),
[`G2_BrokerParadoAlemDaRetencao_SoAsJaPublicadasSomemEOBacklogPublicaQuandoVolta`](tests/Waybill.Tests.Integration/G2/G2_BrokerParadoAlemDaRetencao_NenhumaPendenteApagada.cs)

Publication is at least once: a consumer can receive the same message more than once. G3 is how its effect is
applied once.

## G3. The effect a consumer writes to its own database is applied once, even when the message is delivered more than once

Holds when:

- The handler runs inside `IInbox<TContext>.ProcessAsync` and writes through the context it receives, in the
  inbox's `READ COMMITTED` transaction. Writing through another instance of the context while the handler runs
  fails loudly, and a context that already has a transaction open is refused. Work the handler forks and that
  saves after `ProcessAsync` returns runs outside the inbox, and is not covered.
  [`G3_DbContextForaDaTransacao_FalhaExplicita_ENadaAplica`](tests/Waybill.Tests.Integration/G3/G3_DbContextForaDaTransacao_FalhaExplicita.cs),
  [`G3_ContextoComTransacaoAberta_Recusado`](tests/Waybill.Tests.Integration/G3/G3_DbContextForaDaTransacao_FalhaExplicita.cs),
  [`G3_TarefaDerivadaDoHandler_GravaDepoisSemSerRecusada`](tests/Waybill.Tests.Integration/G3/G3_DbContextForaDaTransacao_FalhaExplicita.cs)
- The key is `(handler, message_id)`: each named handler applies the effect once, independently of the others.
  [`G3_DoisHandlersDoMesmoEvento_OsDoisAplicam_CadaUmUmaVez`](tests/Waybill.Tests.Integration/G3/G3_DoisHandlersDoMesmoEvento_OsDoisAplicam.cs)
- The message id is the same on every delivery. Waybill publishes it as the AMQP `message_id`, and
  `GetWaybillMessageId()` reads it in a consumer written with plain RabbitMQ.Client.
  [`G3_ConsumidorRabbitMqSemFramework_EntregaDupla_AplicaUmaVezEFazAckDasDuas`](tests/Waybill.Tests.Integration/G2/RabbitMq/G3_ConsumidorRabbitMqSemFramework.cs)
- Your consumer acknowledges the message after `ProcessAsync` returns. A repeated delivery, whether sequential,
  concurrent or after a lost ack, returns `Duplicate` without running the handler. A concurrent delivery waits for
  the first transaction to finish, so the handler must fit within the broker's `consumer_timeout`.
  [`G3_EntregaDuplaEmSequencia_AplicaUmaVez_SegundaEhDuplicataSemChamarOHandler`](tests/Waybill.Tests.Integration/G3/G3_EntregaDuplaEmSequencia_AplicaUmaVez.cs),
  [`G3_EntregaParalela_AplicaUmaVez_SegundaEsperaASaiComoDuplicata`](tests/Waybill.Tests.Integration/G3/G3_EntregaParalela_AplicaUmaVez.cs),
  [`G3_FalhaDoAckDepoisDoCommit_ReentregaEhDuplicataESoFazAck`](tests/Waybill.Tests.Integration/G3/G3_FalhaDoAckDepoisDoCommit_ReentregaEhDuplicata.cs),
  [`G3_RetryDuranteCommit_AplicaUmaVez_RetryVoltaComoDuplicata`](tests/Waybill.Tests.Integration/G3/G3_RetryDuranteCommit_AplicaUmaVez.cs)
- A failing handler rolls back everything, including the inbox row and any events it enqueued, so the next delivery
  processes the message again.
  [`G3_FalhaNoHandlerAposInsert_RollbackCompletoEReentregaProcessa`](tests/Waybill.Tests.Integration/G3/G3_FalhaNoHandlerAposInsert_RollbackEReprocessa.cs),
  [`G3_HandlerEnfileiraEFalha_SemInboxNemOutbox_OuTudoNoMesmoCommit`](tests/Waybill.Tests.Integration/G3/G3_HandlerEnfileiraEFalha_SemInboxNemOutbox.cs)
- The repeated delivery arrives within `InboxRetention`. After retention deletes the inbox row, the same message is
  processed again. Replaying a queue or an offset older than the retention is not covered.
  [`Inbox_ReentregaDentroDaRetencaoEhDuplicata_DepoisDaLimpezaProcessaDeNovo`](tests/Waybill.Tests.Integration/Retencao/Inbox_ReentregaDepoisDaLimpeza_ProcessaDeNovo.cs)

## What Waybill does not promise

| Not promised | Why |
| --- | --- |
| Delivering each message once | Publication is at least once. Only the effect written together with the inbox happens once |
| Order | Version 0.1 publishes in parallel batches, with no ordering promise. Ordering by aggregate key is planned for 0.2 |
| External side effects once (e-mail, a call to a payment gateway) | The inbox protects the consumer's database, not the calls that leave it |
| Fixed latency | It depends on the polling interval and on the backlog |
| Deduplication forever | The inbox remembers messages only within `InboxRetention` |
| Atomicity with anything outside the `DbContext`'s database | A write to Redis, another database or a file is still a dual write |
| An event that matches its data after a PostgreSQL failover with asynchronous replication | The failover can lose a commit whose event was already published. Synchronous replication closes this gap where the business requires it |

How to run Waybill over time (retention, autovacuum, what to monitor) is in [OPERATIONS.md](docs/OPERATIONS.md).
