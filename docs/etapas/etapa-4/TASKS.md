# Etapa 4 — Tarefas

- [x] `IInbox<TContext>`, `InboxResult`, `AddWaybillInbox<TContext>`; transação própria em READ COMMITTED, INSERT … ON CONFLICT, rollback e limpeza do tracker na falha
- [x] Guarda contra gravação por outra instância do contexto fora da transação do inbox
- [x] Helper `GetWaybillMessageId()` para as propriedades AMQP (Waybill.RabbitMQ)
- [x] Cenários `G3_*`, um commit por cenário; consumidor RabbitMQ.Client sem framework
- [x] `FakeInbox<TContext>` e teste de equivalência
- [x] Revisão de código independente; CHANGELOG, README, API pública; CI verde
