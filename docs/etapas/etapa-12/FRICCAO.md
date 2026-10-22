# Etapa 12 — Fricção encontrada

Fonte: um agente sem contexto recebeu só o `README.md` e o `docs/OPERATIONS.md` da branch, e um feed local com os pacotes `0.2.0-alpha.local.2` (Oct 22, 2026). Não leu o código-fonte; consultou só os XML docs dos pacotes, como numa IDE. **As três tarefas funcionaram, sem bloqueio:**

1. **Primeiro evento** num projeto novo, com o inbox (`Processed`).
2. **Ordenação:** cinco eventos de uma chave em transações separadas, aplicados em ordem com `ProcessInOrderAsync`. Uma cópia republicada à mão voltou `Duplicate`; com a retenção do inbox em 5 s, a mesma cópia voltou `Regressed`, com o aviso EventId 50, e chegou à fila de mortas.
3. **Upgrade:** um projeto na `0.1.0-alpha` do nuget.org, com cinco eventos pendentes por falta da exchange, subiu para o feed local sem mudar código, migrou em ~4 s (nenhum índice inválido) e publicou o backlog.

## Fricções e destino

| # | Fricção | Severidade | Destino |
| --- | --- | --- | --- |
| 1 | O XML doc do parâmetro `key` de `IOutbox.Enqueue` dizia "once ordering ships (v0.2)" | errado | Corrigido em `src/Waybill/IOutbox.cs` |
| 2 | `MaxPayloadBytes` obrigatório: o host não sobe sem ele, e nem o README nem a tabela de defaults diziam | irritante | README e linha "none: required" na tabela de defaults do `OPERATIONS.md` |
| 3 | Os argumentos da fila ordenada só apareciam por link para o `definitions.json`; "quorum" não era dito | irritante | README: os argumentos da fila, da fila de mortas e o prefetch 1, no próprio texto |
| 4 | O caminho de upgrade ficava dentro da seção da ordenação, onde quem não quer ordenar não procura | irritante | `OPERATIONS.md`: seção "Upgrading from 0.1" no topo, com os passos numerados |
| 5 | Não estava dito que o `IInbox<T>` é scoped e que um consumidor hospedado precisa de um escopo por entrega | menor | README, na seção do inbox |
| 6 | Nada no log confirma que a ordenação ligou (a partida diz só "dispatcher started") | menor | Fica como backlog: mudança de código, fora desta etapa |
| 7 | O README não traz um exemplo de declarar exchange, fila e binding | menor | Mantido: o exemplo do repositório faz isso, e o pacote não cria topologia de propósito |
| 8 | Os links do README apontam para a `main`, que ainda mostra a 0.1 | sem efeito | Some no merge da release na `main` |
| 9 | Os arquivos XML dos pacotes documentam tipos internos | menor | Mantido: não aparecem no IntelliSense |

Também verificado pelo agente, fora do pedido: produtores e um dispatcher da 0.1 continuaram funcionando sobre o schema da 0.2, com a ordenação desligada. Não há teste disso no repositório, então não vira promessa na documentação.

**Pontos positivos relatados:** a API em dez linhas roda como está escrita; os logs de exchange inexistente e de regressão dizem a causa e o que fazer; a migração não exigiu mudança de código e tem a consulta de conferência.
