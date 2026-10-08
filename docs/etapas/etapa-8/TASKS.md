# Etapa 8 — Tarefas

## 8a — espaçamento do `basic.return`

- [x] Schema: migration da v0.2 com `next_attempt_at`; teste de upgrade de um banco da `0.1.0-alpha` com backlog
- [x] Opções `ReturnBackoff` e `MaxReturnBackoff`, validadas no startup; API pública declarada
- [x] Store: `ReturnedSql` grava `next_attempt_at` com expoente limitado; claim com o predicado nos dois níveis
- [x] Cenários `G2_*` da 8a, um commit por cenário; ajuste dos testes de `Returned` existentes; oráculo de caos ampliado; mutação no predicado do claim
- [x] Dez mil linhas em espera: p50 de 3,7 ms, sem índice próprio (ADR 0006)
- [x] `OPERATIONS.md` (opções, tempo até a DLQ; o SQL de reenvio não muda), escopo, ADR 0003, `GUARANTEES.md`, ADR 0006, CHANGELOG
- [x] Revisão de código independente antes da PR: sem defeito de confiança alta; o limite fixo do expoente (20) impedia um `ReturnBackoff` abaixo de um segundo de chegar ao teto, agora calculado das opções

## 8b — lease por partição

- [x] Schema: `settings`, `outbox_partitions` e `outbox_instances` na migration da v0.2, refeita
- [x] Opções `OrderByKey`, `Partitions` e `PartitionLease`, validadas; API pública declarada
- [x] Store de partições: configuração global, heartbeat, renovação, aquisição, devolução e coleta
- [x] Dispatcher: manutenção das partições a cada ciclo, claim com o `EXISTS` de posse, verificação da configuração e parada crítica, devolução no shutdown
- [x] Cenários `G4_*` da 8b, um commit por cenário; oráculos de mandato e de claim fora do mandato no caos; mutações
- [x] `OPERATIONS.md` (opções, ligar e desligar a ordenação, mudar P, com o SQL testado), escopo, ADR 0007, CHANGELOG
- [x] Revisão de código independente antes da PR: nenhum caminho para dois donos ou claim fora do mandato; três falhas de vivacidade (startup simultâneo, espera entre ciclos maior que o lease, manutenção a cada lote) e duas de validação, cada uma com teste antes da correção

## 8c — cabeça por chave

- [x] Desenho revisto duas vezes (revisor adversarial e sondagem; time de cinco revisores). Os bloqueantes foram corrigidos no `PLAN.md` antes de codar, e a 8c foi dividida em 8c-1 e 8c-2 com o autor

### 8c-1 — G4 com M = 1

Um commit por cenário provado, na ordem abaixo.

- [x] Protótipo medido do contador no EF real (`PROTOTIPO-CONTADOR.md`): a lista é necessária (sem ela, 26 deadlocks com três chaves de 20); trava em duas etapas, com `FOR UPDATE` (37% menos p99 sob disputa); desligada, sem custo mensurável com uma chave e −4,3% com três linhas por transação; o p99 da chave quente do spike não vem do `deadlock_timeout`
- [x] Schema: `outbox_keys` (`fillfactor`), `lock_keys`, `released_at`/`released_by`, trigger que numera (`SECURITY DEFINER`), trigger de estado terminal, restrição com `NOT VALID` + `VALIDATE`, índices com `CONCURRENTLY`, `Up` idempotente, `Down` que recusa; `SchemaV0_2` refeita. Upgrade, migration interrompida e `Down`
- [x] Opções: `OrderByKey` para `WaybillDispatcherOptions`; API pública declarada. O limite da chave já existia (255 bytes, v0.1)
- [x] Enfileiramento: interceptor de `SavingChanges` registrado no `AddWaybillOutbox` (contexto sem ele avisa uma vez); ids monotônicos (achado: 197 de 200 fora de ordem na mesma transação). Cenários de lista sem deadlock (com controles negativos), limite de escrita depois de enfileirar, sequência contígua, lista que não sobrevive ao `SaveChanges`, chaves com separadores, RR/SERIALIZABLE, papel só com INSERT
- [ ] Claim com M = 1: instante único, filtro de cabeça, guarda de `settings` no claim sem ordenação, sinal de chave quente. Marcação ou devolução concorrente, posse no limite, chave quente, dispatcher antigo ao lado de quem ordena
- [ ] DLQ bloqueia a chave (log com chave e `sequence`), `blocked_keys` com índice, liberação com `RETURNING` e registro, consultas de diagnóstico, retenção, estado terminal
- [ ] Chave bloqueada com 100 mil à frente (oráculo de buffers). Se não couber, o ponteiro de cabeça vira PR próprio antes da 8c-2
- [ ] Transições: mudar P com `UPDATE settings` e escrita concorrente; reiniciar a ordenação ampliado; ligar com transação aberta; backlog anterior; trigger desligado como erro crítico; troca de dono com linha em voo
- [ ] `OutgoingMessage.Sequence` e cabeçalho `waybill-sequence`; teste estrutural da ordenação desligada
- [ ] Teste de propriedade da primeira entrega (caos, 20 s no PR); backlog de 500 mil a 1 milhão e custo desligada contra a `v0.1.0-alpha` (agendado)
- [ ] Mutações da lista do PLAN
- [ ] Documentos: escopo (G4, linha "Ordenação"); `plano-v0.2.md` (8c-1/8c-2, cenários, três ADRs, nota para a etapa 10); ADR 0008, parte 1; `OPERATIONS.md` (custo medido, ativação, upgrade, rollback, liberação, diagnóstico, isolamento, crescimento de `outbox_keys`); CHANGELOG
- [ ] Revisão de código independente antes da PR

### 8c-2 — M ≥ 2 em rodadas

Desenho fechado no início do PR, a partir da seção 8c-2 do `PLAN.md` e da mudança do `ITransport` da etapa 9.
