# Etapa 3 — Tarefas

## 3a — dispatcher e claim

- [ ] Núcleo: `ITransport`, `OutgoingMessage`, `PublishResult`
- [ ] EF/PostgreSQL: store do dispatcher (claim, marcar, devolver, retorno, DLQ) em SQL cru, conforme ADR 0001
- [ ] `OutboxDispatcher` (um ciclo) e `WaybillDispatcherService` (`BackgroundService`), `AddWaybillDispatcher`, opções validadas
- [ ] Cenários `G2_*` da 3a com transporte falso, um commit por cenário
- [ ] Caos curto: processo filho do dispatcher morto com `Process.Kill`
- [ ] Oito dispatchers com oráculo por trigger (curto no PR, longo no agendado)
- [ ] CHANGELOG, API pública, CI verde

## 3b — transporte RabbitMQ

- [ ] (detalhar no início da 3b)

## 3c — classificação de falhas, breaker e caos

- [ ] (detalhar no início da 3c)
