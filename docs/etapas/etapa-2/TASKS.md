# Etapa 2 — Tarefas

- [x] ADR 0002 (enfileiramento, schema e migrations do pacote, `key_hash`, registro de tipos); Escopo e plano ajustados
- [x] Núcleo: `IOutbox`, envelope, registro de tipos, `WaybillOptions`, murmur2, UUIDv7; testes `Envelope_*` e `KeyHash_*`
- [x] EF: entidade e mapeamento da outbox e do inbox, `AddWaybillOutbox`, migrations do pacote, `WaybillSchema.MigrateAsync`; testes `Schema_*`
- [x] EF: `IOutbox<TContext>`, buffer por `DbContext`, interceptor (`SavingChanges`/`SavedChanges`), log ou exceção no descarte
- [x] Os sete cenários `G1_*`, um commit por cenário
- [x] `Waybill.Testing`: `FakeOutbox<TContext>` e asserções; teste de equivalência
- [ ] `CHANGELOG`, API pública declarada em `PublicAPI.Unshipped.txt`, CI verde
