# Spike da etapa 0 — resultados

Código descartável, arquivado aqui como evidência do ADR 0001 (fora da solution e do CI): SQL cru, Npgsql 10.0.3, Testcontainers 4.15 com `postgres:18-alpine`, xUnit v3, .NET 10.
Ambiente: Windows 11, Docker Desktop (WSL2), 16 GB, gerador de carga e banco na mesma máquina — os números servem
para comparação relativa, não como referência absoluta. Uma execução por cenário, sem aquecimento, salvo onde indicado.
Números brutos em `results/` (execução final) e `results/run*` (anteriores, ver "Histórico").

## Decisões

1. **Ordenação fica na v0.2** (critérios pré-registrados abaixo; C3 e C4 falharam).
2. **A v0.1 faz claim por linha, sem lease por partição** (decisão do autor depois da revisão adversarial). Sem a
   ordenação, o lease por partição não tem consumidor na v0.1 e custa vazão (−14%), duas tabelas, heartbeat,
   rebalanceamento e failover pior. Ele entra na v0.2 como camada sobre o mesmo claim. Ver ADR 0001.

## Critérios de decisão (escritos antes de medir Q4–Q6, mantidos como estavam)

A ordenação **é tecnicamente viável na v0.1** se todos valerem:

| # | Critério | Fonte |
| --- | --- | --- |
| C1 | Lease por partição + cabeça por chave: **0 inversões** por chave no broker com 8 instâncias, M=1 e M=4 | Q4 |
| C2 | Contador por chave com chaves ordenadas: **0 deadlocks**, sequência contígua e na ordem de commit | Q5 |
| C3 | Contador em chaves distintas: p99 da transação de negócio **até 20% acima** do cenário sem contador | Q5 |
| C4 | Ordenado (M=10, 1.000 chaves) com vazão **≥ 50%** do não ordenado, com claim p99 **≤ 50 ms** com 50 mil pendentes | Q6 |

Viável não significa "entra": o plano não tem folga e a 3b soma uma semana ao teto de oito. Se C1–C4 passarem,
a decisão volta ao autor como troca de prazo; se qualquer um falhar, a ordenação vai para a v0.2 sem discussão.

Ressalva de procedimento: o spike não está sob controle de versão, então a anterioridade dos critérios não é verificável
pelas datas dos arquivos. Duas coisas foram acrescentadas **depois** de medir e estão marcadas como tal: a tolerância de
relógio do C2 e a variante "fundida" do C3. Nenhuma delas muda um veredito.

### Vereditos

| # | Resultado | Veredito |
| --- | --- | --- |
| C1 | `PartitionedOrdered` M=1 e M=4: 0 inversões, 0 duplicatas, em todas as execuções. **Limite:** cada dispatcher é sequencial e não houve troca de dono de partição com linhas em voo durante o teste | ✅ no que foi exercitado |
| C2 | Chaves ordenadas: 0 deadlocks em todas as execuções; sequência contígua com 10% de rollback. Ordem de commit: ✅ **com tolerância de 5 ms acrescentada depois de medir** — em 4 de 6 execuções da chave quente fundida houve 1 recuo de 0,4 a 1,25 ms, explicado pelo relógio (ver "Ambiente"). O oráculo é fraco nesse cenário (commits a ~1 ms um do outro) | ✅ com ressalva |
| C3 | Contador simples (o do critério): p99 **+16% a +70%** em 9 execuções, acima de 20% em 8. Execução final: 12,4 vs 8,0 ms (+55%) | ❌ |
| C4 | M=10, 1.000 chaves, sem latência de confirmação: **35% a 38%** em 4 execuções (final: 30,6 mil vs 82,8 mil msg/s). Claim p99 28,8 a 32,8 ms ✅ | ❌ |

Leitura dos números, para a v0.2 e não para reabrir esta decisão:

- **C3: a causa é o upsert, não uma ida extra ao banco** (a versão anterior deste texto estava errada). A variante fundida
  (contador e INSERT num só comando, acrescentada depois de medir) tem as mesmas idas ao banco que o cenário sem contador
  e ainda custa **+4% a +30%** (passaria em 6 de 8 execuções). O custo é o INSERT/UPDATE em `outbox_keys` com índice e WAL.
  As chaves "distintas" do teste são quase todas novas: mede o primeiro uso de cada chave, não o caso estacionário.
- **C4:** com 20 ms de confirmação do broker, mais perto do real, o ordenado faz **66% a 74%** do não ordenado. O critério
  não fixou a latência; valeu a leitura mais estrita.
- Em valor absoluto, ~30 mil msg/s ordenadas ficam muito acima do público-alvo. Os critérios eram relativos de propósito.

## Respostas às seis perguntas

| Pergunta | Resposta medida | Testes |
| --- | --- | --- |
| 1. N dispatchers reivindicam lotes disjuntos? | **Sim.** 8 instâncias, 25 mil mensagens (5 mil produzidas durante a drenagem), **todas as instâncias com trabalho** (2.946 a 3.302 linhas cada), 0 publicadas 2x. Sob reclaim (lease de 300 ms, 15% dos lotes estourando o lease, falhas no meio do lote): 2.000 reivindicações após lease ou devolução, **nenhuma linha com dois leases válidos sobrepostos**, fence sem buracos | `Q1_*`, `Q1b` |
| 2. Instância morta libera o lote? | **Sim, nas duas janelas testadas.** Morta publicando (claim commitado): volta no lease, 5,1 s com lease de 5 s, nunca antes (limite inferior assertado). Morta com o claim aberto: volta em **0,03 a 0,06 s**, porque o backend abortou a transação — pelo proxy do Docker Desktop, a queda da conexão chegou ao PostgreSQL. **Não testado:** morte durante a marcação, queda de host com TCP meio-aberto | `Q2a_*`, `Q2b` |
| 3. A marcação atrasada é barrada? | **Sim.** Donos diferentes: o antigo marca 0 de 50. **Mesmo owner** (zumbi e sucessor com o mesmo nome): só o fence barra, e barra também a devolução atrasada. Lease vencido sem novo dono: o antigo ainda marca (correto: ninguém mais publicou) | `Q3_*`, `Q3c` |
| 4. Cabeça por chave funciona com `SKIP LOCKED`? | **Sozinha, só com M=1.** M=4: **1.268 a 1.936 inversões** em três execuções depois da correção do A1 (controle negativo, assertado). Com lease por partição: 0 inversões em M=1 e M=4, nos limites descritos em C1 | `Q4_*` |
| 5. Custo do contador por chave? | Chaves distintas: 4.252 tx/s vs 6.540 sem contador. Chave quente: 181 tx/s, p99 ~1,2 s. **Ordem aleatória de chaves: 26 a 34 deadlocks e vazão ~0 em 8 s** (5 chaves e 32 workers: pior caso deliberado); ordenadas: 0. Fundido no fim da transação: chave quente 1.215 tx/s, p99 115 ms | `Q5_*` |
| 6. Vazão com e sem ordenação? | 1.000 chaves, sem latência de broker: por linha 96 mil; por partição 82,8 mil (**−14%**); ordenado M=1 31 mil; M=10 30,6 mil msg/s. Com 20 chaves: ordenado M=1 cai para **1.089 msg/s (2,3 msg/ciclo)**; M=10, 11 mil | `Q6_*` |

## Achados para o contrato da v0.1 (levar ao Escopo)

**A1. A condição de "reivindicável" fica no nível do `FOR UPDATE` e é repetida no `UPDATE` externo.** Ao travar uma linha
que outra transação alterou e commitou, o PostgreSQL reavalia (EvalPlanQual) só os predicados da tabela travada; condição
em subconsulta ou `JOIN` usa a tupla antiga. Vale para qualquer `FOR UPDATE`/`FOR SHARE` com `JOIN` ou subconsulta. Exige
`READ COMMITTED` (em níveis mais altos o resultado seria erro 40001). Encontrado como bug no próprio spike: 1.202 duplicatas
em 10 mil com lease de 30 s (`results/run1/q4.md`). A mesma falha estava na liberação de partições (corrigida; v0.2).

**A2. Token de fencing separado do contador de tentativas.** O Escopo usa uma coluna `attempt` para as duas coisas. Não dá:
se a devolução por falha de transporte desfaz o incremento, o token deixa de ser único; se não desfaz, a falha de transporte
gasta tentativa, contra a G2. Schema do spike: `fence` (sobe a cada claim) e `attempts` (só defeito). Q1b: 2.000 reclaims,
900 devoluções, 0 tentativas consumidas.

**A3. `owner` único por encarnação de processo.** O fence já separa zumbi e sucessor com o mesmo nome (Q3c); o `owner`
único é para diagnóstico e para não depender só do fence.

**A4. Janelas de morte do dispatcher e o custo do failover.** Morte publicando: o lote espera o lease. Morte com o claim
aberto: volta imediatamente. No claim por linha, só as linhas da instância morta esperam; o resto do backlog segue (Q2a:
300 de 500 linhas continuaram fluindo). Recomendado, não medido: `lock_timeout`, `statement_timeout` e
`idle_in_transaction_session_timeout` na conexão de claim, para o caso de host caído com TCP meio-aberto.

**A5. Ordem nunca por timestamp, e lease muito acima do jitter do relógio.** O relógio de parede do container recua
(1 recuo de 0,9 a 1,7 ms em cada sonda de 30 a 60 s, teste `Ambiente_*`).

## Candidatos à v0.2 (não entram no contrato da v0.1)

Insumo para o plano da v0.2, com o grau de evidência de cada um:

| Item | Evidência | Pendente |
| --- | --- | --- |
| Lease por partição, `outbox_partitions` (owner, lease_until, epoch) e `outbox_instances` (heartbeat), fatia justa `ceil(P / vivas)` | Q1 (distribuição ok), Q2a (failover no lease) | P e lease globais no banco e validados no startup; `epoch` gravado e **não usado** — usar no fencing ou remover; GC de instâncias; tempo de rebalanceamento não medido |
| Filtro de cabeça por chave dentro da partição, forma com window em subconsulta + `FOR UPDATE OF o` + A1 | Q4 | **Risco da revisão:** `SKIP LOCKED` pula uma linha-prefixo travada por Mark/Release concorrente e, com M≥2, leva a seguinte — quebra a ordem sem publicação zumbi. Teste de troca de dono com linhas em voo e de bloqueio por `dlq` (hoje, remover essas cláusulas não quebra nenhum teste). Varredura O(backlog): medir com 500 mil a 1 milhão pendentes |
| Contador por chave no fim da transação, fundido no INSERT | Q5 (chave quente ×6,7) | **Conflita com o EF:** o `SavingChanges` roda antes dos comandos do `SaveChanges`, então o contador não fica "fundido" ali. Medir dentro do EF real, e a alternativa de trigger `BEFORE INSERT`. Medir o caso estacionário (UPDATE em chave existente) e o crescimento de `outbox_keys`, que nunca é limpa |
| Fencing não protege a publicação (G4 assimétrica) | Q3b, só como modelo: o publicador falso aceita tudo | Repetir com RabbitMQ real |
| Vazão ordenada ≈ chaves ativas × M por ciclo | Q6 | Default de M |
| Índices: claim em `(id)`; cabeça em `(key, sequence)` incluindo `dlq` | Nenhuma comparação medida | Detalhe de implementação, não contrato |
| Oráculo de ordem do contador | Q5 por `commit_ts`, fraco | Leitor concorrente que asserte prefixo contíguo por snapshot, ou ordem por LSN |
| Q5 mede `INSERT … ON CONFLICT DO UPDATE`; o Escopo especifica `UPDATE … RETURNING` | — | p99 ~1 s da chave quente simples coincide com `deadlock_timeout`; não investigado |

## Ambiente (não muda o contrato)

- Relógio de parede do container não é monotônico (Docker Desktop, WSL2). Sonda reproduzível em `Ambiente_RelogioDoContainer_Monotonicidade`.
- `dotnet test` no .NET 10 exige `global.json` com `"runner": "Microsoft.Testing.Platform"` para o xUnit v3.
- O cenário "ordem aleatória" da Q5 pode estourar o `CommandTimeout` de 30 s do Npgsql; o teste conta isso como timeout.

## Revisão adversarial (2026-10-04)

Uma revisão independente leu o código, os números e os documentos. Corrigido no spike em consequência:

- Bug: liberação de partições em excesso sem `owner = @w` (mesma classe do A1). Corrigido, com a condição repetida também na aquisição.
- Bug: devoluções eram somadas como marcações (`MarkedTotal`); escondido porque nenhum teste falhava a publicação.
- `attempt` separado em `fence` + `attempts` (A2).
- Testes novos: Q1 com distribuição por instância assertada, Q1b (reclaim sob concorrência com oráculo de sobreposição de leases e falha parcial de lote), Q2a com limite inferior, Q2b (claim aberto), Q3c (mesmo owner), controle negativo da Q4 assertado, sonda de relógio.
- Ciclos sem partição passaram a contar na latência de claim (antes favoreciam os modos particionados).
- Textos: causa do C3, faixas entre execuções, ressalva do C2, ADR sem "medido a favor" e com a alternativa de advisory lock por partição.

Da revisão, o que **não** foi feito por ser só da v0.2 está na tabela de candidatos acima.

## Não medido

- Advisory lock como alternativa (descartado por argumento no ADR 0001).
- Morte do dispatcher durante a marcação; host caído com TCP meio-aberto.
- Variações de plano do `UPDATE … WHERE id IN (SELECT … LIMIT … SKIP LOCKED)`.
- Autovacuum e bloat sob carga longa (etapa 5).

## Como reproduzir

```
cd spike && dotnet test --project tests/Spike.Tests     # ~4 min, Docker ligado
```

## Histórico

- `results/run1`: primeira execução — bug A1 (duplicatas na cabeça por chave) e erro de configuração da Q2.
- `results/run2`: execução completa depois da correção do A1, mais a Q5 repetida 6 vezes (variância do C3, recuos de relógio).
- `results/run3-pre-revisao`: execução completa antes das correções da revisão (q4 a q6) e a primeira execução das Q1–Q3 já revisadas (q1-q3, ambiente).
- `results/` (raiz): execução final, 32 de 32 testes passando.
