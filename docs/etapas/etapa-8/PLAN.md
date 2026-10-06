# Etapa 8 — Plano e critérios de aceite

## 8a — espaçamento do `basic.return` (PostgreSQL real, transporte falso salvo indicação)

| Teste | Cenário | Resultado esperado | Onde roda |
| --- | --- | --- | --- |
| `G2_Returned_EspacamentoCrescenteAteOTeto` | Transporte devolve `Returned` sempre; `ReturnBackoff` e `MaxReturnBackoff` pequenos; a espera é pulada por SQL entre os ciclos | Depois do k-ésimo retorno, `next_attempt_at − clock_timestamp()` ≈ `min(base × 2^(k−1), teto)`; na `MaxReturns`ª, DLQ com o motivo | PR |
| `G2_Returned_LinhaEmEsperaNaoEReivindicada` | Uma linha em espera e outras pendentes | O claim leva as outras e não leva a que espera; vencida a espera, ela é reivindicada | PR |
| `G2_FalhaDeTransporteDepoisDeUmReturn_NaoReiniciaAProgressao` | `Returned`, espera vencida, `Retry` (timeout de confirmação, para não abrir o breaker), depois `Returned` de novo | O `Retry` não gasta tentativa e reabre na hora; o segundo retorno espera `2 × base`, não `base` | PR |
| `G2_Returned_MuitasTentativas_IntervaloNaoEstoura` | `MaxReturns` = 1000, linha com 998 tentativas gastas, `ReturnBackoff` de um dia | Nenhum erro de `interval out of range`; a espera fica no teto | PR |
| `G2_DezMilEmEspera_ClaimContinuaFluindo` | Dez mil linhas em espera com ids menores que cem pendentes | As pendentes são reivindicadas; latência do claim registrada na saída do teste. Se o p50 passar de 50 ms, a 8a ganha índice próprio antes do PR (decisão registrada no ADR) | PR |
| `G2_Returned_OrcamentoProprioDepoisDlq_ComMotivo` (existente) | Ajustado: `ReturnBackoff` = 0 | Orçamento inalterado; a linha volta reivindicável na hora, como na v0.1 | PR |
| `G2_RabbitMq_SemRota_ReturnedAteDlq_ComNoRoute` (existente) | Ajustado: pula a espera por SQL | `NO_ROUTE` até a DLQ, agora espaçado | PR |
| `Operacao_ReenfileirarDaDlq` (existente) | Ajustado: pula a espera entre os retornos. O SQL do `OPERATIONS.md` não muda, porque a linha na DLQ tem `next_attempt_at` nula | A mensagem reenfileirada sai na hora | PR |
| `G2_OitoDispatchers_NenhumaLinhaComDoisLeasesValidos` (existente, caos) | Ampliado: o transporte também devolve `Returned`, e o oráculo registra claim de linha ainda em espera | Nenhum claim durante a espera; só retornos gastam tentativa | PR: curto; agendado: 10 min |
| `Schema_UpgradeDaV01ComBacklog_LinhasContinuamReivindicaveis` | Banco com o schema da `0.1.0-alpha` e pendentes; aplica a migration da v0.2 | Migration aditiva; `next_attempt_at` nula; o backlog drena | PR |
| `Opcoes_*` (unidade) | `ReturnBackoff` negativo; `MaxReturnBackoff` menor que `ReturnBackoff` | O host não sobe, com mensagem que diz o que mudar | PR |

**Armadilhas da 8a**

- `interval × 2^k` estoura com k grande: o expoente é limitado antes da multiplicação, no SQL. "Intervalos sem teto" está na lista de bugs que não podem voltar.
- A espera vem do relógio do banco, como o lease. Os testes não esperam o relógio passar: avançam a linha por SQL (`UPDATE … SET next_attempt_at = clock_timestamp()`).
- O predicado novo vai nos dois níveis do claim. Verificado por mutação: tirar só do CTE é pego pelo teste de dez mil linhas em espera; tirar dos dois níveis, pelo oráculo de caos; tirar só do `UPDATE` externo não tem teste determinístico (janela de corrida), como o predicado de status (ADR 0006).

## 8b e 8c

Detalhados no início de cada PR, a partir de `docs/plano-v0.2.md`, Etapa 8, e das decisões do `SPEC.md`.

## Pronto quando (8a)

- Os cenários acima passam contra PostgreSQL real (15 e 18 no CI).
- ADR 0006 escrito: espaçamento do `basic.return` e a interação com a cabeça por chave.
- `OPERATIONS.md` com as opções novas e o tempo total até a DLQ; escopo (G2 e Contrato técnico), ADR 0003 e `GUARANTEES.md` atualizados; API pública declarada; CHANGELOG atualizado.
