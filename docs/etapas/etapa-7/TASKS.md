# Etapa 7 — Tarefas

- [x] Release pipeline: integração sem `Category=Long`, `timeout-minutes`, validação de Source Link e símbolos no caminho `-test`
- [x] `GUARANTEES.md` (G1 a G3, condições, testes citados, o que não promete); `Garantias_CadaTesteCitadoExiste` e `Garantias_CadaGarantiaTemTeste`
- [x] Revisão dos ADRs 0001 a 0005 contra o código; escopo sem *provisório*; decidir `lock_timeout`, `statement_timeout` e `idle_in_transaction_session_timeout`
- [x] README da release (links absolutos, when not to use, comparação medida, what it does not solve); `Readme_ApiEmDezLinhas_Compila`
- [ ] CHANGELOG `[0.1.0-alpha]`; descrição do `Waybill.Testing`; PublicAPI Shipped
- [x] Tag `v0.1.0-alpha-test.1` verde; validação por agente sem contexto com feed local; `FRICCAO.md`
- [ ] Revisão de código independente; CI verde; validação do autor; PR
- [x] Linha "Operação sustentável" no `GUARANTEES.md`, só depois da carga longa verde (etapa 5)
- [ ] Com ok do autor: tag assinada `v0.1.0-alpha`, publicação no NuGet, Source Link conferido do nuget.org; pedido do prefixo `Waybill.*`
