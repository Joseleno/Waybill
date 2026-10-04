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

- [ ] (detalhar no início da 3b)

## 3c — classificação de falhas, breaker e caos

- [ ] (detalhar no início da 3c)
