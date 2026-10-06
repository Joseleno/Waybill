# Waybill — Plano de Desenvolvimento

Oct 3, 2026 · @Joseleno

Deriva das garantias e do contrato técnico de Doc. Atualizado em Oct 4, 2026 com o resultado do spike da etapa 0: ordenação para a v0.2, etapa 3b removida, claim por linha na v0.1.

## Como usar este plano

A ordem das etapas segue uma regra: o que pode mudar o desenho vem antes do que depende dele, e a promessa vem por último. Por isso o spike abre o trabalho e o README fecha. Rascunhar o README cedo é bom exercício de API; publicá-lo cedo é prometer o que ainda não existe.

Cada etapa termina num conjunto de testes, não numa data. A duração é estimativa de trabalho em tempo integral e serve para dimensionar o todo, não para cobrar o calendário. A soma dá oito semanas, o teto do timebox, sem folga; a reserva está declarada na etapa 3, que é a mais arriscada: se ela atrasar, o cenário de carga longa da etapa 5 passa para a v0.2 e o exemplo da etapa 6 encolhe para um serviço. Este plano cobre só a v0.1; Kafka, OpenTelemetry e a API de operação têm plano próprio depois.

| Etapa | Duração | Entrega central |
| --- | --- | --- |
| 0. Spike do claim | 3 a 5 dias | Concluído em Oct 4, 2026: certeza sobre o mecanismo de claim, lease e fencing; ADR 0001 escrito |
| 1. Repositório, solution e CI | 2 dias | Concluído em Oct 4, 2026: esqueleto que empacota e roda testes, CI e release validados |
| 2. Núcleo e gravação atômica | 1 semana | Concluído em Oct 4, 2026: G1 provada; fake do outbox |
| 3. Dispatcher e RabbitMQ | 2 semanas | Concluído em Oct 4, 2026 (PRs 3a, 3b, 3c): G2 provada, com o claim por linha provado no spike |
| 4. Inbox | 1 semana | Concluído em Oct 4, 2026: G3 provada, incluindo consumidor que também produz; fake do inbox. A limpeza do inbox foi para a etapa 5, junto da do outbox |
| 5. Limpeza e observabilidade | 3 a 4 dias | Concluído em Oct 5, 2026: retenção, métrica e health check (PR #8); carga longa verde em 5 h, depois de achar que o índice do claim fica inchado após uma transação longa e documentar a reconstrução (ADR 0004) |
| 6. Exemplo executável | 1 semana | Concluído em Oct 5, 2026 (PR #9): exemplo validado por um estranho, ergonomia do registro revista (ADR 0005) |
| 7. Documentação e release | 3 a 4 dias | Concluído em Oct 6, 2026: v0.1.0-alpha publicada no nuget.org (PRs #11 e #12, tag assinada); `GUARANTEES.md` com rastreabilidade testada |

Das quatro decisões que estavam abertas, três foram fechadas em Oct 4, 2026: a licença é Apache-2.0; a matriz é só .NET 10 e EF Core 10, com PostgreSQL 15+; e a ordenação fica na v0.2, porque o spike mediu o custo e ele não passou nos critérios escritos antes da medição. Falta a métrica mínima da v0.1, necessária só na etapa 5.

Duas convenções valem para todas as etapas. Cada teste que prova uma garantia leva o nome dela, no estilo `G1_RetryDuranteCommit_NaoDuplica`, para que a rastreabilidade da etapa 7 não vire renomeação. E cada decisão de desenho vira ADR na etapa em que é tomada, não no fim: a etapa 7 só revisa.

## Sequência e dependências

&#91;embedded content: etapas · 8 semanas, 3 pontos de decisão\]

As etapas correm em série porque cada uma precisa do que a anterior provou. O desenho ainda mostra o ponto de decisão sobre a ordenação, que o spike fechou: a etapa 3b não existe mais. A única folga real está na etapa 5, que pode ser adiantada em paralelo com a 4. O dispatcher sozinho ocupa um quarto do cronograma, e é onde vale concentrar atenção e reserva de tempo.

## Etapa 0 — Spike do claim

Resolve a maior incerteza do projeto antes que ela custe caro: o claim com lease e fencing se comporta como o contrato técnico afirma? Se não, o schema muda, e é melhor descobrir agora do que na etapa 3.

É código descartável. Nada de abstração, pacote ou injeção de dependência: SQL cru, Npgsql, um projeto de teste com Testcontainers. O que sobra da etapa é a certeza, não o código.

**O que escrever**

- Tabela mínima de outbox com `status`, `lease_until`, `owner`, `attempt`, `key` e `sequence`
- O `UPDATE … WHERE id IN (SELECT … FOR UPDATE SKIP LOCKED)` do claim, com lease por partição `hash(key) % P`
- A marcação final com fencing por `owner` e `attempt`
- Um publicador falso, com latência e falha controladas, no lugar do RabbitMQ

**Perguntas que a etapa precisa responder**

| Pergunta | Como responder |
| --- | --- |
| N dispatchers reivindicam lotes disjuntos? | Teste com 8 instâncias concorrentes e verificação de que nenhuma linha foi reivindicada duas vezes ao mesmo tempo |
| Instância morta libera o lote? | `kill -9` no meio do lote; o lote volta a ser reivindicável após o lease |
| A marcação atrasada é barrada? | Forçar o lease a vencer, deixar outra instância reivindicar e só então marcar com o `owner` antigo |
| Selecionar só a cabeça de cada chave funciona com `SKIP LOCKED`? | Consulta com a condição de cabeça sob concorrência; `FOR UPDATE` não aceita `DISTINCT` nem funções de janela, então a forma da consulta é parte da resposta |
| Qual o custo do contador por chave? | Transações concorrentes na mesma chave e em chaves diferentes; medir contenção e confirmar ausência de deadlock com chaves ordenadas |
| Quantas mensagens por ciclo a ordenação permite? | Medir vazão com e sem ordenação, para saber o preço de trazê-la à v0.1 |

**Pronto quando**

- [x] As seis perguntas têm resposta medida, não suposta
- [x] Existe uma decisão escrita sobre ordenação na v0.1 ou na v0.2, com o número que a justifica: v0.2
- [x] ADR escrito: por que `SKIP LOCKED` e não advisory lock; o lease por partição foi adiado para a v0.2, com o que o spike mediu dele
- [x] As descobertas que mudam o contrato técnico foram levadas de volta ao documento de escopo

**Resultado (Oct 4, 2026)**

O spike e uma revisão adversarial dele estão em `spike/` (código arquivado, `RESULTADOS.md` e números brutos, fora da solution e do CI) e em `docs/adr/0001-claim-por-linha-skip-locked-e-fencing.md`, com 32 testes. O que mudou o desenho:

- A ordenação vai para a v0.2: o contador por chave custou de 16% a 70% no p99 da transação (critério: até 20%), e o claim ordenado fez de 35% a 38% da vazão do não ordenado (critério: pelo menos 50%).
- Sem ordenação, o lease por partição não tem consumidor na v0.1: custou 14% de vazão, duas tabelas, heartbeat e um failover pior. A v0.1 faz claim por linha e só grava a coluna `partition`.
- A coluna `attempt` do contrato virou duas: `fence`, o token de fencing, e `attempts`, as tentativas consumidas.
- A condição de reivindicável precisa estar no nível do `FOR UPDATE`. Fora dele, o spike produziu reivindicação dupla.
- A instância morta com o claim aberto devolve as linhas na hora; morta durante a publicação, no lease.

## Etapa 1 — Repositório, solution e CI

O repositório nasce cedo porque o histórico de commits faz parte do portfolio. O que não nasce cedo é a promessa: o README do primeiro dia descreve o estado experimental e o que já existe, sem roadmap de funcionalidades futuras.

**Estrutura da solution**

| Projeto | Papel |
| --- | --- |
| `Waybill` | Núcleo: contratos, envelope, políticas; depende só de `Microsoft.Extensions.*` e `System.Diagnostics` |
| `Waybill.EntityFrameworkCore.PostgreSql` | Captura no `DbContext`, migrations, claim e consultas |
| `Waybill.RabbitMQ` | Transporte |
| `Waybill.Tests.Unit` | O que não precisa de infraestrutura |
| `Waybill.Tests.Integration` | Testcontainers com Postgres e RabbitMQ reais |
| `Waybill.Tests.Chaos` | Concorrência, `kill -9` e Toxiproxy; job separado no CI |
| `samples/` | Vazio por ora; recebe o exemplo na etapa 6 |

**Infraestrutura do repositório**

- Licença Apache-2.0 no primeiro commit, no arquivo `LICENSE`
- MinVer para versionamento, com sufixo `-alpha` até estabilizar
- Source Link e `.snupkg`
- GitHub Actions com .NET 10 e matriz de versões do PostgreSQL (15, o piso, e 18, a mais recente), publicação por tag via Trusted Publishing com OIDC
- Dois jobs de teste desde o início: a suíte por PR (unitários, integração e caos curto) e um job agendado para os cenários longos, de uma hora e de carga longa (5 h), que não cabem em PR
- Analisador de API pública ligado, aceitando churn em `PublicAPI.Unshipped.txt` durante a alpha; o congelamento em `Shipped` fica para a v1.0
- `SECURITY.md` já nesta etapa, porque é a resposta à objeção do autor único
- `CONTRIBUTING`, `CHANGELOG` e a pasta `docs/adr`, que já recebe o ADR 0001 da etapa 0; o spike fica arquivado em `spike/` como evidência dele

**Rascunhos que não são publicados**

Escreva agora, como exercício de design, o trecho de README com a API em dez linhas e o esqueleto do `GUARANTEES.md`. Eles forçam decisões de nomenclatura antes da implementação. Ficam num branch ou numa pasta de rascunho até a etapa 7.

**Pronto quando**

- [ ] `dotnet pack` gera os três pacotes com metadados completos
- [ ] O CI roda os três projetos de teste, ainda que quase vazios, e fica verde
- [ ] Uma tag de teste publica num feed local ou privado, validando o pipeline antes de valer no NuGet

## Etapa 2 — Núcleo e gravação atômica

Entrega G1: o evento existe se, e somente se, a transação fizer commit. É a etapa onde a revisão adversarial encontrou mais armadilhas, e cada uma virou um teste.

**Entregas**

Desenho fechado no ADR 0002; SPEC, PLAN e TASKS em `docs/etapas/etapa-2/`.

- API de enfileiramento explícita, `IOutbox<TContext>`, com `message_id` UUIDv7 fixado no cliente no momento do enfileiramento; tipo registrado, serialização e tamanho validados no `Enqueue`
- A linha de outbox entra no change tracker do `DbContext` no próprio `Enqueue`; a unidade de trabalho do EF é a única fonte de verdade (ADR 0002)
- Evento enfileirado e nunca salvo: log de erro por padrão no fim do escopo de DI, com opção explícita de lançar. Lançar no `Dispose` não pode ser o default, porque o caso mais comum de evento pendente é o handler falhando antes do `SaveChanges`, e a exceção do Waybill substituiria a do bug. O sinal forte fica no fake de testes, que assere “nenhum evento pendente ao final”
- Fake do outbox em memória, no pacote novo `Waybill.Testing`, com asserções do tipo “contém o evento X” e “nenhum evento pendente”, para que o usuário teste o próprio código sem Postgres
- Envelope completo: tipo registrado explicitamente com nome estável, `traceparent`, `tracestate`, `correlation_id`, tenant opcional, content-type
- Serialização com System.Text.Json e source generation, com registro explícito de tipos
- Migrations do próprio pacote, no schema `waybill`, com as tabelas `outbox` e `inbox`, mais os índices parciais, aplicadas por `WaybillSchema.MigrateAsync`. A `outbox` já nasce com `key`, `key_hash`, `sequence` (nula na v0.1), `fence` e `attempts`, para que o upgrade da v0.2 não reescreva linhas pendentes. As tabelas de chaves ficam para a v0.2: um `CREATE TABLE` já é aditivo, e o spike mostrou que a forma delas ainda pode mudar

**Testes que provam**

| Cenário | Resultado esperado |
| --- | --- |
| Rollback da transação | Tabela de outbox vazia |
| Conexão derrubada durante o COMMIT, com reexecução pela estratégia de retry | Exatamente um evento; a segunda tentativa falha por chave duplicada |
| `SaveChanges` falha e é repetido na mesma transação | Exatamente uma linha de outbox por evento |
| `DbContext` descartado com evento pendente | Log de erro, sem exceção; com a opção ligada, exceção. O fake de testes falha o teste do usuário em qualquer caso |
| Handler lança antes do `SaveChanges` | A exceção que chega ao chamador é a do handler, nunca a do Waybill |
| `ExecuteUpdate` e SQL cru | Nenhum evento gerado, comportamento documentado |
| Dois `DbContext` com conexão e transação compartilhadas | Eventos dos dois na mesma transação |

**Armadilhas**

O ponto mais sutil é onde limpar os eventos de domínio. Limpar em `SavingChanges` perde eventos quando o salvamento falha e é repetido; limpar em `SavedChanges` sem deduplicação por referência duplica as linhas. Só a combinação das duas coisas passa no terceiro teste.

**Pronto quando** os sete cenários passam contra Postgres real, a migration aplica num banco limpo e num banco com dados, e o fake do outbox tem seu próprio teste provando que se comporta como o real nos casos de rollback e de evento pendente.

## Etapa 3 — Dispatcher e RabbitMQ

A maior etapa, e a que entrega G2. Aproveita o que o spike provou e acrescenta o transporte real, com a parte mais delicada do contrato: distinguir defeito da mensagem de falha de transporte.

**Entregas**

- Dispatcher como `BackgroundService`, com claim em transação curta e publicação fora dela
- Claim por linha, como o spike provou: condição de reivindicável no nível do `FOR UPDATE` e repetida no `UPDATE` externo, em `READ COMMITTED`; token `fence` separado de `attempts`; `owner` único por encarnação de processo. A coluna `key_hash` é gravada e não usada. O lease por partição (`key_hash % P`) entra na v0.2 como filtro sobre este mesmo claim, sem reescrever o coração do dispatcher, como o spike mostrou
- Lease maior que o timeout de confirmação mais uma margem; a instância cancela a própria espera ao atingir a margem e trata a publicação como de resultado desconhecido
- Transporte RabbitMQ com publisher confirms, mensagem persistente, filas quorum e `mandatory`
- Classificação de falhas e circuit breaker, incluindo a republicação um a um em canal novo quando um canal fecha no meio de um lote
- DLQ do outbox como status na própria tabela, com motivo registrado
- Graceful shutdown: para de reivindicar, espera as confirmações em voo até a margem e libera os leases
- ADR da classificação de falhas: por que cada erro cai onde cai (ADR 0003)

**A tabela de classificação**

| Falha | Trata como | Efeito |
| --- | --- | --- |
| Tamanho recusado na publicação (limite do broker menor que o configurado, ou linha gravada sob limite maior que o atual) | Defeito da mensagem | DLQ com motivo. Tipo não registrado e serialização não chegam aqui: são recusados no `Enqueue` (ADR 0002) |
| Erro de conexão, fechamento de canal | Transporte | Reabre o claim sem gastar tentativa; abre o circuit breaker |
| Timeout de confirmação | Transporte | Reabre o claim e reduz o lote; não abre o breaker |
| `basic.return` por rota inexistente | Caso próprio | Orçamento próprio de tentativas; não abre o breaker |

**Testes que provam**

| Cenário | Resultado esperado | Onde roda |
| --- | --- | --- |
| Broker parado por uma hora | Zero mensagens na DLQ; a fila volta a drenar sozinha | Job agendado |
| Broker com latência alta, mas no ar | Breaker fechado, lote reduzido, sem oscilação | PR, com Toxiproxy |
| Uma mensagem gravada sob um limite maior que o atual, num lote de cem | Só ela vai para a DLQ, sem tocar o broker; as outras noventa e nove publicam | PR |
| Limite local configurado acima do `max_message_size` do broker; uma mensagem entre os dois | O canal fecha com o lote em voo; o lote é republicado um a um em canal novo; só ela vai para a DLQ | PR |
| Toxiproxy derruba a conexão no meio da publicação | Nada se perde; no máximo duplica | PR |
| `kill -9` do dispatcher com lote reivindicado | Morto durante a publicação: o lote volta após o lease, nunca antes; marcação atrasada é barrada pelo fencing. Morto com o claim aberto: as linhas voltam assim que o backend aborta | PR |
| A mesma instância reivindica de novo uma linha com lease vencido | A marcação e a devolução do ciclo antigo são barradas só pelo `fence` | PR |
| Oito dispatchers concorrentes por dez minutos, com lease curto | Todas as instâncias com trabalho; nenhuma linha com dois leases válidos ao mesmo tempo, verificado por um registro de auditoria do teste com o intervalo [claim, fim do lease ou devolução) de cada `(id, fence)`, sem sobreposição; falha de transporte não consome tentativa | Job agendado |

**Armadilhas**

Quando o canal fecha por causa de uma mensagem grande, o cliente não diz qual mensagem causou, e todas as publicações pendentes daquele canal falham juntas. Por isso o lote precisa ser republicado um a um em canal novo para isolar a culpada; sem isso, uma mensagem grande leva o lote inteiro para a DLQ e viola G2.

Ao travar uma linha que outra transação alterou e commitou, o PostgreSQL só reavalia os predicados da tabela travada. Qualquer condição de claim posta numa subconsulta ou num `JOIN` deixa passar uma linha já reivindicada; o spike achou esse bug duas vezes. A forma `UPDATE … WHERE id IN (SELECT … LIMIT n FOR UPDATE SKIP LOCKED)` também depende de o planner não reexecutar a subconsulta: vale um teste que force variações de plano, ou a forma `WITH … AS MATERIALIZED`.

**Pronto quando** os oito cenários passam, os de PR rodam a cada pull request e os dois longos têm histórico verde no job agendado. Esta é a etapa com reserva: se estourar, o cenário de carga longa da etapa 5 passa para a v0.2.

## Ordenação — primeiro item do plano da v0.2

Esta era a etapa 3b, condicional. O spike da etapa 0 disse que o custo não compensa na v0.1, então ela não entra no cronograma de oito semanas e vira o primeiro item do plano da v0.2. Fica aqui como insumo, com o que o spike deixou em aberto.

**Entregas**

- Lease por partição `hash(key) % P` sobre o claim por linha da etapa 3, com tabelas de posse de partição e de instâncias vivas, P e lease globais no banco e validados no startup
- Filtro de cabeça por chave dentro da partição; até M mensagens consecutivas da mesma chave em série no mesmo canal
- Tabelas `outbox_keys` e `inbox_keys`, por migration aditiva
- Contador `outbox_keys` incrementado durante o `SaveChanges`, com as chaves em ordem; medir dentro do EF real onde ele roda e quanto custa
- `sequence` no envelope
- Mensagem na DLQ bloqueia a própria chave; métrica de chaves bloqueadas
- Verificação de sequência no inbox, opcional, com `inbox_keys` e envio de regressão à DLX
- ADR: por que o inbox detecta regressão e não reordena

**Testes que provam**

| Cenário | Resultado esperado |
| --- | --- |
| Teste de propriedade com N dispatchers e leases forçados a vencer | No RabbitMQ, toda regressão chega à DLX do consumidor; nenhuma é aplicada; nenhuma é descartada em silêncio |
| Mensagem de uma chave vai para a DLQ | Só aquela chave para; as demais continuam |
| Consumidor que assina só parte dos tipos de uma chave | Não fica preso em gap |
| Transações concorrentes tocando K1 e K2 em ordens opostas | Sem deadlock |
| Troca de dono de partição com linhas em voo e a partição vencida antes das linhas, com M ≥ 2 | A chave fica bloqueada até o lease das linhas vencer |
| Marcação ou devolução concorrente com o claim ordenado, M ≥ 2 | Nenhuma inversão: o claim não leva a mensagem seguinte quando a cabeça está travada (risco aberto pelo spike) |
| Backlog de 500 mil a 1 milhão de pendentes | Latência do claim ordenado medida; a consulta de cabeça varre o backlog da partição |

**Pronto quando** os sete cenários passam e G4 do RabbitMQ entra no `GUARANTEES.md` com a redação assimétrica do escopo: ordem na publicação, salvo reentrega, com detecção no consumidor.

## Etapa 4 — Inbox

Entrega G3. É a parte do pacote que o consumidor envolve em volta do próprio handler, sem trocar o cliente de broker que ele já usa.

**Entregas**

- API que abre a transação, grava no inbox com `INSERT … ON CONFLICT DO NOTHING` e entrega ao handler a mesma conexão e transação. O handler não traz um `DbContext` próprio do container de DI: ou recebe o contexto do pacote, ou o pacote verifica em runtime que o contexto usado está na mesma transação, e falha alto se não estiver
- Chave única `(handler, message_id)`, com nome de handler obrigatório e único
- Nível de isolamento `READ COMMITTED` exigido e verificado
- Consumidor que também produz: o handler pode enfileirar eventos no outbox dentro da mesma transação do inbox, que é o caso comum em coreografia
- Fake do inbox em memória, para testar handlers sem Postgres nem broker, com asserção de “mensagem X foi aplicada uma vez”
- Adaptadores de exemplo para RabbitMQ.Client, para mostrar que o inbox não exige um loop de consumo próprio
- Limpeza do inbox por retenção: movida para a etapa 5, onde é feita junto da retenção do outbox

**Testes que provam**

| Cenário | Resultado esperado |
| --- | --- |
| Mesma mensagem entregue duas vezes em sequência | Efeito aplicado uma vez |
| Mesma mensagem entregue duas vezes em paralelo, invocando a API do inbox diretamente em duas tarefas (pelo broker, uma fila não entrega a mesma mensagem duas vezes ao mesmo tempo) | Efeito aplicado uma vez; a segunda espera a primeira e sai pelo caminho de duplicata |
| Dois handlers diferentes para o mesmo evento | Os dois aplicam |
| Falha no handler após o INSERT do inbox | Rollback completo; a mensagem volta e é reprocessada |
| Falha do ack depois do commit | A reentrega cai na duplicata e só faz ack |
| Handler enfileira um evento e depois falha | Rollback remove a linha do inbox e a do outbox |
| Handler usa um `DbContext` fora da transação do inbox | Falha explícita, com mensagem que diz o que fazer |

**Armadilhas**

O `ON CONFLICT` contra uma inserção ainda não comitada não retorna na hora: ele espera a primeira transação terminar. Se o handler for mais lento que o `consumer_timeout` do RabbitMQ, a segunda entrega segura a mensagem sem ack e o canal é fechado. O limite precisa estar documentado, e o teste de paralelo precisa cobrir handler lento.

**Pronto quando** os sete cenários passam, existe um exemplo de consumidor que usa o inbox sem depender de nenhum framework de mensageria, e o fake do inbox tem seu próprio teste de equivalência com o real.

## Etapa 5 — Limpeza e observabilidade

O que mantém o pacote utilizável depois do primeiro mês em produção. São duas entregas pequenas em código e grandes em consequência.

**Entregas**

- Limpeza por retenção com DELETE em lote, apagando só linhas `published` do outbox e linhas expiradas do inbox
- Garantia de que `outbox_keys` e `inbox_keys` nunca são limpas, porque um contador reiniciado faria mensagem nova parecer regressão
- Métrica de idade da mensagem pendente mais antiga, via `System.Diagnostics.Metrics`, sem depender de OpenTelemetry
- Health check do dispatcher, com teste próprio: broker fora do ar reporta degradado, não indisponível, porque o outbox continua aceitando eventos
- Documentação de autovacuum para as tabelas, com `autovacuum_vacuum_scale_factor` baixo. Sem `fillfactor`: ele só ajuda HOT update, e HOT é impossível aqui porque `status` está no predicado do índice parcial

**Testes que provam**

| Cenário | Resultado esperado | Onde roda |
| --- | --- | --- |
| Carga longa (5 h) com uma transação longa aberta | Latência do claim e tamanho estabilizam depois que ela fecha; a métrica acompanhada é a latência, não só o tamanho | Job agendado, com a duração configurável. Runners do GitHub limitam um job a 6 h; 24 h exigiriam runner próprio (ADR 0004) |
| Broker parado por mais tempo que a retenção | Nenhuma mensagem pendente é apagada | PR, com retenção de segundos |
| Broker parado e religado | A métrica de idade cresce e volta a zero; o health check vai a degradado e volta | PR |

**Armadilha**

Cada claim muda o `status`, que é coluna do predicado do índice parcial. Isso impede HOT update e gera duas entradas de índice por mensagem. Com o horizonte do vacuum preso por uma transação longa, a latência do claim cresce mesmo com a tabela aparentemente estável. Por isso o teste de carga longa mede tempo, não só bytes.

**Pronto quando** os três cenários passam, com o de carga longa no job agendado, e existe um documento curto de operação com os defaults e o que monitorar.

## Etapa 6 — Exemplo executável

É aqui que você usa o pacote como um estranho usaria, e descobre o que a API tem de ruim. Toda fricção encontrada nesta etapa é fricção que o primeiro usuário também encontraria, e ainda dá tempo de corrigir antes da v0.1.

**Entregas**

- `docker compose up` sobe Postgres, RabbitMQ e dois serviços; as migrations são aplicadas por um serviço de inicialização do compose, nunca pela API em produção, e o README do exemplo explica a diferença
- Uma API que recebe uma cobrança e grava o evento junto, no mesmo commit
- Um consumidor que usa RabbitMQ.Client direto, com o inbox do Waybill envolvendo o handler, e que enfileira um segundo evento na mesma transação, mostrando o consumidor que também produz
- Testes do próprio exemplo usando os fakes das etapas 2 e 4, sem subir infraestrutura, para provar que o caminho de teste do usuário funciona
- Um cenário de falha reproduzível por script: derrubar o broker, ver a fila acumular, religar e ver drenar
- README próprio do exemplo, com os comandos e o que observar

**O que esta etapa costuma revelar**

| Pergunta | Por que importa |
| --- | --- |
| Quantas linhas de configuração para começar? | Se passar de dez, a API está pesada |
| Os fakes bastam para testar o handler e o caso de uso? | Se faltou asserção, é ajuste nos fakes, não volta de etapa |
| Os nomes fazem sentido para quem não escreveu o pacote? | É a última chance barata de renomear, antes do congelamento da API |
| O que acontece quando o usuário esquece de registrar um tipo? | A mensagem de erro precisa dizer o que fazer |

**Pronto quando** alguém que nunca viu o pacote consegue subir o exemplo e publicar o primeiro evento só com o README.

## Etapa 7 — Documentação e release

A promessa vem agora, quando cada linha dela tem um teste atrás. O rascunho da etapa 1 é revisado contra o que de fato foi implementado, e o que não foi provado sai do texto.

**Entregas**

- `GUARANTEES.md` com as três garantias da v0.1, suas condições, o que o pacote não promete, e o nome do teste que prova cada linha
- README com a API em dez linhas, quando não usar o Waybill, e a comparação honesta com Wolverine e CAP
- Revisão dos ADRs escritos nas etapas 0, 3 e 4, agora contra o código final; nenhum ADR novo nesta etapa
- `CHANGELOG` da v0.1.0-alpha
- Tag assinada e publicação no NuGet; pedido de reserva do prefixo `Waybill.*`, que pode ser recusado por ser palavra comum; se for, seguir sem a reserva

**Regra de corte**

Se uma frase do README descreve comportamento sem teste correspondente, ela sai ou vira item de roadmap marcado como não implementado. Essa é a aplicação prática da regra do projeto, e é o que separa o Waybill dos pacotes que anunciam mais do que entregam.

**Pronto quando**

- [x] O pacote instala num projeto novo e publica o primeiro evento seguindo só o README
- [x] Cada linha do `GUARANTEES.md` aponta para um teste nomeado no repositório
- [x] A v0.1.0-alpha está no NuGet com Source Link e símbolos funcionando

## Rastreabilidade

Cada garantia tem uma etapa que a implementa e um teste que a prova. Esta tabela é a que vai virar o cabeçalho do `GUARANTEES.md` e a que responde, numa entrevista, como você sabe que o pacote faz o que diz.

| Garantia | Etapa | Teste que a prova |
| --- | --- | --- |
| G1. O evento existe se, e somente se, a transação fizer commit | 2 | Rollback deixa a tabela vazia; retry do commit não duplica; `SaveChanges` repetido gera uma linha; handler que lança antes do `SaveChanges` entrega a própria exceção, não a do Waybill |
| G2. Todo evento persistido é publicado ou vai para a DLQ por um defeito da lista fechada | 3 | Broker parado por 1 h sem nada na DLQ; mensagem grande isolada num lote de cem, tanto na validação local quanto no fechamento de canal; Toxiproxy no meio da publicação |
| G3. O efeito no banco do consumidor é aplicado uma vez | 4 | Entrega dupla em paralelo aplica uma vez; dois handlers do mesmo evento aplicam os dois; handler que enfileira e falha não deixa nem inbox nem outbox |
| G4. Ordem por chave de agregado | v0.2 | Teste de propriedade com leases forçados a vencer; no RabbitMQ, toda regressão chega à DLX; consumidor com assinatura parcial não fica preso em gap |
| Operação sustentável (não é garantia, é condição de uso) | 5 | Carga longa (5 h) com transação longa aberta; latência do claim estabiliza |

**O que não entra na v0.1 e por quê**

G4 fica para a v0.2: o spike da etapa 0 mediu o custo da ordenação e ele não passou nos critérios escritos antes da medição. O lease por partição vai junto, porque sem ordenação ele não tem consumidor. OpenTelemetry completo fica para a v0.2 porque a métrica de atraso já resolve o caso operacional urgente. A API de operação da DLQ fica para a v1.0 porque, até haver adotante externo, um SQL documentado resolve.
