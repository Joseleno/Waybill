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

Tarefas escritas no início do PR.

## 8c — cabeça por chave

Tarefas escritas no início do PR.
