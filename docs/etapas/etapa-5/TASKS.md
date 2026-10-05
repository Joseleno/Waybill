# Etapa 5 — Tarefas

- [x] ADR 0004 (retenção, fronteira da G3, métrica, health check); "24 h" vira "carga longa (5 h)" nos docs de design
- [x] Migration `ix_inbox_processed_at`; `AddWaybillRetention` com opções validadas e serviço de limpeza em lotes
- [x] Cenários `Retencao_*` e `G2_BrokerParadoAlemDaRetencao_NenhumaPendenteApagada`, um commit por cenário
- [x] `Inbox_ReentregaDepoisDaLimpeza_ProcessaDeNovo` (fronteira da G3)
- [x] Amostrador e gauge `waybill.outbox.oldest_pending.age`; cenários `Metrica_*`
- [x] `DispatcherStatus` e health check; cenários `HealthCheck_*`
- [x] Caos curto `BrokerParadoEReligado_MetricaEHealthCheckAcompanham` (Toxiproxy)
- [ ] `CargaLonga_TransacaoLongaAberta_LatenciaDoClaimEstabiliza`; input de duração e artefato CSV no `scheduled.yml`; uma execução local curta antes do commit
- [ ] `docs/OPERATIONS.md`; README, CHANGELOG, API pública; ressalva da G3 no HANDOFF
- [ ] Revisão de código independente; CI verde; execução verde da carga longa no job agendado
