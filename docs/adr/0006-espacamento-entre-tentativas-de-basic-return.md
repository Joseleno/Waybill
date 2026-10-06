# ADR 0006 — Espaçamento entre tentativas de `basic.return`

- Status: aceito
- Data: 2026-10-06
- Etapa: 8a (primeiro PR da ordenação por chave, v0.2)

## Contexto

Na v0.1, uma mensagem devolvida como sem rota (`basic.return`, com `mandatory`) voltava a `pending` na hora e era reivindicada no ciclo seguinte. Com `PollingInterval` de 1 s e `MaxReturns` = 5, um binding ausente mandava a mensagem à DLQ em ~5 s. Binding ausente costuma ser passageiro: ordem de deploy, ou a fila declarada pelo consumidor só quando ele sobe. Em 5 s ninguém corrige nada, e cada mensagem vira um reenvio manual.

A ordenação por chave da v0.2 piora o custo. Uma mensagem na DLQ bloqueia a própria chave até liberação manual, e a API de liberação só vem na v1.0. O que hoje é um reenvio passaria a ser uma chave travada.

## Decisão

1. **Coluna `next_attempt_at` na `outbox`, nula, por migration aditiva.** Ela só tem valor enquanto a linha espera depois de um retorno. Volta a nula quando a linha vai para a DLQ, por retorno ou por defeito, então o SQL de reenvio da DLQ não precisa mudar.
2. **Espera crescente com teto, no relógio do banco.** Depois do k-ésimo retorno, `next_attempt_at = clock_timestamp() + min(ReturnBackoff × 2^(k−1), MaxReturnBackoff)`. Defaults: `ReturnBackoff` = 1 min, `MaxReturnBackoff` = 10 min, `MaxReturns` = 5 (inalterado). São 15 min de espera somada antes da DLQ. `ReturnBackoff` = 0 devolve o comportamento da v0.1.
3. **Só o `basic.return` agenda espera.** Defeito da mensagem vai direto à DLQ, como antes. Falha de transporte e lease vencido reabrem o claim na hora, sem gastar tentativa e sem reiniciar a progressão: a espera seguinte continua de onde parou, porque ela é derivada de `attempts`.
4. **A condição de reivindicável ganha `next_attempt_at IS NULL OR next_attempt_at <= clock_timestamp()`, no nível do `FOR UPDATE` e repetida no `UPDATE` externo**, pela regra do ADR 0001, item 2.
5. **O expoente para no menor valor que já alcança o teto, calculado das opções (`ReturnPolicy.MaxExponent`), antes da multiplicação.** Com `MaxReturns` na casa das centenas, `2^k` estouraria o `interval` do PostgreSQL muito antes de o teto valer, e o erro derrubaria a marcação do lote inteiro. Parado nesse expoente, `ReturnBackoff × 2^k` fica abaixo do dobro do teto, que é no máximo um dia. A primeira versão usava um limite fixo de 20; a revisão mostrou que, com backoff abaixo de um segundo (1 ms e teto de um dia), a espera parava em ~17,5 min sem chegar ao teto.
6. **O índice parcial `ix_outbox_claimable` não muda.** O relógio não pode entrar no predicado de um índice. As linhas em espera continuam no índice, e cada claim as lê e passa por elas. Medido: com dez mil linhas em espera à frente de cem pendentes, o claim teve p50 de 3,7 ms e máximo de 16,8 ms (Docker/WSL2, PostgreSQL 18). O critério escrito antes da medição era abrir um índice próprio acima de 50 ms de p50. O teste registra a latência e afirma só que o backlog flui; um limite de tempo no CI seria frágil, então uma regressão de plano não o derruba.

## Alternativas descartadas

| Alternativa | Por que não |
| --- | --- |
| Intervalo fixo entre retornos | Não acomoda ao mesmo tempo o binding que aparece em segundos e o que leva minutos: ou é curto e volta a mandar à DLQ cedo, ou é longo e atrasa o caso comum |
| Só aumentar `MaxReturns` | O tempo total continua preso ao `PollingInterval`, e cada tentativa é uma publicação a mais contra um broker que já disse que não há rota |
| Espera em memória no dispatcher | Some no restart e não é vista pelas outras instâncias: a mensagem seria reivindicada por outra antes da hora |
| DLQ na hora, recomendando alternate exchange | Já é recomendado no `OPERATIONS.md`. Não ajuda quem não configurou, e com a ordenação a DLQ trava a chave |

## Interação com a cabeça por chave

Com a ordenação ligada (etapa 8c), uma linha em espera é a cabeça da sua chave: as mensagens seguintes da mesma chave esperam junto. O teto total (15 min por padrão) é a janela que o operador tem para criar o binding antes de a mensagem ir à DLQ e a chave ficar bloqueada até a liberação. O ADR da ordenação retoma esse ponto.

## Consequências

- Mudança de comportamento: com os defaults, um `312 NO_ROUTE` leva 15 min até a DLQ, não ~5 s. Está no `CHANGELOG` e no `OPERATIONS.md`.
- A métrica de idade da mensagem pendente mais antiga cresce durante a espera. É o sinal certo: a mensagem está atrasada.
- O predicado repetido no `UPDATE` externo não tem teste determinístico. A janela de corrida é pequena demais: a mutação que o retira passou em três execuções do teste de oito dispatchers. É a mesma situação do predicado de status, e a regra vem do ADR 0001. A retirada do predicado dos dois níveis é pega pelo oráculo de caos (600 claims durante a espera numa execução). A retirada só do CTE é pega pelo teste de dez mil linhas em espera.

## Testes que provam

`G2_Returned_EspacamentoCrescenteAteOTeto`, `G2_Returned_LinhaEmEsperaNaoEReivindicada`, `G2_FalhaDeTransporteDepoisDeUmReturn_NaoReiniciaAProgressao`, `G2_Returned_MuitasTentativas_IntervaloNaoEstoura`, `G2_DezMilEmEspera_ClaimContinuaFluindo`, `G2_Returned_OrcamentoProprioDepoisDlq` (com espera zero, o comportamento da v0.1), `G2_RabbitMq_SemRota_ReturnedAteDlq`, `Schema_UpgradeDaV01ComBacklog_LinhasContinuamReivindicaveis` (integração); `G2_OitoDispatchers_NenhumaLinhaComDoisLeasesValidos`, com o oráculo de claim durante a espera (caos); `Opcoes_*` (unidade).
