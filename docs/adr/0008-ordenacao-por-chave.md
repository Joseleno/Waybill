# ADR 0008 — Ordenação por chave: numeração no banco e cabeça por chave

- Status: aceito (parte 1, com M = 1; parte 1b, o ponteiro de cabeça; parte 2, M ≥ 2 em rodadas)
- Data: 2026-10-08 (parte 1b: 2026-10-13)
- Etapa: 8c-1 (terceiro PR da ordenação por chave, v0.2); parte 1b no PR seguinte

## Contexto

A G4 promete que os eventos de uma chave de agregado saem na ordem de commit. O spike da etapa 0 (ADR 0001) provou a cabeça por chave sobre o lease por partição. Também deixou três riscos abertos:
- o `SKIP LOCKED` pula uma cabeça travada e, com mais de uma linha por chave no lote, leva a seguinte;
- o contador por chave dentro do EF não foi medido no lugar onde ele de fato roda;
- chaves travadas em ordem diferente causam deadlocks em série.

A 8a espaçou o `basic.return` (ADR 0006), e a 8b entregou a posse da partição (ADR 0007). Esta parte fecha a G4 na publicação com M = 1. O desenho passou por duas revisões antes de virar código: um revisor adversarial com uma sondagem no EF 10, no Npgsql 10 e no PostgreSQL 18, e depois um time de cinco revisores. Três defeitos bloqueantes da primeira versão caíram ali. A implementação e o teste de propriedade acharam mais dois, registrados abaixo.

## Decisão

1. **Quem numera é o banco.** Um trigger `BEFORE INSERT` na `outbox` numera toda linha com chave enquanto a linha de `waybill.settings` existe:
   - faz um upsert em `outbox_keys (key, seq)` e grava `seq + 1` em `NEW.sequence`;
   - sem `settings`, deixa a `sequence` nula;
   - a aplicação não grava `sequence`, e `OrderByKey` passou para `WaybillDispatcherOptions`, de modo que só os dispatchers precisam concordar;
   - a função é `SECURITY DEFINER`, para que um papel com INSERT só na outbox continue enfileirando;
   - `outbox_keys` nunca é limpa, nem pela retenção, nem por SQL de operação, porque recomeçar uma chave em 1 seria regressão para o consumidor.
2. **Ordem de commit.** O upsert roda na instrução do INSERT, dentro da transação da aplicação, e a linha do contador fica travada até o commit:
   - a `sequence` segue a ordem de commit;
   - não fica buraco no rollback nem no savepoint do EF;
   - a transação anterior sai do ProcArray antes de soltar a trava, então todo snapshot que vê `n` vê `n − 1`.
3. **A lista de chaves viaja na linha.** O EF insere em ordem de `id`, não de chave. Duas transações com K1 e K2 em ordens opostas fariam deadlock. O caminho escolhido:
   - um interceptor de `SavingChanges` grava em `lock_keys`, em toda linha com chave, as chaves distintas do `SaveChanges` quando são duas ou mais;
   - o trigger trava a lista, ordenada por `COLLATE "C"`, na primeira linha da outbox: primeiro insere as chaves novas (`ON CONFLICT DO NOTHING`), depois trava todas com `FOR UPDATE`;
   - em seguida, anula a coluna;
   - um marcador `set_config(…, true)`, que vale só na transação, evita travar de novo nas linhas seguintes;
   - a trava cai depois das linhas da aplicação, no mesmo ponto em que um `SaveChanges` de uma chave trava;
   - a lista vai só na primeira linha com chave que o EF insere (o menor `id`), e não em todas: em todas, um `SaveChanges` de N chaves carregaria N × N chaves, também com a ordenação desligada (achado da revisão de código; com 10 mil chaves passaria do limite de 1 GB de uma mensagem do PostgreSQL).

   O interceptor é registrado pelo `AddWaybillOutbox` e não faz I/O nem guarda estado. Ele não reabre o buraco de G1 do ADR 0002: aquele interceptor anexava linhas de um buffer próprio, e este só preenche uma coluna de linhas que já estão no change tracker. Um contexto montado fora de `AddDbContext` fica sem o interceptor e gera um aviso, uma vez por tipo. Sem ele nada se perde nem sai de ordem; só pode haver deadlock, e um deadlock se resolve repetindo a transação.
4. **Ids na ordem do `Enqueue`.** Dois UUIDv7 do mesmo milissegundo comparavam em ordem aleatória, então mensagens de uma chave numa transação recebiam `sequence` fora da ordem do `Enqueue`: numa rajada de 200, 197. O `message_id` passou a ser um UUIDv7 monotônico no processo (RFC 9562, seção 6.2, método 1). Um contador de 12 bits em `rand_a` ordena os ids do mesmo milissegundo, e passando de 4096 o gerador usa o milissegundo seguinte.
5. **Cabeça por chave.** A cabeça é a primeira linha com `sequence` não terminal da chave:
   - não terminal: `pending` (inclusive em espera depois de um `basic.return`), `claimed` e `dlq`; terminal: `published` e `released`;
   - com M = 1, o claim leva só cabeças: além da condição de reivindicável e da posse da partição, exige que não exista antecessora não terminal (`ix_outbox_key_sequence`);
   - a condição de reivindicável e a posse se repetem no `UPDATE` externo; o filtro de cabeça não se repete, pelo item 6;
   - o relógio é lido uma vez por instrução e vale para todas as linhas e para o lease que elas recebem;
   - linhas com chave e sem `sequence` (gravadas antes de ligar a ordenação) não entram no filtro.
6. **Por que o snapshot velho da subconsulta de cabeça não quebra a ordem (A1).** Ao travar uma linha alterada por outra transação, o PostgreSQL reavalia só os predicados da própria linha; a subconsulta de cabeça usa o snapshot do comando. Os atores que mudam linhas por fora, um a um:
   - **Zumbi com lease vencido e linha ainda não reivindicada de novo.** O cercamento confere `owner` e `fence`, não o lease, então ele pode marcar, devolver ou mandar à DLQ a cabeça. No snapshot ela segue `claimed`, portanto não terminal: a sucessora não entra, e no próximo claim tudo é relido.
   - **`ReleaseOwnedSql`.** Devolve só linhas `claimed` do próprio dono. Mesmo efeito.
   - **Reenvio da DLQ e liberação.** O reenvio leva `dlq` a `pending`; a liberação, `dlq` a `released`. No snapshot a linha ainda é `dlq`, então o snapshot só erra para o lado conservador.
   - **Retenção.** Apaga só `published`, que já é terminal.
   - **O trigger.** Nenhuma antecessora nasce depois de uma sucessora visível, porque a `sequence` segue a ordem de commit.
   - **O que segura tudo isso.** Uma linha nunca sai de terminal, e um trigger recusa `published` ou `released` voltarem. Com uma linha por chave no lote, uma cabeça pulada pelo `SKIP LOCKED` não leva nada atrás dela. É o risco aberto pelo spike.
7. **DLQ e liberação.**
   - Uma linha na DLQ para a própria chave, e as outras chaves seguem.
   - O log da DLQ traz a chave e a `sequence`.
   - `waybill.outbox.blocked_keys` conta as chaves paradas e não é reportado com a ordenação desligada. É lido pelo índice parcial `ix_outbox_blocked_keys`.
   - A liberação é um SQL do `OPERATIONS.md`, executado por teste como está escrito. Ele passa as linhas `dlq` da chave a `released`, com `released_at` e `released_by`, e devolve as sequências que viram lacuna.
   - A API de liberação continua na v1.0.
8. **Transições.**
   - **Mudar P** atualiza `settings` e recria as partições, sem apagar a linha, então a numeração não para.
   - **Desligar** apaga `settings` e não toca `outbox_keys`.
   - **Ligar com tráfego** deixa sem `sequence` as mensagens de transações abertas antes de `settings` (as RC que já tinham inserido e as RR, pelo snapshot). Elas não são ordenadas, e o `OPERATIONS.md` manda ligar com os produtores parados para ter ordem desde a primeira mensagem.
   - **Claim sem ordenação** ignora linhas com chave enquanto `settings` existe. Assim, uma instância antiga num deploy rolante não as leva antes de parar.
   - **Trigger.** O dispatcher que ordena confere `pg_trigger.tgenabled` com a configuração, no startup e a cada `PartitionLease`. Trigger ausente ou desligado é erro crítico.
9. **`OutgoingMessage.Sequence` e `waybill-sequence`.**
   - O transporte recebe a `sequence` da mensagem ordenada, e nula nas demais.
   - O RabbitMQ a envia no cabeçalho `waybill-sequence`, como string decimal, ao lado de `waybill-key`.
   - Uma linha levada pela cabeça que tem mais da mesma chave atrás manda o laço direto ao ciclo seguinte; sem isso, uma chave sozinha sairia a uma mensagem por `PollingInterval`.
10. **O enunciado da G4 na publicação.** Por chave, a partir da primeira mensagem com `sequence`, a primeira cópia de cada mensagem a chegar ao broker chega depois da primeira cópia de todas as anteriores, na ordem de commit. Cópias repetidas podem chegar depois e são reconhecidas pelo `message_id`. Uma mensagem liberada fica fora da sequência: deixa lacuna e, se uma tentativa anterior dela ficou sem confirmação, uma cópia ainda pode chegar a qualquer momento. Vai para o escopo agora e para o `GUARANTEES.md` só na etapa 12, com a detecção no consumidor (etapa 10).

## Respostas às perguntas do plano da v0.2

| Pergunta | Resposta |
| --- | --- |
| A ordenação é opcional? | Sim, desligada por padrão (ADR 0007, item 1). Desligada, o contador e o filtro não rodam. O trigger só confere a tabela de uma linha e anula a lista |
| Qual o critério de custo? | Desligada, a vazão do claim fica a no máximo 5% da `0.1.0-alpha`, e o p99 da transação é medido e publicado, com alarme acima de +35% (revisto no adendo de 2026-10-21; antes, 5% também no p99), no job agendado contra a versão emulada no mesmo teste. Ligada, o custo é medido e publicado no `OPERATIONS.md`, sem critério de aprovação |
| Como o espaçamento convive com a cabeça? | Uma linha em espera é cabeça e para a chave. Na DLQ, continua parando até ser reenviada ou liberada. O teto total do espaçamento (15 min por padrão, ADR 0006) é o prazo do operador antes de a chave parar |
| Como liberar uma chave antes da v1.0? | Com o SQL do `OPERATIONS.md` (item 7), testado como está escrito |

## Alternativas descartadas

| Alternativa | Por que não |
| --- | --- |
| Contador no `SavingChanges` | Roda antes de o EF abrir a transação; com um comando só, o EF nem abre uma. O upsert sairia da transação |
| Trava da lista no começo do comando do `SaveChanges` | Deadlock reproduzido contra um `SaveChanges` de uma chave que toca a mesma linha da aplicação: o EF grava as linhas da aplicação antes das da outbox |
| Lista por `SET LOCAL` anteposto ao lote | O `NpgsqlModificationCommandBatch.Consume` exige que cada instrução do lote afete exatamente uma linha; qualquer instrução anteposta quebra o `SaveChanges` |
| Lista por `set_config` em ida própria | Um `ROLLBACK TO SAVEPOINT` restaura a lista velha. O retry da estratégia de execução reenvia sem ela. O estado entre eventos do EF vaza em contextos de pool |
| Travar a lista com `ON CONFLICT DO UPDATE` sem efeito | Medido no protótipo: 37% mais p99 sob disputa que as duas etapas, e uma versão nova da linha a cada trava |
| A aplicação decide a numeração (`OrderByKey` em quem enfileira) | Uma aplicação desalinhada gravaria sem ordem enquanto os dispatchers ordenam |
| Recusar no `Enqueue` um contexto sem o interceptor | O `FakeOutbox` compartilha a verificação e é usado com contextos InMemory montados à mão; a falta do interceptor não fere a G4 |
| Detectar trigger ausente por `created_at` posterior a `settings` | Falso alarme na transição, com transações abertas antes de ligar |
| Repetir o filtro de cabeça no `UPDATE` externo | Desnecessário pelo item 6, e a reavaliação usaria o snapshot velho do mesmo jeito |
| M ≥ 2 já na 8c-1 | A G4 não depende de M, e quase metade dos riscos achados vinha das rodadas: ficou para a 8c-2 |

## Consequências

- **Custo medido no protótipo** (EF 10, PG 18, 32 produtores; `docs/etapas/etapa-8/PROTOTIPO-CONTADOR.md`):
  - com chaves distintas, o contador custa de 5% a 12% de vazão, com p99 igual;
  - uma chave quente fica em ~347 transações por segundo, p99 ~600 ms;
  - com 2 ms de trabalho depois do `SaveChanges`, a chave quente cai à metade.
- **A trava da chave vai até o commit.** Uma transação que enfileira K e depois escreve na linha R da aplicação faz deadlock com outra que escreve R e depois enfileira K. Nenhuma lista cobre isso. O `OPERATIONS.md` manda enfileirar no último `SaveChanges`.
- **Isolamento.** Em `REPEATABLE READ` ou `SERIALIZABLE`, enfileirar ao mesmo tempo na mesma chave dá 40001. O EF o entrega como falha transitória, e repetir resolve.
- **Custo do claim com fila longa à frente.** O claim percorre `ix_outbox_claimable` em ordem de `id` e lê, e descarta, as linhas de uma chave com fila longa à frente, seja uma chave parada na DLQ, seja uma chave quente. São ~4 buffers e ~12 µs por linha (p50 de 120 ms com 10 mil linhas, 1,19 s com 100 mil). Passou do limite de 50 ms da etapa, então um ponteiro de cabeça vem em PR próprio antes da 8c-2. Achado para esse desenho: uma marca de cabeça calculada no INSERT corre contra a publicação concorrente da cabeça anterior. Resolvido pela parte 1b, abaixo.
- **Ordem de upgrade.** O INSERT do pacote passa a ter `lock_keys`, então a migration da v0.2 roda antes do deploy de quem enfileira. O `Down` recusa depois que uma chave foi numerada ou uma linha liberada.
- **Ordenação desligada.** O claim fica igual ao da versão anterior; o p99 da transação variou mais de 5% entre rodadas só por ruído nesta máquina, e o critério é decidido no job agendado.
  - O job agendado reprovou o p99 da transação em 6 de 9 execuções (+4,7% a +15,7%). A causa estava no teste: a linha de base registrava o contexto à mão, e a variante desligada usava o `AddDbContext`, que monta as opções em cada escopo. Esse custo do cliente caía na conta da ordenação.
  - Medido em Oct 20, 2026, local, com o p99 da transação contra a linha de base:
    - linha de base contra ela mesma: −2,5% a +1,1%;
    - registro assimétrico: +6,1% a +10,8%;
    - as duas com `AddDbContext`: mediana de +3,1%.
  - O trigger de numeração custa ~3,4 µs por linha com chave e de 7 a 30 µs por transação (`pgbench`, sem `fsync`). `SECURITY DEFINER` e `SET search_path` não mudam o custo de forma mensurável.
  - O teste passou a registrar as duas variantes com `AddDbContext` e subiu de 5 para 7 rodadas. O critério de 5% não mudou nesse momento; foi revisto no adendo de 2026-10-21, ao fim deste ADR.
  - O claim da parte 2 com a ordenação desligada ficou numa mediana de −2% contra o da tag, com caudas de −6% a −14% quando a máquina estava carregada. Linha de base contra ela mesma: ±0,6%.
- **Chave longa.** A chave continua limitada a 255 bytes UTF-8 no `Enqueue`, desde a v0.1, bem abaixo do limite do btree de `outbox_keys`.
- **Relógio do `MessageId`.** O gerador nunca recua: se o relógio do processo saltar para o futuro e voltar, os ids seguem com o timestamp do futuro até o relógio alcançá-lo. A retenção, que escolhe pelo tempo do id, só retém essas linhas por mais tempo.

## Testes que provam

- **Enfileiramento:**
  - `G4_ListaDeChaves_SemDeadlock` (ordens opostas com chaves novas e existentes; uma chave e duas na mesma linha da aplicação; marca da lista por transação; com controles negativos que deadlocam);
  - `G4_ListaDeChaves_NaoSobreviveAoSaveChanges`;
  - `G4_SequenciaContiguaNaOrdemDeCommit`;
  - `G4_MesmaChaveNaMesmaTransacao_SequenciaNaOrdemDoEnqueue`;
  - `G4_ChavesComSeparadoresEUnicode_ListaFiel`;
  - `G4_AplicacaoEmRRouSerializable_Erro40001ERepetirResolve`;
  - `G4_PapelSoComInsert_Enfileira`;
  - `G4_ContextoSemInterceptor_AvisaUmaVez`;
  - `Limite_EscritaDepoisDeEnfileirar_PodeDarDeadlock`.
- **Claim:**
  - `G4_ClaimOrdenado` (uma cabeça por chave por lote, em ordem; marcação ou devolução concorrente; dispatcher sem ordenação ao lado de quem ordena; instante único);
  - `G4_ChaveQuenteSozinha_NaoEsperaPollingInterval`;
  - `G4_ChaveBloqueadaComCemMilAFrente_ClaimContinuaFluindo`.
- **DLQ e operação:**
  - `G4_ChaveNaDlq`;
  - `Operacao_LiberarChave`;
  - `Operacao_ConsultasDeDiagnostico`;
  - `G4_Retencao_NaoApagaOutboxKeysNemReleased`;
  - `G4_EstadoTerminal_NaoVolta`.
- **Transições:**
  - `G4_TransicoesDaOrdenacao` (ligar com transação aberta, backlog anterior, trigger desligado, troca de dono com cabeça em voo);
  - `Operacao_MudarP`;
  - `Operacao_ReiniciarOrdenacao`;
  - `G4_OrdenacaoDesligada_SemContadorNemFiltro`;
  - `G4_SequenceNoEnvelope` (transporte falso e RabbitMQ real).
- **Schema:**
  - `Schema_MigracaoInterrompidaNoIndice_ReexecutarCompleta`;
  - `Schema_DownComOrdenacaoUsada_Recusa`;
  - `Schema_DownSemOrdenacaoUsada_VoltaAV01EVolta`;
  - upgrade da `0.1.0-alpha` ampliado.
- **Unidade:** `MessageIdTests`; `Propriedades_ComSequence_HeaderWaybillSequence`.
- **Caos:** `G4_Propriedade_PrimeiraEntregaEmOrdem` (20 s no PR, 10 min no agendado). Ele achou a ressalva das liberadas: uma cópia atrasada de uma mensagem, depois recusada e liberada, chegou depois das sucessoras.
- **Agendado:** `G4_Backlog_ClaimOrdenadoMedido` e `G4_CustoDesligada_ContraV01`.

**Verificado por mutação:**
- **Lista de chaves.** O interceptor sem lista e o trigger ignorando a lista são pegos pelos testes de ordens opostas. O marcador da sessão inteira é pego pelo teste da marca por transação.
- **Claim.**
  - O filtro de cabeça é pego por quatro testes, e pelo teste de propriedade, com 431 inversões.
  - `dlq` fora dos bloqueantes é pego pelo teste da DLQ; `claimed` fora dos bloqueantes, pela troca de dono.
  - A guarda de `settings` no claim sem ordenação é pega pelo teste do dispatcher antigo.
  - O sinal de chave quente é pego pelos testes de cabeça e de chave quente.
- **Operação.** A verificação do trigger, o trigger de estado terminal, o `SECURITY DEFINER`, o índice inválido, o `VALIDATE` e a recusa do `Down` são pegos pelos testes de cada um.
- **Aviso.** O aviso de interceptor ausente é pego pelo teste dele.

## Parte 1b — ponteiro de cabeça

### Contexto

A parte 1 decidia a cabeça no claim, linha a linha, e o claim lia e descartava toda a fila atrás de uma cabeça que não podia sair. A sondagem do desenho, em PG 18.6 e 15.19, mostrou que o caso não é só a cabeça na DLQ:
- uma chave quente sozinha fazia cada claim varrer as n sucessoras para tentar encher o lote: com 100 mil pendentes, 109 ms por claim, O(n²) para drenar a chave;
- com uma chave dominando as estatísticas, o planner sondava o `NOT EXISTS` da cabeça por Seq Scan, atravessando as linhas já publicadas: drenar 4 mil levou 1.010 s.

O desenho passou por um time de cinco revisores (concorrência, desempenho, EF e produtor, operação, testes e escopo) e pela sondagem, que implementou a marca à mão e forçou as intercalações em PostgreSQL real. A abordagem sem estado (achar a cabeça de cada chave a cada claim) foi descartada por custo: com 500 mil chaves ativas, milhões de buffers por claim.

### Decisão

11. **A marca.** Coluna `is_head boolean DEFAULT true`, fora do modelo do usuário. `true` é candidata ao claim: a cabeça, ou um falso positivo. `false` quer dizer que a antecessora é linha da própria transação; nula, que a decisão ficou para o commit. Só `true` entra no `ix_outbox_claimable`, cujo predicado passa a ser `status IN ('pending', 'claimed') AND (sequence IS NULL OR is_head)`, com o mesmo nome. Os três SQL que o percorrem repetem o predicado, para que um plano genérico o use. A marca só restringe as candidatas: o claim continua decidindo a cabeça no snapshot, agora por `min(sequence)`, que o planner resolve por uma descida de índice com qualquer estatística. Um falso positivo custa uma sonda; uma marca faltando pararia a chave, e o resto da decisão existe para que isso não aconteça.
12. **No INSERT.** Depois de numerar, o trigger grava `false` se a antecessora é a última linha numerada desta transação (lembrada num GUC local, desfeito com o savepoint), `true` fora de READ COMMITTED, nula se há antecessora inacabada visível, e `true` nos outros casos.
13. **No commit.** Um constraint trigger diferido decide as linhas nulas: escolhe a antecessora inacabada mais alta sem trava e a trava com `FOR SHARE SKIP LOCKED`, refazendo o filtro.
    - **Travou:** ela continua inacabada até o fim deste commit. Quem a terminar depois espera esse fim, e a promoção, VOLATILE e com snapshot novo, vê esta linha. A marca fica nula.
    - **Pulou ou já terminou:** a linha se marca. No pior caso é um falso positivo.
    - **O produtor nunca espera o dispatcher**, então não há ciclo de travas.
    - **Por que fora de READ COMMITTED a linha nasce `true`:** travar uma antecessora publicada depois do snapshot daria 40001 no commit, um modo de falha novo.
14. **Promoção.** Quando uma linha numerada termina (`published` ou `released`) ou é apagada inacabada, a primeira linha inacabada da chave recebe a marca. A promoção não depende de `settings`, para que uma chave numerada siga andando com a ordenação desligada e religada. Ela só roda em READ COMMITTED: em níveis mais estritos leria o snapshot da transação e deixaria sem marca a linha de um produtor commitada no meio. O dispatcher já roda em READ COMMITTED, e uma liberação ou um `DELETE` de operador em outro nível é recusado com mensagem (achado da revisão independente).
15. **Desligar.** O SQL `ordering-reset` marca toda linha numerada inacabada, na transação que apaga `settings`: o claim sem ordenação percorre o mesmo índice, e atrás de uma cabeça na DLQ, que não para nada com a ordenação desligada, as linhas nunca sairiam. O `UPDATE` não vê a linha de um produtor que numerou com `settings` ainda presente e não tinha commitado; o dispatcher sem ordenação marca essas sobras no startup e a cada `PartitionLease`, quando nenhuma instância ordena (achado da revisão independente).
16. **Privilégios e checagens.** As funções novas são `SECURITY DEFINER`, como a do contador. A checagem do dispatcher (startup e a cada `PartitionLease`) cobre os quatro triggers, e uma sessão do dispatcher com `session_replication_role = replica`, onde a própria marcação não dispararia a promoção, é erro crítico.
17. **Rede.** O Waybill não deixa cabeça sem marca, mas uma ação externa pode deixar. Os atores um a um:
    - **um trigger desligado:** pego pela checagem;
    - **uma sessão replica de operador, um restore parcial, um `UPDATE` de `is_head` à mão:** cobertos pela varredura abaixo;
    - **um `DELETE` de linha inacabada:** promove, como a liberação, e o `OPERATIONS.md` diz para liberar, não apagar.

    A cada manutenção de partições, o dispatcher percorre uma fatia de 2 mil chaves das suas partições, saltando de chave em chave no índice, e marca a primeira linha inacabada que estiver sem marca. Cada reparo gera um log de erro com a chave e a `sequence` e conta em `waybill.outbox.heads_repaired`, que fica em zero se nada mexeu na outbox por fora. O `OPERATIONS.md` traz a consulta e o SQL para achar e marcar de uma vez.

### Alternativas descartadas (parte 1b)

| Alternativa | Por que não |
| --- | --- |
| Sem estado: achar a cabeça de cada chave a cada claim | Custo O(chaves ativas) por claim; com 500 mil chaves, milhões de buffers |
| Marca como dica, sem a decisão no commit | A corrida do INSERT com a publicação deixa cabeça sem marca no uso normal; o reparo viraria rotina, a métrica não alertaria, e uma chave esperaria um período da varredura |
| Verificar no commit toda linha da rajada | Numa rajada de 200 linhas de uma chave, o commit ia de 2,5 ms para 6,6 ms, travando linhas da própria transação |
| Um segundo índice ao lado do `ix_outbox_claimable` | Com a ordenação desligada, toda linha entraria nos dois, e a ordenação desligada deixaria de custar zero |
| Manter o 40001 em REPEATABLE READ | Falha nova no commit sempre que a antecessora sai durante a transação; a marca `true` só custa uma sonda |
| Promoção só enquanto `settings` existe | Uma chave numerada parada pela ordenação desligada e religada ficaria sem marca |

### Consequências (parte 1b)

- **Custo do claim.** Medido: com 100 mil linhas paradas atrás de uma cabeça na DLQ, o claim de 100 linhas lê ~4,5 mil buffers (p50 de 5,6 ms), contra ~400 mil antes. O custo é por linha reivindicada, não por linha parada. Uma chave quente sozinha drena com o mesmo custo por mensagem de 1 mil a 8 mil.
- **Ordenação desligada.** O índice tem o mesmo conteúdo da parte 1. O WAL por mensagem medido na sondagem ficou dentro de ±0,2%.
- **Escrita extra.** Cada promoção é um `UPDATE` não HOT da sucessora (`is_head` está no predicado do índice): mais uma versão de tupla e entradas de índice por mensagem publicada com fila atrás.
- **Espera do dispatcher.** A marcação pode esperar o fim do commit de um produtor que travou a antecessora. É curto, exceto com `SET CONSTRAINTS ALL IMMEDIATE`, FK diferida lenta da aplicação, replicação síncrona ou `PREPARE TRANSACTION`, documentados como limite. A ordem não quebra; um lease vencido republica.
- **RR e SERIALIZABLE.** Nenhum 40001 novo; numa chave quente, produtores nesses níveis põem falsos positivos no claim.
- **Estatísticas vazias.** Depois de um TRUNCATE, as primeiras execuções das consultas dos triggers leem a tabela (13 varreduras numa transação de 50 mil linhas), e o plano genérico desce o índice em seguida.
- **Fora daqui.** Previa-se que, na 8c-2, uma marca falso positiva trouxesse ao lote uma sucessora de outra cabeça, a deduplicar. Não traz: a cabeça é decidida por `min(sequence)`, e um falso positivo nunca vira cabeça (parte 2, item 18). O Hash Join com Seq Scan no `UPDATE` externo do claim em plano genérico, que vinha da parte 1, foi corrigido na parte 2 (item 18).

### Testes que provam (parte 1b)

- **Intercalação forçada** (`G4_Ponteiro_VerificacaoNoCommit`): publicação antes do commit do produtor; marcação esperando o commit; marcação em voo, confirmada ou desfeita; produtor de duas chaves contra marcação em lote; liberação concorrente; rajada; `Limite_SetConstraintsImmediate_MarcacaoEsperaOProdutor`.
- **Custo:** `G4_ChaveQuenteSozinha_DrenoLinear`; `G4_ChaveBloqueadaComCemMilAFrente_ClaimContinuaFluindo` (10 mil e 100 mil); `G4_Ponteiro_PlanoGenerico_UsaOIndice`; `G4_Ponteiro_EstatisticasVazias_TransacaoGrandeLinear`.
- **Atores:** `G4_Ponteiro_DeleteDaCabeca_SucessoraViraCabeca`; `G4_Ponteiro_DesligarEReligar_ChavesAndam` (também com um produtor em voo no desligamento); `Operacao_LiberarChave` (recusa fora de READ COMMITTED); `G4_AplicacaoEmRRouSerializable` e `G4_PapelSoComInsert` ampliados; `G4_TriggerDesligado_ErroCritico` (os quatro triggers) e `G4_SessaoReplica_ErroCritico`.
- **Rede e operação:** `G4_Ponteiro_Rede_ReparaCabecaSemMarca`; `Operacao_MarcarCabecas`; `Operacao_ConsultasDeDiagnostico` e `Operacao_ReiniciarOrdenacao` ampliados.
- **Caos:** `G4_Propriedade_PrimeiraEntregaEmOrdem` amostra a invariante "a primeira linha inacabada de cada chave está marcada" numa instrução, durante a corrida e no fim, com um produtor numa chave quente.

**Verificado por mutação (parte 1b):**
- **Decisão no commit.**
  - Sem ela, sete dos oito testes de intercalação falham.
  - Com `FOR KEY SHARE` no lugar de `FOR SHARE`, a marcação não espera o produtor nem o produtor pula a antecessora: cinco falham.
  - Sem `SKIP LOCKED`, o produtor de duas chaves e a marcação em lote fazem deadlock (40P01).
  - Sem a dedup, falha a rajada.
  - Travando também fora de READ COMMITTED, o RR dá 40001.
  - Sem `SECURITY DEFINER`, falha o papel só com INSERT.
- **Promoção.**
  - `STABLE` falha todos: o PostgreSQL recusa o `UPDATE`.
  - Só em `published`: falham a liberação e o teste dos 100 mil.
  - Sem a promoção no DELETE: falha o teste do DELETE.
  - Condicionada a `settings`: equivalente sob o SQL documentado, que marca tudo ao desligar.
- **Claim.**
  - Sem a marca: falham o dreno linear e o teste dos 100 mil.
  - `NOT EXISTS` no lugar do `min`, ou a cláusula esquecida num SQL: falha o plano genérico.
- **Varredura.** Sem ela, falha o teste da rede; sem o filtro de partição, o das fatias.
- **Estatísticas vazias.** Consulta do trigger sem descida de índice: falha o teste delas.
- **Checagens.** Cada trigger fora da checagem é pego pelo caso dele no teste do trigger desligado.

## Parte 2 — M ≥ 2 em rodadas

### Contexto

Com M = 1, uma chave anda uma mensagem por ciclo: claim, ida ao broker e marcação. A G4 não depende de M; M só amortiza o claim e a marcação de uma chave ocupada. O desenho foi sondado em PG 18 e revisto por um time de quatro revisores (concorrência, desempenho, dispatcher e ADR 0003, testes e escopo), dois deles com sondagem no esquema real em PG 18.6 e 15.19. As decisões e as medidas estão no `PLAN.md` da etapa 8 (seção 8c-2); o SQL validado, em `SONDAGEM-8C2-CLAIM.sql` e `SONDAGEM-8C2-PROMOCAO.sql`.

### Decisão

18. **Claim em série.** `MaxPerKey` (M, de 1 a 100, acima de 1 só com a ordenação) leva, de cada cabeça, até M − 1 linhas seguintes da chave, numa instrução:
    - as cabeças vêm exatamente como na parte 1. Os dois SQL saem dos mesmos trechos em C#, para que nenhuma proteção exista num e falte no outro;
    - as sucessoras vêm de `ix_outbox_key_sequence`, por descida a partir da cabeça, até a parte da cabeça no que sobrou do lote (`ceil(sobra / cabeças com sucessora)`), com o prefixo decidido no snapshot: a primeira que não pode sair (lease vivo de outro dono, espera de `basic.return`, DLQ) encerra a série;
    - o corte é por rodada (todas as segundas antes de qualquer terceira), o que reparte o lote e nunca corta uma série no meio;
    - cada sucessora é travada por chave primária, com as condições repetidas na trava; uma pulada ou mudada entre o snapshot e a trava (EvalPlanQual) encerra a série ali;
    - o `UPDATE` acha as linhas por `= ANY(array)` em vez de juntar com a CTE, o que tirou o Hash Join sobre Seq Scan dos dois SQL em plano genérico.

    Uma marca falso positiva nunca vira cabeça (`min(sequence)`), então nenhuma linha vem duas vezes. Com M efetivo 1, inclusive no meio aberto (lote de 1), vale o SQL da parte 1.
19. **Rodadas no dispatcher.** O lote é publicado em uma chamada ao transporte por rodada; a rodada k leva a k-ésima mensagem de cada chave, e só das chaves cuja mensagem da rodada k − 1 foi confirmada. A confirmação antes da rodada seguinte é o mecanismo da ordem: o RabbitMQ publica um lote com `Task.WhenAll`, sem ordem entre as mensagens. Cada chamada leva no máximo uma mensagem com `sequence` por chave; linhas sem `sequence` vão todas na rodada 1. O `ITransport` não muda de assinatura; a declaração "preservo a ordem dentro do lote", que o Kafka usaria, fica para a etapa 9. O contrato do transporte passa a dizer o que já valia com M = 1: `Confirmed` quer dizer que o broker ordena a mensagem antes de qualquer outra publicada depois.
20. **Encerramento.** Qualquer `Retry` numa rodada (de qualquer causa, resultado inválido, exceção ou contagem errada) encerra o lote: nenhuma rodada nova para chave nenhuma. Sob pressão ou com a conexão caindo, mais publicações seriam só mais duplicatas. `Returned` ou `Defect` encerra só a própria chave. O que não chega a ser enviado volta a `pending` sem gastar tentativa e nunca chega ao `React`. Um defeito de tamanho é decidido na rodada da linha, não antes do lote.
21. **Prazo.** O lote inteiro divide um `PublishTimeout`, contado de antes do claim pelo `TimeProvider`. A rodada 1 tem o `PublishTimeout` inteiro, como sempre; a rodada k só começa se sobra tempo e se o que sobra é ao menos o que a rodada k − 1 levou, e recebe o que sobra como prazo. Assim as rodadas cabem no lease, salvo dois casos: a rodada 1 depois de um claim lento, que já era assim com M = 1, e a marcação que espera o commit de um produtor, que não tem limite (decisão do autor; ver as alternativas). Os timers continuam no relógio de parede.
22. **Reação (ADR 0003).** O `React` recebe a rodada 1 inteira e, das rodadas seguintes, tudo menos o `ConfirmTimeout`: o prazo de uma rodada tardia foi o que sobrou, e o estouro dela não diz nada do broker. Nack, conexão e causa não declarada contam em qualquer rodada. Um lote com rodada não iniciada conta como saudável.
23. **M efetivo.** Como o lote segue o broker, M segue o prazo. Quando o prazo corta uma rodada, M cai para as rodadas que couberam, com um log; quando um lote roda inteiro, dobra de volta até `MaxPerKey`. Sem isso, com o broker lento, as sucessoras seriam reivindicadas e devolvidas a cada ciclo, sem sinal.
24. **Shutdown e falhas entre rodadas.** O pedido de parada é conferido antes de cada rodada, a primeira inclusive, e nunca lançado. O resultado de cada rodada é gravado assim que chega, e a marcação roda num `finally`: o que foi confirmado vira `published` mesmo quando algo lança depois. Devolvido no shutdown, seria republicado pelo próximo dono, até M cópias por chave a cada deploy. A marcação repete em 40P01 e 40001, porque as instruções são cercadas por fence.
25. **Promoção em laço** (refaz a `SchemaV0_2`). A revisão achou que duas terminações concorrentes da mesma chave deixavam a chave sem cabeça marcada: o zumbi publica m2 enquanto o novo dono publica m1, ou um `DELETE` de m2 (este já com M = 1). A promoção de m1 mirava m2, esperava a outra transação, e o EvalPlanQual não reavaliava o alvo. Agora a promoção:
    - lê a menor não terminal da chave e sai se ela vem antes da linha terminada, porque quem terminar essa promove;
    - senão, trava o alvo com `FOR NO KEY UPDATE`, que espera quem está mexendo nele, e refaz o status;
    - se o alvo terminou no meio, procura de novo.

    O trigger de `UPDATE` só dispara quando a linha terminada estava marcada. Assim, uma série publicada num `UPDATE` promove uma vez, a partir da cabeça, e não uma vez por linha, percorrendo as que a própria instrução acabou de publicar.
26. **Sessões do dispatcher sem JIT.** Com fila grande, o custo estimado do claim em plano genérico passava do `jit_above_cost`, e o PostgreSQL compilava o claim a cada execução: cerca de 0,5 s por claim com 200 mil linhas na fila. A conexão própria do dispatcher roda `SET jit = off` em cada conexão física e não reseta a sessão ao voltar ao pool (ele não guarda estado de sessão). A revisão final apontou que o parâmetro de startup `options`, usado na primeira versão, é recusado por poolers como o PgBouncer. Atrás de um pooler em modo transação, o `SET` não acompanha a transação até o servidor; ali o caminho é `ALTER ROLE … SET jit = off` para o papel do dispatcher, como diz o `OPERATIONS.md`.

### Alternativas descartadas (parte 2)

| Alternativa | Por que não |
| --- | --- |
| Corte por chave (a primeira cabeça leva suas M − 1 antes da segunda) | Favorece a chave mais antiga e exige verificação de prefixo; o corte por rodada preserva o prefixo por construção |
| Prefixo por junção entre a lista cortada e a travada | Quadrático em plano genérico: 809 mil comparações e 115 ms com B = 1000, 2,7 s com B = 5000 |
| Travar as sucessoras antes do corte | 900 travas sem uso num lote cheio de cabeças |
| Declarar a ordem no `ITransport` já | O único transporte que a usaria é o Kafka da etapa 9; desenhar antes seria adivinhar |
| `lock_timeout` na marcação | Desistir libera a instância, mas republica o lote depois do lease. Esperar não duplica: nenhum outro claim leva a linha travada (decisão do autor) |
| M fixo com o broker lento | Sucessoras reivindicadas e devolvidas a cada ciclo, sem sinal |
| Prazo e timers no `TimeProvider` | Os timers no relógio falso penduram os testes que falseiam só o relógio do breaker e dependem de timeout real |

### Consequências (parte 2)

- **Vazão de uma chave.** Uma chave anda M mensagens por (claim + M idas ao broker + marcação). Medido com uma chave sozinha, 600 mensagens e confirmação de 2 ms por chamada: 144 mensagens/s com M = 1 e 387 com M = 20 (600 e 30 ciclos).
- **Custo do claim.** Cerca de 27 buffers por linha reivindicada em todos os formatos medidos; 20 a 26 ms com B = 1000 e 138 ms com B = 5000; backlog de 550 mil, p50 de 5,3 ms com M = 1 e 7,6 ms com M = 10.
- **O lote pode sair abaixo do ótimo** com séries desiguais, pelo teto por cabeça. Inofensivo.
- **Duplicatas multiplicadas por M.** Um crash ou uma falha da marcação depois de r rodadas republica até r mensagens por chave, contra uma com M = 1. A G4 permite: são cópias posteriores.
- **Falsos positivos persistentes.** Um produtor em RR numa chave quente, ou religar a ordenação com chaves paradas na DLQ, marca todas as linhas, e o claim volta ao custo de antes do ponteiro: 405 mil buffers com 2 mil chaves paradas e 50 atrás de cada, contra 2,5 mil só com as cabeças. A ordem continua certa. Rebaixar essas marcas pela varredura ficou na issue #21.
- **Rede.** A âncora da varredura em plano genérico não conseguia buscar o cursor e lia o índice desde a primeira chave: 1.390 buffers numa fatia de 10 chaves, agora 124. Dois SQL, com e sem cursor. Uma falha da varredura é logada e não derruba o ciclo.

### Testes que provam (parte 2)

- **Claim** (`G4_Rodadas_Serie`, `G4_Rodadas_Claim`):
  - antecessora do meio, no snapshot e travada;
  - mudança entre o snapshot e a trava, forçada pelo relógio de teste no `search_path`;
  - sucessora que não gasta vaga;
  - lote cheio de cabeças, sem trava (`xmax = 0`);
  - corte por rodada;
  - sucessora com `id` menor;
  - cadeia menor que M;
  - falso positivo;
  - liberada no meio;
  - cabeça em espera de `basic.return`.
- **Testes da parte 1 em M ∈ {1, 3}:** dono antigo da partição; troca de dono em voo; marcação ou devolução concorrente; chave na DLQ; instante único, agora por contagem de leituras do relógio.
- **Plano e custo** (`G4_Rodadas_PlanoGenerico_SemLacoQuadraticoNemSeqScan`, `G4_Rodadas_Custo`): sem laço quadrático, Seq Scan ou sort em disco nos dois SQL; custo linear no lote; série que não lê além da sua parte; chave quente com M = 100.
- **Rodadas** (`G4_Rodadas_Dispatcher`, `G4_Rodadas_Prazo`, `G4_Rodadas_Shutdown`):
  - ordem por `sequence`;
  - linhas sem `sequence`;
  - `Retry` de cada causa encerrando o lote;
  - `Returned` e defeito na série;
  - promoção depois da marcação da série;
  - laço;
  - marcação repetida em 40P01 e 40001;
  - rodada tardia;
  - pressão só fora do prazo;
  - conexão na rodada 2;
  - M efetivo caindo e voltando;
  - meio aberto;
  - shutdown e exceção entre rodadas.
- **Promoção** (`G4_Ponteiro_Promocao`): terminações concorrentes nas duas ordens, com falso positivo, `DELETE` e liberação; terminações simultâneas sem deadlock; uma promoção por série.
- **Concorrência:** `G4_TrocaDeDono_ZumbiComFenceNaSucessora`.
- **Caos:** `G4_Propriedade_PrimeiraEntregaEmOrdem` com M e lote sorteados por instância, entrega em qualquer ordem dentro da chamada e nenhuma chamada com duas mensagens ordenadas de uma chave.
- **Medidas:** `G4_Rodadas_VazaoPorChave_Medida`; `Limite_FalsosPositivosPersistentes_ClaimLeAFilaDasChavesParadas`; `G4_Backlog_ClaimOrdenadoMedido` com M = 1 e 10 (agendado).
- **Issue #21:** `G4_Ponteiro_Rede_PlanoGenerico_UsaOIndice`; `G4_Ponteiro_Rede_ErroNaVarredura_NaoDerrubaOCiclo`; `Limite_SetConstraintsImmediate_EsperaLongaSemDuplicata`.

**Verificado por mutação (parte 2).** Cada uma quebra um teste nomeado:
- **Claim:**
  - prefixo por junção;
  - `UPDATE` juntado à CTE;
  - sem as condições na trava;
  - sem prefixo no snapshot;
  - sem prefixo depois da trava;
  - corte por chave;
  - trava antes do corte;
  - rank por `id`;
  - sem teto por cabeça;
  - cabeças sem a espera de `basic.return`, que só caiu depois do teste da cabeça em espera: com M ≥ 2, a cabeça descartada pelo `UPDATE` deixaria passar as sucessoras;
  - cabeças só pela marca;
  - liberada bloqueando;
  - relógio lido de novo na série.
- **Promoção:**
  - sem refazer o status;
  - parar num alvo marcado;
  - sem a saída por antecessora, que só caiu com o teste das terminações simultâneas;
  - trigger em toda linha.
- **Dispatcher:**
  - prazo por rodada;
  - `Retry` encerrando só a chave;
  - shutdown lançado;
  - M fixo;
  - `MoreOfKeys` sem as devolvidas, contando as paradas ou por qualquer linha;
  - defeito antes do lote;
  - não enviada gastando tentativa;
  - sem repetir a marcação;
  - rodada k sem conferir a k − 1;
  - `React` contando toda rodada;
  - início sem "≥ D";
  - série inteira numa chamada, pega pelo caos.

## Adendo — critério de custo da ordenação desligada (2026-10-21)

Status: aceito, decisão do autor.

### Contexto

O critério da etapa 8 dizia: com a ordenação desligada, o p99 da transação e a vazão do claim ficam a no máximo 5% da `0.1.0-alpha` no job agendado. Depois do PR #23, o agendado de Oct 21 reprovou o p99 de novo (+11,1%). O `PLAN.md` da etapa já previa isso: se o critério falhasse no runner Linux, a decisão voltaria ao autor. Antes de decidir, o teste foi medido no CI em três runs, numa branch descartável:

| Medida | Resultado |
| --- | --- |
| A/A (as duas variantes como a `0.1.0-alpha`), 6 runners | −4,2% a +1,0%: o método mede bem, e o ruído do runner é de ±4% |
| A/B (desligada contra a `0.1.0-alpha`), 20 runners em três runs | +1,1% a +27,1%, mediana de +13% |
| Sem o trigger `outbox_sequence`, 14 runners | mediana de +5,6%, 7 de 14 dentro dos 5% |
| Sem o trigger, os índices novos e o interceptor, 8 runners | mediana de +3,7%, 5 de 8 dentro dos 5% |
| Trigger com uma flag em sequence no `WHEN` (abaixo), 8 runners | mediana de +6,2%, 3 de 8 dentro dos 5% |
| Vazão do claim, 20 runners | −0,8% a +17,6%: dentro do critério |

O custo está no trigger de numeração: com a ordenação desligada, ele roda em toda linha com chave só para ler `waybill.settings`. No `pgbench`, com uma linha por transação, 16 clientes e sem `fsync` (CPU como limite): 73 mil transações por segundo sem trigger, 70 mil com um trigger vazio e 63 mil com o atual, cerca de 25 µs a mais na latência média. O `SECURITY DEFINER` e o `SET search_path` não pesam; a consulta por linha pesa. No CI, com 16 produtores disputando 4 vCPUs, esse custo vira fila e aparece no p99.

### Decisão

1. **O critério do p99 da transação passa de "no máximo 5%" para custo medido e publicado.** Mesmo um trigger de custo zero deixaria o teste dentro dos 5% em só metade dos runners: o critério não separa custo de ruído neste teste. O número vai para a tabela "What it costs" do `OPERATIONS.md`, como o custo da ordenação ligada.
2. **O teste agendado vira alarme de regressão grosseira:** reprova acima de +35% no p99, acima do maior valor medido (+27,1%). Uma regressão pequena, como uma segunda consulta por linha, não é pega; nenhum teste de p99 a pegaria com esse ruído.
3. **A proteção fina continua estrutural:** `G4_OrdenacaoDesligada_SemContadorNemFiltro` prova que, desligada, a gravação não lê nem escreve `outbox_keys`.
4. **A vazão do claim mantém o critério de 5%.**
5. **O trigger não muda.**

### Alternativas descartadas

| Alternativa | Por que não |
| --- | --- |
| Manter os 5% | O agendado ficaria vermelho para sempre, e um alarme sempre vermelho ninguém olha |
| Trigger desabilitado sem `settings` (`ALTER TABLE ... ENABLE/DISABLE TRIGGER` por um trigger em `settings`) | Sozinho não passa dos 5% (sem trigger nenhum, a mediana fica em +5,6%). O `ALTER` pega SHARE ROW EXCLUSIVE na `outbox` e espera qualquer transação aberta que escreveu nela, com os produtores enfileirados atrás. `CREATE OR REPLACE TRIGGER` reabilita o trigger, um `TRUNCATE` em `settings` não dispara nada e a replicação lógica não leva o `ALTER` |
| Flag em sequence no `WHEN` do trigger (`pg_sequence_last_value`, sem consulta) | Recupera quase todo o custo do trigger (+6,2% contra +12,5%), mas a flag não é transacional e precisa andar junto com `settings`. Desligada com a ordenação ligada (rollback do SQL de desligar, failover com replicação lógica), mensagens com chave sairiam sem numeração: a G4 quebraria. Fica registrada como a primeira opção se o custo pesar para alguém |

### Consequências

- Quem não liga a ordenação paga o trigger em toda linha com chave, e o custo está publicado. Mensagem sem chave não paga nada.
- O nome do teste agendado muda para `G4_CustoDesligada_ClaimAteCincoPorCentoETransacaoMedida`.
