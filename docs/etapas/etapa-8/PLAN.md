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
| Custo desligada | p99 da transação e vazão do claim contra a tag `v0.1.0-alpha` | No máximo 5% | Agendado |

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

### 8c-2 — M ≥ 2 em rodadas

Mais perto da linha de corte do plano. Desenho a fechar no início do PR, com base nestes pontos já levantados:

- **Opção.** `MaxPerKey` (M) em `WaybillDispatcherOptions`, default 1, de 1 a 100, acima de 1 só com a ordenação ligada.
- **Claim em três passos.**
  - (1) Cabeças, como na 8c-1.
  - (2) Sucessoras: para cada cabeça, `CROSS JOIN LATERAL (… ORDER BY sequence LIMIT M − 1 FOR UPDATE SKIP LOCKED)`, porque `FOR UPDATE` não aceita janela nem agregado no mesmo nível.
  - (3) Corte por (posição da cabeça, `sequence`) até o lote efetivo B, com as cabeças primeiro. Prefixo numa CTE final: uma sucessora só fica se todas as não terminais entre a cabeça e ela estão no conjunto já cortado.
  - O lote tem no máximo B linhas. Com B ocupado só por cabeças, M não age naquele ciclo. No meio aberto, M = 1.
- **Rodadas como política do transporte.**
  - O transporte declara se preserva a ordem dentro do lote. O RabbitMQ não preserva (`Task.WhenAll`, isolamento depois de um 406) e recebe rodadas. O Kafka da etapa 9 pode receber a série numa transação só. Isso é desenhado junto com a mudança do `ITransport` da etapa 9.
  - "Uma mensagem por chave por lote" fica descrito como comportamento atual, não como contrato do `ITransport`.
- **Desfecho interno de devolução, fora do `PublishResult`.**
  - O `React` recebe os resultados reais da rodada 1 e as falhas de conexão de qualquer rodada. Conexão abre o breaker em qualquer rodada.
  - Nenhuma rodada nova começa depois do pedido de shutdown.
- **Prazo.**
  - Sejam T0 o início da rodada 1 (relógio monotônico) e D a duração da rodada k − 1. A rodada k só começa se `PublishTimeout − (agora − T0) ≥ D`. Senão, as rodadas ≥ k voltam a `pending` sem gastar tentativa e sem pressão.
  - Timeout numa rodada k > 1 não conta pressão. Exceção registrada no ADR 0003.
  - Um lote com rodada não iniciada conta como saudável para o lote crescer.
- **Defeito local.** Defeito de tamanho no meio da série conta como falha da rodada daquela chave.
- **Vazão.** Por chave, M ÷ (claim + M idas de confirmação + marcação), documentada no `OPERATIONS.md`.
- **Testes previstos.**
  - `G4_SucessoraComIdMenor_LoteDeUm_CabecaSai`;
  - `G4_PosseNoLimite_UmInstantePorInstrucao` (relógio de teste pelo `search_path`, movido da 8c-1);
  - `G4_AntecessoraDoMeio_PrefixoCorta` (travada, em espera, `dlq`);
  - `G4_RodadasSeguemSequenceNaoId`;
  - `G4_MaiorQueACadeiaELoteCurto_PrefixoPorChave`;
  - `G4_CorteDoLote_NaoQuebraOPrefixo`;
  - `G4_RodadaComRetornoDeN_NaoEnviaASeguinte`;
  - `G4_DefeitoLocalNoMeioDaSerie_NaoEnviaASeguinte`;
  - `G4_RodadaTardia` em três casos com `FakeTimeProvider` (não começa; estoura sem pressão; controle: timeout na rodada 1 conta pressão);
  - `G4_ShutdownNaRodadaDois_PrimeiraEntregaEmOrdem`;
  - a marcação concorrente e a troca de dono com M ≥ 2;
  - a propriedade com M ≥ 2;
  - unidade de `MaxPerKey`.

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
