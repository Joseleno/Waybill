# Waybill — Plano da v0.2

Oct 6, 2026 · @Joseleno

Deriva do escopo (tabela "Funcionalidades por versão" e Contrato técnico), do ADR 0001 e da seção "Ordenação" do `docs/plano.md`, que este plano substitui como fonte das entregas e dos cenários da ordenação.

## Como usar este plano

Vale a regra da v0.1: o que pode mudar o desenho vem antes do que depende dele, e a promessa vem por último. A ordenação abre a versão porque o Kafka e a verificação de sequência no inbox dependem dela; a G4 só entra no `GUARANTEES.md` na última etapa, com o teste que a prova.

A numeração das etapas continua a da v0.1, para que `docs/etapas/etapa-N/` não colida. Cada etapa segue o fluxo de sempre: SPEC, PLAN com os cenários como aceite, TASKS, um commit por cenário provado.

O timebox é de oito semanas, como o da v0.1. A duração de cada etapa é estimativa de trabalho em tempo integral, feita por analogia com a v0.1 (o dispatcher e o RabbitMQ levaram duas semanas). A soma dá de sete a oito semanas, sem folga real.

| Etapa | Duração | Entrega central |
| --- | --- | --- |
| 8. Ordenação por chave | 3 semanas, em três PRs | Espaçamento do `basic.return`, lease por partição, cabeça por chave, `sequence` no envelope; G4 na publicação |
| 9. Transporte Kafka | 2 semanas | `Waybill.Kafka` com producer transacional por partição; G2 e G4 estrita no Kafka |
| 10. Verificação de sequência no inbox | 4 a 5 dias | `inbox_keys`, regressão detectada e devolvida à aplicação para a DLX; G4 do RabbitMQ completa |
| 11. OpenTelemetry | 3 a 5 dias | Trace da requisição HTTP até o handler no exemplo; métricas de atraso, chaves bloqueadas e DLQ |
| 12. Documentação e release | 3 a 4 dias | `GUARANTEES.md` com a G4, `OPERATIONS.md`, `0.2.0-alpha` no nuget.org |

**Linha de corte.** O núcleo da versão são as etapas 8, 9 e 10: sem elas a G4 não se sustenta em nenhum dos dois transportes. A etapa 11 é cortável. Se a etapa 8 ou a 9 estourar, a 11 sai da v0.2, e a tabela "Funcionalidades por versão" do escopo é atualizada no mesmo PR que registrar o corte, porque hoje ela promete OpenTelemetry na v0.2. Um segundo corte, se preciso, é o cenário de backlog de um milhão de linhas, que passa do PR para o job agendado.

**Uma migration só no release.** As etapas 8, 9 e 10 mexem no schema. Enquanto a v0.2 não tiver tag, elas acrescentam à mesma migration da versão, em vez de empilhar uma por etapa. O upgrade de um banco da `0.1.0-alpha` com backlog pendente é cenário de aceite da etapa 8 e é repetido na 12.

## Decisões a tomar no início

Estas perguntas não têm resposta ainda. Cada uma é respondida no SPEC da etapa indicada e vira ADR na mesma etapa.

| Pergunta | Etapa | Ponto de partida |
| --- | --- | --- |
| A ordenação é opcional? | 8 | Candidato: opcional, desligada por padrão. O spike mediu de 16% a 70% a mais no p99 da transação com o contador e de 35% a 38% da vazão no claim ordenado. Com ela desligada, nem o contador nem o filtro de cabeça rodam, e o caminho da v0.1 não muda |
| Qual o critério de custo? | 8 | Escrito antes da medição, como no spike. Candidato: com a ordenação desligada, nenhuma regressão acima de 5% no p99 da transação e na vazão do claim em relação à `0.1.0-alpha`; com ela ligada, o custo é medido e publicado no `OPERATIONS.md`, sem critério de aprovação |
| Como o espaçamento convive com a cabeça por chave? | 8 | Uma linha em espera é cabeça e bloqueia a chave, e na DLQ continua bloqueando até liberação manual. O espaçamento precisa de teto total configurável, para que o operador tenha tempo de corrigir o binding antes de a chave travar |
| Como o claim verifica a posse da partição? | 8 | A condição de posse fica no nível do `FOR UPDATE` e é repetida no `UPDATE` externo (ADR 0001, item 2). Uma condição em `JOIN` ou subconsulta com a tabela de posse cai na armadilha que o spike já pagou duas vezes; o `epoch` da partição precisa entrar no fencing ou ter justificativa escrita para não entrar |
| Como liberar uma chave bloqueada antes da v1.0? | 8 | SQL documentado no `OPERATIONS.md`, como o reenvio da DLQ, com um teste que executa exatamente esse SQL. A API de liberação continua na v1.0 |
| Como o `ITransport` recebe a partição? | 9 | O producer transacional precisa saber a partição do lease e o `epoch`, e o transporte precisa de um resultado para "fenceado pelo broker". A mudança é na API pública do núcleo; o RabbitMQ ignora os campos novos |
| O que exatamente a G4 promete no Kafka? | 9 | A primeira ocorrência de cada mensagem no log segue a ordem de commit, inclusive após a expiração de um lease. Republicação depois de falha entre o commit da transação Kafka e a marcação no banco gera duplicata tardia (N, N+1, N, N+1), que o inbox absorve pelo `message_id`. A redação do escopo ("ordem estrita por chave") é ajustada se o teste mostrar isso |

## Etapa 8 — Ordenação por chave

A etapa de maior risco. Fecha os riscos que o spike deixou abertos e entrega a G4 na publicação. É dividida em três PRs, como a etapa 3: o espaçamento do `basic.return` primeiro, porque é pequeno e muda a condição de reivindicável sobre a qual o resto se apoia.

**8a. Espaçamento do `basic.return`**

- Coluna `next_attempt_at`, nula, na `outbox`. A condição de reivindicável ganha `next_attempt_at IS NULL OR next_attempt_at <= clock_timestamp()`, no nível do `FOR UPDATE` e repetida no `UPDATE` externo
- `basic.return` grava o próximo horário com espaçamento crescente e teto, a partir do relógio do banco. Defeito da mensagem continua indo direto à DLQ, e falha de transporte continua sem gastar tentativa nem agendar espera
- Opções novas em `WaybillDispatcherOptions`, com defaults no `OPERATIONS.md`
- ADR: espaçamento do `basic.return`, a escolha do teto e a interação com a cabeça por chave

**8b. Lease por partição**

- Partição `key_hash % P` sobre o claim por linha da v0.1, como camada por cima dele (ADR 0001, item 5)
- Tabelas de posse de partição e de instâncias vivas, com heartbeat, `epoch` por troca de dono e coleta das instâncias mortas
- P e lease globais, gravados no banco e validados no startup: instância com valor diferente para com erro crítico, como a verificação de `READ COMMITTED`
- Fatia justa entre as instâncias vivas

**8c. Cabeça por chave** (revisto na abertura da 8c, em Oct 7, 2026, com o autor, depois de duas revisões do desenho: o detalhe está no `PLAN.md` da etapa e no ADR 0008)

Dividida em dois PRs, porque a G4 não depende de M:

- **8c-1, G4 com M = 1.**
  - Numeração por um trigger enquanto `settings` existe, com `outbox_keys`; as chaves de um `SaveChanges` viajam na linha e são travadas em ordem; `message_id` monotônico no processo.
  - Filtro de cabeça: não existe linha da mesma chave com `sequence` menor em `pending`, `claimed` ou `dlq`; `published` e `released` são terminais.
  - `sequence` no envelope.
  - Mensagem na DLQ bloqueia a chave; métrica de chaves bloqueadas; liberação (`released`) e consultas de diagnóstico no `OPERATIONS.md`.
  - `outbox_keys` nunca é limpa; mudar P sem apagar `settings`.
  - ADR 0008, parte 1.
- **Entre 8c-1 e 8c-2: ponteiro de cabeça.** Medido na 8c-1: o claim lê e pula as linhas de uma chave com fila longa à frente, de forma linear (1,19 s com 100 mil linhas).
- **8c-2, M ≥ 2 em rodadas.**
  - A k-ésima mensagem de cada chave sai na rodada k, como política do transporte, a ser desenhada com a mudança do `ITransport` da etapa 9.
  - ADR 0008, parte 2, e a exceção de pressão no ADR 0003.

**Testes que provam**

| Cenário | Resultado esperado | PR |
| --- | --- | --- |
| `basic.return` repetido até `MaxReturns` | O intervalo entre as tentativas cresce até o teto; a mensagem vai à DLQ só depois do tempo total configurado | 8a |
| Falha de transporte durante a espera de um `basic.return` | Não gasta tentativa e não reinicia nem encurta a espera | 8a |
| Dez mil linhas em espera à frente do backlog, num tipo sem binding | Latência do claim medida; as demais mensagens continuam fluindo | 8a |
| Instância sobe com P ou lease diferente do gravado no banco | Erro crítico que diz o que mudar; o dispatcher não reivindica nada | 8b |
| Instâncias entram e saem durante a carga | Nenhuma partição com dois donos válidos ao mesmo tempo; as partições da instância que saiu são redistribuídas; instâncias mortas saem da tabela | 8b |
| Transações concorrentes tocando K1 e K2 em ordens opostas | Sem deadlock | 8c |
| Mensagem de uma chave vai para a DLQ | Só aquela chave para; as demais continuam; a métrica de chaves bloqueadas sobe | 8c |
| O SQL de liberação do `OPERATIONS.md` é executado | A chave volta a fluir; a liberação fica registrada | 8c |
| Troca de dono de partição com linhas em voo e a partição vencida antes das linhas, com M ≥ 2 | A chave fica bloqueada até o lease das linhas vencer | 8c |
| Marcação ou devolução concorrente com o claim ordenado, M ≥ 2 | Nenhuma inversão: o claim não leva a mensagem seguinte quando a cabeça está travada (risco aberto pelo spike) | 8c |
| Teste de propriedade com N dispatchers e leases forçados a vencer | Sem instância zumbi, nenhuma inversão na publicação. Com zumbi, toda inversão vem de republicação após lease vencido, e o teste registra cada uma | 8c |
| Ordenação desligada | Caminho da v0.1 intacto: nenhum contador, nenhum filtro de cabeça; custo dentro do critério escrito | 8c |
| Backlog de 500 mil a 1 milhão de pendentes | Latência do claim ordenado medida; a consulta de cabeça varre o backlog da partição | 8c |
| Banco da `0.1.0-alpha` com backlog pendente recebe a migration | Migration aditiva; o backlog drena sem reescrita de linhas | 8c |

**Armadilhas**

O `SKIP LOCKED` pula uma linha-cabeça travada por uma marcação ou devolução concorrente e, com M ≥ 2, leva a seguinte. A ordem quebra sem nenhuma publicação zumbi, e o spike achou isso só na revisão adversarial. Por isso o cenário tem nome próprio e entra antes do teste de propriedade.

O índice parcial do claim não pode filtrar por `next_attempt_at`, porque o relógio não é imutável. Linhas em espera ficam no índice e são lidas a cada ciclo. Com um tipo quente sem binding, isso vira milhares de linhas lidas e descartadas por claim; o cenário de dez mil linhas mede o preço antes de decidir se precisa de índice próprio.

**Pronto quando** os catorze cenários passam, o teste de propriedade e o de backlog têm histórico verde no job agendado, e os três ADRs (0006, 0007 e 0008) estão escritos. Os cenários com M ≥ 2 ficam na 8c-2; o de marcação ou devolução concorrente também roda com M = 1 na 8c-1 (`G4_MarcacaoOuDevolucaoConcorrente_ClaimNaoLevaASeguinte`), assim como o de troca de dono com linhas em voo e o teste de propriedade, cujo oráculo passou a ser a primeira entrega em ordem (ADR 0008).

## Etapa 9 — Transporte Kafka

Entrega o segundo transporte e a única forma de ordem estrita do escopo: o broker fenceia o producer zumbi, coisa que o RabbitMQ não faz.

**Entregas**

- Pacote `Waybill.Kafka`, com Confluent.Kafka, auditado antes de entrar (licença, manutenção, dependência nativa do librdkafka). A regra do núcleo continua: `src/Waybill` não depende dele
- Um producer transacional por partição do lease, com `transactional.id` derivado da partição. A instância que assume a partição chama `InitTransactions` e fenceia o dono anterior
- Lote publicado numa transação Kafka por partição; a marcação no banco vem depois do commit da transação, com o fencing de sempre
- Lease maior que `transaction.timeout.ms` mais uma margem, validado no startup, como a regra do timeout de confirmação
- Mudança no `ITransport` para levar a partição e o `epoch`, e um resultado para "fenceado"
- Classificação das falhas do Kafka, no formato da tabela do ADR 0003: o que é defeito, o que é transporte, o que faz o papel do `basic.return`
- Exemplo de consumidor com Confluent.Kafka e inbox, em `isolation.level=read_committed`
- Kafka nos testes de integração e de caos, com Testcontainers
- ADR: transporte Kafka, fencing pelo broker e classificação de falhas

**Testes que provam**

| Cenário | Resultado esperado |
| --- | --- |
| Instância com lease de partição vencido tenta publicar | Fenceada pelo broker; nada dela chega a um consumidor `read_committed` |
| Teste de propriedade com N dispatchers, leases forçados a vencer e zumbis | Primeira ocorrência de cada mensagem em ordem por chave; duplicatas tardias absorvidas pelo inbox |
| Falha entre o commit da transação Kafka e a marcação no banco | A mensagem é republicada; o consumidor com inbox aplica uma vez |
| Broker parado por uma hora | Zero mensagens na DLQ; a fila volta a drenar sozinha |
| Mensagem acima do `message.max.bytes` do broker num lote | Só ela vai para a DLQ; as demais publicam |
| Toxiproxy derruba a conexão no meio da transação | Nada se perde; a transação é abortada e o lote volta |
| Consumidor com inbox sob rebalance de partições | Efeito aplicado uma vez |

**Armadilha**

Cada partição do lease é um producer, com suas próprias conexões ao cluster. P grande multiplica conexões por instância. O default de P e o seu custo vão para o `OPERATIONS.md`, medidos nesta etapa.

**Pronto quando** os sete cenários passam, os longos têm histórico verde no job agendado, e a G2 do `GUARANTEES.md` tem a linha do Kafka com os testes dela.

## Etapa 10 — Verificação de sequência no inbox

Fecha a G4 do RabbitMQ. Sem fencing de produtor, a ordem lá vale na publicação: a primeira cópia de cada mensagem chega na ordem de commit (ADR 0008). Cópias repetidas podem chegar depois, e uma mensagem liberada da DLQ ainda pode chegar de uma tentativa anterior. O consumidor com a verificação ligada detecta a regressão e não a aplica. **Premissa revista na 8c-1:** com a primeira entrega em ordem, uma regressão com `message_id` inédito só aparece depois da retenção do inbox, ou numa mensagem liberada. O escopo desta etapa deve ser reavaliado quando ela abrir.

**Entregas**

- Tabela `inbox_keys`, com a última `sequence` aplicada por handler e chave, gravada na mesma transação do handler; nunca limpa pela retenção
- Verificação opcional, por handler. A ordem dos testes é parte do contrato: primeiro a deduplicação por `(handler, message_id)`, depois a sequência. Ao contrário, uma reentrega legítima de mensagem já aplicada seria tratada como regressão
- Resultado novo do inbox para regressão. Quem manda à DLX é a aplicação, como no ack: o Waybill não tem laço de consumo. O exemplo com RabbitMQ.Client faz `basic.nack` sem requeue para a DLX da fila
- Sem reordenação: gap não é erro, porque um consumidor que assina só parte dos tipos vê gaps legítimos
- ADR: por que o inbox detecta regressão e não reordena

**Testes que provam**

| Cenário | Resultado esperado |
| --- | --- |
| Teste de propriedade no RabbitMQ com N dispatchers e leases forçados a vencer | Toda regressão chega à DLX do consumidor; nenhuma é aplicada; nenhuma é descartada em silêncio |
| Consumidor que assina só parte dos tipos de uma chave | Não fica preso em gap |
| Reentrega de uma mensagem já aplicada, depois da seguinte | Sai como duplicata, não como regressão |
| Regressão de mensagem cujo registro no inbox já foi limpo pela retenção | Vai à DLX como regressão; não é aplicada |
| Verificação desligada | Comportamento da v0.1, sem leitura de `inbox_keys` |

**Pronto quando** os cinco cenários passam e o exemplo mostra o consumidor mandando a regressão à DLX.

## Etapa 11 — OpenTelemetry

Cortável. A métrica de idade da v0.1 já resolve o caso operacional urgente; esta etapa dá rastro de ponta a ponta.

**Entregas**

- `ActivitySource` e `Meter` no núcleo, só com `System.Diagnostics`: o núcleo continua sem depender do pacote OpenTelemetry
- Span de enfileiramento, de publicação e de processamento no inbox, ligados pelo `traceparent` que o envelope já carrega desde a v0.1
- Métricas de atraso, chaves bloqueadas e mensagens na DLQ
- O exemplo exporta para um coletor no `docker compose` e mostra o trace

**Testes que provam**

| Cenário | Resultado esperado |
| --- | --- |
| Requisição HTTP no exemplo que grava, publica e é consumida | Um único trace da requisição até o handler |
| Mensagem republicada após lease vencido | O span novo continua o mesmo trace |
| Chave bloqueada e mensagem na DLQ | As métricas sobem e voltam a zero depois da liberação |

**Pronto quando** os três cenários passam e o README do exemplo mostra onde ver o trace.

## Etapa 12 — Documentação e release

A G4 entra no `GUARANTEES.md` agora, com a redação assimétrica do escopo e o nome do teste atrás de cada linha.

**Entregas**

- `GUARANTEES.md` com a G4: no Kafka, a redação que o teste da etapa 9 sustentou; no RabbitMQ, ordem na publicação, salvo reentrega, com detecção no consumidor
- `OPERATIONS.md` com a ordenação (como ligar, o custo medido, P e lease), a liberação de chave e o espaçamento do `basic.return`
- README atualizado, com o Kafka e quando ligar a ordenação
- Revisão dos ADRs escritos nas etapas 8 a 11 contra o código final; nenhum ADR novo nesta etapa
- `CHANGELOG` da `0.2.0-alpha`, tag assinada e publicação por Trusted Publishing

**Pronto quando**

- [ ] Cada linha nova do `GUARANTEES.md` aponta para um teste nomeado
- [ ] O upgrade de um banco da `0.1.0-alpha` com backlog pendente passa, agora com a migration final
- [ ] A `0.2.0-alpha` está no nuget.org com Source Link e símbolos funcionando, em todos os pacotes, incluindo o `Waybill.Kafka`

## Rastreabilidade

| Garantia | Etapa | Teste que a prova |
| --- | --- | --- |
| G2. Todo evento persistido é publicado ou vai para a DLQ, também no Kafka | 9 | Broker parado por 1 h sem nada na DLQ; mensagem grande isolada no lote; Toxiproxy no meio da transação |
| G4. Ordem por chave, no Kafka | 8, 9 | Teste de propriedade com zumbis fenceados pelo broker; duplicata tardia absorvida pelo inbox |
| G4. Ordem por chave, no RabbitMQ | 8, 10 | Teste de propriedade com toda regressão na DLX; consumidor com assinatura parcial sem gap; reentrega legítima sai como duplicata |
| Operação sustentável (não é garantia, é condição de uso) | 8 | Linhas em espera à frente do backlog; backlog de um milhão de pendentes; chave bloqueada liberada pelo SQL documentado |

**O que não entra na v0.2 e por quê**

A API para reprocessar ou substituir mensagens da DLQ e liberar uma chave bloqueada continua na v1.0. Na v0.2, a liberação é um SQL documentado e testado, o que basta até haver adotante externo pedindo outra coisa.
