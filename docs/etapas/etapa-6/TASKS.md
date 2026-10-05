# Etapa 6 — Tarefas

- [ ] Billing: domínio, migrations EF, endpoints, configuração do Waybill, modo migrate; `Billing.Tests`
- [ ] Receipts: consumidor RabbitMQ.Client, handler no inbox que também produz, modo migrate; `Receipts.Tests`
- [ ] Compose: Dockerfiles, `definitions.json`, init dos bancos, serviço `migrate`, healthchecks
- [ ] Scripts: `smoke.sh`, `broker-outage.sh` e `broker-outage.ps1`, verificados localmente
- [ ] `samples/README.md` (inglês): subir, publicar o primeiro evento, o cenário de falha, por que a API não migra
- [ ] CI: projetos do exemplo na solution e nos testes; job `sample` com smoke e falha do broker; exemplo fora do pack
- [ ] Validação por agente sem contexto; `FRICCAO.md` com as quatro perguntas; correções no pacote com teste (ADR se mudar API pública)
- [ ] Revisão de código independente; README raiz e CHANGELOG; CI verde; validação do autor
- [ ] Etapa 5: marcar a execução verde da carga longa no job agendado
