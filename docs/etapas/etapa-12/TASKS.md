# Etapa 12 — Tarefas

- [ ] Decisões do SPEC confirmadas pelo autor (linhas (D) do PLAN)
- [ ] `Garantias_CadaGarantiaTemTeste` espera G1 a G4; G4 no `GUARANTEES.md` com testes citados; "What Waybill does not promise" e "Operating over time"
- [ ] Revisão dos ADRs 0006 a 0011 contra o código; escopo (linha G4, benchmark da v0.2)
- [ ] README da `0.2.0-alpha`: status, garantias, "Ordering by key", when not to use, comparação remedida; `Readme_Ordenacao_Compila`
- [ ] Releitura do `OPERATIONS.md`; CHANGELOG `[0.2.0-alpha]` por tema, com o upgrade da `0.1.0-alpha`
- [ ] Verificação completa; revisão de código independente; tag `v0.2.0-alpha-test.1` verde
- [ ] Validação por agente sem contexto (primeiro evento, ordenação, upgrade da `0.1.0-alpha`); `FRICCAO.md`; PR para a `develop`
- [ ] Branch `release/0.2.0-alpha`: CHANGELOG datado, PublicAPI Shipped; PR para a `main`; agendado verde com o código da release
- [ ] Com ok do autor: tag assinada `v0.2.0-alpha`, publicação, validação dos pacotes do nuget.org, back-merge, releases no GitHub (`v0.1.0-alpha` e `v0.2.0-alpha`)
