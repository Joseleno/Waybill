# Etapa 4 — Tarefas

- [ ] `IInbox<TContext>`, `InboxResult`, `AddWaybillInbox<TContext>`; transação própria em READ COMMITTED, INSERT … ON CONFLICT, rollback e limpeza do tracker na falha
- [ ] Guarda contra gravação por outra instância do contexto fora da transação do inbox
- [ ] Helper `GetWaybillMessageId()` para as propriedades AMQP (Waybill.RabbitMQ)
- [ ] Cenários `G3_*`, um commit por cenário; consumidor RabbitMQ.Client sem framework
- [ ] `FakeInbox<TContext>` e teste de equivalência
- [ ] Revisão de código independente; CHANGELOG, README, API pública; CI verde
