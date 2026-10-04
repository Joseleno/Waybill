# Etapa 4 — Plano e critérios de aceite

| Teste | Cenário | Resultado esperado |
| --- | --- | --- |
| `G3_EntregaDuplaEmSequencia_AplicaUmaVez` | Mesma mensagem processada duas vezes | Efeito uma vez; segunda vez `Duplicate`, handler não chamado |
| `G3_EntregaParalela_AplicaUmaVez` | Duas tarefas chamam a API do inbox ao mesmo tempo com a mesma mensagem (pelo broker, uma fila não entrega a mesma mensagem duas vezes ao mesmo tempo) | Efeito uma vez; a segunda espera a primeira e sai como `Duplicate` |
| `G3_DoisHandlersDoMesmoEvento_OsDoisAplicam` | Handlers diferentes, mesma mensagem | Os dois aplicam |
| `G3_FalhaNoHandlerAposInsert_RollbackEReprocessa` | O handler lança depois do INSERT do inbox | Rollback completo; reentrega processa |
| `G3_FalhaDoAckDepoisDoCommit_ReentregaEhDuplicata` | Commit feito, ack perdido | A reentrega cai na duplicata e só faz ack |
| `G3_HandlerEnfileiraEFalha_SemInboxNemOutbox` | O handler enfileira um evento e lança | Nem linha de inbox nem de outbox; no sucesso, as duas no mesmo commit |
| `G3_DbContextForaDaTransacao_FalhaExplicita` | O handler grava por outra instância do contexto | Falha explícita, com o que fazer; nada aplicado |
| `G3_ConsumidorRabbitMqSemFramework` | RabbitMQ.Client puro + inbox, mensagem entregue duas vezes | Aplica uma vez; ack depois do commit |
| `FakeInbox_EquivalenteAoReal_*` | Mesmo código contra o fake e o real | Mesmo resultado |

**Pronto quando** os cenários passam contra PostgreSQL real, existe um consumidor de exemplo sem framework e o fake tem teste de equivalência.
