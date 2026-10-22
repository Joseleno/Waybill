# 8c-1 — Protótipo medido do contador

Oct 7, 2026. Primeira tarefa da 8c-1 (`PLAN.md`): medir no EF real o custo do contador por chave e escolher como o trigger trava a lista de chaves. Insumo do ADR 0008 e do `OPERATIONS.md`. O programa foi descartado; o método e o SQL estão aqui para que a medição possa ser refeita.

## Método

- **Ambiente:** EF Core 10.0.4, Npgsql EF 10.0.3, .NET 10, PostgreSQL 18 (`postgres:18-alpine`) em Docker no Windows 11, na mesma máquina dos produtores. `max_connections = 200`, `log_lock_waits = on`, `deadlock_timeout = 1s` (e 100 ms numa rodada de controle).
- **Carga:** 32 produtores concorrentes por 10 s em cada célula, com um `DbContext` novo por transação. Cada transação insere uma linha numa tabela da aplicação e enfileira na outbox (payload de 64 bytes). Os erros 40P01 e 40001 são contados, e a transação não é repetida.
- **Banco:** recriado a cada célula. A tabela `outbox` tem `id`, `key`, `sequence`, `lock_keys text[]` e `payload`. `outbox_keys (key text PK, seq bigint)` usa `fillfactor = 80`.
- **Interceptor:** um interceptor de `SavingChanges`, sem I/O e sem estado, grava em `lock_keys` as chaves distintas do `SaveChanges` quando são duas ou mais.
- **Conferência ao fim de cada célula:** por chave, a `sequence` vai de 1 a n sem buraco nem repetição, e `outbox_keys.seq` é igual ao máximo. `lock_keys` fica nula em toda linha gravada.

**Variantes**

| Variante | O que roda no INSERT da outbox |
| --- | --- |
| V0 | Nada (sem trigger) |
| Voff | O trigger da V2b com a ordenação desligada (sem a linha de `settings`) |
| V1 | Trigger que só numera: upsert `seq + 1 RETURNING seq INTO NEW.sequence` |
| V2a | V1 mais a trava da lista: `INSERT … SELECT DISTINCT … ORDER BY key COLLATE "C" ON CONFLICT DO UPDATE SET seq = k.seq` |
| V2b | V1 mais a trava da lista em duas etapas: `INSERT … ORDER BY key COLLATE "C" ON CONFLICT DO NOTHING`, depois `SELECT … WHERE key = ANY(lista) ORDER BY key COLLATE "C" FOR UPDATE` |

Nas V2, a lista é travada uma vez por `SaveChanges`. Um marcador `set_config('waybill.locked_keys', lista, true)` evita repetir a trava nas linhas seguintes, e depois disso `NEW.lock_keys := NULL`.

**Cargas**

| Carga | Chave de cada transação |
| --- | --- |
| distinct-new | Uma chave nova por transação (primeiro uso) |
| distinct-existing | Uma chave sorteada entre 100 mil já existentes em `outbox_keys` |
| hot | A mesma chave em todas |
| hot+work | A mesma chave, numa transação explícita que faz 2 ms de trabalho (`pg_sleep`) depois do `SaveChanges` |
| multi-key | Três chaves distintas sorteadas entre 20, em ordem aleatória |
| multi-key-mixed | Até três chaves sorteadas entre 2000, num conjunto que começa vazio (chaves novas e existentes na mesma lista) |

## Resultados

Zero violações de sequência e `lock_keys` nula em todas as células. Os números abaixo valem só como comparação entre variantes nesta máquina, não como valores absolutos.

**Primeira rodada (uma execução por célula)**

| Carga | V0 tx/s, p99 | V1 tx/s, p99 | V2a tx/s, p99 | V2b tx/s, p99 | Deadlocks |
| --- | --- | --- | --- | --- | --- |
| distinct-new | 4693, 14,9 ms | 4420, 13,0 ms | 4240, 12,3 ms | 4299, 11,6 ms | 0 |
| distinct-existing | 4824, 10,7 ms | 4448, 11,2 ms | 4243, 12,4 ms | 4272, 11,8 ms | 0 |
| hot | 4948, 10,6 ms | 347, 606 ms | 344, 619 ms | 347, 579 ms | 0 |
| hot+work | 2804, 17,7 ms | 171, 1166 ms | 172, 1228 ms | 169, 1113 ms | 0 |
| multi-key | 4678, 11,7 ms | **4, 22 100 ms** | 575, 1131 ms | 601, 626 ms | **26 na V1**; 0 nas outras |

Controle da chave quente na V1 com `deadlock_timeout = 100ms`: 350 tx/s, p99 de 692 ms.

**Segunda rodada (três execuções intercaladas por célula; média de tx/s, faixa do p99)**

| Carga | V0 | Voff | V1 | V2a | V2b |
| --- | --- | --- | --- | --- | --- |
| distinct-new | 4748 tx/s; 11,0–12,6 ms | 4734 tx/s; 10,6–10,9 ms | | | |
| multi-key | 4594 tx/s; 10,9–11,4 ms | 4395 tx/s; 11,0–11,8 ms | | 567 tx/s; 1049–1131 ms | 603 tx/s; 652–710 ms |
| multi-key-mixed | | | 4072 tx/s; 13,9–14,9 ms | 3901 tx/s; 14,5–15,0 ms | 3811 tx/s; 15,0–16,0 ms |

Nenhum deadlock, erro de serialização ou outro erro na segunda rodada. A V0 variou ±8% entre execuções em distinct-new.

## Leitura

- **O custo do contador com chaves distintas** é de 5% a 12% de vazão, com p99 igual ao da V0. O spike mediu +16% a +70% de p99 com Npgsql puro. A diferença vem da base: com o EF, a transação já custa ~7 ms de p50, e o upsert pesa pouco em proporção. Para o `OPERATIONS.md` vale a comparação relativa a esta base, não o número absoluto.
- **Com a ordenação desligada, o trigger não custa nada mensurável** com uma chave por transação (−0,3%, dentro do ruído). Com três linhas por transação, custa −4,3%, perto do limite de 5% do critério de custo. O critério oficial é medido contra a tag `v0.1.0-alpha` no job agendado. Se passar do limite ali, a primeira coisa a mexer é encurtar o caminho do trigger sem `settings`, por exemplo com uma condição `WHEN` que não chame a função.
- **Chave quente:** ~347 tx/s por chave, com p99 de ~600 ms, em todas as variantes com contador. É o teto de uma chave cuja trava é serializada até o commit.
  - Com 2 ms de trabalho depois do `SaveChanges`, a vazão cai pela metade (171 tx/s) e o p99 passa de 1 s. O tempo que a trava fica presa domina, o que confirma a regra de enfileirar no último `SaveChanges`.
  - O p99 de ~1 s do spike não vem do `deadlock_timeout`: com 100 ms, o p99 não mudou. A cauda é a fila da trava da linha, que não é justa entre os que esperam.
- **A lista de chaves é necessária.** Sem ela (V1), três chaves de 20 por transação deram 26 deadlocks e 4 tx/s em 10 s. Com ela (V2a e V2b), zero deadlocks em todas as execuções. Com 2000 chaves e pouca disputa, nem a V1 deadlocou: o problema aparece com a disputa.
- **Escolha: V2b.**
  - Sob disputa, teve 37% menos p99 que a V2a (652–710 contra 1049–1131 ms) e 6% mais vazão.
  - Sem disputa, custa ~2% de vazão a mais pelo comando extra, e só nas transações com duas ou mais chaves.
  - Não grava versão nova de linha para chave existente, o que reduz o inchaço de `outbox_keys`.
- **Por que as duas etapas da V2b não fazem deadlock** (vai para o ADR 0008):
  - Na primeira etapa, a transação só espera por uma chave nova que outra está inserindo, e enquanto espera segura apenas chaves novas que ela mesma inseriu, todas menores na ordem.
  - Ao fim da primeira etapa, toda chave da lista ou já existia commitada, ou foi inserida pela própria transação, ou teve o inseridor terminado. Por isso o `FOR UPDATE` da segunda etapa nunca encontra uma inserção alheia ainda não commitada, que ele pularia.
  - A segunda etapa trava em ordem.
  - Em 9 execuções com chaves novas e existentes misturadas, não houve nenhum deadlock.
