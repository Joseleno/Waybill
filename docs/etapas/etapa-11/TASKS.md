# Etapa 11 — Tarefas

Um commit por cenário provado, na ordem abaixo. Os nomes dos testes estão no `PLAN.md`. As tarefas 1 a 6 são o piso do corte parcial (decisão do autor): se a etapa apertar, ela fecha nelas, com o "pronto quando" do piso no SPEC.

- [x] SPEC revisto por revisão adversarial; decisões do autor (corte com piso, chave e tenant por opção, dashboard do Aspire) em Oct 21, 2026
- [x] PLAN revisto por revisão adversarial e por um time de quatro revisores (diagnóstico .NET e OpenTelemetry, RabbitMQ.Client, PostgreSQL e EF, testes e API), com experimentos
- [ ] PLAN aprovado pelo autor
- [ ] 1. Testes de integração: `TraceRecorder` (fontes nomeadas, raiz gravada, `Sample` como ParentBased, aceite por `TraceId`, `message_id` ou nome do publish, `None` para o resto, callbacks filtrados) e o helper que roda o dispatcher sem `Current`
- [ ] 2. Propagação: `InternalsVisibleTo` para `Waybill.RabbitMQ`; `Propagation` em W3C dentro do `PublishOneAsync`; `Rastro_RabbitMq_SoFonteDoClienteOuvida_ConsumidorNoTrace`, visto falhando antes (o defeito da v0.1), e `Rastro_RabbitMq_NinguemOuve_TraceparentDaRequisicao`
- [ ] 3. Schema: `ix_outbox_dlq` na `SchemaV0_2`, com `HasIndex` com nome no modelo, no Designer e no snapshot, `DROP INDEX IF EXISTS` no `Down`, baseline do custo atualizado; `Schema_ModeloSemMudancaPendente`, `Schema_IndiceDaDlq_UsadoNaContagem`, os `Schema_*` ampliados
- [ ] 4. Métricas no núcleo (`WaybillDiagnostics.MeterName` e `CreateMeter`); gauge `waybill.outbox.dlq` com sentinela, o sampler num `SELECT` só, `GaugeReader` genérico; `Metrica_DlqEChavesBloqueadas_SobemEVoltamAZero`; comentário de `Metrica_SemPendentes_Zero`
- [ ] 5. `InboxMetrics` com `TryAddSingleton` no `AddWaybillInbox`, contador de regressões; `Metrica_Regressoes_ServicoSemDispatcher`
- [ ] 6. Documentos do piso: CHANGELOG (o defeito da v0.1, as métricas, o índice e a recriação dos bancos de desenvolvimento), `OPERATIONS.md` (as métricas e a agregação por máximo; a propagação corrigida), ADR 0011 com a propagação; se a etapa parar aqui, a tabela de funcionalidades do escopo no mesmo PR
- [ ] 7. Núcleo: `WaybillDiagnostics.ActivitySourceName`, `WaybillActivity` com `StartIsolated` e `StopIsolated`, API pública declarada; `TraceRecorder` no projeto de testes unitários; `create` no `EnvelopeFactory` e a opção `RecordKeyAndTenantInTraces`; os `Rastro_Create_*`, `Rastro_ChaveETenant_SoComAOpcao` (parte do `create`), `FakeOutboxEquivalenceTests` ampliado
- [ ] 8. `send` no dispatcher: `ExecuteAsync` zerando o `Current`; abertura na rodada com nome e atributos do transporte; `SendActivity` interno; troca do `traceparent` no dicionário; `FinishResult` com os ids aplicados e `Fenced` derivado; desfecho mapeado; fim no `try/finally` interno; os `Rastro_Send_*`, `Rastro_Ciclo_SqlForaDosSpansDeMensagem`, `Rastro_SemSpanDeCiclo`
- [ ] 9. Transporte: `SendActivity` no `Current`, evento de isolamento, destino pela regra das convenções; `Rastro_RabbitMq_DuasFontes_ClienteDentroDoSend`, `Rastro_RabbitMq_Isolamento406_EventoNoSend`
- [ ] 10. `process` no inbox; os `Rastro_Process_*` e a parte do `process` de `Rastro_ChaveETenant_SoComAOpcao`
- [ ] 11. `Rastro_PontaAPonta_RequisicaoAteHandler`
- [ ] 12. Mutações do PLAN, registradas nos commits
- [ ] 13. Exemplo: pacotes auditados, fontes e meter, dashboard do Aspire no compose, `smoke.sh`, README do exemplo com a tela do trace
- [ ] 14. Documentos dos spans: `OPERATIONS.md` (rastro, fontes, amostragem, `error.type`, consumidor à mão), XML doc do `ITransport`, README, CHANGELOG, a linha do escopo citando `Rastro_PontaAPonta`, ADR 0011 completo
- [ ] 15. Revisão de código independente antes da PR
