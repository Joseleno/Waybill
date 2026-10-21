# Etapa 10 — Plano e critérios de aceite

Deriva do `SPEC.md` desta etapa, aprovado pelo autor em Oct 21, 2026. Um PR só, para a `develop`; um commit por cenário provado.

Revisto no mesmo dia por um time de quatro revisores: PostgreSQL, RabbitMQ, API .NET e testes. Os de PostgreSQL e RabbitMQ mediram contra `postgres:18` e `rabbitmq:4-alpine` (4.3.6, RabbitMQ.Client 7.2.2). O que eles mediram está marcado **(medido)**.

## Desenho

| Tema | Decisão |
| --- | --- |
| Schema | `waybill.inbox_keys (handler text, key text, last_seq bigint NOT NULL, last_message_id uuid NOT NULL)`, `pk_inbox_keys (handler, key)`, `WITH (fillfactor = 80)` como a `outbox_keys` (toda mensagem em ordem reescreve a linha da chave, sem mudar coluna indexada: HOT). Acrescentada à `SchemaV0_2` como a `outbox_keys`: SQL idempotente, entidade no `WaybillSchemaContext`, snapshot refeito |
| `Down` | Recusa também com linhas em `inbox_keys`, com `IF to_regclass('waybill.inbox_keys') IS NOT NULL THEN IF EXISTS (...)` aninhado. Um banco que rodou a `SchemaV0_2` antes desta etapa não tem a tabela, e o `EXISTS` direto falharia **(medido)**. Ganha `DROP TABLE IF EXISTS waybill.inbox_keys`. Bancos de desenvolvimento e volumes do exemplo já na `SchemaV0_2` precisam ser recriados (sem tag, a migration ainda muda); o README do exemplo diz isso |
| Permissões | O papel da aplicação precisa de `SELECT`, `INSERT` e `UPDATE` em `waybill.inbox_keys`; vai no `OPERATIONS.md` |
| `MessageOrder` (núcleo) | `public readonly record struct MessageOrder`, não posicional: construtor explícito, propriedades só de leitura `Key` e `Sequence`, igualdade por valor, `with` inútil. Como o `PublishResult`. Regra da chave idêntica à do `Enqueue`: não nula, até 255 UTF-8 bytes, **vazia aceita** (o `Enqueue` aceita, e o transporte manda `waybill-key` vazio com `sequence`: recusar a vazia transformaria em veneno uma mensagem do próprio Waybill). `sequence >= 1`. Lança `ArgumentException` e `ArgumentOutOfRangeException`, checando o tamanho por conta própria (o `EnsureShortString` lança `InvalidOperationException`). `default` é inválido e está documentado |
| `OrderedInboxResult` | `public enum OrderedInboxResult { Processed = 1, Duplicate = 2, Regressed = 3 }`, sem membro 0, como o `PublishStatus`. O XML doc de `Regressed` diz: não confirmar; mandar à DLX; o motivo está no log; pode já ter sido aplicada (cópia tardia depois da retenção) |
| Método | `Task<OrderedInboxResult> ProcessInOrderAsync(string handler, Guid messageId, MessageOrder? order, Func<TContext, CancellationToken, Task> handle, CancellationToken cancellationToken = default)` no `IInbox<TContext>`. O XML doc abre com "detecta regressão; nunca reordena nem segura mensagem". Recusa `order is { Key: null }` (o `default`) logo no início, junto das checagens do handler, antes da estratégia de execução; `<exception>` documentado |
| Corpo comum | Um `ProcessCoreAsync(handler, messageId, MessageOrder? order, handle, ct)` privado devolve `OrderedInboxResult`. O passo da chave fica entre o `inserted == 0` e o `InboxScope.Enter` do `DbContextInbox`. `Regressed` e o `Duplicate` por sequência fazem rollback antes de entrar no escopo. O `ProcessAsync` passa `null` e mapeia o resultado (`Regressed` ali é `UnreachableException`). Cancelamento e exceção do handler: como hoje (rollback e propaga). O `ProcessAsync` continua sem tocar `inbox_keys` |
| SQL da verificação | `INSERT INTO waybill.inbox_keys AS k ... ON CONFLICT (handler, key) DO UPDATE SET last_seq = EXCLUDED.last_seq, last_message_id = EXCLUDED.last_message_id WHERE k.last_seq < EXCLUDED.last_seq`. Uma linha: segue. Nenhuma: o `ON CONFLICT` travou a linha (FOR NO KEY UPDATE, mesmo com o `WHERE` falso **(medido)**), e um `SELECT last_seq, last_message_id` **em comando separado** (`Database.SqlQuery<T>`) lê o valor confirmado mais novo. Igual com o mesmo id: `Duplicate`. O resto: `Regressed` |
| Ordem das travas | Linha de `inbox` → uma linha de `inbox_keys` → `outbox_keys` ordenadas pela lista (se o handler enfileira com a ordenação ligada). Nenhuma transação segura duas linhas de `inbox_keys`; sem ciclo novo **(medido)**. A retenção roda em autocommit com `SKIP LOCKED` e não toca `inbox_keys` |
| Esperas | A espera pela linha da chave conta contra o timeout de comando do Npgsql (30 s por padrão), e o timeout é transitório: a estratégia repete a unidade inteira. Vai no XML doc e no `OPERATIONS.md`, ao lado do `consumer_timeout` |
| Log | `DbContextInbox` vira `internal sealed partial`, com `ILogger<DbContextInbox<TContext>>` pela DI (todo host e teste já chama `AddLogging`; o doc do `AddWaybillInbox` diz que precisa). A estratégia de execução pode repetir a lambda: a lambda devolve os detalhes, e o log sai uma vez, depois do `ExecuteAsync`. `EventId 50`, `Warning`, regressão: `{Handler}`, `{Key}`, `{Sequence}`, `{LastSequence}`, `{MessageId}`, `{LastMessageId}`, `{Regression}` (`Lower` ou `Reused`, interno). `EventId 51`, `Debug`, duplicata por sequência. Inquilino e correlação ficam fora: a aplicação os põe com `logger.BeginScope`, e o `MessageId` basta para cruzar |
| RabbitMQ | `public static MessageOrder? GetWaybillOrder(this IReadOnlyBasicProperties properties)`. Aceita cabeçalho `byte[]` (o que chega do broker **(medido)**) e `string` (propriedades montadas em teste). `long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture)`. Sem `waybill-sequence`: nulo, mesmo com `waybill-key`. `waybill-sequence` sem `waybill-key`, malformado ou < 1: `InvalidOperationException`, validado antes de construir o `MessageOrder` |
| `FakeInbox` | `FakeInboxMemory` guarda `(handler, key) → (last_seq, last_message_id)` e conta `Regressions`. Mesma ordem do real: reserva do `message_id`, depois trava da chave (uma entrega da mesma chave em andamento faz a segunda esperar), depois sequência. Em `Regressed` e no `Duplicate` por sequência, `Release(..., processed: false)` desfaz a reserva, como o rollback |
| API pública e CHANGELOG | Entradas em `PublicAPI.Unshipped.txt` de quatro projetos: núcleo, EF, RabbitMQ e Testing (`FakeInbox.ProcessInOrderAsync`, `FakeInboxMemory.Regressions`). O membro novo no `IInbox` quebra quem implementa a interface já publicada: entrada "Breaking" no CHANGELOG |
| Exemplo | `Billing` liga `OrderByKey` (já enfileira com chave). `Receipts`: `ProcessInOrderAsync` com `GetWaybillOrder`, `switch` sobre os três valores. A política de falha do SPEC: até dez tentativas no consumidor com espera crescente e teto de 30 s (cerca de 2,5 min, para atravessar um reinício do banco; cinco tentativas curtas mandariam à DLX a mensagem em mãos a cada queda de 15 s, decisão da implementação), depois `nack` sem requeue para a DLX, com log de erro. Sai o comentário que confia no `x-delivery-limit` e o cálculo de espera por `x-delivery-count`: o `nack` com requeue não conta entregas **(medido)** |
| `definitions.json` | Na fila: `x-single-active-consumer: true`, `x-dead-letter-strategy: at-least-once`, `x-overflow: reject-publish`. Sem o `reject-publish`, o broker cai em silêncio para at-most-once e só loga **(medido)**. Na fila de mortas: `x-overflow: reject-publish`. Cheia, a morta fica retida na fila de origem (`messages − ready − unacked`) **(medido)**; o limite da quorum é aproximado |
| Argumentos que não pegam | Reimportar o `definitions.json` sobre uma fila existente mantém os argumentos antigos, sem erro nem log **(medido)**. O `smoke.sh` confere `rabbitmqctl list_queues name arguments` e falha com a dica `docker compose down -v` |
| `smoke.sh` | Publica pela API de gerenciamento (porta 15672) uma regressão `Reused`: `message_id` novo, mesma chave, `waybill-sequence` "1" como string. Confere o log `EventId 50`, o `message_id` na `.dead` e que o recibo não duplicou. Uma mensagem ilegível também iria à DLX, então só a presença na `.dead` não prova nada |
| Documentação | `OPERATIONS.md`, seção nova "Sequence verification in the inbox":<br>- as condições do broker, com a armadilha do at-least-once e a métrica da morta retida;<br>- o consumo exigido;<br>- a trava por chave e as esperas;<br>- um produtor e um nome de handler;<br>- a política de falha e o gap;<br>- desligar a ordenação: dali em diante as mensagens saem sem `sequence`, inclusive o backlog numerado antes, e o consumidor só as deduplica;<br>- `nack` com requeue não conta entregas;<br>- a DLX do consumidor como evidência;<br>- a consulta por `message_id`;<br>- as permissões;<br>- o SQL `<!-- inbox-forget-keys -->`, com os consumidores do handler parados.<br><br>O `escopo-e-fronteiras.md` passa a dizer "menor, ou igual com outro `message_id`" e "vai à DLX, com o motivo no log" (ADR 0010) |
| ADR 0010 | Por que o inbox detecta e não reordena; o motivo no log (emenda do escopo); as condições do broker e a política de falha como parte da garantia |

## Testes que provam

PostgreSQL real em todos; RabbitMQ real onde indicado.

Regras para todos:
- "Handler não chamado" é provado por contador de chamadas, não pelo efeito: o rollback esconderia o efeito de um handler que rodou.
- Cada intercalação é forçada: a segunda sessão só é solta depois de aparecer em `pg_stat_activity` esperando trava (`KeyLockHarness`), e o teste confere que o handler dela não entrou enquanto esperava.

| Teste | Cenário | Resultado esperado | Onde roda |
| --- | --- | --- | --- |
| `G4_Inbox_EmOrdem_AplicaEAvancaAChave` | 1, 2, 3 da mesma chave | Três `Processed`; `last_seq` = 3 com o id da 3 | PR |
| `G4_Inbox_AssinaturaParcial_GapNaoPrende` | 2, 5, 9 | Três `Processed` | PR |
| `G4_Inbox_DoisHandlers_SequenciasIndependentes` | h1 em 5; h2 recebe a 3 | `Processed` em h2 | PR |
| `G4_Inbox_ReentregaDepoisDaSeguinte_EhDuplicata` | 1, 2, de novo a 1 | `Duplicate` pelo registro do inbox | PR |
| `G4_Inbox_RegressaoDepoisDaRetencao_NaoAplica` | 1, 2; a retenção apaga os registros do inbox; chega a 1 | `Regressed`; handler não chamado; nada novo em `inbox`; `inbox_keys` intacta; a reentrega dá `Regressed` de novo; log `EventId 50` com todos os campos e `Lower` | PR |
| `G4_Inbox_SequenciaIgualMesmoId_EhDuplicata` | 1, 2; a retenção apaga; chega a 2 de novo | `Duplicate`; handler não chamado; log `EventId 51` | PR |
| `G4_Inbox_SequenciaReusada_EhRegressao` | 1, 2; chega outra mensagem com sequência 2 (produtor restaurado de backup) | `Regressed`, log com `Reused` | PR |
| `G4_Inbox_LiberadaChegaTarde_EhRegressao` | 1, 3 (a 2 foi liberada); chega a 2 | `Regressed`, log `Lower` | PR |
| `G4_Inbox_ConcorrentesDaMesmaChave_NuncaForaDeOrdem` | Duas entregas, 4 e 5; a primeira segura a transação dentro do handler; nas duas ordens de chegada; com chave nova e com chave existente (esperas diferentes: `transactionid` e `tuple` **(medido)**) | A primeira aplica; a segunda espera sem entrar no handler; 4 depois de 5 sai `Regressed`; 5 depois de 4 aplica | PR |
| `G4_Inbox_PrimeiraDaChaveDesfeita_ASegundaAplica` | Primeira entrega de chave nova (5) falha no handler enquanto outra da mesma chave, de sequência menor (4), espera | A 4 aplica; `last_seq` = 4 | PR |
| `G4_Inbox_CommitAmbiguo_ChaveAvancaUmaVez` | Perda do ack do primeiro commit (`LoseFirstCommitAcknowledgement`) | `Duplicate` na repetição; efeito uma vez; `last_seq` certo | PR |
| `G4_Inbox_CommitAmbiguoComASeguinteNoMeio_EhDuplicata` | Perda do ack do commit de s; antes da repetição, s+1 da mesma chave é processada (gancho no interceptor) | `Duplicate`: a deduplicação vem antes da sequência. Na ordem invertida sairia `Regressed` | PR |
| `G4_Inbox_HandlerFalha_NadaGravado_ReentregaProcessa` | O handler lança exceção | Exceção propagada; `inbox` e `inbox_keys` intactas; a reentrega dá `Processed` | PR |
| `G4_Inbox_HandlerEnfileiraOrdenadas_SemDeadlock` | Ordenação ligada; handlers concorrentes, cada um enfileirando num só `SaveChanges` chaves sobrepostas, sem linha de aplicação compartilhada; **estratégia sem repetição** (o Npgsql trata 40P01 como transitório e repetiria em silêncio) | Nenhuma exceção. É guarda de regressão: pela ordem das travas, o ciclo não existe | PR |
| `G4_Inbox_SemOrdem_SoDeduplicacao` | `ProcessInOrderAsync` com `order` nulo, com outra sessão segurando `LOCK TABLE waybill.inbox_keys IN ACCESS EXCLUSIVE MODE` | Termina, sem esperar a trava | PR |
| `G4_Inbox_VerificacaoDesligada_NaoTocaInboxKeys` | `ProcessAsync` com mensagens da mesma chave fora de ordem, com a mesma trava exclusiva e `lock_timeout`. Controle positivo: `ProcessInOrderAsync` com ordem bloqueia nela (`KeyLockHarness`). As estatísticas de `pg_stat_user_tables` chegam atrasadas e não provam nada **(medido)** | Todas `Processed`, sem esperar a trava | PR |
| `G4_Retencao_NaoApagaOutboxKeysNemReleased` (ampliado) | Retenção com tudo expirado | `inbox_keys` intacta | PR |
| `Schema_DownComOrdenacaoUsada_Recusa` (novo caso) | Linhas em `inbox_keys` | `Down` recusado | PR |
| `Schema_DownSemOrdenacaoUsada_VoltaAV01EVolta` (ampliado) | `Down` e `Up` | `inbox_keys` sai e volta | PR |
| `Schema_DownSemInboxKeys_NaoFalha` | Banco sem a tabela (`SchemaV0_2` anterior a esta etapa) | O `Down` não falha por tabela ausente | PR |
| `Operacao_EsquecerSequenciasDoHandler` | O SQL `<!-- inbox-forget-keys -->`, como está escrito | A sequência 1 volta a dar `Processed` naquele handler; as linhas de outro handler continuam | PR |
| `MessageOrder_*`, `GetWaybillOrder_*` (unidade) | Chave nula, vazia (aceita) ou longa; `sequence` 0 ou negativa; `default`; cabeçalho ausente, `byte[]`, `string`, malformado, sem chave, chave sem sequência. Ida e volta: `Enqueue` com chave vazia → propriedades do transporte → `GetWaybillOrder` | Erro, nulo ou valor, como descrito | PR |
| `FakeInboxEquivalenceTests` (ampliado) | Os cenários acima que não dependem de transação, mais a trava por chave (`FakeInbox_EntregaParalelaDaMesmaChave`) | Mesmos resultados no fake e no real | PR |
| `G4_RabbitMq_RequeueDepoisDeFalha` | Fila quorum com a configuração do exemplo, lida do `samples/rabbitmq/definitions.json`; 1 e 2 da mesma chave; prefetch 1; `nack` com requeue da 1, dez vezes; depois canal fechado com a 1 sem ack | A 1 volta antes da 2 em todas **(medido em 4.3.6)**; o `nack` não mexe em `x-delivery-count`; o canal fechado conta. Se o broker mudar, o teste avisa | PR |
| `G4_RabbitMq_RegressaoChegaADlx` | Configuração do exemplo; o consumidor recebe uma regressão e faz `nack` sem requeue; depois, com a fila de mortas cheia, outra | A primeira está na `.dead` com o mesmo `message_id` e `x-death reason=rejected`; a segunda fica retida na origem (prova que o at-least-once está valendo); nada aplicado | PR |
| `G4_Propriedade_RegressaoNaDlx` (caos) | Só mensagens com chave. N dispatchers com a ordenação ligada; um deles zumbi, num segundo proxy do Toxiproxy com latência de subida maior que o lease, ligada e desligada em ciclos; retenção do inbox em zero; um consumidor ativo, sequencial, sem falhas do handler; topologia lida do `definitions.json`. Depois da drenagem, para cada chave, o teste escolhe mensagens aplicadas com `sequence < last_seq`, roda a retenção e publica uma cópia de cada direto na exchange | Aplicadas (pelas linhas de efeito comitadas) estritamente crescentes por chave; cada mensagem aplicada uma vez; o conteúdo da `.dead` é exatamente o conjunto das `Regressed` (ids e contagem), lida só depois que a origem não retém mortas; cada cópia injetada sai `Regressed`; no fim, `last_seq` = maior sequência aplicada por chave; regressões contadas por fonte, e as naturais (do zumbi) > 0 só no agendado | PR: 20 s; agendado: 10 min |

A política de falha do exemplo (o gap depois das tentativas) é documentada e mostrada no `smoke.sh`; não é garantia e não tem teste de concorrência.

## Armadilhas

Cada mutação tem de ser pega pelo teste citado, e fica registrada no commit.

- **Ordem dos testes.** Deduplicação antes da sequência. Inverter: `CommitAmbiguoComASeguinteNoMeio` (o `CommitAmbiguo` simples não pega: na repetição, `last_seq` já é s com o mesmo id).
- **Trava antes do handler.** Testar a sequência depois do handler, ou não travar a chave antes dele: `ConcorrentesDaMesmaChave` (a segunda entrou no handler enquanto devia esperar).
- **`<` no `WHERE`.** Com `<=`, a igual com o mesmo id sobrescreveria e aplicaria: `SequenciaIgualMesmoId`.
- **`last_message_id` sem atualizar:** `SequenciaIgualMesmoId`, que entrega 1, 2 e de novo 2.
- **Rollback da regressão.** Comitar em vez de desfazer: `RegressaoDepoisDaRetencao` (a reentrega sairia `Duplicate`).
- **`handler` fora do alvo do `ON CONFLICT` ou do `SELECT`:** `DoisHandlers_SequenciasIndependentes`.
- **Log omitido ou `Lower` e `Reused` trocados:** as asserções de log de `RegressaoDepoisDaRetencao`, `SequenciaReusada` e `LiberadaChegaTarde`.
- **`FakeInbox`.** Inverter a ordem, usar `<=` ou gravar na regressão: a equivalência.
- **O `SELECT` como CTE do upsert.** Lê o snapshot de antes da espera: num teste, devolveu `last_seq = 3` com 5 já comitado **(medido)**. Sempre comando separado. O `RETURNING old.*` do PostgreSQL 18 resolveria, mas o piso é o 15.
- **Repetição da estratégia de execução.** O `ChangeTracker.Clear()` e o recomeço do zero valem para o caminho novo. O log sai fora da lambda.
- **Cabeçalhos em `byte[]`.** Um `is string` trataria toda mensagem como sem ordem, em silêncio: unidade com `byte[]` e o teste no broker.
- **at-least-once sem `reject-publish`.** Cai para at-most-once em silêncio: `RegressaoChegaADlx` com a fila de mortas cheia.
- **Fixture diferente do exemplo.** A topologia dos testes de RabbitMQ vem do `definitions.json`, não de uma declaração à parte.

## Fora do PLAN

Tudo o que o SPEC deixa fora. A ordem de trabalho fica no `TASKS.md`.
