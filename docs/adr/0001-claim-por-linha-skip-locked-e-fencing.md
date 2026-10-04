# ADR 0001 — Claim por linha com `FOR UPDATE SKIP LOCKED`, lease na linha e token de fencing

- Status: proposto (spike da etapa 0, revisado)
- Data: 2026-10-04
- Evidência: [`spike/RESULTADOS.md`](../../spike/RESULTADOS.md), testes em `spike/tests/Spike.Tests/`: `Q1`, `Q1b`, `Q2a`, `Q2b`, `Q3`, `Q3c`

## Contexto

Várias instâncias do dispatcher leem a mesma tabela de outbox. Cada mensagem precisa ser publicada por uma instância
de cada vez, a publicação acontece fora da transação do banco (a confirmação do broker pode levar segundos) e uma
instância pode morrer em qualquer ponto: com o claim aberto, publicando ou marcando.

Estavam em aberto: como reivindicar linhas (`SKIP LOCKED` ou advisory lock) e se o lease por partição entra já na
v0.1 (o plano previa "claim por partição desde a v0.1" para a ordenação da v0.2 não reescrever o dispatcher).

## Decisão (v0.1)

1. **Claim por linha** em transação curta, `READ COMMITTED`:
   `UPDATE outbox SET status = 'claimed', owner = @w, fence = fence + 1, lease_until = clock_timestamp() + @lease
   WHERE id IN (SELECT id … WHERE <reivindicável> ORDER BY id LIMIT n FOR UPDATE SKIP LOCKED) AND <reivindicável> RETURNING …`.
   Publicação fora da transação.
2. **A condição de reivindicável fica no nível que tem o `FOR UPDATE` e é repetida no `UPDATE` externo.** Ao travar
   uma linha que outra transação alterou e commitou, o PostgreSQL reavalia (EvalPlanQual) só os predicados da tabela
   travada; uma condição em subconsulta ou `JOIN` usa a tupla antiga. Vale para todo `FOR UPDATE`/`FOR SHARE` com
   `JOIN` ou subconsulta. Em `REPEATABLE READ` ou `SERIALIZABLE` o resultado seria erro 40001, por isso `READ COMMITTED` é exigido.
3. **Token de fencing separado do contador de tentativas.** `fence` sobe a cada claim e nunca desce; `attempts` conta
   só tentativas consumidas por defeito da mensagem. Marcação e devolução exigem `owner = @w AND fence = @f`.
   Lease vencido e falha de transporte não consomem tentativa. Com uma coluna só (`attempt`, como estava no Escopo),
   ou o token deixa de ser único, ou a falha de transporte gasta tentativa — o contrato exige as duas coisas.
4. **`owner` único por encarnação de processo** (por exemplo, host + pid + guid de inicialização). Com nome estável de pod,
   um processo zumbi e o seu sucessor teriam o mesmo `owner`; o `fence` ainda os separa (Q3c), mas o diagnóstico fica ambíguo.
5. **Lease por partição não entra na v0.1.** O claim grava a coluna `partition` no INSERT (barato) e não a usa.
   O lease por partição, com as tabelas de partição e de instâncias, vai para a v0.2 junto com a ordenação, como camada
   por cima deste mesmo claim (o spike mostrou que ele se acrescenta como `AND partition = ANY(@minhas)` sem reescrever o claim).

## Por que `SKIP LOCKED` e não advisory lock

Não houve medição contra advisory lock; a decisão é por argumento.

| Alternativa | Por que não |
| --- | --- |
| Advisory lock de transação por linha | Só vale com a transação aberta; manter a transação aberta durante a publicação contraria o contrato ("publicar fora da transação"): conexão presa, `idle in transaction`, vacuum segurado |
| Advisory lock de sessão por linha | Não funciona atrás do PgBouncer em modo transaction, que o público-alvo usa. Usa a tabela de locks compartilhada; com lotes de 100 isso não é decisivo |
| Advisory lock de sessão **por partição**, como posse | É a alternativa mais forte: dispensaria tabelas de partição e de instâncias, heartbeat e fatia justa, e o lock morre com a conexão (vantagem, não defeito). Cai pelo PgBouncer em modo transaction, pela ausência de fencing (não há token que barre a marcação de quem perdeu a sessão) e porque uma conexão TCP meio-aberta segura o lock até o keepalive do kernel, que por padrão leva horas |

Advisory lock não impede, por si, colunas de `owner` e `fence`; o argumento não é "falta rastro", é que a posse que
precisa valer **durante a publicação fora da transação** tem de estar nos dados, com expiração e token.

O que os testes mostram do mecanismo escolhido (não é comparação):

- 8 instâncias, 25 mil mensagens, todas as instâncias com trabalho, 0 reivindicações duplicadas (Q1).
- Com lease de 300 ms, lotes lentos e falhas no meio do lote: 2.000 reivindicações após lease ou devolução, nenhuma linha com dois leases válidos sobrepostos, nenhuma tentativa consumida (Q1b).
- Morte publicando: o lote volta no lease (5,0 s com lease de 5 s), não antes (Q2a). Morte com o claim aberto: as linhas voltam em ~0,06 s, porque o backend aborta a transação (Q2b).
- Marcação atrasada barrada pelo `owner` (Q3) e, com o mesmo `owner`, só pelo `fence` (Q3c).

## Consequências

- O schema da v0.1 troca `attempt` por `fence` + `attempts`. A coluna `partition` existe desde a v0.1 para o upgrade ser aditivo; `sequence` também precisa ser reservada se a v0.2 não puder reescrever linhas pendentes.
- No failover, só as linhas reivindicadas pela instância morta esperam o lease; o resto do backlog segue (Q2a, modo por linha: 300 de 500 linhas continuaram fluindo). Com lease por partição, todas as chaves da instância morta esperariam o lease inteiro.
- O lease precisa ficar muito acima do jitter do relógio de parede (medido no Docker/WSL2: recuos de até ~1,7 ms) e acima do timeout de confirmação + margem, como o Escopo já diz.
- Recomendado, não medido: `lock_timeout`, `statement_timeout` e `idle_in_transaction_session_timeout` na conexão de claim, para que um backend preso não segure linhas além do esperado.
- A forma `UPDATE … WHERE id IN (SELECT … LIMIT n FOR UPDATE SKIP LOCKED)` depende de o planner não reexecutar a subconsulta. A repetição da condição no `UPDATE` externo limita o dano; a etapa 3 deve testar variações de plano ou usar `WITH … AS MATERIALIZED`.

## Para a v0.2 (fora desta decisão)

O spike também exercitou lease por partição e filtro de cabeça por chave. Ficam como insumo do ADR da v0.2, não como decisão:

- Cabeça por chave sozinha só é segura com M=1; com M=4 houve 1.268 a 1.936 inversões em três execuções. Com lease por partição, 0 inversões em M=1 e M=4 — mas sem troca de dono durante o teste.
- Riscos abertos da revisão: `SKIP LOCKED` pula uma linha-prefixo travada por um Mark/Release concorrente e, com M≥2, leva a seguinte (quebra a ordem sem publicação zumbi); `epoch` gravado e não usado no fencing; P e lease precisam ser globais e validados no startup; GC de `outbox_instances`; varredura do filtro de cabeça é O(backlog pendente).
