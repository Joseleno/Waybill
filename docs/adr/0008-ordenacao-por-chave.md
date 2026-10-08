# ADR 0008 — Ordenação por chave: numeração no banco e cabeça por chave

- Status: aceito (parte 1, com M = 1; a parte 2, M ≥ 2 em rodadas, entra com a 8c-2)
- Data: 2026-10-08
- Etapa: 8c-1 (terceiro PR da ordenação por chave, v0.2)

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
| Qual o critério de custo? | Desligada, o p99 da transação e a vazão do claim ficam a no máximo 5% da `0.1.0-alpha`, medidos no job agendado contra a versão emulada no mesmo teste. Ligada, o custo é medido e publicado no `OPERATIONS.md`, sem critério de aprovação |
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
- **Custo do claim com fila longa à frente.** O claim percorre `ix_outbox_claimable` em ordem de `id` e lê, e descarta, as linhas de uma chave com fila longa à frente, seja uma chave parada na DLQ, seja uma chave quente. São ~4 buffers e ~12 µs por linha (p50 de 120 ms com 10 mil linhas, 1,19 s com 100 mil). Passou do limite de 50 ms da etapa, então um ponteiro de cabeça vem em PR próprio antes da 8c-2. Achado para esse desenho: uma marca de cabeça calculada no INSERT corre contra a publicação concorrente da cabeça anterior.
- **Ordem de upgrade.** O INSERT do pacote passa a ter `lock_keys`, então a migration da v0.2 roda antes do deploy de quem enfileira. O `Down` recusa depois que uma chave foi numerada ou uma linha liberada.
- **Ordenação desligada.** O claim fica igual ao da versão anterior; o p99 da transação variou mais de 5% entre rodadas só por ruído nesta máquina, e o critério é decidido no job agendado.
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
