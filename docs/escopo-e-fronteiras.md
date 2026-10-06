# Waybill — Escopo e Fronteiras

Oct 3, 2026 · @Joseleno

Derivado da Waybill — Análise de Negócio. Atualizado em Oct 4, 2026 com o resultado do spike da etapa 0 (`spike/RESULTADOS.md` e `docs/adr/0001-claim-por-linha-skip-locked-e-fencing.md`).

## Escopo

O Waybill garante que um evento gravado junto com uma mudança de dados chega ao broker, e que o efeito gravado no banco do consumidor é aplicado uma vez, mesmo que a mensagem seja entregue mais de uma vez. O que acontece antes do `SaveChanges` e o que o handler faz fora do banco estão fora do escopo.

O documento segue a ordem das perguntas que um time faz ao avaliar o pacote:

1. Onde o pacote termina e onde começa a minha aplicação?
2. Como a mensagem anda?
3. Que dor cada funcionalidade resolve?
4. O que ele promete, e o que não promete?
5. Que contrato técnico ele assume?
6. O que ele não faz?
7. O que ele não resolve, mesmo bem usado?

## Fronteira

&#91;embedded content: fronteira · aplicação, pacote e infraestrutura\]

A aplicação continua dona do que é negócio e do consumo das mensagens; o pacote assume só o caminho confiável entre o commit e o broker, mais a deduplicação no consumidor. Alertas e dashboards ficam com o time, montados sobre as métricas que o pacote expõe.

## Como funciona

&#91;embedded content: fluxo da mensagem · do commit ao handler\]

A mensagem nasce no mesmo commit do dado. O dispatcher reserva um lote numa transação curta e publica fora dela. Se o broker estiver fora do ar, a mensagem espera sem gastar tentativa; só um defeito da própria mensagem a leva à DLQ do outbox. No consumidor, a transação começa gravando o `message_id` no inbox: se ele já existe, é duplicata e a aplicação só faz o ack. O ack vem sempre depois do commit.

## Dores e funcionalidades

&#91;embedded content: mapa de dores · 5 etapas do caminho da mensagem\]

Cada etapa tem um sintoma típico e uma funcionalidade que o trata. Ordenação por chave de agregado e OpenTelemetry entram na v0.2; o resto está na v0.1, incluindo uma métrica mínima de atraso. A tabela seguinte dá o teste que prova cada uma.

## Funcionalidades por versão

A v0.1 tem oito funcionalidades, e cada uma só entra com o teste que a prova. A ordenação por chave de agregado fica na v0.2: o spike da etapa 0 mediu o custo e ele não passou nos critérios escritos antes da medição. A estratégia candidata é lease por partição com filtro de cabeça por chave, com riscos abertos listados em Ordenação, no Contrato técnico.

| Funcionalidade | Dor que resolve | Versão | Teste que prova |
| --- | --- | --- | --- |
| Gravação do evento na mesma transação do `DbContext`, com `message_id` fixado antes do commit | Dual-write; retry que duplica evento | v0.1 | Rollback deixa a tabela de outbox vazia. Conexão derrubada durante o COMMIT e reexecução: exatamente um evento. `SaveChanges` repetido na mesma transação: exatamente uma linha. `DbContext` descartado com evento pendente: log de erro, e a exceção original do handler preservada |
| Claim por linha num comando só, em autocommit, com `FOR UPDATE SKIP LOCKED`, lease e token de fencing; publicação fora da transação | Mesma linha enviada por várias instâncias; conexão presa durante a publicação | v0.1 | N dispatchers concorrentes, todos com trabalho; com lease curto, nenhuma linha com dois leases válidos ao mesmo tempo; instância morta durante a publicação libera o lote quando o lease expira, e com o claim aberto libera na hora; marcação atrasada não sobrescreve outra instância nem um ciclo novo da mesma instância; instância cancela a espera na margem do lease |
| Polling por status com índice parcial | Mensagem pulada por cursor; polling caro | v0.1 | Commits fora da ordem de id; nenhuma mensagem fica para trás |
| Publisher confirms, mensagem persistente e `mandatory` no RabbitMQ | Perda silenciosa no broker | v0.1 | Toxiproxy derruba o RabbitMQ no meio da publicação; nada se perde |
| Classificação de falhas: lista fechada de defeitos para a DLQ; circuit breaker só para conexão e canal; timeout de confirmação reduz o lote | Broker fora do ar mandando tudo para a DLQ; broker lento fazendo o breaker oscilar; mensagem grande derrubando o canal e levando o lote junto | v0.1 | Broker parado por 1 h: zero mensagens na DLQ. Broker com latência alta: breaker fechado, lote reduzido. Uma mensagem recusada por tamanho num lote de 100: só ela vai para a DLQ |
| Inbox transacional: `INSERT … ON CONFLICT DO NOTHING` com chave `(handler, message_id)` no início da transação do handler | Duplicata no consumidor, inclusive entregas concorrentes; dois handlers do mesmo evento se anulando | v0.1 | Mesma mensagem entregue duas vezes ao mesmo tempo: efeito aplicado uma vez. Dois handlers do mesmo evento: os dois aplicam |
| Limpeza por retenção com DELETE em lote, só de linhas `published` e expiradas | Tabelas crescendo; limpeza apagando mensagem não entregue | v0.1 | Carga longa (5 h) com transação longa aberta: latência do claim e tamanho estabilizam depois que ela fecha; broker parado por mais que a retenção: nada apagado |
| Métrica de idade da mensagem pendente mais antiga (`System.Diagnostics.Metrics`) | Atraso invisível | v0.1 | Broker parado: a métrica cresce e volta a zero depois |
| Ordenação por chave com lease por partição; `sequence` no envelope; mensagem na DLQ bloqueia a própria chave | Ordem quebrada pela publicação paralela | v0.2 | Teste de propriedade com N dispatchers e leases forçados a expirar: no Kafka, ordem estrita por chave; no RabbitMQ, toda regressão chega à DLX do consumidor, nenhuma é aplicada. Mensagem na DLQ bloqueia só a sua chave |
| Verificação de sequência no inbox (`inbox_keys`) | Regressão aplicada em silêncio | v0.2 | Consumidor que assina só parte dos tipos não fica preso em gap; regressão vai à DLX com motivo |
| OpenTelemetry: trace propagado, atraso, chaves bloqueadas, DLQ | Investigação sem rastro | v0.2 | Um único trace da requisição HTTP até o handler no exemplo |
| Transporte Kafka com producer transacional por partição | Demanda do projeto de exemplo; fencing do produtor zumbi | v0.2 | Instância com lease vencido tenta publicar: fenceada pelo broker; consumidor com inbox sob rebalance de partições |
| API para reprocessar ou substituir mensagens da DLQ e liberar uma chave bloqueada | Operação manual no banco | v1.0 | Reprocessar mantém o `message_id` e passa pelo inbox; substituir gera novo id; mensagem mais antiga que a retenção do inbox é recusada; liberação fica registrada |

## Garantias

Na v0.1, o Waybill promete três coisas; a quarta, a ordem por chave de agregado, entra na v0.2 e é assimétrica: estrita só no Kafka, com producer transacional. Cada garantia vale sob uma condição explícita, e este é o conteúdo do futuro `GUARANTEES.md`. Duas rodadas de revisão adversarial tentaram quebrar cada linha; os contraexemplos que sobreviveram viraram condições ou mecanismos aqui e no Contrato técnico.

| Garantia | Vale quando |
| --- | --- |
| G1. O evento existe se, e somente se, a transação fizer commit | O evento é enfileirado pela API do pacote antes do `SaveChanges`, na mesma transação do `DbContext`; o `message_id` é fixado no cliente antes do primeiro commit, então a repetição de um commit que já tinha sucedido falha por chave duplicada em vez de gerar um segundo evento; `SaveChanges` repetido na mesma transação materializa cada evento uma só vez; o commit é durável (`synchronous_commit` ≥ `on`) e, com réplica, `synchronous_standby_names` está configurado (`remote_apply` se o consumidor ler da réplica) |
| G2. Todo evento persistido é publicado pelo menos uma vez, ou vai para a DLQ do outbox por um defeito da lista fechada, com o motivo registrado; nunca é descartado em silêncio | O broker confirma o recebimento (publisher confirms, mensagem persistente, fila durável ou quorum). Tipo não registrado, falha de serialização e payload acima do limite configurado são recusados no `Enqueue`, antes de gravar (ADR 0002); à DLQ só vai a mensagem recusada por tamanho na publicação (limite do broker menor que o configurado, ou linha gravada sob um limite maior que o atual). Erro de conexão ou de canal e lease vencido reabrem o claim sem gastar tentativa; timeout de confirmação reabre o claim e reduz o lote, sem abrir o circuit breaker; `basic.return` tem orçamento próprio de tentativas, com espera crescente entre elas (ADR 0006), e não abre o breaker. Linhas `pending`, `claimed` e `dlq` nunca são apagadas pela limpeza |
| G3. O efeito gravado no banco do consumidor é aplicado uma vez, mesmo com entregas repetidas | O handler grava na mesma conexão e transação do inbox, em `READ COMMITTED`; a chave única é `(handler, message_id)`, com nome obrigatório e único por handler; o `message_id` se repete em todo reenvio; a API de reprocessamento da v1.0 recusará mensagens mais antigas que a retenção do inbox do consumidor; até lá, e também depois, replay de offset ou de fila fora dessa janela não é coberto |
| G4. Eventos da mesma chave de agregado são publicados na ordem de commit (v0.2) | **Kafka:** lease por partição `hash(key) % P`, producer transacional com `transactional.id` por partição e consumidores em `isolation.level=read_committed`: ordem estrita por chave, inclusive após expiração de lease, porque o producer zumbi é fenceado pelo broker. **RabbitMQ:** o broker não oferece fencing de produtor; a ordem vale na publicação, salvo republicação após expiração de lease. O envelope leva `sequence` por chave e o consumidor com verificação ligada detecta regressão e envia a mensagem à DLX, nunca a aplica nem a descarta em silêncio. Se uma mensagem da chave vai para a DLQ do outbox, a chave fica bloqueada até reprocessamento ou liberação manual, sem atrasar as outras chaves. O spike da etapa 0 deixou riscos abertos contra esta linha (ver Ordenação, no Contrato técnico); ela só vai para o `GUARANTEES.md` depois de resolvidos |

**O que o pacote não promete**

| Não promete | Por quê |
| --- | --- |
| Entrega única de ponta a ponta (cada mensagem processada uma só vez) | A publicação é at-least-once; só o efeito gravado junto com o inbox acontece uma vez |
| Ordem estrita de ponta a ponta no RabbitMQ | Sem fencing de produtor, uma publicação atrasada de uma instância com lease vencido pode chegar depois da mensagem seguinte. O consumidor detecta e envia à DLX; não há como impedir no broker |
| Reordenar no consumidor | O consumidor que assina só alguns tipos de evento vê gaps legítimos na sequência da chave; estacionar a mensagem seguinte esperaria para sempre. Por isso o inbox detecta regressão, mas não reordena |
| Ordem no consumo sem configuração do broker | No consumo, a ordem exige single active consumer ou consistent-hash (RabbitMQ), ou uma partição por chave sem mudar o número de partições (Kafka) |
| Ordem global entre agregados | Exigiria serializar commits, publicação e consumo; e mesmo assim a ordem de id não é a ordem de commit |
| Recuperar, após liberar uma chave bloqueada, a mensagem que ficou na DLQ | Para consumidores com verificação de sequência, ela já é uma regressão; liberar a chave é aceitar isso, e a ação fica registrada |
| Efeito externo uma única vez (e-mail, chamada a gateway) | O inbox protege o banco do consumidor, não as chamadas que saem dele |
| Latência fixa | Depende do intervalo de polling e da fila; os números de referência saem com o benchmark da v0.2 |
| Deduplicação eterna | O inbox só lembra mensagens dentro da retenção configurada; a tabela de sequências por chave, ao contrário, nunca é limpa |

## Contrato técnico

As garantias acima só valem dentro deste contrato. Na etapa 7, cada linha da v0.1 foi revisada contra o código final; o que é da v0.2 ou da v1.0 está marcado assim.

| Tema | Decisão |
| --- | --- |
| Matriz de suporte | .NET 10 e EF Core 10, com Npgsql 10; RabbitMQ.Client 7 (API assíncrona); Confluent.Kafka na v0.2. PostgreSQL 15+ (o 13 saiu de suporte em nov/2025 e o 14 sai em nov/2026); o CI testa o 15 e o 18, e o spike rodou só o 18 |
| Transações suportadas | `Database.BeginTransaction` e a transação implícita do `SaveChanges`. Com `EnableRetryOnFailure`, a transação roda dentro de `CreateExecutionStrategy().ExecuteAsync`, e o `message_id` fixado antes do commit faz a reexecução de um commit já aplicado falhar por chave duplicada. `TransactionScope` e `AutoTransactionBehavior.Never` sem transação aberta não são suportados e o `Enqueue` os recusa; vários `DbContext` só com conexão e transação compartilhadas |
| Captura de eventos | Enfileiramento explícito por `IOutbox<TContext>` (ADR 0002). O `Enqueue` fixa o `message_id`, valida o tipo registrado, serializa e valida o tamanho na hora, e o erro aparece para quem enfileira. No mesmo `Enqueue`, a linha de outbox entra no change tracker do `DbContext`: o próximo `SaveChanges` bem-sucedido a insere, um que falha a mantém, `SaveChanges` repetido na mesma transação gera exatamente uma linha por evento, e `ChangeTracker.Clear()` a descarta junto com os dados. Encerrar o escopo de DI com eventos enfileirados e não salvos gera log de erro por padrão, com opção explícita de lançar (um `DbContext` criado fora do DI não é coberto); lançar no Dispose não é o default porque o caso comum de evento pendente é o handler falhando antes do SaveChanges, e a exceção do pacote esconderia a do bug. O fake de testes assere “nenhum evento pendente” |
| Escrita fora do EF Core | `ExecuteUpdate`, `ExecuteDelete`, SQL cru e Dapper não geram eventos |
| Envelope | `message_id` UUIDv7 gerado no cliente ao enfileirar; `sequence` por chave (v0.2); tipo registrado explicitamente com nome estável (`billing.invoice-paid.v1`), independente do nome da classe, nunca por `Type.GetType`; `traceparent` e `tracestate` W3C; `correlation_id`; tenant opcional; content-type |
| Serialização e payload | System.Text.Json com o `JsonTypeInfo` do registro (source generation recomendada, não imposta); limite de tamanho é configuração obrigatória, validado no `Enqueue` e de novo antes de publicar, e deve ficar abaixo do `max_message_size` do broker (o pacote não o consulta; acima dele, a mensagem é isolada e vai à DLQ); payload grande vai por claim-check, feito pela aplicação |
| Tabelas | `outbox` e `inbox` na v0.1; `outbox_keys`, `inbox_keys` e as tabelas de posse de partição na v0.2, por migration aditiva. Tudo no schema `waybill`, entregue por migrations do próprio pacote (histórico em `waybill.__waybill_migrations`, aplicadas por `WaybillSchema.MigrateAsync` num serviço de inicialização); o `DbContext` do usuário só mapeia a outbox, com `ExcludeFromMigrations` (ADR 0002). Upgrade de schema compatível com mensagens pendentes. A `outbox` já nasce com `key`, `key_hash` (murmur2 da chave, ou do `message_id`) e `sequence` (nula na v0.1); na v0.2 a partição é `key_hash % P` na consulta, então mudar P não reescreve linhas pendentes. A limpeza apaga só linhas `published` do outbox e linhas expiradas do inbox; `outbox_keys` e `inbox_keys` nunca são limpas, porque um contador reiniciado faria mensagens novas parecerem regressão, e por isso crescem com o número de chaves que já emitiram evento |
| Claim e publicação | Claim por linha num comando só, em autocommit e `READ COMMITTED` (verificado antes do primeiro ciclo; outro nível para o dispatcher com erro crítico, ADR 0001): `WITH candidates AS MATERIALIZED (SELECT … ORDER BY id LIMIT n FOR UPDATE SKIP LOCKED) UPDATE outbox SET status = 'claimed', owner, fence = fence + 1, lease_until … FROM candidates … RETURNING …`, publicação fora da transação, marcação e devolução com fencing por `owner` e `fence` (no shutdown, a devolução das linhas que ainda são da instância filtra só por `owner`, único por encarnação). Tempo só do relógio do banco, e nunca timestamp para decidir ordem. O lease é maior que o timeout de confirmação mais uma margem; ao atingir a margem, a instância cancela a espera e trata a publicação como de resultado desconhecido. O fencing protege a marcação no banco, não a publicação: uma instância zumbi ainda pode publicar tarde (ver G4). Sem lease por partição na v0.1: ele não teria consumidor antes da ordenação |
| Claim: detalhes (ADR 0001) | A condição de "reivindicável" (`pending`, ou `claimed` com lease vencido, e fora da espera depois de um `basic.return`, ADR 0006) fica no nível que tem o `FOR UPDATE` e é repetida no `UPDATE` externo: ao travar uma linha alterada e commitada por outra transação, o PostgreSQL reavalia só os predicados da tabela travada, e uma condição em subconsulta ou `JOIN` produziu reivindicação dupla no spike. `fence` é o token de fencing, sobe a cada claim e nunca desce; `attempts` conta só tentativas consumidas: defeito da mensagem e `basic.return` (orçamento `MaxReturns`). Uma coluna só não serve às duas coisas: ou o token deixa de ser único, ou a falha de transporte gasta tentativa. `owner` é único por encarnação de processo. O lease fica muito acima do jitter do relógio de parede, que recua (medido no spike: até ~1,7 ms no Docker/WSL2). Sem `lock_timeout`, `statement_timeout` nem `idle_in_transaction_session_timeout` próprios: o claim é um comando em autocommit, e a espera do cliente tem o limite do `CommandTimeout` do Npgsql (ADR 0001) |
| Classificação de falhas (ADR 0003) | Defeito da mensagem (DLQ, com motivo): linha gravada sob limite maior que o atual; mensagem que não se expressa em AMQP; mensagem que, publicada sozinha, faz o broker fechar o canal com 406. Tipo não registrado, serialização, payload acima do limite e nome, `correlation_id`, chave ou `tenant_id` acima de 255 bytes são recusados antes, no `Enqueue`; publicação que falha com a conexão e o canal de pé também é defeito. Transporte (reabre o claim, sem contar tentativa): erro de conexão e fechamento de canal, que abrem o circuit breaker (aberto: nada é reivindicado; meio aberto: sonda com uma mensagem, e a sonda que falha por conexão, timeout ou nack reabre pelo dobro, até 30 s); timeout de confirmação, nack e `Retry` sem causa, que reduzem o lote à metade sem abrir o breaker (três ciclos seguidos de pressão com lote 1 contam como queda silenciosa e abrem); exceção do transporte conta como falha de conexão. `basic.return`: orçamento próprio de tentativas (`MaxReturns`), com espera crescente e teto entre elas (`ReturnBackoff`, `MaxReturnBackoff`; 15 min até a DLQ por padrão, ADR 0006), sem abrir o breaker; recomendamos alternate exchange. Quando o broker fecha o canal com 406 durante um lote, as mensagens não confirmadas são republicadas uma a uma em canal novo para isolar a culpada |
| Ordenação (v0.2) | Opcional e desligada por padrão. Lease por partição `key_hash % P` (ADR 0007), a única estratégia que admite fencing em algum transporte. Dentro da partição, só a cabeça de cada chave é reivindicada, e até M mensagens consecutivas da mesma chave saem em série no mesmo canal. Cabeça = não existe linha da mesma chave com `sequence` menor e status diferente de `published`. Sequência por chave: `UPDATE outbox_keys SET seq = seq + 1 … RETURNING seq`, executado em `SavingChanges`, com as chaves em ordem para evitar deadlock; o lock dura até o commit e serializa só a mesma chave, e rollback não deixa gap. Medido no spike: cabeça por chave sem lease por partição só é segura com M=1; chaves fora de ordem causam deadlocks em série; uma chave quente fica limitada pelo lock do contador. Resolvidos na etapa 8b (ADR 0007): P e o lease da partição são globais, gravados no banco e conferidos no startup e a cada lease; o claim verifica a posse da partição no próprio comando, então uma instância que perdeu a partição enquanto estava pausada não reivindica nada dela; o `epoch` identifica o mandato e não entra no fencing da linha. Abertos, a resolver na etapa 8c antes de prometer G4: `SKIP LOCKED` pula uma linha-cabeça travada por marcação ou devolução concorrente e, com M ≥ 2, leva a seguinte; o custo do contador depende de onde ele roda dentro do `SaveChanges` |
| Inbox (consumidor) | A transação começa com `INSERT INTO inbox … ON CONFLICT DO NOTHING` em `READ COMMITTED`; zero linhas = duplicata, rollback e ack. Entrega concorrente da mesma mensagem espera a primeira transação terminar, então o handler precisa caber no `consumer_timeout` (RabbitMQ) ou `max.poll.interval.ms` (Kafka). Com verificação de sequência ligada (v0.2): `inbox_keys(handler, key, last_seq)` é atualizada na mesma transação; `sequence` menor que `last_seq` com `message_id` inédito é regressão e vai à DLX com motivo. A verificação só pode ser ligada quando o produtor roda com ordenação ativa, senão a publicação paralela da v0.1 viraria regressão |
| Hospedagem | Dispatcher como `BackgroundService`, na própria API ou num worker separado. Graceful shutdown: para de fazer claim, espera o lote em voo até o `PublishTimeout`, dentro do `ShutdownTimeout` do host, e devolve as linhas que ainda são da instância. Se o host cortar a espera antes, uma mensagem do lote pode ser publicada e também devolvida: sai de novo, como duplicata, nunca como perda |
| Configuração | Defaults documentados em `OPERATIONS.md` para intervalo de polling, tamanho do lote, timeout de publicação, lease, `MaxReturns`, espera entre retornos e retenção; `autovacuum_vacuum_scale_factor` baixo na `outbox`; health check do dispatcher |
| Operação da DLQ (v1.0) | Duas ações distintas: reprocessar (mesmos bytes, mesmo `message_id`) e substituir payload, que gera novo `message_id` e fica registrada como correção; ambas recusam mensagens mais antigas que a retenção do inbox dos consumidores. Liberar uma chave bloqueada é ação registrada |
| Kafka (v0.2) | `transactional.id = waybill-{partição}` com producer transacional, que fenceia o zumbi por epoch; `EnableIdempotence` sozinho só evita duplicata no retry interno; partitioner fixo murmur2; consumidores em `isolation.level=read_committed`; commit de offset só depois do commit do inbox |

## O que o pacote não faz

O Waybill publica e deduplica; ele não é um framework de mensageria. Cada item abaixo ficou de fora de propósito, e a coluna da direita diz quem cuida dele. Esta é a lista única de exclusões: a Análise de Negócio aponta para cá.

| Não faz | Quem cuida |
| --- | --- |
| Consumir mensagens: loop de consumo, roteamento para handlers, ack | A aplicação, com o cliente do broker ou um framework; o Waybill só oferece o inbox para envolver o handler |
| Tratar poison message no consumo | A aplicação, com DLX na fila do broker. A DLQ do Waybill cobre só falhas de publicação, do lado do produtor |
| Criar a topologia do broker (exchanges, filas, bindings, tópicos) | A aplicação ou a infraestrutura como código |
| Sagas, orquestração e compensação | Wolverine, MassTransit, NServiceBus ou código próprio |
| Agendamento e mensagens atrasadas | Hangfire, Quartz ou o delayed retry das quorum queues (RabbitMQ 4.3+); o plugin de delayed exchange foi arquivado em 2026 |
| Mediator e CQRS em memória | O código da aplicação ou outra biblioteca |
| Request/response e RPC sobre o broker | Fora do propósito; use HTTP ou gRPC |
| Versionar contratos e evoluir schema de eventos | A aplicação; no Kafka, um Schema Registry |
| Mensagens grandes | Claim-check feito pela aplicação; o pacote documenta o limite de tamanho |
| Suportar SQL Server, MySQL ou Mongo | Fora até a 1.0; cada banco dobra a matriz de testes |
| Multi-tenant com schema por tenant | Fora até a 1.0; o tenant pode ir como header do envelope |
| Painel de administração e alertas | O time, sobre as métricas que o pacote expõe (Grafana, Datadog) |
| Ser compatível com o envelope do MassTransit | No máximo um adapter de exemplo no repositório |

## O que o pacote não resolve

Alguns problemas continuam existindo com o pacote bem configurado, porque estão fora do alcance de qualquer outbox. O README deve dizer isso antes que um usuário descubra em produção.

| Problema | Por que continua | Caminho |
| --- | --- | --- |
| Consistência de negócio entre serviços | O pacote garante que o evento chega, não que o outro serviço aceita a decisão | Saga com compensação explícita |
| Evento sem dado depois de um failover do Postgres | Com réplica assíncrona ou `synchronous_commit=off`, o failover pode perder um commit cujo evento já foi publicado. No consumidor, o efeito pode sumir depois do ack | Replicação síncrona nos bancos onde o negócio exigir |
| Evento com conteúdo errado | Um bug no evento é publicado fielmente | Evento compensatório e testes de contrato |
| Contrato que quebra consumidores | O pacote transporta bytes; não conhece o schema | Versionamento de contrato e Schema Registry |
| Broker fora do ar por horas | A tabela de outbox acumula e a entrega atrasa; nada vai para a DLQ, mas o disco cresce | Alerta sobre a idade da mensagem pendente mais antiga |
| Consumidor lento e backlog | O pacote mede o atraso, não escala o consumidor | Escalar consumidores e particionar |
| Escrita fora do banco do pacote (Redis, outro banco, arquivo) | A atomicidade vale só para a transação do `DbContext` | Trazer a escrita para o mesmo banco ou aceitar o dual-write ali |
