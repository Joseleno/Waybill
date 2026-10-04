# Etapa 2 — Plano e critérios de aceite

Todos os cenários de G1 rodam contra PostgreSQL real (Testcontainers, 15 e 18 no CI).

## G1 — cenários do plano

| Teste | Cenário | Resultado esperado |
| --- | --- | --- |
| `G1_Rollback_TabelaVazia` | Rollback da transação explícita depois do `SaveChanges` | Nenhuma linha de outbox |
| `G1_RetryDuranteCommit_NaoDuplica` | Conexão "cai" logo depois do COMMIT (interceptor de transação lança erro transitório) e a estratégia de retry reexecuta | Exatamente um evento; a reexecução falha por chave duplicada |
| `G1_SaveChangesRepetidoNaMesmaTransacao_UmaLinhaPorEvento` | O primeiro `SaveChanges` falha (violação de constraint numa entidade do usuário), o erro é corrigido e o `SaveChanges` é repetido na mesma transação | Exatamente uma linha por evento |
| `G1_DescarteComEventoPendente_LogaErro` | Escopo encerrado com evento enfileirado e nunca salvo | Log de erro, sem exceção; com a opção ligada, exceção |
| `G1_HandlerLancaAntesDoSaveChanges_ExcecaoDoHandler` | O código do usuário lança antes do `SaveChanges` | A exceção que chega ao chamador é a do usuário |
| `G1_EscritaForaDoEf_NaoGeraEvento` | `ExecuteUpdate` e SQL cru | Nenhum evento gerado |
| `G1_DoisDbContextComTransacaoCompartilhada_MesmoCommit` | Dois `DbContext` de tipos diferentes com conexão e transação compartilhadas | Eventos dos dois no mesmo commit; no rollback, nenhum |

## Cenários de suporte

| Teste | O que prova |
| --- | --- |
| `Envelope_*` | `message_id` UUIDv7 fixado no `Enqueue`; tipo não registrado e payload acima do limite falham no `Enqueue`; `traceparent` capturado |
| `KeyHash_*` | murmur2 estável, com vetores de referência do Kafka |
| `Schema_MigraBancoLimpo` e `Schema_MigraBancoComDados` | As migrations do pacote aplicam do zero e sobre dados existentes, sem tocar nas tabelas do usuário |
| `Schema_ModeloDoUsuarioNaoGeraMigrationDaOutbox` | `AddWaybillOutbox()` não entra nas migrations do usuário |
| `FakeOutbox_EquivalenteAoReal_*` | O fake e o real concordam em: salvo, falha de `SaveChanges` (nada salvo) e evento pendente no descarte |

## Pronto quando

- Os sete cenários de G1 passam contra PostgreSQL real.
- A migration aplica num banco limpo e num banco com dados.
- O fake tem teste de equivalência com o real.
- ADR 0002 escrito; Escopo, plano e `CHANGELOG` atualizados.
