# Waybill — Parecer da Revisão

Oct 3, 2026 · @Joseleno

Revisão de Doc e Doc por quatro frentes: correção técnica, verificação de fatos, produto e coerência, e edição.

## Veredito

O diagnóstico de mercado e das dores está sólido, mas as garantias ainda não fecham. Dos cerca de 40 fatos checados, quase todos se confirmaram. O problema está no contrato técnico: três garantias se contradizem entre si ou falham em cenários reais.

| Severidade | Achados | Onde se concentram |
| --- | --- | --- |
| Crítico | 4 | Garantias: failover, DLQ contra “pelo menos uma vez”, DLQ contra ordenação, corrida no inbox |
| Alto | 9 | Lease e `SKIP LOCKED`, sequência por chave, ordem no consumo, contradições entre v0.1 e v0.2, posicionamento |
| Médio | 12 | Kafka, EF Core, limpeza, RabbitMQ, lacunas de escopo, fatos imprecisos |
| Baixo | 8 | Termos, repetições, rótulos de diagrama |

A regra que os próprios documentos adotaram, “nenhuma garantia sem teste que a prove”, hoje é violada pelo texto. Corrigir as garantias vem antes de qualquer trabalho na API.

## Garantias: o que precisa mudar

Quatro problemas críticos e cinco altos estão no contrato técnico. Cada linha traz o texto pronto para entrar no Escopo e Fronteiras.

| # | Problema | Texto corrigido |
| --- | --- | --- |
| 1 | **Crítico.** Failover com réplica assíncrona não é “consistente, mas perdido”: o dispatcher lê no primário e pode publicar um evento cujo commit some no failover. Resultado: evento sem dado, o fantasma que o pacote promete eliminar | “Com réplica assíncrona ou `synchronous_commit=off`, um failover pode perder um commit cujo evento já foi publicado. Caminho: replicação síncrona onde o negócio exigir.” A garantia 1 passa a exigir commit durável e replicado |
| 2 | **Crítico.** Broker fora do ar esgota as N tentativas e manda tudo para a DLQ, contradizendo “pelo menos uma vez” | “Falhas de transporte (conexão, timeout) não contam tentativa: o dispatcher para por circuit breaker. Só falhas da própria mensagem (serialização, tamanho, não roteável) levam à DLQ.” Garantia 2: “…é publicado pelo menos uma vez ou vai para a DLQ do outbox com o motivo registrado; nunca é descartado em silêncio” |
| 3 | **Crítico.** Ordenação por chave e DLQ “sem atrasar as outras” se contradizem: publicar N+1 com N na DLQ quebra a ordem | “Com ordenação ativa, uma mensagem em retry ou na DLQ bloqueia a própria chave, sem atrasar outras chaves. Política configurável: bloquear (padrão, com alerta) ou seguir abrindo mão da ordem daquela chave” |
| 4 | **Crítico.** “Já no inbox? não → handler” tem corrida: duas entregas concorrentes veem “não” e as duas executam | “A transação começa com `INSERT INTO inbox … ON CONFLICT DO NOTHING`. Zero linhas inseridas = duplicata: rollback e ack. Ack só depois do commit” |
| 5 | **Alto.** “O consumidor o processa uma vez só” contradiz “não promete exactly-once” | “…e que o efeito gravado no banco do consumidor é aplicado uma vez, mesmo que a mensagem seja entregue mais de uma vez” |
| 6 | **Alto.** Garantia 3 incompleta: chave do inbox, estabilidade do id e retenção | “Vale quando o handler grava na mesma transação do inbox; a chave única é `(consumer_name, message_id)`; o `message_id` nasce no INSERT do outbox e se repete em todo reenvio; a retenção do inbox supera a janela máxima de reentrega” |
| 7 | **Alto.** `SKIP LOCKED` + lease está ambíguo: o lock de linha dura só a transação, e publicar com a transação aberta é o anti-padrão que o próprio documento critica | “Claim em transação curta (`UPDATE … SET lease_until, owner … WHERE id IN (SELECT … FOR UPDATE SKIP LOCKED)`), commit, publicação fora da transação, marcação final com fencing por `owner` e `attempt`. Tempo só do relógio do banco” |
| 8 | **Alto.** “Sequência por agregado” não diz como é gerada; `MAX()+1` duplica sob concorrência | “A sequência vem da versão do agregado ou de `UPDATE outbox_keys SET seq = seq + 1 … RETURNING seq`, que serializa só a mesma chave” |
| 9 | **Alto.** A ordem é garantida na publicação, não no consumo | “No consumo, a ordem vale com single active consumer ou consistent-hash (RabbitMQ), ou uma partição por chave sem mudar o número de partições (Kafka)” |

O diagrama “Como funciona” acompanha os itens 4 e 5: o ack é da aplicação nos dois ramos, e o título deixa de dizer “só tem efeito uma vez” sem condição.

## Fatos e fontes

Cerca de 37 afirmações se confirmaram, incluindo todas as licenças e os downloads da tabela de mercado. Dois fatos estão errados e nove imprecisos; os dois primeiros eu conferi diretamente nas fontes.

| Afirmação | Veredito | O que a fonte diz | Correção |
| --- | --- | --- | --- |
| Organização `waybill` no GitHub “não confirmada” | Ocupada | [github.com/waybill](https://github.com/waybill) é um usuário sem repositórios públicos | Escolher outra org (ver decisões) |
| Agendamento via “delayed exchange do RabbitMQ” | Errado | O plugin foi [arquivado em 16/04/2026](https://github.com/rabbitmq/rabbitmq-delayed-message-exchange); o [RabbitMQ 4.3](https://www.rabbitmq.com/blog/2026/04/23/rabbitmq-4.3-release) trouxe delayed retry nativo nas quorum queues | “Hangfire, Quartz, ou delayed retry das quorum queues (RabbitMQ 4.3+)” |
| “Todo pacote dedicado com mais de 25 mil downloads está preso a um ecossistema” | Errado | A própria tabela lista o Freakout (28,6 mil, sem framework); GenericOutbox tem 80 mil e CUSTIS.NetCore.Outbox 152 mil | “A maioria dos pacotes com tração exige um framework; os independentes passam de 25 mil downloads, mas têm pouca adoção visível” |
| Freakout é “o independente mais estabelecido” | Impreciso | GenericOutbox e CUSTIS têm mais downloads | “Um dos independentes mais conhecidos, mantido na org rebus-org” |
| Twinbox “alpha, escopo quase idêntico” | Impreciso | 1.3.0 estável no NuGet; escopo bem maior (6 bancos, 10 brokers, dashboard) | “Escopo mais amplo; 1.3.0 no NuGet, 0 downloads” |
| “Batching levou de 1.350 para 32.500 msg/s” | Impreciso | O ganho soma índice, paralelismo e lote | “Um conjunto de otimizações levou de 1.350 para \~32.500 msg/s” |
| Poison message “bloqueia o agregado inteiro” | Impreciso | A fonte diz que todas as linhas atrás dela esperam | “bloqueia todas as mensagens atrás dela” |
| npiontko sobre “idade da mais antiga” e trace | Impreciso | O artigo fala em idade média e não trata de trace | “Ninguém mede a idade das pendentes nem a vazão de entrada × saída” |
| Stripe “criou” idempotency keys | Impreciso | A Stripe implementa, não reivindica a invenção | “o mesmo problema que a Stripe resolve com idempotency keys” |
| RabbitMQ: “só há garantia com publisher confirms” | Impreciso | A doc cita transações AMQP ou confirms | “exige transações AMQP ou publisher confirms” |
| “Reservar o prefixo `Waybill.*`” | Impreciso | A [reserva](https://learn.microsoft.com/en-us/nuget/nuget-org/id-prefix-reservation) é pedida ao NuGet e pode ser recusada para palavras comuns | “Solicitar a reserva (sujeita a aprovação)” |
| Suporte ao MassTransit v8 “termina no fim de 2026” | Impreciso | As fontes dizem “até pelo menos o fim de 2026” | “garantido até pelo menos o fim de 2026” |
| “938 pacotes com outbox” | Ressalva | São 938 resultados de busca com versões estáveis, não implementações distintas | “938 resultados para ‘outbox’ no NuGet” |

## Coerência e lacunas de escopo

Os documentos vendem como diferencial coisas que só chegam na v0.2. Também deixam sem resposta perguntas que um Tech Lead faria antes de adotar.

**Contradições**

- O resumo da Análise apresenta ordenação por agregado e observabilidade como diferencial, mas as duas são da v0.2. O diferencial da v0.1 deve citar só o que ela entrega.
- “Kafka não é promessa no README” convive com Kafka fixo na v0.2 e num gate. Como o projeto de exemplo já vai usar Kafka, a decisão está tomada: assumir Kafka na v0.2 e tirá-lo do gate de ordenação.
- “Traces e métricas” aparece no diagrama de fronteira sem versão, e o texto sobre broker fora do ar pressupõe alerta. Proposta: uma métrica mínima já na v0.1, a idade da mensagem pendente mais antiga.
- O `SECURITY.md` é mitigação imediata para o “autor único”, mas o roadmap o põe na v1.0. Ele deve ir para a v0.1.
- No fluxo, “Descarta e faz ack” está marcado como do pacote, mas o pacote não consome mensagens. O ack é da aplicação.
- A DLQ do pacote é do lado do produtor (falha de publicação). “Poison message travando a fila” é problema do consumidor, resolvido com DLX do broker pela aplicação. Os documentos misturam as duas.

**Lacunas: nem em “faz” nem em “não faz”**

| Lacuna | Prioridade | O que definir |
| --- | --- | --- |
| Matriz de suporte | Alta | Versões de .NET, EF Core, Npgsql, PostgreSQL e RabbitMQ.Client (a v7 é assíncrona e muda o transporte) |
| Schema e migrations | Alta | Como as tabelas chegam ao banco e como sobem de versão com mensagens pendentes |
| Envelope | Alta | `message_id` (UUIDv7?), tipo, `traceparent`, correlation, tenant, content-type, interoperabilidade com consumidores não .NET |
| Testes do usuário | Alta | Fake em memória e asserções do tipo “o outbox contém o evento X” |
| Hospedagem do dispatcher | Alta | BackgroundService na própria API ou worker separado; graceful shutdown |
| Configuração e health checks | Média | Defaults de polling, lote, lease, tentativas e retenção |
| Escrita fora do EF Core | Média | Dapper ou `NpgsqlTransaction`: suportado ou não; `ExecuteUpdate` e SQL cru não passam pelo interceptor |
| Payload | Média | Limite de tamanho, claim-check, dados pessoais no payload |
| Multi-tenant por coluna | Média | Citado em “não faz”, mas ausente das funcionalidades: vira funcionalidade com teste ou sai |

As perguntas do adotante que os documentos não respondem: como é a API em dez linhas, como testo meu código, quanto custa no meu Postgres, como migro do meu outbox caseiro, e se o inbox funciona com o consumidor que já tenho (RabbitMQ.Client, Confluent ou MassTransit).

## Mercado e posicionamento

Três argumentos de negócio não se sustentam como estão escritos.

1. **Contra o Wolverine, “tamanho, foco e garantias provadas” não convence.** O Wolverine também tem testes e já ordena por chave. A diferença precisa ser medida e publicada: número de dependências transitivas, se exige runtime no host, ausência de geração de código em runtime, tamanho da API pública. Vale confirmar se o `IDbContextOutbox<T>` do Wolverine exige `UseWolverine()`, porque essa é a comparação central.
2. **A janela “fim de 2026” não é realista.** Com 6 a 8 semanas a partir de 03/10, a v0.1-alpha sai entre meados de novembro e o início de dezembro, sem ordenação nem guia de migração. Nenhuma fintech troca o outbox de produção por uma alpha em dezembro, e o MassTransit v8 continua funcionando depois do fim dos patches. Texto sugerido: “Público secundário em 2027: times no MassTransit v8 planejando a saída. A v0.1 serve para avaliação, não para essa migração.”
3. **O público-alvo é incoerente com o escopo.** Times que já usam RabbitMQ costumam usar o MassTransit justamente para consumir, e o pacote não consome. A saída é desenhar o inbox para envolver qualquer consumidor (RabbitMQ.Client, Confluent, um consumer do MassTransit) e mostrar isso no exemplo.

As metas de estrelas (50 a 150 em seis meses) contrastam com os comparáveis, como o SimpleOutbox com 1 estrela. Metas que dependem só do autor medem melhor: releases, ADRs publicados, um artigo, benchmark e tempo de resposta a issues. O CleanStart aparece como “uso real”, mas nenhuma integração com ele está prevista no escopo.

## Editorial

O maior problema de tom são as personas. Se o documento for lido por um recrutador ou avaliador, elas soam como texto gerado e tiram a autoria de você.

- **Personas.** “O time de quatro especialistas convergiu”, “regra do red team” e “o especialista de produto recomenda” devem virar argumento direto. Exemplo: “Uma revisão adversarial das garantias deixou uma regra: nenhuma garantia vai para o README sem um teste de concorrência que a prove.”
- **Autopromoção e exposição.** “É exatamente o que se espera de um Tech Lead” vira “um template não deixa esse rastro”. “Competir com a busca de recolocação” expõe sua situação: “o timebox impede que o projeto concorra com outras prioridades”.
- **Frases de efeito.** “Esse último ponto é argumento de venda, não fraqueza” e “aprende numa tarde” são promessas não verificáveis. Troca: “um time consegue ler o código inteiro e, se precisar, manter um fork”.
- **Repetições.** O “fora do escopo” da Análise duplica o “não faz” do Escopo; o nome é explicado três vezes; “efeito externo” e “ordem entre agregados” aparecem em “não promete” e em “não resolve”. Cada lista deve viver num só lugar, com referência no outro.
- **Estrutura da Análise.** O resumo é uma ficha técnica e não diz o problema. A ordem mais clara é: problema, dores, público, mercado, por que pacote, escopo, riscos, nome e confiança.
- **Diagramas.** “Alertas e dashboards” está na coluna Infraestrutura, mas o texto diz que ficam com o time. “Ordem quebrada” está na etapa Consumidor, mas a causa é o dispatch paralelo. “Handler” aparece no produtor e no consumidor; no produtor, “Caso de uso altera o agregado”.

**Glossário proposto**

| Hoje aparece como | Forma única |
| --- | --- |
| o outbox / a outbox | o outbox (padrão); a tabela de outbox |
| relay, poller, worker | dispatcher; instância do dispatcher |
| confirm, ack (produtor) | confirmação do broker |
| ack (consumidor) | ack |
| agregado, chave, entidade | chave de agregado |
| comitar, comitado | fazer commit; persistido |
| publish, lag, dedup, bloat | publicação, atraso, deduplicação, inchaço |
| MT, 248M, US$ 1M | MassTransit, 248 mi, US$ 1 milhão |

Ficam em inglês os termos consagrados: commit, rollback, broker, handler, DLQ, retry, lease, gate.

## Decisões que dependem de você

A revisão abriu três decisões novas e confirmou as que já estavam pendentes. As novas vêm primeiro.

- [x] **Nome da organização no GitHub.** `waybill` está ocupado. Opções: `waybill-net`, `waybilldotnet`, ou o repositório na sua conta (`Joseleno/Waybill`), como já é o CleanStart. Decidido: Joseleno/Waybill
- [x] **Política de ordenação com DLQ.** Mensagem na DLQ bloqueia a chave (padrão sugerido, com alerta) ou a chave segue sem ordem. Decidido: bloqueia a chave, com alerta
- [ ] **Métrica mínima na v0.1.** Idade da mensagem pendente mais antiga via `System.Diagnostics.Metrics`, sem OpenTelemetry completo
- [x] **Ordenação na v0.1 ou na v0.2.** Decidido em Oct 4, 2026 pelo spike: v0.2
- [x] **Licença.** Decidido em Oct 4, 2026: Apache-2.0
- [x] **Matriz de suporte.** Decidido em Oct 4, 2026: .NET 10, EF Core 10, Npgsql 10, RabbitMQ.Client 7, PostgreSQL 15+

## Plano de correção

As correções cabem em uma rodada por documento. Os itens que não dependem das decisões acima podem ser aplicados já.

**Escopo e Fronteiras**

1. Reescrever as garantias e a tabela “não promete” com os textos da seção Garantias.
2. Corrigir o escopo em uma frase: o efeito é aplicado uma vez, não o processamento.
3. Redesenhar o fluxo: inbox com `INSERT … ON CONFLICT`, ack da aplicação nos dois ramos, retry sem contar falha de transporte.
4. Ajustar os outros dois diagramas: alertas para a coluna da aplicação, “ordem quebrada” para o Dispatcher, itens da v0.2 marcados.
5. Separar a DLQ do outbox (produtor) da DLX do broker (consumidor).
6. Nova seção “Contrato técnico”: envelope, schema e migrations, hospedagem e shutdown do dispatcher, configuração, matriz de suporte.
7. Trocar “delayed exchange” por delayed retry das quorum queues e unificar os termos pelo glossário.

**Análise de Negócio**

1. Reescrever o resumo como argumento: problema, recorte entre v0.1 e v0.2, sem personas.
2. Corrigir os fatos da tabela de fontes.
3. Reescrever o posicionamento contra o Wolverine com critérios medidos e mover a janela do MassTransit v8 para público de 2027.
4. Trocar as metas de estrelas por metas que dependem do autor.
5. Remover a lista “fora do escopo” e apontar para o Escopo e Fronteiras; mover o `SECURITY.md` para a v0.1 no roadmap.
6. Reordenar as seções e aplicar o glossário.

O que fica para o próximo documento, o desenho da API: a API em dez linhas para produtor e consumidor, o fake em memória para testes, o esboço do schema das tabelas e o guia de migração de um outbox caseiro.

**Status em 03/10/2026:** o plano acima foi aplicado nos dois documentos, incluindo os quatro diagramas. Continuam em aberto a licença, a versão da ordenação, a métrica mínima na v0.1 e a matriz de suporte.

**Status em 04/10/2026:** licença, versão da ordenação e matriz decididas (ver a lista acima). Continua em aberto a métrica mínima na v0.1. O piso do PostgreSQL (15+) foi fechado no mesmo dia.

## Segunda e terceira rodadas: revisão adversarial das garantias

Duas rodadas focadas só em Garantias e Contrato técnico, com a instrução de construir contraexemplos. Elas encontraram falhas de desenho que a primeira revisão não viu, e uma delas estava na própria correção proposta pela rodada anterior. Todas as linhas abaixo já estão aplicadas no Escopo e Fronteiras.

| # | Garantia | Contraexemplo | Correção aplicada |
| --- | --- | --- | --- |
| 1 | G4 | **Crítico.** O fencing por `owner` e `attempt` protege o banco, não o broker: A publica N e trava; o lease vence; B republica N e publica N+1; o frame atrasado de A chega. A fila vê N, N+1, N. O RabbitMQ não tem fencing de produtor | G4 assimétrica: estrita só no Kafka com producer transacional por partição; no RabbitMQ, ordem na publicação salvo reentrega, com `sequence` no envelope e detecção de regressão no consumidor |
| 2 | G3/G4 | **Crítico.** A correção “inbox descarta `sequence` ≤ última aplicada” transforma reordenação em perda: N+1 chega antes de N, e N é descartada em silêncio. O mesmo ocorre ao liberar uma chave bloqueada | Regressão vai à DLX do consumidor com motivo, nunca ack silencioso; verificação só liga com produtor ordenado |
| 3 | G4 | **Alto.** Reordenar no inbox não é viável: consumidor que assina só alguns tipos vê gaps legítimos e estacionaria para sempre | O inbox detecta regressão, mas não reordena; registrado em “não promete” |
| 4 | G1 | **Alto.** `EnableRetryOnFailure` com queda durante o COMMIT reexecuta e grava um segundo evento com outro id | `message_id` fixado no cliente antes do commit: a reexecução falha por chave duplicada |
| 5 | G1 | **Alto.** `SaveChanges` repetido na mesma transação gera evento em dobro ou nenhum, conforme quando o interceptor limpa os eventos | Materializar em `SavingChanges` com deduplicação por referência, limpar em `SavedChanges` |
| 6 | G2 | **Alto.** “Publicado ou DLQ” é satisfeita mandando tudo para a DLQ; e um canal fechado por mensagem grande derruba o lote inteiro sem dizer a culpada | Lista fechada de defeitos; limite de tamanho validado antes de publicar; lote republicado um a um em canal novo; `basic.return` com orçamento próprio |
| 7 | G2 | **Alto.** Breaker aberto por timeout de confirmação oscila com broker lento | Breaker só por conexão e canal; timeout reduz o lote |
| 8 | G3 | **Alto.** “Retenção maior que a janela de reentrega” é inverificável: reprocessar a DLQ ou resetar offset depois da retenção aplica o efeito duas vezes | A API de reprocessamento recusa mensagens mais antigas que a retenção; replay fora da janela não é coberto |
| 9 | G4 | **Alto.** Kafka transacional exige `isolation.level=read_committed`, senão o consumidor lê as escritas abortadas do zumbi | Condição explícita de G4 |
| 10 | G1 | **Médio.** `synchronous_commit=on` sem `synchronous_standby_names` continua assíncrono | Condição de replicação corrigida |
| 11 | G4 | **Médio.** Contador por chave segura o lock até o commit: deadlock entre chaves e uma mensagem por ciclo | Incremento em `SavingChanges` com chaves ordenadas; até M cabeças consecutivas por ciclo |
| 12 | Claim | **Médio.** Sem cancelar a espera na margem do lease, a instância vira zumbi por tempo indefinido | Lease ≥ timeout de confirmação + margem; cancela a espera na margem |
| 13 | G3 | **Médio.** `consumer_name` por serviço faz o segundo handler do mesmo evento ser descartado como duplicata | Chave `(handler, message_id)`, nome obrigatório por handler |
| 14 | G1 | **Médio.** Evento enfileirado depois do último `SaveChanges` some sem erro | Exceção ao descartar `DbContext` com evento pendente |
| 15 | Limpeza | **Médio.** Cabeça calculada por “predecessor published” quebra quando a limpeza apaga o predecessor; `outbox_keys` limpa reinicia o contador | Cabeça = nenhuma linha da chave com `sequence` menor e status ≠ `published`; `outbox_keys` e `inbox_keys` nunca são limpas |
| 16 | G3 | **Médio.** `ON CONFLICT` contra inserção não comitada espera, e em `SERIALIZABLE` vira erro | `READ COMMITTED` exigido; handler precisa caber no timeout do consumidor |
| 17 | DLQ | **Médio.** Reprocessar os mesmos bytes reproduz o defeito | Duas ações: reprocessar e substituir payload com novo id |

**Tentaram quebrar e não conseguiram:** gap de sequência (rollback não deixa gap), DLQ com claim de N+1, rebalance do Kafka com offset depois do inbox, timeout de confirmação e nack (só duplicata), DELETE em lote contra claim (status disjuntos), shutdown com publicação em voo.

**Decisão fechada por consequência:** a estratégia de ordenação passa a ser lease por partição `hash(key) % P`, porque é a única que admite fencing em algum transporte. A opção “cabeça por chave” sozinha não é fenceável.

**Veredito atualizado:** com estas correções o contrato está defensável para virar `GUARANTEES.md`. O risco que resta não se resolve em documento: o claim, o inbox e a classificação de falhas precisam de um spike com Testcontainers antes de fechar a API.

## Quarta rodada: spike da etapa 0 e revisão adversarial do spike (04/10/2026)

O spike pedido no veredito acima foi feito em `spike/` (SQL cru, Npgsql, Testcontainers com PostgreSQL 18, 32 testes). Uma revisão independente leu o código, os números e estes documentos e tentou quebrar as conclusões. O detalhe está no `RESULTADOS.md` e no ADR 0001 do spike; aqui fica o que mudou o contrato.

| # | Onde | Achado | Correção |
| --- | --- | --- | --- |
| 1 | Claim | **Crítico.** Ao travar uma linha que outra transação alterou e commitou, o PostgreSQL só reavalia os predicados da tabela travada. A consulta de cabeça por chave, com a condição de status numa subconsulta, reivindicou a mesma linha duas vezes (1.202 duplicatas em 10 mil com lease de 30 s). A revisão achou a mesma falha na liberação de partições | Condição de reivindicável no nível do `FOR UPDATE` e repetida no `UPDATE` externo; `READ COMMITTED` exigido |
| 2 | G2 | **Alto.** A coluna `attempt` era token de fencing e contador de tentativas ao mesmo tempo: ou o token deixa de ser único, ou a falha de transporte gasta tentativa | `fence` (token) separado de `attempts` (só defeitos) |
| 3 | Claim | **Médio.** Com nome de instância estável, zumbi e sucessor têm o mesmo `owner`; o fencing por `owner` não os separa | Testado: o `fence` sozinho barra a marcação e a devolução atrasadas. `owner` único por encarnação de processo |
| 4 | Ordenação | **Decisão.** Contador por chave +16% a +70% no p99 (critério: até 20%); claim ordenado com 35% a 38% da vazão (critério: pelo menos 50%). Critérios escritos antes da medição | Ordenação na v0.2; etapa 3b removida |
| 5 | Claim | **Decisão.** Sem ordenação, o lease por partição não tem consumidor na v0.1, custa 14% de vazão, duas tabelas e heartbeat, e faz todas as chaves de uma instância morta esperarem o lease | Claim por linha na v0.1; lease por partição na v0.2, como filtro sobre o mesmo claim |
| 6 | G4 | **Alto, aberto.** `SKIP LOCKED` pula uma linha-cabeça travada por marcação ou devolução concorrente e, com M ≥ 2, leva a seguinte: quebra a ordem sem publicação zumbi, inclusive no Kafka | Risco registrado no Escopo; teste obrigatório no plano da v0.2 |
| 7 | G4 | **Médio, aberto.** Cabeça por chave sem lease por partição só é segura com M=1 (1.268 a 1.936 inversões com M=4); o `epoch` da partição é gravado e não participa do fencing; o contador "no fim da transação" conflita com o momento em que `SavingChanges` roda | Pendências do plano da v0.2 |
| 8 | Ambiente | **Baixo.** O relógio de parede do container recua (~1 ms no Docker/WSL2) e gerou um falso alarme de ordem de commit | Ordem nunca por timestamp; lease muito acima do jitter |

**O que a revisão do spike corrigiu nas conclusões dele:** a causa do custo do contador (o upsert, não uma ida extra ao banco), a variância entre execuções (faixas no lugar de um número), a tolerância de relógio declarada como posterior à medição, e testes que provavam menos do que afirmavam (distribuição entre instâncias, `attempt` isolado, kill com o claim aberto, falha parcial de lote, controle negativo da cabeça por chave).

**Veredito:** o claim por linha com `SKIP LOCKED`, lease e token de fencing está provado no nível de spike para a v0.1. Os mecanismos novos entram no Escopo marcados como provisórios até a etapa 3. A ordenação continua sendo a parte de maior risco do projeto e tem pendências concretas para a v0.2.
