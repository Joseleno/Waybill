# Etapa 11 — OpenTelemetry

Derivado de `docs/plano-v0.2.md`, seção "Etapa 11", da linha "OpenTelemetry" da tabela de funcionalidades de `docs/escopo-e-fronteiras.md` e do item "Métricas de regressão" que a etapa 10 deixou para cá. Rascunho de Oct 21, 2026, revisto depois de uma revisão adversarial, para o ponto de controle do corte em Oct 28.

## Objetivo

Dar rastro de ponta a ponta sem tirar o núcleo do BCL: **a requisição que grava, cada publicação pelo dispatcher e o processamento no inbox ficam num único trace**, e o operador tem métrica para o que hoje só aparece em SQL: mensagens na DLQ do outbox e regressões no inbox.

## O que já existe, e um defeito da v0.1

- O envelope leva `traceparent` e `tracestate` desde a v0.1, capturados do `Activity.Current` no `Enqueue` (`EnvelopeFactory`), e o transporte RabbitMQ os copia para os cabeçalhos.
- O meter `Waybill` (ADR 0004) já tem `waybill.outbox.oldest_pending.age` (o "atraso" do plano), `waybill.outbox.blocked_keys` (as "chaves bloqueadas", etapa 8) e `waybill.outbox.heads_repaired`. Ele é registrado só pelo `AddWaybillDispatcher`.
- Nenhum `ActivitySource`.
- **Defeito:** se a aplicação ouve a fonte `RabbitMQ.Client.Publisher`, o cliente 7.x abre um span com pai no `Activity.Current`, que é nulo no dispatcher, e sobrescreve o `traceparent` dos cabeçalhos sem condição (`RabbitMQActivitySource.BasicPublish`, `DefaultContextSetter`). O consumidor recebe um trace novo, sem relação com a requisição. Vale hoje, sem nenhum span do Waybill, e esta etapa corrige.

## Decisões de desenho

| Tema | Decisão |
| --- | --- |
| Dependência | Só `System.Diagnostics` (`ActivitySource`, `Meter`), como manda a regra do núcleo. Nenhum pacote OpenTelemetry nos `src/`. O nome da fonte, `Waybill`, fica numa constante pública, para o `AddSource` da aplicação. Sem ouvinte, `StartActivity` devolve nulo |
| Contexto na publicação | Em volta de cada `BasicPublishAsync`, o transporte põe no `Activity.Current` o contexto da mensagem: o span de envio dela, se houver, ou uma `Activity` sem fonte cujo pai é o `traceparent` dos cabeçalhos. Assim o span do cliente, quando ouvido, fica no trace da mensagem, e o `traceparent` que ele grava continua nele. Nunca um span de lote ou de ciclo. O `Current` é posto dentro do método assíncrono de cada mensagem, e um teste prova que ele não vaza para o fluxo do dispatcher |
| Span de criação | No `Enqueue`, depois da validação (tipo, tamanho, strings curtas), um span curto `create {nome da mensagem}`, kind Producer, filho do `Activity.Current`. O `traceparent` gravado passa a ser o desse span. Sem ouvinte, fica o da v0.1. O `Enqueue` continua sem I/O. Uma mensagem descartada antes do commit deixa um span de criação sem continuação; isso é aceito e documentado. O `FakeOutbox` usa o mesmo `EnvelopeFactory` e também emite o span |
| Span de envio | O dispatcher é dono dele: um por mensagem e por claim, aberto antes da chamada ao transporte e fechado depois do `FinishAsync`, com o desfecho registrado (`published`, `retry`, `returned`, `dlq`, `not_sent`, `fenced`) e status de erro fora de `published`. Assim cobre o que se decide fora do transporte: canal indisponível, payload acima do limite, timeout do lote, `MaxReturns` esgotado, fence perdido. O contexto chega ao transporte no próprio cabeçalho `traceparent` da mensagem, que o dispatcher troca pelo do span: nenhuma mudança na API do `ITransport`, e um transporte de terceiros propaga o span sem saber dele. O isolamento depois de um 406 é um evento no span, não um span novo |
| Convenções | As de mensageria do OpenTelemetry, na versão que o PLAN fixar (estão em desenvolvimento; o CHANGELOG avisa que os nomes podem mudar com elas): `send {destino}`, `messaging.operation.type = send`, `messaging.operation.name`, `messaging.system`, `messaging.destination.name`, `messaging.message.id`, `error.type`, mais `waybill.sequence` quando há. **Desvio consciente:** as convenções pedem que o envio só tenha link para o contexto de criação. Aqui ele tem esse link e também esse contexto como pai, porque o escopo promete um único trace e a republicação horas depois tem de cair nele. As convenções pedem kind Client quando há span de criação e Producer quando não há, mas o dispatcher não sabe se houve um, que pode ter sido aberto em outro processo: o PLAN fixa o kind. O desvio vai para o ADR |
| Sem span de ciclo | Claim, marcação, varredura e retenção não abrem span: seriam um trace por ciclo de polling, sem relação com as mensagens. Os comandos SQL ficam com a instrumentação do Npgsql, se a aplicação ligar, e um teste prova que eles não caem dentro dos spans de mensagem |
| Span de processamento | No inbox (`ProcessAsync` e `ProcessInOrderAsync`), `process {handler}`, kind Internal, filho do `Activity.Current`. É Internal porque o consumo é da aplicação: com a instrumentação do cliente ouvida, o span Consumer dele (`deliver`, pai no publicador por padrão) já é o pai, e um segundo Consumer contaria o processamento duas vezes. O span fica fora da estratégia de execução, e cada tentativa é um evento. O resultado (`processed`, `duplicate`, `regressed`) é atributo; `regressed` e exceção do handler fecham com erro. Quem não usa a instrumentação do cliente põe o contexto no `Activity.Current` à mão; o exemplo usa a do cliente |
| Métrica da DLQ | Gauge `waybill.outbox.dlq`, mensagens em `dlq`, amostrado pelo `OldestPendingSampler` com os outros, com a ordenação ligada ou desligada. Índice parcial novo `WHERE status = 'dlq'` na migration da v0.2, ainda sem tag: sem ele, cada instância varre as linhas retidas a cada amostra. Cada dispatcher reporta o mesmo total, então a soma entre instâncias é errada: o `OPERATIONS.md` manda agregar por máximo, e isso vale também para `blocked_keys` |
| Métrica de regressões | Contador `waybill.inbox.regressions`, com o handler e o motivo (`lower`, `reused`). O medidor do inbox é registrado pelo `AddWaybillInbox`, com o `IMeterFactory` quando há, porque um serviço só consumidor não tem dispatcher. O nome do handler vira tag: o XML doc pede um valor constante. O `FakeInbox` continua contando em `Regressions`, sem métrica |
| Exemplo | Billing e Receipts exportam por OTLP para um coletor no `docker compose`, ouvindo também as fontes do RabbitMQ.Client. Só o exemplo ganha pacotes OpenTelemetry (hospedagem, ASP.NET Core, exportador OTLP), e eles passam pelo `dependency-audit`. O README do exemplo mostra onde ver o trace e as métricas |
| Testes com ouvinte | Um `ActivityListener` vale para o processo inteiro, e as classes de teste rodam em paralelo; ouvir `Waybill` quebraria, por exemplo, os testes do envelope que conferem o `traceparent` da v0.1. O PLAN define o isolamento: o ouvinte de cada teste só amostra o trace do próprio teste, ou os testes ficam numa coleção sem paralelismo |

## Cenários de aceite

| Cenário | Resultado esperado |
| --- | --- |
| Requisição HTTP que grava, publica e é consumida, com as fontes ouvidas | Um único trace: requisição, `create`, `send`, `process`. Teste de integração, e o exemplo mostra o mesmo no coletor |
| Só `RabbitMQ.Client.Publisher` ouvida, `Waybill` não | O consumidor recebe o `traceparent` no trace da requisição (o defeito da v0.1 corrigido) |
| `Waybill` e `RabbitMQ.Client.Publisher` ouvidas | O span do cliente fica dentro do `send` da mensagem; o consumidor continua no trace |
| Ninguém ouve | Nenhum span; o `traceparent` que chega ao consumidor é o da requisição, como na v0.1 |
| Mensagem republicada depois de lease vencido | O `send` novo está no mesmo trace do primeiro, com o mesmo pai |
| Lote com mensagens de requisições diferentes | Cada `send` no trace da sua mensagem; nenhum atravessa traces; o `Activity.Current` do dispatcher não muda depois do ciclo |
| Desfechos: confirmada, devolvida, defeito, falha de transporte, payload acima do limite, timeout do lote, `MaxReturns` esgotado, fence perdido | O `send` fecha com o desfecho registrado no banco, e erro fora de `published` |
| Isolamento depois de um 406 | Um `send` por mensagem, com o evento de isolamento; as inocentes fecham como `published` |
| Chave bloqueada e mensagem na DLQ | `waybill.outbox.blocked_keys` e `waybill.outbox.dlq` sobem e voltam a zero depois da liberação |
| Regressão no inbox, num serviço sem dispatcher | `waybill.inbox.regressions` conta uma, com handler e motivo; o `process` fecha com `regressed` e erro |
| Handler lança; commit ambíguo repetido pela estratégia | `process` com erro no primeiro caso; no segundo, um span só, com um evento por tentativa |
| Núcleo | `Nucleo_DependeSoDeSystemEMicrosoftExtensions` continua passando |

**Pronto quando:**
- os cenários passam;
- o README do exemplo mostra onde ver o trace;
- um ADR registra:
  - a dona e o pai de cada span;
  - o desvio das convenções;
  - o `Activity.Current` na publicação;
  - o que não abre span.

## Perguntas para o autor

1. **Corte (Oct 28).** A etapa é cortável pela linha de corte do plano. Pelo calendário, ela cabe: a v0.2 abriu em Oct 6 com oito semanas, as etapas 8 e 10 estão fechadas, e restam esta (3 a 5 dias) e a 12 (3 a 4). Recomendação: manter, com um piso declarado para um corte parcial. O piso é a correção da propagação e as duas métricas. Os spans e o coletor vêm depois.
2. **Chave e tenant como atributos de span.** A chave costuma ser o id do agregado, às vezes de um cliente, e traces saem da aplicação para um fornecedor. Recomendação: chave e `tenant_id` fora por padrão, ligáveis por opção em `WaybillOptions`. A `sequence` vai sempre.
3. **Coletor do exemplo.** Recomendação: o dashboard do .NET Aspire em modo standalone, uma imagem que recebe OTLP e mostra traces, métricas e logs. A tag fica fixa, sem autenticação no exemplo, e os dados ficam só em memória. A alternativa é o Jaeger, que mostra só traces.

## Fora da etapa

- Span de ciclo do dispatcher, de claim ou de retenção (decisão acima).
- Instrumentar o consumo: o Waybill não tem laço de consumo.
- Histograma de latência de publicação e outras métricas além das duas novas: só com pedido.
- Dashboards e alertas: do time, como diz o escopo.
- Amostragem: uma republicação horas depois cai num trace que um coletor com *tail sampling* já pode ter fechado. Fica documentado, sem tratamento.
- Kafka (ADR 0009).
