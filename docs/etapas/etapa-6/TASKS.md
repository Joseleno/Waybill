# Etapa 6 — Tarefas

- [x] Billing: domínio, migrations EF, endpoints, configuração do Waybill, modo migrate; `Billing.Tests`
- [x] Receipts: consumidor RabbitMQ.Client, handler no inbox que também produz, modo migrate; `Receipts.Tests`
- [x] Compose: Dockerfiles, `definitions.json`, init dos bancos, serviço `migrate`, healthchecks
- [x] Scripts: `smoke.sh`, `broker-outage.sh` e `broker-outage.ps1`, verificados localmente
- [x] `samples/README.md` (inglês): subir, publicar o primeiro evento, o cenário de falha, por que a API não migra
- [x] CI: projetos do exemplo na solution e nos testes; job `sample` com smoke e falha do broker; exemplo fora do pack
- [x] Validação por agente sem contexto; `FRICCAO.md` com as quatro perguntas; correções no pacote com teste (ADR se mudar API pública)
- [ ] Revisão de código independente; README raiz e CHANGELOG; CI verde; validação do autor
- [ ] Etapa 5: marcar a execução verde da carga longa no job agendado
