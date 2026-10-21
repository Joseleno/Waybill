# Etapa 10 — Tarefas

Um commit por cenário provado, na ordem abaixo. Os nomes dos testes estão no `PLAN.md`.

- [x] SPEC revisto por revisão de construtor e revisão adversarial; aprovado pelo autor (Oct 21, 2026)
- [x] PLAN revisto por um time de quatro revisores (PostgreSQL, RabbitMQ, API .NET, testes), com medições no Postgres 18 e no RabbitMQ 4.3.6
- [x] PLAN aprovado pelo autor (Oct 21, 2026)
- [x] Broker: `G4_RabbitMq_RequeueDepoisDeFalha`, que fixa o medido na revisão (a devolvida volta antes; `nack` não conta entregas), com a topologia lida do `definitions.json`
- [x] Schema: `inbox_keys` na `SchemaV0_2` (fillfactor 80), entidade, snapshot; `Down` com `to_regclass`, recusa com `inbox_keys` e a apaga; retenção não a toca
- [x] Núcleo: `MessageOrder` validado; testes de unidade; API pública declarada
- [x] Inbox: `OrderedInboxResult`, `ProcessInOrderAsync`, SQL da verificação, log `EventId` 50; os cenários `G4_Inbox_*`, um commit cada
- [ ] Mutações de todas as armadilhas do PLAN (feitas: ordem dos testes, trava antes do handler, `<`, `last_message_id`, rollback, log, `Lower`/`Reused`, `to_regclass`; faltam as do `FakeInbox` e do RabbitMQ), cada uma pega pelo teste citado, registradas no commit
- [x] RabbitMQ: `GetWaybillOrder` (com `byte[]`); `G4_RabbitMq_RegressaoChegaADlx`
- [x] `FakeInbox` com a mesma regra; equivalência ampliada
- [x] Propriedade `G4_Propriedade_RegressaoNaDlx` no caos (PR curto e agendado), oráculo da DLX exata, injeção depois da drenagem, regressões naturais > 0 no agendado
- [ ] Exemplo: `Billing` com `OrderByKey`; `Receipts` com `ProcessInOrderAsync`, `switch`, até cinco tentativas no consumidor e depois DLX; `definitions.json` nas condições do broker; `smoke.sh` confere os argumentos das filas e publica uma regressão `Reused`; README do exemplo com `docker compose down -v`
- [ ] `OPERATIONS.md` (seção nova e SQL `inbox-forget-keys` testado), escopo, ADR 0010, CHANGELOG (com "Breaking" pelo membro novo no `IInbox`)
- [ ] Revisão de código independente antes da PR
