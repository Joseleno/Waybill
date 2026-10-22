# Etapa 11 — Tarefas

Um commit por cenário provado, na ordem abaixo. Os nomes dos testes estão no `PLAN.md`. As tarefas 1 a 6 são o piso do corte parcial (decisão do autor): se a etapa apertar, ela fecha nelas, com o "pronto quando" do piso no SPEC.

- [x] SPEC revisto por revisão adversarial; decisões do autor (corte com piso, chave e tenant por opção, dashboard do Aspire) em Oct 21, 2026
- [x] PLAN revisto por revisão adversarial e por um time de quatro revisores (diagnóstico .NET e OpenTelemetry, RabbitMQ.Client, PostgreSQL e EF, testes e API), com experimentos
- [x] PLAN aprovado pelo autor (Oct 21, 2026)
- [x] 1. Testes de integração: `TraceRecorder` (fontes nomeadas, raiz gravada, `Sample` como ParentBased, aceite por `TraceId`, `message_id` ou nome de raiz, `None` para o resto, callbacks filtrados). O dispatcher roda depois de parar a raiz, sem `Current`, conferido no teste
- [x] 2. Propagação: `StartPropagation` em W3C dentro do `PublishOneAsync`, só com a fonte de publisher do cliente ouvida; `Rastro_RabbitMq_SoFonteDoClienteOuvida_ConsumidorNoTrace`, visto falhando antes (três mensagens, três traces novos: o defeito da v0.1), e `Rastro_RabbitMq_NinguemOuve_TraceparentDaRequisicao`
- [x] 3. Schema: `ix_outbox_dlq` na `SchemaV0_2`, com `HasIndex` com nome no modelo, no Designer e no snapshot, `DROP INDEX IF EXISTS` no `Down`, baseline do custo atualizado; `Schema_ModeloSemMudancaPendente`, `Schema_IndiceDaDlq_UsadoNaContagem`, os `Schema_*` ampliados
- [x] 4. Métricas no núcleo (`WaybillDiagnostics.MeterName` e `CreateMeter`); gauge `waybill.outbox.dlq` com sentinela, o sampler num `SELECT` só, `GaugeReader` genérico; `Metrica_DlqEChavesBloqueadas_SobemEVoltamAZero`; comentário de `Metrica_SemPendentes_Zero`
- [x] 5. `InboxMetrics` com `TryAddSingleton` no `AddWaybillInbox`, contador de regressões; `Metrica_Regressoes_ServicoSemDispatcher`
- [x] 6. Documentos do piso: CHANGELOG (o defeito da v0.1, as métricas, o índice e a recriação dos bancos de desenvolvimento), `OPERATIONS.md` (as métricas e a agregação por máximo; a propagação corrigida), ADR 0011 com a propagação; se a etapa parar aqui, a tabela de funcionalidades do escopo no mesmo PR
- [x] 7. Núcleo: `WaybillDiagnostics.ActivitySourceName`, `WaybillActivity` com `StartIsolated` e `StopIsolated`, API pública declarada; `TraceRecorder` no projeto de testes unitários; `create` no `EnvelopeFactory` e a opção `RecordKeyAndTenantInTraces`; os `Rastro_Create_*`, `Rastro_ChaveETenant_SoComAOpcao` (parte do `create`), `FakeOutboxEquivalenceTests` ampliado
- [x] 8. `send` no dispatcher: `ExecuteAsync` zerando o `Current`; abertura na rodada com nome e atributos do transporte; `SendActivity` interno; troca do `traceparent` no dicionário; `FinishResult` com os ids aplicados e `Fenced` derivado; desfecho mapeado; fim no `try/finally` interno; os `Rastro_Send_*`, `Rastro_Ciclo_SqlForaDosSpansDeMensagem`, `Rastro_SemSpanDeCiclo`
- [x] 9. Transporte: `InternalsVisibleTo` do núcleo para `Waybill.RabbitMQ`; `SendActivity` no `Current`, evento de isolamento, destino pela regra das convenções; `Rastro_RabbitMq_DuasFontes_ClienteDentroDoSend`, `Rastro_RabbitMq_Isolamento406_EventoNoSend`
- [x] 10. `process` no inbox; os `Rastro_Process_*` e a parte do `process` de `Rastro_ChaveETenant_SoComAOpcao`
- [x] 11. `Rastro_PontaAPonta_RequisicaoAteHandler`
- [x] 12. Mutações do PLAN, registradas nos commits. Todas mortas:
  - propagação desligada;
  - salvar e restaurar o `Current` no lugar do `StartIsolated` (o teste unitário do `Current` parado; o de SQL não a pega, ver PLAN);
  - `StartIsolated` sem zerar o `Current`;
  - pai do `send` vindo do dispatcher;
  - `TryParse` sem `isRemote`;
  - desfecho sem o que o banco aplicou (fence e `MaxReturns`);
  - spans fechados fora do `try` interno;
  - evento de isolamento omitido;
  - `SendActivity` sem virar `Current`;
  - `create` antes da validação;
  - `create` sem W3C;
  - `process` dentro da estratégia;
  - `InboxMetrics` só com o dispatcher, e com `AddSingleton`;
  - índice sem nome.
- [x] 13. Exemplo: pacotes auditados (OpenTelemetry 1.19.x, Apache-2.0, sem CVE, só nos samples), fontes e meter, dashboard do Aspire 13.6.1 no compose, `smoke.sh` confere o dashboard, README do exemplo descreve o trace em texto. O compose não subiu localmente (memória da máquina); o job do exemplo no CI o roda
- [x] 14. Documentos dos spans: `OPERATIONS.md` (rastro, fontes, amostragem, `error.type`, consumidor à mão), XML doc do `ITransport`, README, CHANGELOG, a linha do escopo citando `Rastro_PontaAPonta`, ADR 0011 completo
- [ ] 15. Revisão de código independente antes da PR
