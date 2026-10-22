# Etapa 12 — Tarefas

- [x] Decisões do SPEC confirmadas pelo autor (linhas (D) do PLAN): todas as recomendações, Oct 22, 2026
- [x] `Garantias_CadaGarantiaTemTeste` espera G1 a G4; G4 no `GUARANTEES.md` com testes citados; "What Waybill does not promise" e "Operating over time"
- [x] Revisão dos ADRs 0006 a 0011 contra o código; escopo (linha G4, benchmark da v0.2): 18 correções de texto nos ADRs, nenhuma de comportamento
- [x] README da `0.2.0-alpha`: status, garantias, "Ordering by key", when not to use, comparação remedida (Wolverine 6.51.2: 86 e 95 pacotes; CAP 10.0.2: 23; Waybill: 25); `Readme_Ordenacao_Compila`
- [x] Releitura do `OPERATIONS.md` (seções da ordenação, do inbox e do rastro conferidas pelos revisores dos ADRs; frases de versão corrigidas); CHANGELOG por tema, com o upgrade da `0.1.0-alpha` (data na branch de release)
- [x] Verificação completa; revisão de código independente (7 achados, corrigidos); tag `v0.2.0-alpha-test.1` verde (run 39769493108)
- [x] Validação por agente sem contexto (primeiro evento, ordenação, upgrade da `0.1.0-alpha`): as três funcionaram; `FRICCAO.md` com os destinos; PR #32 para a `develop`
- [ ] Branch `release/0.2.0-alpha`: CHANGELOG datado, PublicAPI Shipped; PR para a `main`; agendado verde com o código da release
- [ ] Com ok do autor: tag assinada `v0.2.0-alpha`, publicação, validação dos pacotes do nuget.org, back-merge, releases no GitHub (`v0.1.0-alpha` e `v0.2.0-alpha`)
