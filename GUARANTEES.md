# Guarantees

Waybill makes four promises; the fourth, order per aggregate key, holds only with ordering turned on. Each one holds under the conditions listed with it, and each condition Waybill can
test links to the test that proves it; the few it cannot test (database durability, the broker's consumer timeout) say so. A unit test ([`Garantias_CadaTesteCitadoExiste`](tests/Waybill.Tests.Unit/RastreabilidadeTests.cs))
fails the build when a test cited here is renamed, moved or deleted. Anything not written here is not promised.

The unit and integration tests run on every pull request against PostgreSQL 15 and 18, and the short
chaos tests against PostgreSQL 18, with .NET 10, EF Core 10, Npgsql 10 and RabbitMQ.Client 7. The tests marked as long (a one-hour broker outage, eight dispatchers for ten minutes, the ordering properties for ten
minutes) run on a schedule.

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
- **A publish that throws while the connection and the channel stay up.** Not a network failure, so a retry would
  fail the same way and block the queue behind it. If the channel goes down with the error, it is a transport
  failure instead.
  [`G2_PublicacaoQueFalhaComConexaoECanalDePe_EhDefeito_ComCanalFechado_EhRetry`](tests/Waybill.Tests.Unit/G2_PublicacaoQueFalhaComCanalDePeTests.cs)
- **An unroutable message (`basic.return`), after its own retry budget (`MaxReturns`), with a growing wait between
  returns (`ReturnBackoff`, capped by `MaxReturnBackoff`), so a binding created meanwhile still lets it through.**
  [`G2_Returned_EspacamentoCrescenteAteOTeto_DepoisDlq`](tests/Waybill.Tests.Integration/G2/G2_Returned_EspacamentoCrescenteAteOTeto.cs),
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

## G4. Messages of one aggregate key reach the broker first in commit order, and a consumer that verifies sequences never applies one out of order

With ordering by key turned on (`WaybillDispatcherOptions.OrderByKey`, off by default), the database numbers each
message enqueued with a key, in commit order, and the envelope carries that number as `sequence` (the
`waybill-sequence` header on RabbitMQ). RabbitMQ has no way to fence off a publisher that lost its claim, so the promise
on publication is about the first copy of each message, and the consumer catches what comes after.
[`G4_SequenceNoEnvelope_ConsumidorRecebeOHeaderWaybillSequence`](tests/Waybill.Tests.Integration/G4/G4_SequenceNoEnvelope.cs)

**On publication.** Per key, from the first message that has a `sequence`, the first copy of each message to reach the
broker arrives after the first copy of every earlier one. A later copy of a message, published again after its lease
ran out or after a publication whose outcome was unknown, may still arrive at any time, with the same `message_id`.
[`G4_Propriedade_PrimeiraEntregaEmOrdem_Curto`](tests/Waybill.Tests.Chaos/G4_Propriedade_PrimeiraEntregaEmOrdem.cs),
[`G4_Propriedade_PrimeiraEntregaEmOrdem_DezMinutos`](tests/Waybill.Tests.Chaos/G4_Propriedade_PrimeiraEntregaEmOrdem.cs)

Holds when:

- Messages of one key are enqueued with that key. The numbers follow the commit order, and a rollback leaves no gap;
  messages of one key in one transaction follow the order of `Enqueue`.
  [`G4_SequenciaContigua_LeitorConcorrenteSempreVePrefixoContiguo`](tests/Waybill.Tests.Integration/G4/G4_SequenciaContiguaNaOrdemDeCommit.cs),
  [`G4_SequenciaContigua_RollbackForcado_SeguinteFicaComONumero`](tests/Waybill.Tests.Integration/G4/G4_SequenciaContiguaNaOrdemDeCommit.cs),
  [`G4_MesmaChaveNaMesmaTransacao_SequenciaNaOrdemDoEnqueue_Rajada`](tests/Waybill.Tests.Integration/G4/G4_MesmaChaveNaMesmaTransacao_SequenciaNaOrdemDoEnqueue.cs)
- Every dispatcher on the database orders, with the same `Partitions` and `PartitionLease`. Each key belongs to one
  partition, held by one dispatcher at a time, and the claim checks that in the same statement. A dispatcher whose
  settings differ from the database's, whose ordering triggers are missing or disabled, whose session runs as a
  replica, or that does not order while the database does, stops with a critical log and claims nothing. Until it
  finds out, a dispatcher that does not order claims no keyed row.
  [`G4_EntradaESaidaSobCarga_NenhumaParticaoComDoisDonos_Curto`](tests/Waybill.Tests.Chaos/G4_EntradaESaidaSobCarga_NenhumaParticaoComDoisDonos.cs),
  [`G4_EntradaESaidaSobCarga_NenhumaParticaoComDoisDonos_DezMinutos`](tests/Waybill.Tests.Chaos/G4_EntradaESaidaSobCarga_NenhumaParticaoComDoisDonos.cs),
  [`G4_DonoAntigoDaParticao_NaoReivindica_NemComLeaseDaParticaoCurto`](tests/Waybill.Tests.Integration/G4/G4_DonoAntigoDaParticao_NaoReivindica.cs),
  [`G4_PDiferenteDoBanco_ErroCriticoENaoReivindica_DizOQueMudar`](tests/Waybill.Tests.Integration/G4/G4_PDiferenteDoBanco_ErroCriticoENaoReivindica.cs),
  [`G4_TriggerDesligado_ErroCritico`](tests/Waybill.Tests.Integration/G4/G4_TransicoesDaOrdenacao.cs),
  [`G4_SessaoReplica_ErroCritico`](tests/Waybill.Tests.Integration/G4/G4_TransicoesDaOrdenacao.cs),
  [`G4_OrdenacaoDesligadaComConfiguracaoNoBanco_Para_NoStartup`](tests/Waybill.Tests.Integration/G4/G4_OrdenacaoDesligadaComConfiguracaoNoBanco_Para.cs),
  [`G4_DispatcherSemOrdenacaoAindaVivo_NaoReivindicaLinhaComChave`](tests/Waybill.Tests.Integration/G4/G4_ClaimOrdenado.cs)
- A key's next message is claimed only after the one before it is finished. When a partition changes owner with
  messages in flight, the key waits for their lease; a late mark or hand-back from the old owner does not let the next
  message through.
  [`G4_CabecaPorChave_UmaPorChavePorLote_EmOrdem`](tests/Waybill.Tests.Integration/G4/G4_ClaimOrdenado.cs),
  [`G4_TrocaDeDonoComLinhasEmVoo_ChaveEsperaOLeaseDasLinhas`](tests/Waybill.Tests.Integration/G4/G4_TransicoesDaOrdenacao.cs),
  [`G4_TrocaDeDono_ZumbiComFenceNaSucessora_OrdemEChaveAndando`](tests/Waybill.Tests.Integration/G4/G4_TrocaDeDono_ZumbiComFenceNaSucessora.cs),
  [`G4_MarcacaoOuDevolucaoConcorrente_ClaimNaoLevaASeguinte`](tests/Waybill.Tests.Integration/G4/G4_ClaimOrdenado.cs)
- With `MaxPerKey` above 1, a batch publishes a key's messages in rounds: each call to the transport carries at most
  one message of a key, and the next one goes only after the broker confirmed the one before. A message to be
  published again ends the batch.
  [`G4_RodadasSeguemSequenceNaoId`](tests/Waybill.Tests.Integration/G4/G4_Rodadas_Dispatcher.cs),
  [`G4_RodadaComRetornoDeN_NaoEnviaASeguinte`](tests/Waybill.Tests.Integration/G4/G4_Rodadas_Dispatcher.cs),
  [`G4_RetryNaRodada_EncerraOLote`](tests/Waybill.Tests.Integration/G4/G4_Rodadas_Dispatcher.cs),
  [`G4_ShutdownEntreRodadas_RodadaUmMarcadaSemRepublicar`](tests/Waybill.Tests.Integration/G4/G4_Rodadas_Shutdown.cs)
- A message in the outbox DLQ, or waiting after a `basic.return`, stops its key and only its key. A dead-lettered
  message holds the key until it is requeued or released with the SQL in OPERATIONS.md.
  [`G4_MensagemNaDlq_SoAquelaChaveParaEMetricaSobe`](tests/Waybill.Tests.Integration/G4/G4_ChaveNaDlq.cs),
  [`G4_CabecaEmEspera_NadaDaChaveSai`](tests/Waybill.Tests.Integration/G4/G4_Rodadas_Serie.cs),
  [`Operacao_SqlDoOperationsLiberaAChave_EElaVoltaAFluirEmOrdem`](tests/Waybill.Tests.Integration/Operacao_LiberarChave.cs)

Messages written before ordering was turned on, or by a transaction still open when it was, have no `sequence` and are
not ordered.
[`G4_OrdenacaoLigadaComBacklog_OrdemValeAPartirDaPrimeiraSequence`](tests/Waybill.Tests.Integration/G4/G4_TransicoesDaOrdenacao.cs),
[`G4_LigarComTransacaoAberta_LinhaSemSequenceNaoOrdenada`](tests/Waybill.Tests.Integration/G4/G4_TransicoesDaOrdenacao.cs)

**On the consumer.** `IInbox<TContext>.ProcessInOrderAsync`, given the message's key and sequence (`GetWaybillOrder()`
on RabbitMQ), never applies, for a handler and a key, a message whose sequence is not after the last one that handler
applied. It deduplicates first: a message already applied returns `Duplicate`, also when it arrives after the next one.
A regression returns `Regressed`, with nothing applied, nothing recorded and a warning with the reason, and your
consumer sends it to the dead-letter exchange.
[`G4_Propriedade_RegressaoNaDlx_Curto`](tests/Waybill.Tests.Chaos/G4_Propriedade_RegressaoNaDlx.cs),
[`G4_Propriedade_RegressaoNaDlx_DezMinutos`](tests/Waybill.Tests.Chaos/G4_Propriedade_RegressaoNaDlx.cs),
[`G4_Inbox_EmOrdem_TresProcessadasEAChaveNaUltima`](tests/Waybill.Tests.Integration/G4/G4_Inbox_EmOrdem_AplicaEAvancaAChave.cs),
[`G4_Inbox_ReentregaDaPrimeiraDepoisDaSegunda_EhDuplicata`](tests/Waybill.Tests.Integration/G4/G4_Inbox_ReentregaDepoisDaSeguinte_EhDuplicata.cs),
[`G4_Inbox_OutraMensagemComAUltimaSequencia_EhRegressaoReusada`](tests/Waybill.Tests.Integration/G4/G4_Inbox_SequenciaReusada_EhRegressao.cs)

Holds when:

- The handler runs inside `ProcessInOrderAsync`, under the conditions of G3. The last sequence moves in the same
  transaction as the effect: a failing handler moves nothing, and a delivery whose commit outcome was unknown comes
  back as a duplicate.
  [`G4_Inbox_HandlerLanca_ChaveNaoAndaEReentregaAplica`](tests/Waybill.Tests.Integration/G4/G4_Inbox_HandlerFalha_NadaGravado_ReentregaProcessa.cs),
  [`G4_Inbox_CommitAmbiguo_DuplicataEChaveNaMensagem`](tests/Waybill.Tests.Integration/G4/G4_Inbox_CommitAmbiguo_ChaveAvancaUmaVez.cs)
- Each handler keeps its own last sequence per key, and a second delivery of a key that arrives while the first is
  running waits for it and compares against what it recorded.
  [`G4_Inbox_DoisHandlers_UmEm5NaoBarraA3DoOutro`](tests/Waybill.Tests.Integration/G4/G4_Inbox_DoisHandlers_SequenciasIndependentes.cs),
  [`G4_Inbox_SegundaEsperaAPrimeiraEComparaComOQueElaGravou`](tests/Waybill.Tests.Integration/G4/G4_Inbox_ConcorrentesDaMesmaChave_NuncaForaDeOrdem.cs)
- Retention never deletes the last sequence of a key, so a late copy whose inbox row is gone is still refused.
  [`G4_Inbox_CopiaTardiaDepoisDaRetencao_RegressaoSemEfeitoNemRegistro`](tests/Waybill.Tests.Integration/G4/G4_Inbox_RegressaoDepoisDaRetencao_NaoAplica.cs),
  [`G4_RabbitMq_CopiaTardiaDepoisDaRetencao_VaiParaAFilaDeMortas`](tests/Waybill.Tests.Integration/G2/RabbitMq/G4_RabbitMq_RegressaoChegaADlx.cs),
  [`G4_Retencao_ApagaPublicadaEInboxAntigosEMantemContadoresLiberadasEInboxKeys`](tests/Waybill.Tests.Integration/G4/G4_Retencao_NaoApagaOutboxKeysNemReleased.cs)
- The queue has one active consumer taking one delivery at a time (single active consumer on RabbitMQ), which is
  broker configuration Waybill cannot test, and dead-letters at least once: with the dead-letter queue full, a refused
  regression stays held in the source queue instead of being dropped. On the sample's quorum queue, a delivery nacked
  with requeue comes back before the next one (broker behavior, measured).
  [`G4_RabbitMq_FilaDeMortasCheia_RegressaoFicaRetidaNaOrigem`](tests/Waybill.Tests.Integration/G2/RabbitMq/G4_RabbitMq_RegressaoChegaADlx.cs),
  [`G4_RabbitMq_NackComRequeue_VoltaAntesDaSeguinteSemContarEntrega`](tests/Waybill.Tests.Integration/G2/RabbitMq/G4_RabbitMq_RequeueDepoisDeFalha.cs)

A gap is not an error: a consumer subscribed to only some of a key's message types processes the ones it gets.
[`G4_Inbox_AssinaturaParcial_SequenciasComGapSaoProcessadas`](tests/Waybill.Tests.Integration/G4/G4_Inbox_AssinaturaParcial_GapNaoPrende.cs)

Not covered:

- A message released from the outbox DLQ is outside the sequence. It leaves a gap, and a copy from an earlier attempt
  left unconfirmed may still arrive, as a regression.
  [`G4_Inbox_CopiaDaLiberadaDepoisDaSeguinte_EhRegressao`](tests/Waybill.Tests.Integration/G4/G4_Inbox_LiberadaChegaTarde_EhRegressao.cs)
- A message without a `sequence` (no key, or written or published with ordering off) is only deduplicated.
  [`G4_Inbox_SemOrdem_ProcessaEDeduplicaSemTocarInboxKeys`](tests/Waybill.Tests.Integration/G4/G4_Inbox_SemOrdem_SoDeduplicacao.cs)
- Deadlocks between producers. A key is locked from its INSERT to the commit. The keys of one `SaveChanges` are locked
  in one order when the context is registered with `AddDbContext`, `AddDbContextPool` or a context factory, so two
  such transactions writing them in opposite orders do not deadlock. Without that, or with a key enqueued before an
  application write in the same transaction, opposite orders can deadlock, and the transaction has to be retried
  (OPERATIONS.md). Nothing is published out of order either way.
  [`G4_ListaDeChaves_OrdensOpostas_SemDeadlock`](tests/Waybill.Tests.Integration/G4/G4_ListaDeChaves_SemDeadlock.cs),
  [`G4_ListaDeChaves_OrdensOpostasSemALista_Deadlock`](tests/Waybill.Tests.Integration/G4/G4_ListaDeChaves_SemDeadlock.cs)
- An application transaction at `REPEATABLE READ` or `SERIALIZABLE` that enqueues to a key another transaction is
  writing can fail with a serialization error (40001); running it again succeeds.
  [`G4_AplicacaoEmRRouSerializable_ChaveIncrementadaDepoisDoSnapshot`](tests/Waybill.Tests.Integration/G4/G4_AplicacaoEmRRouSerializable_Erro40001ERepetirResolve.cs)

## What Waybill does not promise

| Not promised | Why |
| --- | --- |
| Delivering each message once | Publication is at least once. Only the effect written together with the inbox happens once |
| Strict order end to end on RabbitMQ | Without a way to fence off a publisher, a late copy from a dispatcher whose lease ran out can arrive after the next message. The consumer detects it and sends it to the dead-letter exchange; the broker cannot prevent it |
| Reordering on the consumer | A consumer subscribed to some message types sees legitimate gaps, and holding the next message back would wait forever. The inbox detects a regression; it never reorders |
| Order with ordering off, or across aggregates | Ordering is off by default, and then messages are published in parallel batches. Order across aggregates would take serializing commits, publication and consumption |
| Getting back the message of a released key | For consumers that verify sequences it is already a regression; releasing the key accepts that |
| External side effects once (e-mail, a call to a payment gateway) | The inbox protects the consumer's database, not the calls that leave it |
| Fixed latency | It depends on the polling interval and on the backlog |
| Deduplication forever | The inbox remembers messages only within `InboxRetention`. The last sequence per key, by contrast, is never deleted |
| Atomicity with anything outside the `DbContext`'s database | A write to Redis, another database or a file is still a dual write |
| An event that matches its data after a PostgreSQL failover with asynchronous replication | The failover can lose a commit whose event was already published. Synchronous replication closes this gap where the business requires it |

## Operating over time

Not a guarantee, but a condition of use that a test checks. Under constant load, with the autovacuum settings
OPERATIONS.md recommends, a transaction held open for hours elsewhere in the database lets dead rows pile up and slows
the claim. Once it closes, autovacuum runs and the claim index is rebuilt as OPERATIONS.md describes, the claim latency
comes back within twice the baseline (or the baseline plus 5 ms) and the outbox stops growing. The scenario runs for
5 hours in a scheduled CI job.
[`CargaLonga_DepoisQueATransacaoLongaFecha_LatenciaDoClaimVoltaETamanhoParaDeCrescer`](tests/Waybill.Tests.Integration/CargaLonga/CargaLonga_TransacaoLongaAberta_LatenciaDoClaimEstabiliza.cs)

With ordering on, a key stopped by the DLQ does not hold back the other keys, also with 100 thousand messages queued
behind it, and the claim reads a bounded number of buffers past it; releasing it with the SQL in OPERATIONS.md lets
it flow again, in order. What ordering costs, and the claim latency with a large backlog, are measured and published
in OPERATIONS.md, with no pass mark on time.
[`G4_ChaveBloqueadaComCemMilAFrente_OutrasChavesFluem_CustoMedido`](tests/Waybill.Tests.Integration/G4/G4_ChaveBloqueadaComCemMilAFrente_ClaimContinuaFluindo.cs),
[`Operacao_SqlDoOperationsLiberaAChave_EElaVoltaAFluirEmOrdem`](tests/Waybill.Tests.Integration/Operacao_LiberarChave.cs)

How to run Waybill over time (retention, autovacuum, the claim index, what to monitor) is in
[OPERATIONS.md](docs/OPERATIONS.md).
