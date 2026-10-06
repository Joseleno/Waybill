# Etapa 8 — Tarefas

## 8a — espaçamento do `basic.return`

- [ ] Schema: migration da v0.2 com `next_attempt_at`; teste de upgrade de um banco da `0.1.0-alpha` com backlog
- [ ] Opções `ReturnBackoff` e `MaxReturnBackoff`, validadas no startup; API pública declarada
- [ ] Store: `ReturnedSql` grava `next_attempt_at` com expoente limitado; claim com o predicado nos dois níveis
- [ ] Cenários `G2_*` da 8a, um commit por cenário; ajuste dos dois testes de `Returned` existentes; mutação no predicado do claim
- [ ] Dez mil linhas em espera: medição e decisão sobre índice próprio
- [ ] `OPERATIONS.md` (opções, tempo até a DLQ, SQL de reenvio zerando `next_attempt_at`), escopo, ADR 0003, `GUARANTEES.md`, ADR 0006, CHANGELOG
- [ ] Revisão de código independente antes da PR

## 8b — lease por partição

Tarefas escritas no início do PR.

## 8c — cabeça por chave

Tarefas escritas no início do PR.
