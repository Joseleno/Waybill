# Etapa 13 — Tarefas

Um commit por cenário provado, na ordem abaixo.

- [x] SPEC revisto e aprovado pelo autor, com as três recomendações (Oct 24, 2026)
- [x] PLAN revisto: viabilidade do host da 0.1 conferida (restauração do nuget.org e build da solution com 0 avisos)
- [x] PLAN aprovado pelo autor (Oct 24, 2026)
- [x] 1. `Waybill.Tests.LegacyHost` (os quatro modos) e o harness `LegacyHost`, com a versão `0.1.0-alpha` conferida no stdout; plano, SPEC e PLAN no mesmo commit
- [x] 2. `Convivencia_JanelaDoUpgrade_01E02Juntas_NenhumaSePerde`
- [x] 3. `Convivencia_Consumidor01_Recebe02EDeduplica`
- [x] 4. `Convivencia_Rollback_01DrenaBacklogNumerado`
- [x] 5. `Convivencia_Rollback_LinhaEmEsperaDeReturn_01PublicaNoCicloSeguinte`
- [x] 6. Bloco `<!-- rollback-claim-index -->` no `OPERATIONS.md` e `Convivencia_Rollback_IndiceDa01_ClaimDa01UsaOIndice`
- [x] 7. `Convivencia_Produtor01ComOrdenacaoLigada_SaiEmOrdem`
- [x] 8. `Convivencia_Dispatcher01ComOrdenacaoLigada_PublicaForaDeOrdem`
- [x] 9. Log de partida: teste `Dispatcher_LogDePartida_DizAOrdenacao` primeiro, depois o `LogStarted`
- [ ] 10. Mutações do PLAN, cada uma derrubando o teste indicado
- [ ] 11. `OPERATIONS.md` ("Upgrading from 0.1", "Upgrading and rolling back", "Ordering by key"): cada frase sobre a 0.1 com o teste dela; CHANGELOG em `[Unreleased]`; ADR 0012
- [ ] 12. Verificação completa, revisão independente e `guarantee-auditor` se o diff tocar README ou `GUARANTEES.md`; PR para a `develop`
- [ ] 13. Comentário na issue #21 (os itens resolvidos com teste ou ADR; aberta só com os três da migration)
