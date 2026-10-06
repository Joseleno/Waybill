# Etapa 3 — Tarefas

## 3a — dispatcher e claim

- [x] Núcleo: `ITransport`, `OutgoingMessage`, `PublishResult`
- [x] EF/PostgreSQL: store do dispatcher (claim, marcar, devolver, retorno, DLQ) em SQL cru, conforme ADR 0001
- [x] `OutboxDispatcher` (um ciclo) e `WaybillDispatcherService` (`BackgroundService`), `AddWaybillDispatcher`, opções validadas
- [x] Cenários `G2_*` da 3a com transporte falso, um commit por cenário
- [x] Caos curto: processo filho do dispatcher morto com `Process.Kill` (morte com o claim aberto: coberta pelo spike, Q2b; o claim é um só comando em autocommit)
- [x] Oito dispatchers com oráculo por trigger (curto no PR, longo no agendado); verificado por mutação
- [x] CHANGELOG, API pública, CI verde na PR #4
- [x] Revisão de código independente antes da PR: timeout imposto mesmo com transporte que ignora o token, backoff em falhas, `default(PublishResult)` nunca confirma

## 3b — transporte RabbitMQ

- [x] `AddWaybillRabbitMQ`: opções (`Uri`, `Exchange`, `ClientProvidedName`), conexão e canal com publisher confirms rastreados, recriados quando fecham
- [x] Publicação: exchange configurada, routing key = nome registrado, `mandatory`, mensagem persistente, propriedades AMQP (`message_id`, `type`, `content_type`, `timestamp`, `correlation_id`) e headers (`traceparent`, `tracestate`, `tenant_id`, `waybill-key`)
- [x] Resultados: confirmação → `Confirmed`; `basic.return` → `Returned` com `ReplyText`; nack e falha de conexão ou canal → `Retry`; exchange inexistente → `Retry` com log de erro (configuração)
- [x] Testes com RabbitMQ real (Testcontainers): publicação com propriedades, sem rota até a DLQ, exchange inexistente e recuperação, ponta a ponta com o host
- [x] Revisão de código independente antes da PR: short string AMQP acima de 255 bytes ia para retry infinito (agora recusada no registro/`Enqueue` e `Defect` no transporte); vazamento de canal; dispose coordenado. CI verde na PR #5

## 3c — classificação de falhas, breaker e caos

- [x] Núcleo: `TransportFailure` (`Connection`, `ConfirmTimeout`, `Nacked`) no `PublishResult`, para o dispatcher distinguir as causas de `Retry`
- [x] Dispatcher: circuit breaker só para conexão e canal (aberto: não reivindica; meio aberto: sonda com lote de 1); redução do lote à metade no timeout de confirmação ou nack, sem abrir o breaker, e volta gradual ao `BatchSize`
- [x] RabbitMQ: classificação das falhas; canal fechado pelo broker no meio do lote → republicação um a um em canal novo, e só a mensagem que fecha o canal sozinha (406) vai para a DLQ
- [x] Testes de unidade do breaker e do lote (tempo falso); integração com `max_message_size`; caos com Toxiproxy (broker parado, latência alta, conexão derrubada no meio da publicação; broker parado por 1 h no agendado)
- [x] ADR 0003: classificação de falhas; CHANGELOG
- [x] Revisão de código independente: bloqueio pela cabeça da fila, breaker fechado sem contato com o broker, queda silenciosa, isolamento em canal dedicado, `Retry` sem causa como pressão
