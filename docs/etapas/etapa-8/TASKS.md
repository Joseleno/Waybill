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
- [x] Claim com M = 1: instante único, filtro de cabeça, guarda de `settings` no claim sem ordenação, sinal de chave quente (posse no limite movido para a 8c-2). Marcação ou devolução concorrente, posse no limite, chave quente, dispatcher antigo ao lado de quem ordena
- [x] DLQ bloqueia a chave (log com chave e `sequence`), `blocked_keys` com índice, liberação com `RETURNING` e registro, consultas de diagnóstico, retenção, estado terminal
- [x] Chave bloqueada com 100 mil à frente: medido (p50 1,19 s, linear), acima do limite; teto de regressão no teste. **Ponteiro de cabeça: PR próprio antes da 8c-2**
- [x] Transições: mudar P com `UPDATE settings` e escrita concorrente; reiniciar a ordenação ampliado; ligar com transação aberta; backlog anterior; trigger desligado como erro crítico; troca de dono com linha em voo
- [x] `OutgoingMessage.Sequence` e cabeçalho `waybill-sequence`; teste estrutural da ordenação desligada
- [x] Teste de propriedade da primeira entrega (caos, 20 s no PR; achou a ressalva das liberadas); backlog de 550 mil e custo desligada contra a `v0.1.0-alpha` emulada (agendado; histórico verde a acompanhar)
- [x] Mutações da lista do PLAN: cada proteção quebrou um teste nomeado (registro nos commits); "ORDER BY de um lado só" deixou de existir (só o trigger ordena)
- [x] Documentos: escopo (G4, linha "Ordenação"); `plano-v0.2.md` (8c-1/8c-2, cenários, três ADRs, nota para a etapa 10); ADR 0008, parte 1; `OPERATIONS.md` (custo medido, ativação, upgrade, rollback, liberação, diagnóstico, isolamento, crescimento de `outbox_keys`); CHANGELOG
- [x] Revisão de código independente antes da PR: nenhum bloqueante; um importante (a lista em toda linha custava N × N chaves por `SaveChanges`, também desligada) corrigido com teste antes; menores registrados (aviso de interceptor ausente com a ordenação desligada é ruído aceito; ordem de upgrade documentada; relógio do `MessageId` no ADR)

### Ponteiro de cabeça — PR entre a 8c-1 e a 8c-2

Desenho e aceite: seção "Ponteiro de cabeça" do `PLAN.md`. Um commit por cenário provado, na ordem abaixo. Cada teste é escrito e visto falhando antes do código. Antes de mutar código, seguir as armadilhas.

- [x] Desenho revisto pelo time de cinco revisores e pela sondagem (PG 18.6 e 15.19); decisões do autor no `PLAN.md`
- [x] **Coluna, índice e claim.**
  - Na `SchemaV0_2` (refeita): `is_head boolean DEFAULT true`; `ix_outbox_claimable` substituído (nome temporário com `CONCURRENTLY`, troca, `DROP` do antigo, mesmo nome final, `Up` idempotente com `indisvalid`); `Down` volta ao predicado da v0.1. `lock_timeout` no `Up`.
  - Coluna no `WaybillSchemaContext`, no snapshot e no `Designer`; nada no modelo do usuário.
  - Em `OutboxStore`: a cláusula `(sequence IS NULL OR is_head)` no `ClaimSql`, no `ClaimOrderedSql` e no `OldestPendingAgeSql`; a cabeça decidida por `min(sequence)`; o sinal de chave quente revisto.
  - Testes: `G4_Ponteiro_PlanoGenerico_UsaOIndice`; `G4_OrdenacaoDesligada_SemContadorNemFiltro` reescrito; upgrade, migração interrompida no índice e `Down` ampliados.
- [x] **Marca no INSERT e promoção.**
  - No `outbox_sequence`, depois de numerar: `false` pela dedup (`waybill.last_keyed`), `true` fora de READ COMMITTED, NULL com antecessora visível, `true` nos outros casos.
  - Função `outbox_head_promote` (VOLATILE, `SECURITY DEFINER`): `AFTER UPDATE OF status` para terminal e `AFTER DELETE` de não terminal, com `WHEN` em `sequence`, sem `settings`.
  - Consultas dos triggers por descida de índice.
  - Helper de sementes com marca coerente para os testes por `generate_series`.
  - Testes: `G4_ChaveQuenteSozinha_DrenoLinear`; `G4_ChaveBloqueadaComCemMilAFrente` como teoria de 10 mil e 100 mil, com teto constante e liberação seguida do claim; `G4_Ponteiro_DeleteDaCabeca_SucessoraViraCabeca`; `G4_Ponteiro_EstatisticasVazias_TransacaoGrandeLinear`; `G4_MesmaChaveNaMesmaTransacao` com uma cabeça por chave.
- [x] **Verificação no commit.**
  - Constraint trigger diferido `outbox_head_check` (`SECURITY DEFINER`): escolhe a antecessora sem trava, trava o id com `FOR SHARE SKIP LOCKED` e refaz o filtro.
  - Barreira de teste estendida com fase diferida (nome posterior) e filtro por status.
  - Testes: publicação antes do commit do produtor; marcação esperando o commit; marcação em voo (teoria: commit e rollback); produtor de duas chaves contra marcação em lote; liberação concorrente; `G4_Ponteiro_RajadaNaTransacao_SoAPrimeiraDecideNoCommit`; RR/SERIALIZABLE ampliado; papel só com INSERT ampliado; `Limite_SetConstraintsImmediate_MarcacaoEsperaOProdutor`.
- [x] **Desligar e religar.**
  - O SQL `ordering-reset` do `OPERATIONS.md` marca as não terminais com `sequence` na mesma transação.
  - Testes: `G4_Ponteiro_DesligarEReligar_ChavesAndam`; `Operacao_ReiniciarOrdenacao` ampliado.
- [x] **Checagens do dispatcher.**
  - `tgenabled` cobre os triggers novos (`PartitionStore`).
  - Recusa de `session_replication_role = replica` no startup, como o `IsolationLevelCheck`.
  - Teste: `G4_TriggerDesligado_ErroCritico` ampliado (cada trigger, e a sessão replica).
- [x] **Rede de varredura.**
  - Reparo incremental nas partições assumidas e a cada `PartitionLease`.
  - Contador `waybill.outbox.heads_repaired` e log de erro com chave e `sequence`.
  - No `OPERATIONS.md`, `unmarked-heads` e `mark-heads`, e `is_head` em `key-rows`.
  - Testes: `G4_Ponteiro_Rede_ReparaCabecaSemMarca`; `Operacao_MarcarCabecas`; `Operacao_ConsultasDeDiagnostico` ampliado.
- [x] **Caos.**
  - No teste de propriedade: a invariante "a menor não terminal de cada chave tem `is_head`", amostrada numa instrução e conferida no fim, mais um produtor numa chave quente durante a drenagem.
  - Rodar o caos curto.
- [x] **Mutações** da lista do `PLAN.md`: cada uma quebra um teste nomeado, registrado no commit.
- [x] **Documentos.**
  - ADR 0008, parte 1b: atores externos e como cada um é coberto, e a tabela de custo.
  - `OPERATIONS.md`: "nunca apague, libere"; limites de espera; RR; escrita extra no autovacuum; upgrade com `lock_timeout`; custo medido de novo.
  - Escopo: a frase do custo linear.
  - `plano-v0.2.md`: etapa 8 com cinco PRs e o gatilho de corte da 11.
  - CHANGELOG.
- [ ] **Verificação completa e revisão independente antes da PR.**
  - Unidade; integração no PG 18 e no 15; caos curto; exemplos; pack.
  - O achado à parte (Hash Join no plano genérico) fica registrado.

### 8c-2 — M ≥ 2 em rodadas

Desenho fechado no início do PR, a partir da seção 8c-2 do `PLAN.md` e da mudança do `ITransport` da etapa 9.
