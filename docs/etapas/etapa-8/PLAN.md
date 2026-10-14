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

## 8b — lease por partição

**Desenho**

| Tema | Decisão |
| --- | --- |
| Opções | `WaybillOptions.OrderByKey` (desligada por padrão), lida pelo dispatcher nesta PR e pelo enfileiramento na 8c. `WaybillDispatcherOptions.Partitions` (P, default 16, de 1 a 1024) e `PartitionLease` (default 60 s, pelo menos o dobro do lease da linha, no máximo um dia) |
| Configuração global | Tabela `waybill.settings`, de uma linha, que existe só enquanto a ordenação está ligada: P e `PartitionLease`. A primeira instância com a ordenação ligada cria a linha e as P linhas de `outbox_partitions` num comando só (`INSERT … ON CONFLICT DO NOTHING`); as outras comparam. Divergência é erro crítico, como a verificação de `READ COMMITTED`: o laço para e o health check acusa. Uma instância com a ordenação desligada que encontra a linha também para, no startup e a cada `PartitionLease`, porque reivindicaria sem filtro de partição ao lado de quem ordena. Ligar, desligar ou mudar P exige parar todos os dispatchers; o SQL fica no `OPERATIONS.md` |
| Tabelas | `outbox_partitions (partition, owner, epoch, lease_until)` e `outbox_instances (owner, heartbeat_at)` |
| Ciclo | No início de cada ciclo, inclusive com o breaker aberto: heartbeat da instância; renovação das partições que ainda são dela (`owner = eu`, lease não vencido); cálculo da fatia justa, `ceil(P / instâncias vivas)`; aquisição de partições livres ou vencidas até a fatia (`FOR UPDATE SKIP LOCKED`, condição repetida no `UPDATE` externo, `epoch + 1`); devolução das que passam da fatia. Instância viva = heartbeat mais novo que `PartitionLease`; a coleta apaga as mais velhas que dez vezes isso. A devolução só acontece entre lotes, sem nada em voo |
| Claim | Ganha `key IS NULL OR EXISTS (posse válida da partição key_hash % P por esta instância, com lease_until > clock_timestamp() + lease da linha)`. Mensagem sem chave não é ordenada e não espera partição |
| Por que um `EXISTS` na tabela de posse | O SPEC previa só `key_hash % P = ANY(@minhas)`, sem tocar a tabela de posse. Não basta: uma instância pausada (GC, VM congelada) depois de renovar pode acordar com `@minhas` velho, depois de outra ter assumido a partição, e as duas reivindicariam a mesma chave ao mesmo tempo, que é a inversão do spike com M ≥ 2. O `EXISTS` lê a posse no snapshot do comando. A armadilha do ADR 0001 (a reavaliação do `FOR UPDATE` usa a tupla antiga das outras tabelas) não morde aqui: nenhuma outra instância pode assumir a partição antes do `lease_until`, e o claim exige que ele dure mais que o lease da linha a partir do relógio do próprio comando |
| `epoch` | Identifica um mandato: sobe a cada troca de dono. Não entra no fencing da linha, porque o fencing da linha (`owner`, `fence`) e o `EXISTS` acima já impedem claim fora do mandato e marcação atrasada. Serve ao transporte (o producer por partição da etapa 9 é recriado quando o `epoch` muda) e ao diagnóstico. Vai para o ADR 0007 com essa prova |
| Shutdown | Devolve as partições e apaga a própria linha de instância depois de devolver as linhas; a fatia é redistribuída no ciclo seguinte de quem fica, sem esperar o lease |

**Testes que provam**

| Teste | Cenário | Resultado esperado | Onde roda |
| --- | --- | --- | --- |
| `G4_OrdenacaoDesligada_ClaimDaV01SemTabelasDePosse` | Ordenação desligada | Nenhuma linha em `settings`, `outbox_partitions` ou `outbox_instances`; claim como antes | PR |
| `G4_PrimeiraInstancia_CriaConfiguracaoEParticoes` | Primeira instância com a ordenação ligada | `settings` com P e lease; P partições; a instância fica com todas | PR |
| `G4_PDiferenteDoBanco_ErroCriticoENaoReivindica` | Segunda instância com P ou `PartitionLease` diferente | Erro crítico que diz o que mudar; nenhuma linha reivindicada; health check `Unhealthy` | PR |
| `G4_OrdenacaoDesligadaComConfiguracaoNoBanco_Para` | Instância sem ordenação diante de `settings` existente, no startup e depois de criada com ela rodando | Para com erro crítico nos dois casos | PR |
| `G4_ClaimSoDasParticoesDaInstancia` | Duas instâncias, chaves espalhadas, mais mensagens sem chave | Cada uma publica só as chaves das próprias partições; as sem chave saem por qualquer uma | PR |
| `G4_DonoAntigoDaParticao_NaoReivindica` | A instância renova, a partição vence por SQL, outra a assume, e a primeira reivindica com a lista velha | Nenhuma linha daquela partição vai para a primeira | PR |
| `G4_FatiaJusta_ConvergeAoEntrarESair` | P = 16; três instâncias entram, uma sai com shutdown, outra morre | Cada uma com no máximo `ceil(P / N)` e todas as P com dono (6/6/4); depois 8/8 já no ciclo seguinte ao shutdown; as partições da morta voltam depois do `PartitionLease`; a morta sai de `outbox_instances` pela coleta | PR |
| `G4_EntradaESaidaSobCarga_NenhumaParticaoComDoisDonos` | Oito instâncias entrando, saindo e morrendo durante a carga, lease curto | Oráculo por trigger em `outbox_partitions`: nenhum par de mandatos válidos sobrepostos na mesma partição; nada perdido | PR: 20 s; agendado: 10 min (caos) |
| `Operacao_ReiniciarOrdenacao` | O SQL de limpeza do `OPERATIONS.md`, executado como está escrito | Depois dele, sobem uma instância com outro P e uma sem ordenação | PR |
| `G4_InstanciasSobemJuntas_TodasLeemAConfiguracao` (da revisão) | Dezesseis instâncias criam a configuração ao mesmo tempo, cinco rodadas | Todas leem a mesma configuração; nenhuma falha | PR |
| `G4_EsperaEntreCiclosMaiorQueOLease_NaoPerdeParticoes` (da revisão) | `PollingInterval` de 5 s com `PartitionLease` de 700 ms, ocioso | As partições seguem no primeiro mandato | PR |
| `G4_LotesCheiosEmSequencia_ManutencaoEspacada` (da revisão) | Dez lotes cheios em sequência | Uma manutenção só | PR |
| `Dispatcher_PartitionLease_SoValidadoComOrdenacao`, `Dispatcher_IntervaloForaDoLimite_*` (unidade) | P fora de 1 a 1024; `PartitionLease` menor que o dobro do lease da linha, só com a ordenação ligada | O host não sobe, com mensagem que diz o que mudar | PR |

**Fora da 8b:** filtro de cabeça, contador, `sequence`, M, liberação de chave e o teste de propriedade de ordem (8c). Com a ordenação ligada e só a 8b, as partições já valem, mas a ordem ainda não é garantida; nada disso sai em release antes da 8c.

## 8c — cabeça por chave

Desenho revisto em Oct 7, 2026, em duas rodadas de revisão. Primeiro, um revisor adversarial e uma sondagem no EF 10 + Npgsql 10 + PostgreSQL 18. Depois, um time de cinco revisores (concorrência, testes, EF e API, operação, escopo). As decisões marcadas "autor" foram tomadas com ele nessa data. A 8c sai em dois PRs: **8c-1** entrega a G4 com M = 1; **8c-2** acrescenta M ≥ 2 em rodadas. A G4 não depende de M, que só amortiza o claim e a marcação.

**Fatos verificados**

- Na sondagem (EF 10.0.4, Npgsql EF 10.0.3, PG 18):
  - O `SavingChanges` roda sem transação, e um `SaveChanges` de um comando só não abre transação.
  - Várias linhas de outbox viram um INSERT por linha, num comando só, em ordem de `id`, com transação.
  - Os comandos da aplicação (schema padrão) vêm antes dos INSERTs da outbox (`waybill`).
  - Travar as chaves no começo do comando faz deadlock com um `SaveChanges` de uma chave que toca a mesma linha da aplicação. Reproduzido.
  - Em `REPEATABLE READ`, o upsert numa chave incrementada por outra transação depois do snapshot falha com 40001.
- No código decompilado do EFCore.PG 10.0.3 (`NpgsqlModificationCommandBatch.Consume`): cada instrução sem `RETURNING` tem de afetar exatamente uma linha, senão o EF lança `DbUpdateConcurrencyException`. Nenhuma instrução pode ser anteposta ao lote do EF, nem mesmo um `SET`.
- No código decompilado do EF 10.0.4: o `SavingChanges` roda uma vez, fora da estratégia de execução. O `TransactionStarted` roda a cada tentativa. Uma exceção no `SavingChanges` não chama o `SaveChangesFailed`.
- No código do Waybill:
  - O laço do dispatcher só emenda o ciclo seguinte com o lote cheio (`WaybillDispatcherService.cs:85`).
  - O `RabbitMqTransport` publica o lote em paralelo (`Task.WhenAll`).
  - O SQL `ordering-reset` apaga `settings`.

### 8c-1 — G4 com M = 1

**Desenho**

| Tema | Decisão |
| --- | --- |
| Quem numera (autor) | O banco. Trigger `BEFORE INSERT` na `outbox`, `WHEN (NEW.key IS NOT NULL)`: se a linha de `settings` existe, faz o upsert em `outbox_keys (key text PK, seq bigint)` e grava `seq + 1` em `NEW.sequence`; se não existe, deixa `sequence` nula. A aplicação não grava `sequence`. A função é `SECURITY DEFINER` com `SET search_path = pg_catalog, waybill`, para que um papel só com INSERT na outbox continue enfileirando. `outbox_keys` tem `fillfactor = 80` (o incremento cabe em HOT) e nunca é limpa: nem pela retenção, nem por SQL de operação, porque recomeçar em 1 seria regressão para o consumidor |
| Ordem de commit | O upsert roda na instrução do INSERT e a trava da chave fica até o commit. A `sequence` segue a ordem de commit e não deixa buraco no rollback nem no savepoint do EF. A transação anterior sai do ProcArray antes de soltar a trava, então todo snapshot que vê `n` vê `n − 1` |
| Lista de chaves contra deadlock (autor) | Coluna `lock_keys text[]`, mapeada no `OutboxRecord`. Um interceptor de `SavingChanges`, sem I/O e sem estado, grava nas linhas Added a lista das chaves distintas do `SaveChanges` quando há duas ou mais. O trigger, se a lista vem e ainda não foi travada nesta transação, trava as chaves em ordem em duas etapas, escolhidas pelo protótipo (`PROTOTIPO-CONTADOR.md`): `INSERT … ORDER BY key COLLATE "C" ON CONFLICT DO NOTHING` para as novas, depois `SELECT … ORDER BY key COLLATE "C" FOR UPDATE` e grava um marcador com `set_config(…, true)`. Em seguida, `NEW.lock_keys := NULL`. A trava cai no primeiro INSERT da outbox, o mesmo ponto do caso de uma chave. A lista vai como parâmetro (sem injeção), sem ida a mais. Sobrevive ao retry da estratégia de execução, e o rollback a savepoint desfaz junto a trava e o marcador. Funciona com pooling, `UseTransaction` e síncrono/assíncrono. É incondicional: com a ordenação desligada, o trigger só anula a coluna. O interceptor é registrado pelo `AddWaybillOutbox` (`ConfigureDbContext`). Revisto ao implementar: um contexto sem ele gera um aviso, uma vez por tipo, em vez de recusa, porque sem ele nada se perde nem sai de ordem (só pode haver deadlock repetível) e contextos montados à mão são o jeito de testar com os fakes do `Waybill.Testing`, que compartilham o `Verify`. Ordem de upgrade: migration da v0.2 antes do deploy de quem enfileira, porque o INSERT passa a ter a coluna |
| Ids na ordem do `Enqueue` (achado ao implementar) | O trigger numera na ordem em que o EF insere, que é a ordem de `id`, e dois UUIDv7 do mesmo milissegundo comparam em ordem aleatória: numa rajada de 200 mensagens de uma chave numa transação, 197 receberam `sequence` fora da ordem do `Enqueue`. O `message_id` passa a ser um UUIDv7 monotônico no processo (contador de 12 bits em `rand_a`, RFC 9562, 6.2, método 1), ainda gerado no cliente |
| `OrderByKey` | Sai de `WaybillOptions` e vai para `WaybillDispatcherOptions`, ainda em `PublicAPI.Unshipped`, ao lado de `Partitions` e `PartitionLease`. Do lado de quem enfileira não sobra opção |
| Limite da chave | O `Enqueue` já recusa chave acima de 255 bytes UTF-8 desde a v0.1 (`EnvelopeFactory.EnsureShortString`, short string do AMQP), bem abaixo dos ~2,7 kB que estourariam o btree de `outbox_keys`. A decisão de 512 bytes tomada com o autor na revisão ficou sem efeito: o revisor que levantou o risco não viu o limite existente, conferido ao implementar. Nada muda |
| Isolamento (autor) | Documentar e testar: em `REPEATABLE READ` ou `SERIALIZABLE`, enfileirar ao mesmo tempo na mesma chave dá 40001, e repetir resolve |
| Trava até o commit | Uma transação que enfileira K e depois escreve na linha R da aplicação faz deadlock com outra que escreve em R e depois enfileira K. Nenhuma lista cobre isso: é o preço do contador dentro da transação. O `OPERATIONS.md` documenta a regra de enfileirar no último `SaveChanges` antes do commit, `lock_timeout` e `idle_in_transaction_session_timeout` nas aplicações, a consulta para achar quem segura a chave (`pg_blocking_pids`) e a ordem de ativação (migration, produtores com o pacote da v0.2, dispatchers). Vale para todo produtor assim que a ordenação liga |
| Ligar, mudar P, desligar | **Mudar P** passa a ser `UPDATE settings` mais a recriação de `outbox_partitions` numa transação, sem apagar `settings`. Com isso, o trigger não para de numerar. **Desligar** é o único SQL que apaga `settings`. **Ligar com tráfego:** transações abertas antes de `settings` (as RC que já inseriram, e as RR, pelo snapshot) gravam sem `sequence`, e essas mensagens não são ordenadas. O `OPERATIONS.md` diz isso e recomenda ligar com os produtores parados para ter ordem desde a primeira mensagem. **Claim sem ordenação** ganha `AND (key IS NULL OR NOT EXISTS (SELECT 1 FROM waybill.settings))`, avaliado uma vez por instrução (InitPlan): uma instância antiga, num deploy rolante, não reivindica linha com chave enquanto alguém ordena |
| Trigger desligado | O dispatcher que ordena confere `pg_trigger.tgenabled` no startup e a cada `PartitionLease`. Trigger ausente ou desligado é erro crítico, como a divergência de P (8b): o laço para e o health check acusa. A heurística por `created_at` foi descartada, porque dava falso alarme na transição. `session_replication_role = replica` é por sessão e não é detectável; fica no `OPERATIONS.md` |
| Estado terminal | Trigger `BEFORE UPDATE OF status` recusa sair de `published` ou `released`. O argumento do claim depende disso, e um SQL de operador que "desfaça" uma liberação daria primeira entrega fora de ordem |
| Cabeça | A cabeça é a primeira linha com `sequence` não terminal da chave. Não terminal: `pending` (inclusive em espera), `claimed`, `dlq`. Terminal: `published`, `released`. "Bloqueante" depende só do status no snapshot, nunca do relógio. Linhas com chave e `sequence` nula não entram no filtro: a G4 vale a partir da primeira `sequence` |
| Claim com M = 1 | Um instante só por instrução (`now AS MATERIALIZED (SELECT clock_timestamp())`), usado na condição de reivindicável, na posse da partição e no `UPDATE` externo. Candidatas: reivindicáveis no instante, com posse, sem antecessora não terminal (índice `(key, sequence)`, O(1)), em ordem de `id`, `LIMIT BatchSize`, `FOR UPDATE SKIP LOCKED`. No `UPDATE` externo repetem-se a condição de reivindicável e a posse, com o mesmo `now`. O filtro de cabeça não se repete, e o ADR 0008 prova por quê: terminal é irreversível e nenhuma antecessora nasce depois de uma sucessora visível. Com uma linha por chave no lote, a ordem do `RETURNING` não importa |
| Chave quente | O `RETURNING` informa se alguma cabeça levada tem sucessora não terminal. Se tem, o ciclo seguinte é imediato. Sem isso, uma chave sozinha sairia a uma mensagem por `PollingInterval` |
| `sequence` no envelope | `OutgoingMessage.Sequence { get; init; }` (`long?`, sem `required`, para não quebrar quem constrói a mensagem). Nula quer dizer não ordenada: sem chave, gravada antes de ligar, ou sessão em modo replica. O RabbitMQ manda o cabeçalho `waybill-sequence` como string decimal, como os outros |
| DLQ e liberação | A linha na DLQ bloqueia a chave. O log de DLQ ganha `{Key}` e `{Sequence}`. Status `released`, com colunas `released_at` e `released_by`. O SQL do `OPERATIONS.md` (`<!-- key-release -->`) libera as linhas `dlq` de uma chave, grava data e `session_user`, e faz `RETURNING id, sequence`. As sequências devolvidas são as lacunas que o consumidor vai ver, e zero linhas quer dizer chave errada. Avisos: para `312 NO_ROUTE`, criar o binding e reenviar, não liberar; `released` não volta, e o trigger de estado terminal garante isso |
| Métricas e diagnóstico | Gauge `waybill.outbox.blocked_keys`, emitido só enquanto `settings` existe, com índice parcial `(key) WHERE status = 'dlq' AND sequence IS NOT NULL`. Consultas prontas no `OPERATIONS.md`, executadas por teste: chaves bloqueadas com a cabeça, o motivo e quantas esperam atrás; cabeças em espera de `basic.return`; linhas não publicadas de uma chave e o dono da partição. O texto explica a leitura conjunta com `oldest_pending.age` |
| Índices | `(key, sequence)` parcial em `sequence IS NOT NULL AND status IN ('pending','claimed','dlq')`. Com a ordenação desligada, nenhuma linha entra nele. Mais o da métrica |
| Migration | Refaz a `SchemaV0_2`. O `Up` é idempotente em SQL cru: `IF NOT EXISTS`, e índice com `indisvalid = false` é removido com `DROP INDEX CONCURRENTLY` e recriado. Restrição com `NOT VALID` + `VALIDATE`. Índices com `CREATE INDEX CONCURRENTLY` fora da transação (`suppressTransaction`). O `Down` recusa (`RAISE EXCEPTION`) se `outbox_keys` tem linhas ou existe `released`. O `OPERATIONS.md` ganha uma seção de upgrade (conexão direta, sem pooler em modo transação; conferir transações longas e `pg_index.indisvalid`) e uma de rollback (o schema só anda para frente; desligar a ordenação antes de voltar o binário) |
| Chave bloqueada com muitas à frente (autor) | Medida num teste de PR com oráculo de buffers do `EXPLAIN (ANALYZE, BUFFERS)`. A latência fica registrada na saída. Se o custo não couber, o ponteiro de cabeça vira PR próprio entre a 8c-1 e a 8c-2 | **Medido em Oct 8, 2026:** linear, ~4 buffers e ~12 µs por linha da fila parada (10 mil: p50 120 ms; 100 mil: p50 1,19 s). Passou do limite: o ponteiro de cabeça vira PR próprio entre a 8c-1 e a 8c-2, e o teste da 8c-1 fica com um teto de regressão de 5 buffers por linha. Achado para o desenho do ponteiro: uma marca de cabeça calculada no INSERT corre contra a publicação concorrente da cabeça anterior (o INSERT não vê a publicação ainda não commitada, e a promoção não vê o INSERT ainda não commitado), então a promoção e o INSERT precisam se serializar ou a marca precisa ser só uma dica com varredura de reserva |
| G4 (autor) | **Por chave, a partir da primeira mensagem com `sequence`, a primeira cópia de cada mensagem a chegar ao broker chega depois da primeira cópia de todas as anteriores, na ordem de commit. Cópias repetidas podem chegar depois e são reconhecidas pelo `message_id`. Uma mensagem liberada fica fora da sequência: deixa lacuna e, se uma tentativa anterior dela ficou sem confirmação, uma cópia ainda pode chegar a qualquer momento.** A ressalva veio do teste de propriedade: uma cópia atrasada de uma mensagem depois recusada e liberada chegou depois das sucessoras. Mensagem só na DLQ não abre esse caso, porque segura a chave Vai para o escopo nesta etapa. No `GUARANTEES.md`, só na etapa 12, como manda o plano. A premissa da etapa 10 muda: regressão com `message_id` inédito só aparece depois da retenção do inbox. Isso fica registrado no `plano-v0.2.md` |
| Documentos que mudam na 8c-1 | Escopo (G4 e linha "Ordenação"); `plano-v0.2.md` (bullets e cenários da 8c, divisão 8c-1/8c-2, três ADRs na etapa 8, nota para a etapa 10); ADR 0008, parte 1 (inclui por que o interceptor e o trigger não violam o ADR 0002, e os atores que mudam linhas por fora, um a um); `OPERATIONS.md`; CHANGELOG |

**Testes que provam (8c-1)**

Testes de concorrência com intercalação forçada: um trigger de teste filtrado por `application_name` espera numa barreira (`pg_advisory_xact_lock`). O teste só solta a barreira depois de ver as duas transações esperando em `pg_stat_activity`. `lock_timeout` de 10 s, retry do EF desligado.

| Teste | Cenário | Resultado esperado | Onde |
| --- | --- | --- | --- |
| Protótipo do contador (medição) | EF real, 32 produtores: chaves distintas, chave quente (também com trabalho depois do `SaveChanges`), várias chaves por transação. Sem contador, trigger sozinho, trigger + `lock_keys`. Trava da lista por `DO UPDATE` sem efeito contra `SELECT … FOR UPDATE`. `log_lock_waits` ligado | p99 e vazão no `OPERATIONS.md` e no ADR. O p99 de ~1 s da chave quente do spike, que coincide com o `deadlock_timeout`, fica explicado | Local |
| `G4_ListaDeChaves_SemDeadlock` (teoria) | (a) K1 e K2 em ordens opostas, chaves novas e existentes; (b) um `SaveChanges` de uma chave e um de duas na mesma linha da aplicação | Espera simultânea observada; zero exceções; dois commits. Controles negativos: sem `lock_keys`, (a) dá 40P01; com a trava anteposta ao comando, (b) dá 40P01 | PR |
| `Limite_EscritaDepoisDeEnfileirar_PodeDarDeadlock` | Enfileirar K e depois escrever em R, contra escrever em R e depois enfileirar K | 40P01 reproduzido, como o `OPERATIONS.md` descreve | PR |
| `G4_SequenciaContiguaNaOrdemDeCommit` | Rollback forçado (A segura a 1, B espera, A desfaz, B fica com a 1); falha de savepoint depois do trigger (segunda linha da outbox recusada por trigger de teste); leitor concorrente por amostragem | `sequence` 1..n sem buraco nem repetição. O leitor roda `count(*) = coalesce(max(sequence), 0)` numa instrução só, sempre verdadeiro | PR |
| `G4_ListaDeChaves_NaoSobreviveAoSaveChanges` (teoria) | Rollback a savepoint, cancelamento, `AddDbContextPool(poolSize: 1)`, `UseTransaction` entre contextos, falha transitória com estratégia de retry | As chaves de `outbox_keys` são exatamente as enfileiradas; `lock_keys` nula em toda linha gravada | PR |
| `G4_ChavesComSeparadoresEUnicode_ListaFiel` | Vírgula, aspas, `\`, `{}`, unicode, 255 bytes (o máximo aceito) | Nenhum erro; `outbox_keys` fiel | PR |
| `G4_AplicacaoEmRRouSerializable_Erro40001ERepetirResolve` (teoria) | Duas transações na mesma chave, retry desligado | 40001 numa; repetida, grava a `sequence` seguinte | PR |
| `G4_ContextoSemInterceptor_AvisaUmaVez` | Contexto registrado à mão, fora de `AddDbContext` | Enfileira; um aviso só, com o nome do contexto | PR |
| `G4_MesmaChaveNaMesmaTransacao_SequenciaNaOrdemDoEnqueue` | Rajada de 200 mensagens de uma chave num `SaveChanges` | `sequence` na ordem do `Enqueue` | PR |
| (já coberto na v0.1) | Chave acima de 255 bytes UTF-8 | Recusada no `Enqueue` pelos testes de envelope existentes | — |
| `G4_PapelSoComInsert_Enfileira` | Papel com INSERT só na outbox, ordenação ligada | Grava com `sequence` | PR |
| `G4_TriggerDesligado_ErroCritico` | `DISABLE TRIGGER`, no startup e com o dispatcher rodando | Para com erro crítico nos dois casos | PR |
| `G4_EstadoTerminal_NaoVolta` | `published` → `pending`, `released` → `pending` | Recusado pelo trigger | PR |
| `G4_MarcacaoOuDevolucaoConcorrente_ClaimNaoLevaASeguinte` (nome do plano) | Cabeça travada numa transação aberta que (a) a marca `published` ou (b) a devolve a `pending` | Nada da chave no claim concorrente. Depois do commit: em (a), a sucessora sai no claim seguinte; em (b), a cabeça sai antes | PR |
| `G4_ClaimOrdenado_UmInstantePorInstrucao` (estrutural) | O SQL do claim ordenado | Uma só leitura de `clock_timestamp()`. Com M = 1 só entram cabeças no lote, então o relógio por linha não consegue levar sucessora sem a cabeça; o teste comportamental (`G4_PosseNoLimite`, relógio de teste pelo `search_path`) vai para a 8c-2 | PR |
| `G4_ChaveQuenteSozinha_NaoEsperaPollingInterval` | Uma chave com 100 pendentes, nenhuma outra, `PollingInterval` de 5 s | Drena sem esperar o intervalo entre mensagens | PR |
| `G4_MensagemNaDlq_SoAquelaChaveParaEMetricaSobe` | Defeito numa chave com sucessoras | A chave para; as outras seguem; `blocked_keys` = 1; o log traz chave e `sequence` | PR |
| `Operacao_LiberarChave` | SQL do `OPERATIONS.md`, como escrito | `RETURNING` com as sequências; `released_at`/`released_by` gravados; a chave volta a fluir; métrica em 0 | PR |
| `Operacao_ConsultasDeDiagnostico` | Consultas do `OPERATIONS.md` sobre chave na DLQ, em espera e com dono | Devolvem a cabeça, o motivo e a fila atrás | PR |
| `Operacao_MudarP_ComEscritaConcorrente_SemSequenceNula` | SQL de mudar P com produtores gravando | Nenhuma linha com chave e `sequence` nula depois de ligar | PR |
| `G4_DispatcherSemOrdenacaoAindaVivo_NaoReivindicaLinhaComChave` | Instância antiga ao lado de `settings` | Só linhas sem chave | PR |
| `Operacao_ReiniciarOrdenacao` (existente) | Ampliado | `outbox_keys` intacta; `blocked_keys` não emitido depois | PR |
| `G4_LigarComTransacaoAberta_LinhaSemSequenceNaoOrdenada` | Transação RR aberta antes de `settings`, grava depois | `sequence` nula, como o `OPERATIONS.md` descreve; nenhum erro crítico | PR |
| `G4_OrdenacaoLigadaComBacklog_OrdemValeAPartirDaPrimeiraSequence` | Linhas com chave anteriores a `settings` | As antigas drenam; as novas saem em ordem | PR |
| `G4_TrocaDeDonoComLinhasEmVoo_ChaveEsperaOLeaseDasLinhas` | Partição vencida por SQL e assumida antes do lease da cabeça em voo | Nada da chave até o lease vencer; depois, em ordem | PR |
| `G4_ChaveBloqueadaComCemMilAFrente_ClaimContinuaFluindo` | 100 mil sucessoras de uma chave na DLQ, de `id` antigo, geradas com `generate_series` e trigger desligado; outras chaves pendentes | As outras são reivindicadas; buffers do claim abaixo do limite; latência registrada | PR |
| `G4_SequenceNoEnvelope` (+ RabbitMQ real) | Mensagem ordenada | `Sequence` no transporte; `waybill-sequence` no consumidor | PR |
| `G4_Retencao_NaoApagaOutboxKeysNemReleased` | Retenção com chaves publicadas e uma liberada | `outbox_keys` intacta; `released` fica | PR |
| `G4_OrdenacaoDesligada_SemContadorNemFiltro` (estrutural) | Sem `settings`, carga com chaves | `sequence` e `lock_keys` nulas. O SQL do claim é a constante da 8a mais a guarda de `settings`. `pg_stat_user_tables` mostra `outbox_keys` sem leitura nem escrita | PR |
| `G4_Propriedade_PrimeiraEntregaEmOrdem` | N dispatchers, leases forçados a vencer, zumbis, timeouts com chamada abandonada, retornos e defeitos. Produtores geram inversão de `id` contra `sequence` de propósito. Entrega = aceitação no transporte falso, com contador global | Por chave, as primeiras ocorrências por `message_id` têm `sequence` estritamente crescente, conferida contra o banco. No fim, toda linha está `published`, `dlq` ou `released`, ou `pending` com antecessora `dlq`. A premissa de canal (FIFO) fica escrita no ADR | PR: 20 s (caos); agendado: 10 min |
| `G4_Backlog_ClaimOrdenadoMedido` | 500 mil a 1 milhão de pendentes, chaves espalhadas e uma quente; custo de pular partições alheias | Latências e `EXPLAIN` no ADR; health `Healthy` e nenhum log de erro | Agendado |
| `Schema_UpgradeDaV01ComBacklog_LinhasContinuamReivindicaveis` (existente) | Ampliado | Migration aditiva; o backlog drena | PR |
| `Schema_MigracaoInterrompidaNoIndice_ReexecutarCompleta` | `pg_cancel_backend` durante o `CREATE INDEX CONCURRENTLY`, depois `MigrateAsync` de novo | Completa; nenhum índice com `indisvalid = false` | PR |
| `Schema_DownComOutboxKeys_Recusa` | `Down` com `outbox_keys` preenchida | Recusa com a mensagem | PR |
| Custo desligada | p99 da transação e vazão do claim contra a tag `v0.1.0-alpha` | No máximo 5% | Agendado | **Medido em Oct 8, 2026, local (Docker no Windows), linha de base emulada no mesmo teste:** claim −2,3% a +4,2%; p99 da transação +5,7% a +10,8% em três rodadas, variação que nesta máquina passa dos 5% só por ruído. Fica no job agendado; se falhar no runner Linux, a decisão volta ao autor (caminho candidato: o trigger só existir com a ordenação ligada) |

**Mutações da 8c-1** (cada uma quebra um teste nomeado):
- tirar a lista;
- `ORDER BY` só de um lado (trigger ou interceptor);
- `is_local = false` no marcador;
- não anular `lock_keys`;
- o instante único;
- `dlq` como bloqueante;
- o filtro de cabeça;
- a guarda de `settings` no claim sem ordenação;
- o trigger de estado terminal;
- a verificação de `tgenabled`;
- o sinal de chave quente.

O filtro de cabeça repetido no `UPDATE` externo não se repete por desenho (ADR 0008).

**Pronto quando (8c-1):** os cenários acima verdes no PG 15 e 18; cada mutação quebra um teste nomeado; ADR 0008 (parte 1), escopo, `plano-v0.2.md` e `OPERATIONS.md` atualizados; propriedade e backlog com três execuções agendadas verdes antes de fechar a etapa 8.

### Ponteiro de cabeça — PR entre a 8c-1 e a 8c-2

Desenho revisto em Oct 13, 2026: um time de cinco revisores (concorrência, desempenho, EF e produtor, operação, testes e escopo) e uma sondagem em PG 18.6 e 15.19, com o esquema das migrations e o SQL do claim copiado literalmente. As decisões marcadas "autor" foram tomadas com ele nessa data. A abordagem sem estado (cabeça achada por chave a cada claim) foi descartada: com 500 mil chaves ativas, milhões de buffers por claim.

**Fatos medidos na sondagem**

- **Chave quente sozinha, claim da 8c-1.** Cada claim descarta as n − 1 sucessoras: 1 mil, 2.077 buffers e 2,0 ms; 10 mil, 20.553 e 13,8 ms; 100 mil, 203.694 e 109 ms. Com uma chave dominando as estatísticas, o planner sonda o `NOT EXISTS` por Seq Scan e atravessa as linhas publicadas: drenar 4 mil levou 1.010 s, com o claim crescendo cerca de 8× por dobra. Forçando o índice, o crescimento é O(n²). `ANALYZE` não muda o plano.
- **Cabeça na DLQ com 100 mil sucessoras e 5 mil chaves pendentes.** 305 mil buffers e p50 de 1.320 ms hoje; com a marca, 2.933 buffers e 2,3 ms. O PG 15 dá o mesmo.
- **Com a marca e a cabeça decidida por `min(sequence)`** (Index Only Scan por MinMax), drenar uma chave sozinha custa o mesmo por linha de 1 mil a 8 mil (razões 0,91, 0,99 e 1,01), e o claim fica em cerca de 0,75 ms por linha.
- **Intercalações forçadas** (barreira num constraint trigger diferido de nome posterior):
  - (a) O produtor trava a antecessora no commit. A marcação do dispatcher espera e termina logo depois do commit do produtor, e a promoção marca a linha dele.
  - (b) Com a marcação sem commit, o produtor commita sem esperar e se marca.
  - (c) Em RR ou SERIALIZABLE, o commit dá 40001.
  - (d) Com `SET CONSTRAINTS ALL IMMEDIATE`, a marcação espera a transação inteira do produtor.
  - Nenhuma deixou cabeça sem marca.
- **Triggers diferidos** disparam por linha, em ordem de nome, com as linhas em ordem de inserção.
- **Rajada de 200 linhas de uma chave numa transação.** Verificando no commit só a primeira linha da chave, o custo fica igual ao de hoje: 90,8 kB de WAL contra 105,6, e commit de 2,5 ms. Verificando todas, o commit vai a 6,6 ms.
- **Ordenação desligada,** WAL medido de forma determinística (`full_page_writes` e autovacuum desligados): diferença de ±0,2%. A tupla cresce 1 byte.
- **Plano genérico** (`force_generic_plan`): os três SQL usam o índice substituído, sem Sort.
- **Estatísticas vazias** (tabela recém-criada ou depois de TRUNCATE): as consultas dos triggers viram Seq Scan, e uma transação grande fica O(n²). Uma de 50 mil linhas levou 18,9 s.

**Desenho**

| Tema | Decisão |
| --- | --- |
| Marca | Coluna `is_head boolean DEFAULT true`, fora do modelo do usuário: fica só no `OutboxRow` interno e no snapshot do `WaybillSchemaContext`, e a API pública não muda. `true` é candidata (a cabeça, ou um falso positivo inofensivo). `false` quer dizer que a antecessora é linha da própria transação. NULL quer dizer que havia antecessora de outra transação no INSERT, e a decisão fica para o commit. Só `true` entra no claim. Todo caminho que pula o trigger grava `true`, então o erro vira falso positivo e nunca chave presa |
| Segurança | A marca só restringe as candidatas. O claim continua decidindo a cabeça no snapshot. Um falso positivo custa uma sonda. Um falso negativo trava a chave, e é isso que o desenho evita |
| Cabeça no claim (autor: corrigir neste PR) | `o.sequence = (SELECT min(h.sequence) FROM waybill.outbox h WHERE h.key = o.key AND h.status IN ('pending','claimed','dlq'))` no lugar do `NOT EXISTS`. Para uma linha não terminal, as duas formas são equivalentes. O planner resolve o `min` por MinMax em `ix_outbox_key_sequence`, mesmo com uma chave dominando as estatísticas. O sinal de chave quente do `RETURNING` passa pela mesma revisão |
| Índice | `ix_outbox_claimable` passa a ser `(id) WHERE status IN ('pending','claimed') AND (sequence IS NULL OR is_head)`. Substitui o atual e mantém o nome (o REINDEX do ADR 0004 não muda). A mesma cláusula, no mesmo texto, entra no `ClaimSql`, no `ClaimOrderedSql` e no `OldestPendingAgeSql`. Sem a cláusula, o `ClaimSql` varre a fila pelo `pk_outbox`. Com a ordenação desligada, o índice tem o mesmo conteúdo da 8c-1 |
| Marca no INSERT | No trigger do contador, depois de numerar: `false` se a antecessora é a `sequence − 1` desta transação, lembrada no GUC local `waybill.last_keyed` (última chave e sequência numeradas, desfeito com o savepoint); `true` fora de READ COMMITTED (autor); NULL se há antecessora não terminal visível; `true` nos outros casos. Limite: chaves alternadas na mesma transação perdem a dedup e pagam a verificação por linha. O custo continua correto |
| Verificação no commit | Constraint trigger `AFTER INSERT DEFERRABLE INITIALLY DEFERRED` com `WHEN (NEW.sequence IS NOT NULL AND NEW.is_head IS NULL)`. Escolhe sem trava a antecessora não terminal mais alta e trava esse id com `FOR SHARE SKIP LOCKED`, refazendo o filtro de status. Se não achar a linha ou se ela estiver travada, grava `is_head = true` na própria linha. Se achar e travar, deixa NULL |
| Por que a verificação não perde cabeça | Se o produtor travou a antecessora antes da marcação, a marcação espera o fim do commit dele. A promoção, função VOLATILE com snapshot novo por instrução, vê então a linha do produtor. Se a marcação travou primeiro, o produtor pula a antecessora e se marca. O produtor nunca espera o dispatcher, então não há ciclo |
| Promoção | `AFTER UPDATE OF status`, de não terminal para terminal, e `AFTER DELETE` de linha não terminal. Os dois com `sequence IS NOT NULL` no `WHEN`, sem depender de `settings`. Marcam a menor não terminal seguinte da chave, se `is_head IS NOT TRUE`. Cobrem a publicação, a liberação, desligar e religar a ordenação, e o DELETE manual (hoje o DELETE destrava a chave; com a marca, sem promoção, a prenderia) |
| Desligar | O SQL de desligar grava `is_head = true` em toda linha não terminal com `sequence`, na mesma transação que apaga `settings`. Sem isso, o `ClaimSql` não vê as sucessoras: na sondagem, de 130 mil pendentes só 5 mil ficavam visíveis. Religar deixa falsos positivos, inofensivos |
| Privilégios | As funções novas são `SECURITY DEFINER` com `SET search_path = pg_catalog, waybill`, como a `outbox_sequence`. Motivo: `FOR SHARE` e o UPDATE da marca exigem o privilégio UPDATE, e um papel só com INSERT falharia no COMMIT, só quando a antecessora sai durante a transação |
| Planos dos triggers | As consultas dos triggers são escritas para descida de índice (`min`, `ORDER BY … LIMIT 1` em `ix_outbox_key_sequence`). Se a medição com estatísticas vazias não bastar, `SET enable_seqscan = off` nas funções |
| Rede (autor) | O dispatcher repara cabeças sem marca nas partições que assume e a cada `PartitionLease`, de forma incremental (uma fatia de chaves por ciclo, com cursor). Cada reparo incrementa o contador `waybill.outbox.heads_repaired` e gera um log de erro com a chave e a `sequence`. Em funcionamento normal o contador fica em zero: um reparo indica ação externa (trigger desligado, sessão replica, restore). O `OPERATIONS.md` ganha a consulta `unmarked-heads` e o SQL `mark-heads`, executados por teste |
| Verificação de triggers | A checagem de `tgenabled`, feita no startup e a cada `PartitionLease`, passa a cobrir os triggers novos. O dispatcher recusa sessão com `session_replication_role = replica` |
| Espera do dispatcher | A marcação pode esperar o commit de um produtor. Normalmente é só o fim do commit. A espera fica longa com `SET CONSTRAINTS ALL IMMEDIATE`, FK diferida lenta da aplicação, replicação síncrona ou `PREPARE TRANSACTION`. Esses casos vão como limite no `OPERATIONS.md`, e o do IMMEDIATE tem teste. A ordem não quebra: a espera segura o lote, e o lease vencido republica (duplicata, nunca perda) |
| RR/SERIALIZABLE (autor) | Sem trava e sem verificação: a linha nasce `true`. Nenhum 40001 novo. Numa chave quente, um produtor em RR põe falsos positivos no claim |
| Migration (autor) | Refaz a `SchemaV0_2`. A coluna tem default constante, então o `ADD COLUMN` só mexe em metadados. O índice é criado com `CREATE INDEX CONCURRENTLY` e nome temporário, depois troca de lugar com o antigo e recebe o nome dele. Os triggers são recriados de forma idempotente: `CREATE CONSTRAINT TRIGGER` não tem `OR REPLACE`, então o `Up` confere `pg_trigger` antes. O `Down` remove os objetos novos. Bancos de desenvolvimento que já aplicaram a versão da 8c-1 precisam ser recriados (nota no handoff). O `Up` usa `lock_timeout`, e o `OPERATIONS.md` explica por quê |
| Fora | M ≥ 2: na 8c-2, uma marca falso positiva pode trazer ao lote uma sucessora de outra cabeça, e a 8c-2 deduplica. Hash Join sobre Seq Scan no UPDATE externo do claim em plano genérico: já existe na 8c-1 e só aparece com auto-prepare. Fica registrado como achado e é corrigido à parte |

**Testes que provam**

Intercalação forçada como na 8c-1, com a barreira estendida em duas coisas: uma fase diferida (constraint trigger de teste com nome posterior ao da verificação) e um filtro por status, para parar só na marcação.

| Teste | Cenário | Resultado esperado | Mutação que pega |
| --- | --- | --- | --- |
| `G4_Ponteiro_PublicacaoAntesDoCommitDoProdutor_SucessoraViraCabeca` | O produtor insere S e para na barreira do AFTER INSERT. O dispatcher marca H `published` e commita. Solta o produtor | S marcada; o claim seguinte leva S | tirar a verificação no commit |
| `G4_Ponteiro_MarcacaoEsperaOCommitDoProdutor_PromocaoVeASucessora` | O produtor para na barreira diferida, com H já travada `FOR SHARE`. O dispatcher marca H. Espera observada; solta | S marcada pela promoção do dispatcher | `FOR KEY SHARE` no lugar de `FOR SHARE`; promoção `STABLE` |
| `G4_Ponteiro_MarcacaoEmVooQuandoProdutorCommita_FalsoPositivoInofensivo` (teoria: o dispatcher commita ou desfaz) | O dispatcher atualiza H e para; o produtor commita | Commit: S sai. Rollback: S marcada, e o claim leva H, não S | tirar a decisão de cabeça do claim, confiando só na marca |
| `G4_Ponteiro_ProdutorDeDuasChavesContraMarcacaoEmLote_SemDeadlock` | Produtor com S_A e S_B; o dispatcher trava H_B e depois H_A | Nenhum 40P01; dois commits | tirar o `SKIP LOCKED` |
| `G4_Ponteiro_LiberacaoConcorrente_SucessoraViraCabeca` | Como o primeiro, com o SQL `key-release` | A chave flui | promover só em `published` |
| `G4_Ponteiro_DeleteDaCabeca_SucessoraViraCabeca` | DELETE manual da cabeça na DLQ | A sucessora sai no claim seguinte | tirar a promoção no DELETE |
| `G4_Ponteiro_DesligarEReligar_ChavesAndam` | Ligar, enfileirar, desligar pelo SQL do `OPERATIONS.md`, drenar parte, religar | Toda chave anda nos dois modos | promoção condicionada a `settings`; desligar sem marcar |
| `G4_Ponteiro_RajadaNaTransacao_SoAPrimeiraDecideNoCommit` | 200 linhas de uma chave num `SaveChanges`, com antecessora de outra transação | Só a primeira fica NULL até o commit; as outras nascem `false`; depois do commit, uma cabeça por chave | tirar a dedup |
| `G4_AplicacaoEmRRouSerializable_…` (ampliada) | Antecessora publicada durante a transação RR | Commit sem erro; linha `true` | travar também fora de READ COMMITTED |
| `G4_PapelSoComInsert_Enfileira` (ampliado) | Antecessora publicada entre o INSERT e o commit | Commit sem erro | tirar o `SECURITY DEFINER` |
| `Limite_SetConstraintsImmediate_MarcacaoEsperaOProdutor` | A aplicação faz `SET CONSTRAINTS ALL IMMEDIATE` e segura a transação | A marcação espera; depois do commit a ordem se mantém, como o `OPERATIONS.md` descreve | — |
| `G4_ChaveQuenteSozinha_DrenoLinear` (teoria: 1 mil e 4 mil) | Uma chave, n pendentes, estatísticas com a chave dominante | Buffers por claim iguais nos dois tamanhos | tirar a marca do claim; `NOT EXISTS` no lugar do `min` |
| `G4_ChaveBloqueadaComCemMilAFrente_ClaimContinuaFluindo` (teoria: 10 mil e 100 mil) | Como na 8c-1, com sementes de marca coerente | Buffers por claim abaixo de uma constante e iguais nos dois tamanhos; a liberação de uma cabeça seguida do claim da próxima | tirar a marca do claim |
| `G4_Ponteiro_PlanoGenerico_UsaOIndice` (estrutural) | `EXPLAIN` dos três SQL com `force_generic_plan` | Index Scan em `ix_outbox_claimable`, sem Seq Scan nem Sort | esquecer a cláusula num SQL |
| `G4_Ponteiro_EstatisticasVazias_TransacaoGrandeLinear` | Tabela recém-criada, 50 mil linhas de uma chave numa transação | Termina dentro de um teto | consulta de trigger que vira Seq Scan |
| `G4_Ponteiro_Rede_ReparaCabecaSemMarca` | Marca apagada com o trigger desligado; o dispatcher assume a partição | Reparada; contador em 1; log com chave e `sequence` | tirar a varredura |
| `G4_TriggerDesligado_ErroCritico` (ampliado) | Cada trigger novo desligado; sessão replica | Erro crítico | tirar um trigger da checagem |
| `Operacao_MarcarCabecas` e `Operacao_ConsultasDeDiagnostico` (ampliada) | `unmarked-heads` e `mark-heads` do `OPERATIONS.md`, como escritos | Acham e marcam; a consulta da chave mostra `is_head` | — |
| `G4_OrdenacaoDesligada_SemContadorNemFiltro` (reescrito) | Sem `settings` | Os `WHEN` dos triggers novos exigem `sequence`; a linha entra nos mesmos índices da 8c-1 | promoção sem `WHEN` |
| `G4_Propriedade_PrimeiraEntregaEmOrdem` (caos, ampliado) | Mais um produtor numa chave quente durante a drenagem | Invariante amostrada numa instrução e conferida no fim: a menor não terminal de cada chave tem `is_head` | qualquer falso negativo |

As sementes por `generate_series` (100 mil, backlog, transições) gravam `is_head` coerente por um helper. Sem isso, os testes medem um estado impossível.

**Mutações** (cada uma quebra um teste nomeado): tirar a verificação no commit; `FOR KEY SHARE`; promoção `STABLE`; tirar o `SKIP LOCKED`; promover só em `published`; tirar a promoção no DELETE; condicionar a promoção a `settings`; desligar sem marcar; tirar a dedup; travar fora de READ COMMITTED; tirar o `SECURITY DEFINER`; tirar a marca do claim; `NOT EXISTS` no lugar do `min`; esquecer a cláusula num SQL; tirar a varredura; tirar um trigger da checagem.

**Corrida sem teste determinístico:** a janela dentro do COMMIT, entre sair do ProcArray e soltar o `FOR SHARE`. Depende do mesmo invariante do PG que a 8c-1 já usa, e fica coberta pela invariante do teste de caos.

**Documentos:** o ADR 0008 ganha a seção "parte 1b", com cada ator externo e como é coberto; o `OPERATIONS.md` ganha desligar, `unmarked-heads`, `mark-heads`, "nunca apague, libere", os limites de espera, RR, a escrita extra no autovacuum, o upgrade com `lock_timeout` e a tabela de custo medida de novo; o escopo troca a frase do custo linear; o `plano-v0.2.md` registra a etapa 8 com cinco PRs e o gatilho de corte da etapa 11; o CHANGELOG é atualizado.

**Pronto quando:** os cenários verdes no PG 15 e 18; cada mutação quebra um teste nomeado; o claim de `G4_ChaveBloqueadaComCemMilAFrente` com p50 ≤ 50 ms e buffers que não dependem do tamanho da fila; a invariante verde no caos curto; WAL com a ordenação desligada igual ao da 8c-1, com margem de 1%.

### 8c-2 — M ≥ 2 em rodadas

Desenho de Oct 13, 2026, feito depois do ponteiro de cabeça (PR #20). O rascunho foi sondado em PG 18 e revisto por um time de quatro revisores antes do código (concorrência, desempenho, dispatcher e ADR 0003, testes e escopo). Os revisores de concorrência e de desempenho sondaram no esquema real das migrations, em PG 18.6 e 15.19. As decisões marcadas "autor" foram tomadas com ele nessa data. O SQL validado está em `SONDAGEM-8C2-CLAIM.sql` e `SONDAGEM-8C2-PROMOCAO.sql`.

**O que o ponteiro mudou**

- **Origem das sucessoras.** Elas não estão no `ix_outbox_claimable`: saem de `ix_outbox_key_sequence`, por descida a partir da cabeça. Nenhum índice novo.
- **A cabeça continua decidida por `min(sequence)` no snapshot.** Uma marca falso positiva nunca vira cabeça, então nenhuma linha aparece duas vezes no lote. A frase "a 8c-2 deduplica", na parte 1b do ADR 0008, é corrigida.

**Fatos medidos**

- **Claim.**
  - O esboço de Oct 7 é quadrático: 151 a 222 ms com 100 cabeças. Ele também travava as sucessoras antes do corte, 900 travas sem uso.
  - O rascunho sondado cortava antes de travar e montava o prefixo por janela. Medido no esquema real:
    - chave quente sozinha com M = 100: 2 ms e cerca de 27 buffers por linha, do mesmo jeito com mil e com 100 mil linhas na fila;
    - 100 chaves × 1000: o prefixo ainda é quadrático no plano genérico, com 115 ms em B = 1000 e 2,7 s em B = 5000;
    - 500 chaves × 200 com M = 100: o `LATERAL` lê 49,5 mil linhas e o Sort vai para o disco, 139 a 176 ms;
    - os dois Hash Join sobre Seq Scan já conhecidos (`UPDATE` externo e `locked`).
  - Com as correções da tabela abaixo, os mesmos cenários ficam entre 20 e 26 ms com B = 1000 e em 138 ms com B = 5000, sem Seq Scan no PG 15 nem no 18.
- **JIT.**
  - Com fila grande, o custo estimado do plano genérico passa do `jit_above_cost`, e o PostgreSQL compila o claim a cada execução. Com 200 mil sem chave, são cerca de 0,5 s por claim.
  - Com `jit=off` na sessão do dispatcher, cai para 1,3 a 1,7 ms. Isso vale também para o claim da 8c-1.
- **Promoção.**
  - Duas terminações concorrentes na mesma chave deixam a chave sem cabeça marcada:
    - o zumbi publica m2 enquanto o novo dono publica m1;
    - ou um DELETE de m2, que já acontece com M = 1.
  - A promoção mira m2, espera, e o EvalPlanQual não reavalia o alvo. Reproduzido.
  - A promoção por linha também custa O(M²) quando a série inteira é publicada num `UPDATE`: 5 a 10 ms com M = 100.
- **Falsos positivos persistentes, que vêm do ponteiro.**
  - Acontecem com um produtor em RR numa chave quente, ou ao religar a ordenação com chaves paradas na DLQ.
  - Nesses casos o claim volta a custar 330 a 630 ms.

**Desenho**

| Tema | Decisão |
| --- | --- |
| Opção | `MaxPerKey` (M), default 1, de 1 a 100, acima de 1 só com `OrderByKey`, validado no `PartitionOptionsValidation`. Por instância, fora de `settings`. M maior que B é aceito e documentado. Desligar a ordenação manda voltar M a 1 |
| Claim | Uma instrução (`SONDAGEM-8C2-CLAIM.sql`). **`heads`:** as candidatas do `ClaimOrderedSql`, palavra por palavra, montadas dos mesmos trechos em C# que o SQL de M = 1. **`budget`:** B menos as cabeças, e quantas cabeças têm sucessora (sonda `max(sequence)`). **`series`:** para cada cabeça com `sequence`, as próximas não terminais por `LATERAL`, até `least(M − 1, ceil(orçamento / cabeças com sucessora))`, com o prefixo no snapshot por janela. **`ranked`:** corte por (`rank`, cabeça) até o orçamento. **`locked`:** `LATERAL … FOR UPDATE SKIP LOCKED` por chave primária, com as condições no nível da trava. **`prefix`:** `row_number() OVER (PARTITION BY cabeça ORDER BY rank) = rank`, sem junção. **`UPDATE`:** `o.id = ANY (array)`, repetindo condições, espera de `basic.return` e posse da partição. Com M efetivo 1, inclusive no meio aberto, o dispatcher usa o `ClaimOrderedSql` |
| Corte por rodada (autor) | Por (`rank`, posição da cabeça): todas as segundas mensagens antes de qualquer terceira. Preserva o prefixo por construção e reparte o lote entre as chaves. O teto por cabeça pode deixar o lote abaixo do ótimo com séries desiguais, o que é inofensivo e vai para o ADR |
| Rodadas no dispatcher (autor) | Uma chamada de `PublishAsync` por rodada. As rodadas são montadas por cabeça e `rank`, numa função pura. Linhas sem `sequence`, com ou sem chave, vão todas na rodada 1. Cada chamada leva no máximo uma mensagem com `sequence` por chave. A rodada k + 1 só começa se toda a rodada k terminou sem `Retry`. Um `Returned` ou `Defect` interrompe só a própria chave. Qualquer `Retry`, resultado inválido, exceção ou contagem errada encerra o lote. O `ITransport` não muda de assinatura; o XML doc ganha: prazo restante do lote, várias chamadas sequenciais por ciclo, e `Confirmed` quer dizer que o broker ordena a mensagem antes de qualquer publicada depois |
| Desfecho interno | O que não chega a ser enviado volta a `pending` sem gastar tentativa, pelo `RetrySql`: rodada anterior sem `Confirmed`, `Retry` no lote, prazo, shutdown, ou `rank` com buraco, que é uma guarda contra um estado não alcançável. Nunca entra no `React` |
| Defeito local | A checagem de tamanho da linha da rodada k acontece na rodada k, não antes do lote |
| Prazo | T0 é marcado antes de emitir o claim. Todo o relógio passa pelo `TimeProvider`: T0 e D por `GetTimestamp`, `CancellationTokenSource(restante, _time)` e `WaitAsync(restante, _time)`. A rodada k só começa se `restante > 0` e `restante ≥ D` da rodada k − 1, e recebe o restante como prazo. O teste de caos do blackhole é ajustado ao relógio |
| Reação (ADR 0003) | O `React` recebe os resultados da rodada 1, mais conexão, `Nacked`, `Unspecified`, inválido e exceção de qualquer rodada. Só o `ConfirmTimeout` de uma rodada k > 1 fica de fora: o prazo dela é o que sobrou. Com B = 1 há uma rodada só, então o meio aberto e a queda silenciosa não mudam |
| M efetivo (autor) | Por instância, no molde do `BatchSizer`. Quando o prazo corta uma rodada, cai para o número de rodadas que de fato rodaram, com um log. Volta a dobrar, até M, quando o lote termina inteiro. Isso evita reivindicar e devolver sucessoras a cada ciclo com broker lento |
| Shutdown | Checado só com `IsCancellationRequested`, nunca com exceção. Nenhuma rodada nova depois do pedido; com o pedido antes da rodada 1, tudo volta sem envio. O `FinishAsync` registra o que foi confirmado mesmo quando uma rodada posterior falha ou o código do dispatcher lança |
| Marcação | Um `FinishAsync` por lote, numa transação. Repetido em 40P01 e 40001: as instruções são idempotentes por fence, e há um deadlock raro com dois zumbis cruzados em duas chaves. Uma linha repetida no `RETURNING` gera log de erro e a chave inteira volta sem envio |
| Promoção (refaz a `SchemaV0_2`, autor) | `outbox_head_promote` em laço (`SONDAGEM-8C2-PROMOCAO.sql`): lê a menor não terminal da chave e sai se não houver nenhuma ou se ela vier antes de `OLD.sequence`. Senão, trava o alvo com `FOR NO KEY UPDATE`, refaz o status, marca e sai; se o alvo terminou, procura de novo. O trigger de UPDATE só dispara quando a linha terminada estava marcada (`OLD.is_head IS TRUE`); o de DELETE não muda. Verde nas intercalações sondadas, e a série de 100 num `UPDATE` faz uma promoção, não 100. Bancos de desenvolvimento que aplicaram a versão do PR #20 precisam ser recriados |
| JIT | `jit=off` nas opções da conexão do `DispatcherDataSource`, que já é dedicado ao dispatcher |
| Laço | `MoreOfKeys` é verdadeiro se a última linha reivindicada de uma chave tem `MoreOfKey`, ou se alguma linha da chave voltou sem envio por prazo ou `Retry`. Chaves paradas por `Returned` ou `Defect` não contam |
| Vazão e duplicatas | Por chave, M mensagens por (claim + M idas de confirmação + marcação). Um crash ou uma falha do `FinishAsync` depois de r rodadas republica até r mensagens por chave. A G4 permite isso, como cópias tardias. Os dois vão para o `OPERATIONS.md` |
| Issue #21 (autor: no fim, PR próprio se o prazo apertar) | `FinishAsync` espera o commit do produtor, sem `lock_timeout`, com documentação. Depois do `PartitionLease` há duplicatas, e com zumbi o caso fica coberto pela promoção em laço. Âncora da varredura em dois SQL; erro da varredura capturado com log próprio. Os falsos positivos persistentes vão para a issue, com o custo e o caminho no `OPERATIONS.md` (decisão do autor) |
| Prazo da etapa (autor) | Em Oct 28 o autor decide se a etapa 11 sai da v0.2, e a decisão fica registrada no `plano-v0.2.md`. O merge da 8c-2 tem de sair até cerca de Oct 30, para o job agendado rodar antes de Nov 3 |
| Fora | Declaração de ordem do `ITransport` (etapa 9, registrada no `plano-v0.2.md`); série numa transação Kafka (etapa 9); rebaixar falsos positivos (issue #21) |

**Testes previstos** (integração, salvo indicação; os nomes seguem a garantia)

| Teste | Cenário | Resultado esperado | Mutação que pega |
| --- | --- | --- | --- |
| Os testes G4 do claim em teoria M ∈ {1, 3}: marcação ou devolução concorrente; dono antigo da partição; troca de dono com linhas em voo; chave na DLQ; `G2_Returned_LinhaEmEsperaNaoEReivindicada` com ordenação; marcação em voo com falso positivo | Como hoje, também pelo SQL novo | Iguais | Cada proteção tirada do `heads` ou do `UPDATE` do SQL novo |
| `G4_ClaimOrdenado_UmInstantePorInstrucao` e `G4_Ponteiro_PlanoGenerico_UsaOIndice` (ampliados) | Os dois SQL | Um `clock_timestamp()`; Index Scan nos dois | `clock_timestamp()` no `LATERAL`; cláusula esquecida |
| `G4_Rodadas_PlanoGenerico_SemLacoQuadraticoNemSeqScan` (estrutural, PG 15 e 18) | 100 × 1000 com estatísticas vazias; fila de 200 mil sem chave | Nenhum Join Filter entre CTEs, Seq Scan em `outbox` ou `external merge` | Prefixo por junção; `IN` no `locked`; `UPDATE` por junção |
| `G4_SucessoraMudaDepoisDoSnapshot_PrefixoCorta` (teoria: S2 vai à DLQ, publicada, apagada, em espera, devolvida a `pending`) | Claim pausado pelo relógio de teste entre o snapshot e a trava; outra sessão commita a mudança | S3 só entra se S2 entra | Tirar as condições do `locked`; tirar as do `UPDATE` (fecha o buraco da 8a) |
| `G4_AntecessoraDoMeio_PrefixoCorta` (teoria: travada, lease vivo, em espera, `dlq`) | Estado no snapshot | Nada depois dela na chave | Tirar o prefixo no snapshot |
| `G4_SucessoraNaoReivindicavel_NaoGastaVaga` | B justo, uma chave com S2 de lease vivo | O lote se completa com outras chaves | Cortar antes do prefixo do snapshot |
| `G4_LoteCheioDeCabecas_MNaoAge` | B só de cabeças, M = 2 e 100 | `xmax = 0` nas sucessoras; buffers iguais | Trava antes do corte |
| `G4_CorteDoLote_RodadaAntesDaChave` | B = 4; chaves A e B com 5 cada; M = 5 | Lote {A1, B1, A2, B2} | Corte por posição da cabeça |
| `G4_SucessoraComIdMenor_LoteDeUm_CabecaSai` | Sucessora com `id` menor que a cabeça, B = 1 | Sai a cabeça | Corte por `id` |
| `G4_MaiorQueACadeiaELoteCurto_PrefixoPorChave` | M maior que a cadeia; lote curto | Cadeia inteira; corte só pelo orçamento | Teto sem `least` |
| `G4_MarcaFalsoPositiva_SucessoraUmaVezNaRodadaCerta` | S2 `true` por SQL, M = 3 | {H, S2, S3}, uma vez cada, rodadas 1 a 3 | Cabeça só por `is_head` |
| `G4_SerieComLiberadaNoMeio_SerieContinua` | S2 `released` | S1 e S3 nas rodadas 1 e 2 | `released` tratado como bloqueante |
| `G4_TrocaDeDono_ZumbiComFenceNaSucessora` | Zumbi com S2, novo dono com B = 1 | Primeira entrega em ordem; invariante do ponteiro no fim | Promoção sem laço |
| `G4_Ponteiro_TerminacoesConcorrentesNaMesmaChave_PromoveAMenor` (teoria: as duas ordens; m2 falso positivo; DELETE; liberação de m2 na DLQ) | Duas terminações forçadas com a barreira | A menor não terminal marcada | Sem refazer o status; sair com alvo marcado; sem a saída por antecessora (40P01) |
| `G4_Ponteiro_FinishComSerie_UmaPromocaoPorChave` | Chave quente, M = 100 | `calls` de `outbox_head_promote` sobe 1; série seguinte marcada | Trigger sem `OLD.is_head` |
| `G4_Ponteiro_CabecaESucessoraNoMesmoLote_PromoveATerceira` (teoria: todas confirmadas; S2 `Returned`; S2 com defeito; S2 não enviada) | Finish da série | A menor não terminal marcada | — |
| `G4_RodadasSeguemSequenceNaoId` | Cadeia que coube inteira; depois, maior que M | Ordem por `sequence`; `MoreOfKeys` falso, depois verdadeiro | Ordem por `id`; sinal por qualquer linha |
| `G4_LinhasSemSequence_VaoNaRodadaUm` | Chave com linhas de `sequence` nula e numeradas; linhas sem chave | As não ordenadas e a cabeça na rodada 1; nenhuma chamada com duas da mesma chave numerada | Agrupar por chave |
| `G4_RetryNaRodada_EncerraOLote` (teoria: `Unspecified`, `Nacked`, `Connection`, `ConfirmTimeout`, inválido, exceção, contagem) | Retry de uma chave na rodada 1 | Nenhuma rodada 2 para chave nenhuma | Encerramento só por chave |
| `G4_RodadaComRetornoDeN_NaoEnviaASeguinte` e `G4_ReturnedNaRodadaDois_GastaTentativaEEspera` | `Returned` na rodada k | A seguinte sem envio e sem tentativa; as outras chaves seguem | — |
| `G4_DefeitoLocalNoMeioDaSerie_NaoEnviaASeguinte` e `G4_DefeitoLocalAtrasDeRetry_NaoVaiParaDlqForaDaVez` | Payload acima do limite na série | DLQ só na sua rodada | Checagem antes do lote |
| `G4_RodadaTardia_NaoComeca_DevolveSemTentativaELoteNaoCai`, `G4_RodadaComPrazoZero_NaoComeca` (`FakeTimeProvider`) | A rodada 1 consome o prazo | Rodada 2 não chamada; `attempts` 0; lote igual | Prazo por rodada; `restante ≥ D` aceitando zero; relógio de parede |
| `G4_TimeoutNaRodadaDois_NaoContaPressao`, `G4_NackNaRodadaDois_ContaPressao`, controle `G4_TimeoutNaRodadaUm_ContaPressao` | Falha na rodada 2 ou 1 | Lote igual / à metade | Pressão por rodada errada |
| `G4_ConexaoNaRodadaDois_AbreOBreaker` | Conexão na rodada 2 | Breaker aberto | — |
| `G4_RodadaCortadaPeloPrazo_MEfetivoCai` e `…_LacoNaoEsperaPollingInterval` | Prazo corta a rodada 3 | M efetivo 2, log; próximo ciclo já | M fixo; `MoreOfKeys` sem as devolvidas |
| `G4_ChaveParadaPorRetorno_LacoNaoGira` | `Returned` na rodada 2 | Laço espera o polling | Contar as paradas em `MoreOfKeys` |
| `G4_ShutdownEntreRodadas_RodadaUmMarcadaSemRepublicar`, `G4_ShutdownAntesDaRodadaUm_DevolveTudo`, `G4_ExcecaoNaMontagemDaRodadaDois_RodadaUmMarcada` | Shutdown ou exceção entre rodadas | Rodada 1 `published`, uma cópia só | `ThrowIfCancellationRequested` |
| `G4_MeioAberto_ComMCem_SondaUmaSo` | Breaker meio aberto, M = 100 | Uma mensagem, SQL de M = 1 | — |
| `G4_Rodadas_LoteGrande_Linear` (teoria B = 1000 e 5000, M = 100) e `G4_Rodadas_SerieNaoLeAlemDoOrcamento` | 100 × 1000; 500 × 200 | Teto de buffers por linha e de tempo B5000 / B1000 ≤ 7; linhas lidas no `LATERAL` ≤ 2 × orçamento | Prefixo quadrático; sem teto por cabeça |
| `G4_ChaveQuenteSozinha_DrenoLinear` (M = 100) | Estado novo em cada tamanho | ≤ 35 buffers por linha; razão 10 mil / 100 mil ≤ 1,3 | Tirar a marca do claim |
| `G4_Rodadas_DispatcherSemJit` | `SHOW jit` pela conexão do dispatcher | `off` | Tirar a opção |
| `G4_Propriedade_PrimeiraEntregaEmOrdem` (caos, ampliado) | M e B sorteados por instância (opções clonadas), entrega embaralhada dentro da chamada, Finish preso por produtor além do `PartitionLease` | Uma mensagem ordenada por chave em cada chamada; oráculo e invariante verdes | Série inteira numa chamada |
| Unidade | `MaxPerKey`; montagem das rodadas (`sequence` nula, buraco de `rank`, linha repetida); M efetivo | — | — |
| Issue #21 | `Limite_SetConstraintsImmediate_MarcacaoEsperaOProdutor` ampliado (lease curto, segundo dispatcher não leva nada da chave, uma entrega); `G4_Ponteiro_Rede_PlanoGenerico_UsaOIndice`; `G4_Ponteiro_Rede_ErroNaVarredura_NaoDerrubaOCiclo`; `Limite_FalsosPositivosPersistentes` (medido) | — | — |

Os critérios de desempenho são afirmados em buffers. A latência só é registrada, como na 8c-1. Os testes de custo partem de estado novo, porque repetir com rollback infla os buffers.

**Harnesses:**
- `FakeTransport` passa a registrar os lotes por chamada;
- `OrderingHarness` ganha `maxPerKey` e clona as opções;
- um relógio de teste novo, pelo `search_path`, troca o `clock_timestamp()` por uma função que espera numa trava consultiva. Ele serve ao `G4_PosseNoLimite` e aos testes de snapshot contra trava.

**Documentos:**
- ADR 0008: parte 2, status, e correção da frase sobre deduplicação;
- ADR 0003: a linha das rodadas;
- `OPERATIONS.md`:
  - `PublishTimeout` passa a cobrir o lote;
  - `MaxPerKey`;
  - shutdown;
  - vazão e duplicatas por chave;
  - M efetivo;
  - a espera do Finish;
  - os falsos positivos persistentes;
  - correção da linha "one message per key per batch";
- escopo: lease contra timeout, e a frase "M = 1";
- `plano-v0.2.md`: o bullet da 8c-2, a declaração de ordem na etapa 9 e o ponto de controle de Oct 28;
- XML doc do `ITransport`;
- CHANGELOG;
- `PublicAPI.Unshipped.txt`.

**Pronto quando:**
- os cenários passam verdes no PG 15 e 18;
- cada mutação da tabela quebra um teste nomeado;
- o caos curto, com M ≥ 2, passa verde;
- a revisão final não deixa nenhum achado crítico nem importante;
- o merge sai até cerca de Oct 30.

**Armadilhas da 8c**

- **`id` e `sequence` divergem dentro da chave.** O `id` nasce no `Enqueue` e a `sequence` no commit. Nenhuma lógica de lote pode deixar a sucessora ocupar o lugar da cabeça.
- **`clock_timestamp()` muda dentro da instrução.** Com ordenação, um instante só por claim.
- **Nada pode ser anteposto ao lote do EF.** Cada instrução tem de afetar exatamente uma linha.
- **GUC volta com o rollback a savepoint e vaza entre usos da sessão.** Por isso a lista viaja na linha, não na sessão.
- **`pg_stat_database.deadlocks` não serve como oráculo.** É cumulativo, atrasado e global. Os testes contam exceções e forçam a intercalação.

**Fora da 8c:** detecção de gap e regressão no consumidor (etapa 10); `epoch` no transporte (etapa 9); API de liberação (v1.0).

## Pronto quando (8a)

- Os cenários acima passam contra PostgreSQL real (15 e 18 no CI).
- ADR 0006 escrito: espaçamento do `basic.return` e a interação com a cabeça por chave.
- `OPERATIONS.md` com as opções novas e o tempo total até a DLQ; escopo (G2 e Contrato técnico), ADR 0003 e `GUARANTEES.md` atualizados; API pública declarada; CHANGELOG atualizado.
