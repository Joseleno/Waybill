# Etapa 13 — Plano e critérios de aceite

Derivado de `docs/etapas/etapa-13/SPEC.md`, aprovado com as três recomendações em Oct 24, 2026.

## Comportamentos que o desenho respeita (verificados)

- **Os pacotes `0.1.0-alpha` restauram no build do repositório** (Oct 24, 2026): um projeto com `PackageReference ... VersionOverride="0.1.0-alpha"` para `Waybill.EntityFrameworkCore.PostgreSql` e `Waybill.RabbitMQ` resolve os três pacotes da 0.1 do nuget.org, sob o gerenciamento central de versões. A solution inteira compila com ele em Release, com 0 avisos.
- **As dependências da 0.1 são as de hoje:** `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.3 e `Microsoft.Extensions.*` 10.0.0, pelo `.nuspec` do nuget.org. Nada força downgrade no host.
- **O claim da 0.1** (tag `v0.1.0-alpha`, `OutboxStore.ClaimSql`, `private const`) é `status IN ('pending', 'claimed') AND (status = 'pending' OR lease_until < clock_timestamp()) ORDER BY id LIMIT $3 FOR UPDATE SKIP LOCKED`, sem `is_head`, `sequence` nem `next_attempt_at`. Uma constante privada é legível por reflexão (`FieldInfo.GetRawConstantValue`), então o teste usa o texto do pacote, não uma cópia.
- **A marcação da 0.1** grava só `pending`, `claimed`, `published` e `dlq`, todos aceitos pelo `ck_outbox_status` da 0.2. A devolução (`RetrySql`, `ReleaseOwnedSql`) volta a `pending` sem tocar em `next_attempt_at`.
- **A retenção da 0.1** apaga só `status = 'published'`: as linhas `released` da 0.2 ficam para sempre depois de um rollback.
- **O schema da 0.2** dá default ou aceita nulo em toda coluna nova (`is_head DEFAULT true`, as outras anuláveis). O gatilho de numeração só age com `waybill.settings` presente, e os de promoção só com `sequence` não nula.
- **O SQL `<!-- ordering-reset -->` do `OPERATIONS.md`** já é executado como está escrito por `Operacao_ReiniciarOrdenacao`, que lê o bloco do arquivo. Os testes novos seguem o mesmo padrão para o bloco novo.
- **Processo filho:** o `ChaosHost` é o modelo. Ele tem `IsTestHost=true`, sem xUnit, e o projeto de teste o referencia com `ReferenceOutputAssembly="false"`, monta o caminho do dll a partir do próprio `bin` e o roda com `dotnet`.
- **Nenhum teste olha o EventId 20** hoje.

## Desenho

| Peça | Como |
| --- | --- |
| `tests/Waybill.Tests.LegacyHost` | Console com a 0.1 do nuget.org (acima), na solution, referenciado por `Waybill.Tests.Integration` sem saída de assembly, como o `ChaosHost`. Modos por argumento: `produce` (N mensagens por `IOutbox`, uma `SaveChanges` cada, com chave opcional; imprime os ids), `dispatch` (o `AddWaybillDispatcher` da 0.1 hospedado, com um transporte que imprime `PUBLISHED <id>` e confirma, até o stdin fechar), `consume` (o `IInbox` da 0.1 sobre uma fila do RabbitMQ; imprime `PROCESSED` ou `DUPLICATE` com o id) e `claim-sql` (imprime a constante do claim da 0.1) |
| Harness | `LegacyHost` no projeto de integração: sobe o processo, lê o stdout linha a linha, fecha o stdin para parar e mata o processo no `Dispose`. Um tempo-limite por espera, com o stderr do filho na mensagem de falha |
| Oráculo de entrega | Os ids gravados pelas duas versões contra os publicados pelas duas (o `FakeTransport` da 0.2 e o stdout da 0.1): todo id gravado aparece ao menos uma vez, e nenhuma linha fica `pending` ou `claimed` depois do dreno |
| SQL novo no `OPERATIONS.md` | Bloco `<!-- rollback-claim-index -->` em "Upgrading and rolling back": `CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_outbox_claimable_v01 ON waybill.outbox (id) WHERE status IN ('pending', 'claimed');`. É o predicado da 0.1, sob o nome que o `Up` da 0.2 já usa para o índice antigo. Quem volta à 0.2 o apaga à mão: a migration já está no histórico e não roda de novo |
| Log de partida | `LogStarted` (EventId 20) ganha o campo `Ordering`: `Waybill dispatcher {Owner} started; ordering by key is {Ordering}.`, com `off` ou `on (Partitions = P, MaxPerKey = M)`. Um método só: duas mensagens no mesmo EventId quebrariam `EventIds_UnicosEmTodosOsPacotes`, e P e M não têm valor com a ordenação desligada |
| CI | Sem job novo: o `ci.yml` já roda a integração em PG15 e PG18, e o host compila com ela. A restauração da 0.1 vem do nuget.org, como os outros pacotes |

## Testes que provam

| Teste | Cenário | Resultado esperado | Onde |
| --- | --- | --- | --- |
| `Convivencia_JanelaDoUpgrade_01E02Juntas_NenhumaSePerde` | Schema da 0.2, ordenação desligada. Produtor e dispatcher da 0.1 (processo) e da 0.2 (no teste) ao mesmo tempo, 200 mensagens de cada, metade com chave | Todo id gravado é publicado; nada fica `pending` ou `claimed`; as linhas da 0.1 ficam sem `sequence` | Integration |
| `Convivencia_Consumidor01_Recebe02EDeduplica` | Dispatcher da 0.2 com o transporte RabbitMQ publica; consumidor da 0.1 (processo) com o inbox da 0.1; uma cópia republicada à mão | Cada mensagem `PROCESSED` uma vez; a cópia `DUPLICATE`; uma linha no `waybill.inbox` por mensagem | Integration |
| `Convivencia_Rollback_01DrenaBacklogNumerado` | Com a ordenação ligada, backlog numerado de 3 chaves, 5 mensagens cada, sucessoras não marcadas e nenhum dispatcher. Ordenação desligada pelo `<!-- ordering-reset -->`; depois só o dispatcher da 0.1 | As 15 publicadas; nada pendente | Integration |
| `Convivencia_Rollback_LinhaEmEsperaDeReturn_01PublicaNoCicloSeguinte` | Linha `pending` com `next_attempt_at` uma hora à frente; só o dispatcher da 0.1 | Publicada no primeiro ciclo da 0.1 | Integration |
| `Convivencia_Rollback_IndiceDa01_ClaimDa01UsaOIndice` | 20 mil linhas `published` e 100 `pending`; `EXPLAIN EXECUTE` do claim da 0.1 (texto do `claim-sql`) em plano genérico, antes e depois do `<!-- rollback-claim-index -->` | Antes, sem `ix_outbox_claimable_v01`; depois, o plano o usa e não tem `Seq Scan` | Integration |
| `Convivencia_Produtor01ComOrdenacaoLigada_SaiEmOrdem` | Ordenação ligada num dispatcher da 0.2; produtor da 0.1 grava 10 mensagens de uma chave | Numeradas de 1 a 10 pelo gatilho e publicadas nessa ordem | Integration |
| `Convivencia_Dispatcher01ComOrdenacaoLigada_PublicaForaDeOrdem` | Limite: cabeça de uma chave na DLQ, sucessora `pending` sem marca; dispatcher da 0.1 | A 0.1 publica a sucessora com a cabeça parada. Prova a frase "ligue a ordenação só sem dispatcher da 0.1" | Integration |
| `Dispatcher_LogDePartida_DizAOrdenacao` | Teoria, ordenação ligada (P = 4, M = 2) e desligada | Um EventId 20 com o estado na mensagem e no campo `Ordering`; ligada, com P e M | Integration |

## Mutações

| Mutação | Derruba |
| --- | --- |
| `LogStarted` sem o campo `Ordering` | `Dispatcher_LogDePartida_DizAOrdenacao` |
| Bloco `<!-- rollback-claim-index -->` com o predicado da 0.2 (`AND (sequence IS NULL OR is_head)`) | `Convivencia_Rollback_IndiceDa01_ClaimDa01UsaOIndice` |
| Transporte do `LegacyHost` que confirma sem imprimir um id em cada cem (o oráculo precisa ver a perda) | `Convivencia_JanelaDoUpgrade_01E02Juntas_NenhumaSePerde` |
| Gatilho `outbox_sequence` desabilitado antes de a 0.1 gravar | `Convivencia_Produtor01ComOrdenacaoLigada_SaiEmOrdem`: as linhas da 0.1 ficam sem `sequence`. Mostra que a ordem das mensagens da 0.1 vem do banco, não do pacote |

## Armadilhas

- **Cache do NuGet:** existe um `0.1.0-alpha.local` no cache global da máquina. A versão do host é `0.1.0-alpha` exata, e o teste confere a `AssemblyInformationalVersion` do `Waybill.dll` carregado pelo filho (impressa no início do stdout) contra `0.1.0-alpha+`.
- **`Task.Delay` curto no Windows dura ~15 ms:** esperas do harness por linha do stdout, nunca por tempo.
- **Plano genérico:** `PREPARE` e `EXPLAIN EXECUTE` com `plan_cache_mode = force_generic_plan`. Com poucas linhas o planejador escolhe `Bitmap Index Scan`: asserir o nome do índice e a ausência de `Seq Scan`.
- **Processo filho que não morre** segura o banco de teste: `Kill(entireProcessTree: true)` no `Dispose`.
- **Logs do filho:** o host desliga os provedores de log, como o `ChaosHost`. Só o protocolo vai ao stdout.
- **Inbox da 0.1:** a chave é `(handler, message_id)`, a mesma da 0.2. O `inbox_keys` não é tocado pela 0.1.

## Fora do PLAN

- Consumidor da 0.2 lendo mensagens publicadas pela 0.1: o caso comum no rollout é o consumidor atrasado, que o `Convivencia_Consumidor01_Recebe02EDeduplica` cobre. O inverso só acontece com um produtor que volta para trás, e fica sem teste.
- Carga e caos com as duas versões: os cenários acima são determinísticos.
- Os três itens da migration na #21 (SPEC).
