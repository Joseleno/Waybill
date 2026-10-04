# Etapa 2 — Tarefas

- [x] ADR 0002 (enfileiramento, schema e migrations do pacote, `key_hash`, registro de tipos); Escopo e plano ajustados
- [ ] Núcleo: `IOutbox`, envelope, registro de tipos, `WaybillOptions`, murmur2, UUIDv7; testes `Envelope_*` e `KeyHash_*`
- [ ] EF: entidade e mapeamento da outbox e do inbox, `AddWaybillOutbox`, migrations do pacote, `WaybillSchema.MigrateAsync`; testes `Schema_*`
- [ ] EF: `IOutbox<TContext>`, buffer por `DbContext`, interceptor (`SavingChanges`/`SavedChanges`/`SaveChangesFailed`), log ou exceção no descarte
- [ ] Os sete cenários `G1_*`, um commit por cenário
- [ ] `Waybill.Testing`: `FakeOutbox<TContext>` e asserções; teste de equivalência
- [ ] `CHANGELOG`, API pública declarada em `PublicAPI.Unshipped.txt`, CI verde
