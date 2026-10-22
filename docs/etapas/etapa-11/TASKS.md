# Etapa 11 — Tarefas

Um commit por cenário provado, na ordem abaixo. Os nomes dos testes estão no `PLAN.md`. As tarefas 1 a 6 são o piso do corte parcial (decisão do autor): se a etapa apertar, ela fecha nelas.

- [x] SPEC revisto por revisão adversarial; decisões do autor (corte com piso, chave e tenant por opção, dashboard do Aspire) em Oct 21, 2026
- [x] PLAN revisto por revisão adversarial, com experimentos de `Activity` e a descompilação do RabbitMQ.Client 7.2.2
- [ ] PLAN aprovado pelo autor
- [ ] 1. Testes: `TraceRecorder` com `TraceId` fixado antes e filtro no `Sample` e nos callbacks, nos dois projetos de teste
- [ ] 2. Propagação: `InternalsVisibleTo` para `Waybill.RabbitMQ`; `Propagation` no transporte, posto dentro do método assíncrono; `Rastro_RabbitMq_SoFonteDoClienteOuvida_ConsumidorNoTrace`, visto falhando antes (o defeito da v0.1), e `Rastro_RabbitMq_NinguemOuve_TraceparentDaRequisicao`
- [ ] 3. Schema: `ix_outbox_dlq` no modelo, no Designer, no snapshot e na `SchemaV0_2`, `Down` correspondente; `Schema_IndiceDaDlq_UsadoNaContagem`, `Schema_DownSemOrdenacaoUsada_VoltaAV01EVolta` ampliado
- [ ] 4. Gauge `waybill.outbox.dlq` no sampler; `Metrica_DlqEChavesBloqueadas_SobemEVoltamAZero`
- [ ] 5. `InboxMetrics` no `AddWaybillInbox`, contador de regressões; `Metrica_Regressoes_ServicoSemDispatcher`
- [ ] 6. Documentos do piso: CHANGELOG (o defeito da v0.1 e as métricas), `OPERATIONS.md` (métricas novas e agregação por máximo; a recomendação de ouvir as duas fontes), ADR 0011 com a propagação
- [ ] 7. Núcleo: `WaybillDiagnostics`, `WaybillActivity` com `StartDetached` e `StopDetached`, API pública declarada; `create` no `EnvelopeFactory` e a opção `RecordKeyAndTenantInTraces`; os `Rastro_Create_*` e `Rastro_ChaveETenant_SoComAOpcao`
- [ ] 8. `send` no dispatcher: abertura na rodada, `SendActivity` interno, troca do `traceparent`, `FinishResult` com os ids aplicados, desfecho mapeado, fim depois do `FinishAsync`; os `Rastro_Send_*` e `Rastro_Ciclo_SqlForaDosSpansDeMensagem`
- [ ] 9. Transporte: `SendActivity` não parado no `Current`, atributos do RabbitMQ antes do primeiro `await`, evento de isolamento; `Rastro_RabbitMq_DuasFontes_ClienteDentroDoSend`, `Rastro_RabbitMq_Isolamento406_EventoNoSend`
- [ ] 10. `process` no inbox; os `Rastro_Process_*`
- [ ] 11. `Rastro_PontaAPonta_RequisicaoAteHandler`
- [ ] 12. Mutações do PLAN, registradas nos commits
- [ ] 13. Exemplo: pacotes auditados, fontes e meter, dashboard do Aspire no compose, `smoke.sh`, README do exemplo com a tela do trace
- [ ] 14. Documentos dos spans: `OPERATIONS.md` (rastro), README, CHANGELOG, escopo, ADR 0011 completo
- [ ] 15. Revisão de código independente antes da PR
