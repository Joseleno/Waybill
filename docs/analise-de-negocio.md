# Waybill — Análise de Negócio

Oct 3, 2026 · @Joseleno

## Resumo executivo

Vale construir, como pacote de nicho e não como concorrente de Wolverine ou CAP. Banco e broker não compartilham transação, e o outbox feito à mão falha sob concorrência, crash ou volume: justamente as condições que não aparecem em desenvolvimento.

O Waybill oferece outbox e inbox para PostgreSQL, EF Core e RabbitMQ, sem framework, e só promete o que um teste de concorrência prova. A v0.1 cabe em 6 a 8 semanas: gravação atômica, dispatcher seguro com várias instâncias, publisher confirms, inbox na mesma transação do handler, limpeza e uma métrica de atraso. Ordenação por chave de agregado, OpenTelemetry e Kafka entram na v0.2, puxados pelo projeto de exemplo.

O retorno de portfolio é alto mesmo com poucos downloads; o uso público depende de manutenção visível ao longo de 6 a 12 meses. Quem usa o MassTransit v8 só pelo outbox é público de 2027, não da v0.1.

Uma revisão adversarial das garantias deixou uma regra para o projeto inteiro: nenhuma garantia vai para o README sem um teste de concorrência que a prove. O contrato completo está em Waybill — Escopo e Fronteiras.

## O problema: dual-write

Não existe transação que cubra banco e broker ao mesmo tempo. Toda aplicação que grava um registro e publica um evento sobre ele pode ficar com um sem o outro ([Debezium](https://debezium.io/blog/2019/02/19/reliable-microservices-data-exchange-with-the-outbox-pattern/), [microservices.io](https://microservices.io/patterns/data/transactional-outbox.html), [Microsoft](https://learn.microsoft.com/en-us/azure/architecture/databases/guide/transactional-outbox-cosmos)).

| Modo de falha | O que acontece | O que o negócio vê |
| --- | --- | --- |
| Publicar depois do commit | O processo morre ou a rede cai entre o commit e o publish | Cobrança criada sem o envio do boleto ou do Pix; pedido pago que não chega ao estoque |
| Publicar antes do commit | O commit falha por deadlock, constraint ou timeout | Pedido fantasma: confirmação de algo que não existe; ERP recebe título inexistente |
| Publicar dentro da transação | A publicação dá certo e depois a transação sofre rollback; a transação fica aberta durante I/O de rede | O mesmo fantasma, mais locks longos e pool de conexões esgotado quando o broker fica lento |
| Timeout ambíguo do broker | O broker gravou, mas a confirmação se perdeu | Pagamento confirmado duas vezes, notificação duplicada, crédito em dobro |
| Sem publisher confirms | O broker reinicia antes de persistir | Perda silenciosa: “a mensagem sumiu” |

O timeout ambíguo é o mesmo problema que a Stripe resolve com idempotency keys ([Stripe](https://stripe.com/blog/idempotency)). A própria documentação do RabbitMQ diz que a garantia de persistência exige transações AMQP ou publisher confirms e que consumidores devem ser idempotentes ([RabbitMQ](https://www.rabbitmq.com/docs/confirms)).

**Impacto por domínio.** Em pagamentos, o efeito é título pago que continua em aberto, cobrança indevida e conciliação manual no fechamento. Em e-commerce, venda acima do estoque e custo logístico sem receita. Em SaaS multi-tenant, o chamado “o webhook não chegou” sem trilha para provar a entrega. Não há post-mortem público confiável que atribua um valor em dinheiro ao dual-write, então o discurso do pacote não deve citar números de prejuízo.

## Dores do outbox feito à mão

O outbox básico é fácil; o que dói vem depois. Toda falha abaixo depende de concorrência, crash ou volume, então nenhuma aparece em dev ou em teste unitário. O outbox caseiro funciona até o primeiro pico ou o primeiro incidente.

| Dor | Por que acontece | Evidência |
| --- | --- | --- |
| Mesma linha enviada por várias instâncias | `SELECT … WHERE processed IS NULL` sem lock duplica quando a API escala para N pods | [npiontko](https://www.npiontko.pro/2025/05/19/outbox-pattern) |
| Ordenação quebrada | Processamento paralelo com `SKIP LOCKED` entrega fora de ordem; o teste com uma instância sempre passa | [Milan Jovanović](https://milanjovanovic.tech/blog/scaling-the-outbox-pattern) |
| Mensagem pulada pelo dispatcher | `bigserial` e timestamp são atribuídos no INSERT, não no COMMIT; um dispatcher que lê por cursor pula mensagens | Revisão técnica; teste próprio na v0.1 |
| Tabela crescendo | Cada `UPDATE` de status gera dead tuple; um caso documentado foi de 200 MB a 18 GB com o vacuum travado | [Tiare Balbi](https://tiarebalbi.com/en/blog/the-transactional-outbox-is-not-a-queue) |
| Poison message | Sem limite de tentativas e DLQ, uma mensagem bloqueia todas as que estão atrás dela | [Tiare Balbi](https://tiarebalbi.com/en/blog/the-transactional-outbox-is-not-a-queue) |
| Polling caro | Seq scan de \~124 ms caiu para \~0,19 ms com índice parcial; índice, paralelismo e lote, juntos, levaram de 1.350 para \~32.500 msg/s | [Milan Jovanović](https://milanjovanovic.tech/blog/scaling-the-outbox-pattern) |
| “A mensagem sumiu” | Ninguém mede a idade das mensagens pendentes nem a vazão de entrada e saída | [npiontko](https://www.npiontko.pro/2025/05/19/outbox-pattern) |
| Duplicata no consumidor | O dispatcher republica se cair entre a publicação e a marcação; sem inbox, o outbox só troca perda por duplicata | [microservices.io](https://microservices.io/patterns/data/transactional-outbox.html) |

Gravar na mesma transação e fazer polling simples é trivial; o trabalho do pacote está nos itens acima.

## Público-alvo e posicionamento

O usuário típico é um time .NET de 3 a 15 pessoas, com 1 a 10 serviços, que já usa PostgreSQL e um broker. Ele publica eventos e quer garantia de entrega sem trocar a forma como consome. Por isso o inbox precisa envolver qualquer consumidor que o time já tenha (RabbitMQ.Client, Confluent ou um consumer do MassTransit), e o projeto de exemplo mostra isso.

Os gatilhos que levam esse time a procurar uma solução:

1. O primeiro incidente de “cliente não recebeu” ou “cobrado em dobro”.
2. Escalar de uma para N réplicas e começar a ver duplicatas.
3. Auditoria, due diligence ou cliente enterprise pedindo rastreabilidade de eventos.
4. A tabela de outbox caseira estourando o disco.
5. Planejar, ao longo de 2027, a saída do MassTransit v8 para quem o usa só pelo outbox.

**Posicionamento proposto:** “Outbox e inbox transacionais para PostgreSQL e EF Core, sem framework. Garantias escritas, provadas por testes de caos.”

O pacote não pretende ser substituto do MassTransit, framework de mensageria nem solução exactly-once. Para quem usa consumidores, roteamento ou sagas do MassTransit, migrar para o Waybill não compensa; o público é quem usa o MassTransit só pelo outbox. O README deve ter uma seção “quando não usar” apontando Wolverine e CAP para quem quer um framework completo.

## Panorama de mercado

Outbox em .NET não falta: a busca por “outbox” no NuGet devolve 938 resultados com versão estável. O que falta é um “só o outbox” consolidado. A maioria dos pacotes com tração exige um framework; os independentes passam de 25 mil downloads, mas têm pouca adoção visível. Downloads consultados na API do NuGet em 03/10/2026.

| Solução | Licença | Downloads | Exige framework | Ponto relevante |
| --- | --- | --- | --- | --- |
| [MassTransit](https://masstransit.massient.com/configuration/middleware/outbox) | v9 comercial, com desconto integral abaixo de US$ 1 milhão de receita; v8 Apache-2.0 com patches até pelo menos o fim de 2026 | 248 mi | Sim | Ordena por sessão do outbox, não por chave de agregado ([discussão](https://github.com/MassTransit/MassTransit/discussions/3615)) |
| [NServiceBus](https://docs.particular.net/persistence/sql/outbox) | RPL-1.5 ou comercial | 86,5 mi | Sim | Outbox com deduplicação, pago para uso comercial |
| [DotNetCore.CAP](https://github.com/dotnetcore/CAP) | MIT | 14,8 mi | Sim, event bus próprio | Declara que não garante idempotência do consumidor ([docs](https://cap.dotnetcore.xyz/user-guide/en/cap/idempotence/)) |
| [Wolverine](https://wolverinefx.net/guide/durability/efcore/outbox-and-inbox) | MIT | 9,1 mi | Parcial: `IDbContextOutbox<T>` funciona fora dos handlers | Ordenação por chave via particionamento global desde jul/2026 ([JasperFx](https://jasperfx.net/news/ordered-messaging-without-the-locks-wolverine-global-partitioning-and-re-sequencer)) |
| [Brighter](https://brightercommand.gitbook.io/paramore-brighter-documentation/outbox-and-inbox/brighteroutboxsupport.md) | MIT | 3,5 mi | Sim, via CommandProcessor | Outbox e inbox maduros |
| CUSTIS.NetCore.Outbox e GenericOutbox | — | 152 mil e 80 mil | Não | Independentes com mais downloads, pouca adoção visível |
| [Freakout](https://github.com/rebus-org/Freakout) | MIT | 28,6 mil | Não | Um dos independentes mais conhecidos, mantido na org rebus-org; ainda na v0.0.38 |
| [SimpleOutbox](https://github.com/alexandrereyes/SimpleOutbox) | MIT | 1,7 mil | Não | Escopo parecido (SKIP LOCKED, PartitionKey), 1 estrela |
| [Twinbox](https://github.com/villa313/twinbox) | MIT | 0 | Não | Escopo mais amplo (6 bancos, 10 brokers, dashboard); 1.3.0 no NuGet, 0 estrelas |

**Concorrente real:** o Wolverine. Ele já oferece outbox com EF Core fora dos handlers e ordenação por chave, e também tem testes. “Foco e garantias provadas” não basta como diferença: ela precisa ser medida e publicada no README. Os critérios são número de dependências transitivas, se exige runtime no host (a confirmar se `IDbContextOutbox<T>` exige `UseWolverine()`), ausência de geração de código em runtime e tamanho da API pública.

**Momento:** o MassTransit v8 tem patches garantidos até pelo menos o fim de 2026 e continua funcionando depois. Quem o usa só pelo outbox vai planejar a saída ao longo de 2027, comparando a v9 paga, um framework inteiro e o fork [OpenTransit](https://github.com/OpenTransitLab/OpenTransit), que ainda não tem release de produção. Esse é um público secundário: a v0.1 serve para avaliação, não para essa migração.

## Por que um pacote NuGet

Num outbox, os bugs são raros e caros. Por isso a forma de distribuição importa: só o pacote leva a correção a quem já usa.

| Alternativa | O que o time ganha | O que perde |
| --- | --- | --- |
| Copiar de um artigo | Zero dependência | Cada cópia carrega o bug para sempre; sem testes de concorrência, sem versão |
| Template (como o CleanStart) | Código próprio desde o início | Gera uma vez e diverge; correções não chegam |
| Framework completo | Mensageria inteira resolvida | Adota modelo de handlers, roteamento e convenções; troca cara depois |
| **Pacote focado** | Correção centralizada via `dotnet add package`, SemVer, suíte de caos herdada | Dependência de um mantenedor único (ver riscos) |

Para quem consome, a superfície é pequena: três pontos de extensão (`IOutbox`, `ITransport`, `IInboxStore`). Um time consegue ler o código inteiro e, se precisar, manter um fork.

Para o autor, o pacote deixa evidência pública e verificável: releases, issues respondidas, ADRs e testes. Um template não deixa esse rastro.

## Escopo e roadmap

A v0.1 é pequena de propósito: só entra o que dá para provar com teste. Ordenação por chave de agregado, OpenTelemetry e Kafka entram na v0.2, puxados pelo projeto de exemplo. O `SECURITY.md` sai já na v0.1, porque responde à objeção do autor único.

&#91;embedded content: roadmap · 3 versões, 3 gates\]

Nenhuma versão avança sem passar pelo seu gate; a v1.0 depende de um adotante externo, não de calendário.

Sagas, agendamento, outros bancos, multi-tenant por schema e painel ficam fora até a 1.0. A lista completa, com quem cuida de cada item, está em Waybill — Escopo e Fronteiras.

## Riscos e mitigações

O pacote vale a pena sob uma condição: cada garantia prometida precisa de um teste de concorrência que a prove. Os textos corrigidos das garantias estão em Waybill — Escopo e Fronteiras; aqui ficam as objeções e os riscos que um usuário encontraria nas primeiras semanas.

**Objeções de adoção**

| Objeção | Mitigação |
| --- | --- |
| “Por que não Wolverine ou CAP, de graça?” | Tabela comparativa honesta no README, incluindo quando não usar o Waybill |
| Autor único, versão 0.x, bus factor 1 | `SECURITY.md`, política de suporte escrita, SemVer rígido, API mínima e fácil de fazer fork |
| “Sem framework, ainda escrevo consumidores, roteamento e DLQ” | Exemplo completo (API → outbox → RabbitMQ → consumidor com inbox) com `docker compose up` |
| Manutenção da matriz .NET × EF Core × Npgsql × RabbitMQ.Client | CI com matriz de versões e o mínimo de dependências transitivas |

**Riscos técnicos que derrubam a credibilidade na primeira issue**

| Risco | Mitigação |
| --- | --- |
| Failover com réplica assíncrona publica um evento cujo commit some | Garantia condicionada a commit durável e replicado; replicação síncrona onde o negócio exigir |
| Broker fora do ar esgota as tentativas e manda tudo para a DLQ | Falha de transporte não conta tentativa (circuit breaker); só defeito da mensagem vai para a DLQ |
| `SKIP LOCKED` e ordenação se contradizem: a instância B pula a mensagem N, travada pela A, e publica a N+1 da mesma chave antes dela | Selecionar só a cabeça de cada chave sem mensagem em voo, ou lease por partição `hash(key) % P`; teste de propriedade sob concorrência |
| Mensagem na DLQ quebra a ordem da chave | Com ordenação ativa, a mensagem na DLQ bloqueia a própria chave, com alerta, sem atrasar as outras |
| Lock de linha só dura a transação; publicar com a transação aberta prende a conexão | Claim em transação curta com lease e fencing; publicação fora da transação |
| Ordem de commit diferente da ordem de id | Polling por status, nunca por cursor; sequência por chave gerada pela versão do agregado ou por contador por chave |
| Entregas concorrentes da mesma mensagem passam pelo “já existe?” | `INSERT … ON CONFLICT DO NOTHING` no inbox antes do handler; ack só depois do commit |
| Mensagem não roteável some no RabbitMQ | Publisher confirms obrigatórios e `mandatory` tratado como falha |
| Inchaço da tabela no MVCC do Postgres | DELETE em lote, monitoramento do horizonte do vacuum, benchmark de 24 h com transação longa aberta |
| `EnableRetryOnFailure` com transação do usuário | Transação dentro de `CreateExecutionStrategy().ExecuteAsync`; `TransactionScope` não suportado |
| PgBouncer em modo transaction quebra LISTEN/NOTIFY | Polling como padrão, NOTIFY só como otimização |
| `Type.GetType(nome)` quebra com trimming e abre desserialização insegura | Registro explícito de tipos e System.Text.Json com source generation |

**Risco de portfolio.** Um avaliador vê como negativo: repositório parado, README com “production-ready” ou “exactly-once”, roadmap cheio sem nada entregue e nenhum teste de concorrência. O timebox impede que o projeto concorra com outras prioridades.

## Nome, licença e sinais de confiança

**Nome escolhido: Waybill.** É o conhecimento de transporte: o documento que acompanha cada remessa e registra o que saiu, para onde vai e se chegou, como o pacote faz com cada mensagem. Pacotes: `Waybill`, `Waybill.EntityFrameworkCore.PostgreSql`, `Waybill.RabbitMQ`, depois `Waybill.Kafka`. Solicitar ao NuGet a reserva do prefixo `Waybill.*` na primeira publicação; a reserva passa por aprovação e pode ser recusada para palavras comuns.

| Nome | NuGet | Observação |
| --- | --- | --- |
| **Waybill** | Livre | Escolhido. Só há pacotes pequenos de logística com nome parecido (`WaybillService`, 326 downloads) |
| Mailstop | Livre | Claro, mas lembra e-mail |
| Stevedore | Livre | Nome já usado por várias ferramentas ([GitHub](https://github.com/topics/stevedore)) |
| Malote, Postigo | Livres | Descartados: a preferência é por um nome em inglês |
| Outpost | Livre no NuGet | Evitar: [hookdeck/outpost](https://github.com/hookdeck/outpost) faz entrega de eventos, mesmo domínio |
| Courier, Relay, Postbox, Pigeon, Outboxer | Ocupados | `Outboxer` já é um outbox com EF, parado desde 2023 |

O repositório fica em `github.com/Joseleno/Waybill`, como o CleanStart. O nome `waybill` como organização está ocupado por um usuário sem repositórios; se o projeto ganhar co-mantenedores, o repositório pode ser transferido para uma organização, e o GitHub redireciona os links.

**Licença.** Apache-2.0 concede patente de forma explícita e licencia as contribuições sem CLA, o que pesa para o jurídico de uma fintech. MIT também serve: é a licença de Wolverine, CAP e do CleanStart. A diferença é pequena; o importante é decidir antes do primeiro commit público. Decidido em Oct 4, 2026: Apache-2.0.

**O que faz um time confiar num pacote de autor único.** Seguindo o [guia oficial da Microsoft para bibliotecas](https://learn.microsoft.com/dotnet/standard/library-guidance/get-started) e práticas complementares:

- [ ] `GUARANTEES.md`: conteúdo definido em Waybill — Escopo e Fronteiras, seção Garantias
- [ ] SemVer com sufixo `-alpha`/`-beta` até estabilizar ([versioning](https://learn.microsoft.com/dotnet/standard/library-guidance/versioning))
- [ ] Source Link e símbolos `.snupkg` ([Source Link](https://learn.microsoft.com/dotnet/standard/library-guidance/sourcelink))
- [ ] Publicação via Trusted Publishing (OIDC) no GitHub Actions, sem chave de API guardada ([.NET Blog](https://devblogs.microsoft.com/dotnet/enhanced-security-is-here-with-the-new-trust-publishing-on-nuget-org/))
- [ ] Núcleo dependendo só de `Microsoft.Extensions.*` e `System.Diagnostics` ([dependencies](https://learn.microsoft.com/dotnet/standard/library-guidance/dependencies))
- [ ] Testes com Testcontainers e job de caos visíveis no CI
- [ ] Benchmarks reproduzíveis com BenchmarkDotNet, resultados versionados
- [ ] Validação de API pública no CI para evitar breaking change acidental
- [ ] `SECURITY.md`, `CHANGELOG`, `ROADMAP.md`, `CONTRIBUTING` e ADRs em `/docs/adr`
- [ ] SLA declarado: primeira resposta a issue em até 72 h

## Métricas e valor para portfolio

Downloads medem mal: o CI os infla e, num pacote de nicho, o número absoluto fica baixo de qualquer forma. As metas abaixo dependem só do autor; estrelas e adotantes entram como sinal, não como meta.

| Meta | 6 meses | 12 meses |
| --- | --- | --- |
| Releases publicadas | v0.1 e v0.2 | v1.0 |
| ADRs no repositório | 5 | 10 |
| Artigos publicados | 1 | 3 |
| Benchmark reproduzível | Publicado com a v0.2 | Atualizado a cada minor |
| Primeira resposta a issues | Até 72 h | Até 72 h |
| Uso real | Projeto de exemplo com RabbitMQ e Kafka | 1 adotante externo declarado |

**Como vira ativo de carreira**

- **ADRs no repositório**, como “por que `SKIP LOCKED` e não advisory lock” e “por que não prometemos exactly-once”. São respostas prontas para entrevistas de system design.
- **Um artigo técnico** sobre `SKIP LOCKED` com ordenação e ordem de commit. Em entrevista, esse texto pode valer mais que o próprio pacote.
- **Uma série no LinkedIn e Dev.to** no tom já usado na divulgação do CleanStart: sintoma verificável, não propaganda.
- **Palestra** em comunidade local ou TDC.
- **Narrativa STAR:** mudança de licença no mercado (situação), lacuna de “só o outbox” (tarefa), design guiado por garantias e testes de caos (ação), releases e adotantes (resultado).

## Decisões em aberto

- [x] Repositório em `github.com/Joseleno/Waybill`
- [x] Com ordenação ativa, mensagem na DLQ bloqueia a própria chave, com alerta; liberar a chave é ação manual registrada
- [x] Licença: Apache-2.0 (Oct 4, 2026)
- [x] Ordenação por chave de agregado na v0.2: o spike da etapa 0 mediu o custo e ele não passou nos critérios definidos antes da medição (Oct 4, 2026)
- [x] Estratégia de ordenação candidata para a v0.2: lease por partição com `hash(key) % P`; o spike deixou riscos abertos, listados no Escopo e Fronteiras
- [ ] Confirmar a métrica mínima de atraso na v0.1
- [x] Matriz de suporte: .NET 10, EF Core 10, Npgsql 10, RabbitMQ.Client 7 (Oct 4, 2026). O .NET 8 sai de suporte em nov/2026
- [x] Piso do PostgreSQL: 15+ (Oct 4, 2026); o 13 saiu de suporte em nov/2025 e o 14 sai em nov/2026
- [x] Idioma da documentação: README e arquivos do repositório em inglês para alcance (Oct 4, 2026); documentos de design seguem em português por ora; artigo em português
- [ ] Compatibilidade com o envelope do MassTransit: cortada do escopo ou só como adapter de exemplo

## Fontes

- [Debezium — Outbox pattern](https://debezium.io/blog/2019/02/19/reliable-microservices-data-exchange-with-the-outbox-pattern/)
- [microservices.io — Transactional outbox](https://microservices.io/patterns/data/transactional-outbox.html)
- [Microsoft — Transactional outbox](https://learn.microsoft.com/en-us/azure/architecture/databases/guide/transactional-outbox-cosmos)
- [Stripe — Idempotency](https://stripe.com/blog/idempotency)
- [RabbitMQ — Confirms](https://www.rabbitmq.com/docs/confirms)
- [npiontko — Outbox pattern](https://www.npiontko.pro/2025/05/19/outbox-pattern)
- [Milan Jovanović — Scaling the outbox](https://milanjovanovic.tech/blog/scaling-the-outbox-pattern)
- [Tiare Balbi — The outbox is not a queue](https://tiarebalbi.com/en/blog/the-transactional-outbox-is-not-a-queue)
- [MassTransit — Outbox](https://masstransit.massient.com/configuration/middleware/outbox) e [licença](https://masstransit.massient.com/configuration/license)
- [MassTransit — ordenação por entidade](https://github.com/MassTransit/MassTransit/discussions/3615)
- [CAP — Idempotência](https://cap.dotnetcore.xyz/user-guide/en/cap/idempotence/)
- [Wolverine — EF Core outbox e inbox](https://wolverinefx.net/guide/durability/efcore/outbox-and-inbox)
- [JasperFx — Global partitioning (jul/2026)](https://jasperfx.net/news/ordered-messaging-without-the-locks-wolverine-global-partitioning-and-re-sequencer)
- [OpenTransit](https://github.com/OpenTransitLab/OpenTransit)
- [Freakout](https://github.com/rebus-org/Freakout), [SimpleOutbox](https://github.com/alexandrereyes/SimpleOutbox), [Twinbox](https://github.com/villa313/twinbox)
- [Microsoft — Open-source library guidance](https://learn.microsoft.com/dotnet/standard/library-guidance/get-started)
- [.NET Blog — Trusted Publishing no NuGet](https://devblogs.microsoft.com/dotnet/enhanced-security-is-here-with-the-new-trust-publishing-on-nuget-org/)
- API de busca do NuGet, consultada em 03/10/2026
- [RabbitMQ 4.3 — delayed retry nas quorum queues](https://www.rabbitmq.com/blog/2026/04/23/rabbitmq-4.3-release)
- [rabbitmq-delayed-message-exchange (arquivado em 16/04/2026)](https://github.com/rabbitmq/rabbitmq-delayed-message-exchange)
- [NuGet — reserva de prefixo de ID](https://learn.microsoft.com/en-us/nuget/nuget-org/id-prefix-reservation)
- [EF Core — resiliência de conexão](https://learn.microsoft.com/ef/core/miscellaneous/connection-resiliency)
- [PostgreSQL — cláusula de lock do SELECT](https://www.postgresql.org/docs/current/sql-select.html#SQL-FOR-UPDATE-SHARE)
