# ADR 0010 — Verificação de sequência no inbox

- Status: aceito
- Data: 2026-10-21
- Etapa: 10 (v0.2)

## Contexto

A etapa 8 (ADR 0008) garante, por chave, que a primeira cópia de cada mensagem chega ao broker na ordem de commit. No RabbitMQ, que não tem fencing de produtor, cópias repetidas podem chegar depois. O inbox as reconhece pelo `message_id` enquanto guarda o registro.

Uma mensagem passa pela deduplicação e chega atrás de outra mais nova da chave em casos estreitos:
- uma cópia tardia depois que a retenção do inbox apagou o registro da original;
- uma tentativa sem confirmação de uma mensagem liberada da DLQ do outbox;
- um produtor restaurado de backup, que reusa sequências.

Sem verificação, ela seria aplicada fora de ordem sem ninguém saber. O escopo promete o contrário: a regressão vai à DLX e nunca é aplicada.

O desenho passou por uma revisão de construtor e por uma revisão adversarial do SPEC. O PLAN passou por um time de quatro revisores (PostgreSQL, RabbitMQ, API .NET e testes), que mediram contra `postgres:18` e `rabbitmq:4-alpine` (4.3.6).

## Decisão

1. **O inbox detecta regressão e não reordena.** Um consumidor que assina só parte dos tipos de uma chave vê gaps legítimos, e uma liberada deixa gap. Segurar a mensagem seguinte à espera do gap seria esperar para sempre. Gap não é erro.
2. **Opcional, por handler, por um método novo.** `IInbox<TContext>.ProcessInOrderAsync(handler, messageId, MessageOrder? order, handle, ct)` devolve `OrderedInboxResult` (`Processed`, `Duplicate`, `Regressed`, sem membro 0). Ser um tipo próprio mantém verdadeira a frase do `InboxResult` ("either way the message may be acknowledged"). Um `else ack` copiado do código antigo também não compila sem tratar `Regressed`. O `ProcessAsync` não muda e nunca lê `inbox_keys`.
3. **Estado em `waybill.inbox_keys (handler, key, last_seq, last_message_id)`.** É gravado na transação do handler, nunca é limpo pela retenção e tem `fillfactor` 80, como a `outbox_keys`. O `Down` da migration recusa voltar com linhas nela.
4. **Ordem dos testes, antes do handler.** Primeiro a deduplicação por `(handler, message_id)`; depois um `INSERT ... ON CONFLICT (handler, key) DO UPDATE ... WHERE last_seq < EXCLUDED.last_seq`; só então o handler.
   - Ao contrário, a reentrega de uma mensagem já aplicada viraria regressão, inclusive na repetição depois de um commit ambíguo.
   - Sem linha afetada, o `ON CONFLICT` já travou a linha da chave, mesmo com o `WHERE` falso. Um `SELECT` em comando separado lê o valor confirmado mais novo: uma CTE leria o snapshot de antes da espera (medido).
   - A trava vale até o commit, e entregas concorrentes da mesma chave no mesmo handler se serializam na ordem de chegada.
5. **Sequência igual.** Com o mesmo `message_id`, é duplicata: a última aplicada, depois da retenção. Com outro `message_id`, é regressão (sequência reusada).
6. **A regressão não grava nada.** A transação inteira volta, inclusive o registro do inbox, e uma reentrega é de novo regressão. Se o registro ficasse, a reentrega sairia `Duplicate` e seria confirmada.
7. **O motivo fica no log, não na mensagem da DLX.** Emenda a linha do escopo "vai à DLX com motivo".
   - O `basic.nack` deixa na DLX só `x-death reason=rejected`.
   - O inbox loga um `Warning` (EventId 50) com o handler, a chave, as duas sequências, os dois `message_id` e o tipo (`Lower` ou `Reused`), uma vez por chamada, fora da estratégia de execução. O operador cruza pelo `message_id`.
   - Republicar na DLX com cabeçalho próprio pediria um resultado mais rico, e um publish e um ack que não são atômicos.
8. **As condições do broker fazem parte da garantia.** Todas estão no `definitions.json` do exemplo, e o teste as lê de lá:
   - um consumidor ativo por fila, uma entrega de cada vez (`x-single-active-consumer`);
   - DLX na fila;
   - `x-dead-letter-strategy: at-least-once` com `x-overflow: reject-publish`; sem ele, o RabbitMQ cai em silêncio para at-most-once (medido);
   - a fila de mortas com `reject-publish`.
9. **Falha do handler: repetir no consumidor, depois DLX.**
   - `basic.nack` com requeue não conta para o `x-delivery-limit` (medido): uma mensagem que sempre falha voltaria para sempre e pararia a fila.
   - O exemplo repete com espera crescente, cerca de 2,5 min, e depois manda à DLX. A seguinte da chave é aplicada (um gap), e a morta, reenviada, dá `Regressed`.
10. **Mensagem sem `sequence` só é deduplicada.** Isso vale para:
    - mensagem sem chave;
    - mensagem escrita com a ordenação desligada;
    - mensagem publicada por um dispatcher que não ordena, inclusive o backlog numerado antes de desligar a ordenação, porque o claim sem ordenação não devolve a `sequence`.

    Desligar a ordenação é abrir mão da ordem. O SPEC listava essa janela como caso de detecção; a implementação mostrou que não é.
11. **`MessageOrder` no núcleo.** O pacote do RabbitMQ não depende do de EF. A regra da chave é a do `Enqueue`: não nula, até 255 bytes UTF-8, vazia aceita, porque o transporte manda chave vazia com sequência. `GetWaybillOrder` lê os cabeçalhos em `byte[]`, como chegam do broker (medido), e em `string`.

## Alternativas descartadas

| Alternativa | Por que não |
| --- | --- |
| Reordenar ou estacionar a mensagem à espera do gap | Gaps legítimos (assinatura parcial, liberação) fariam esperar para sempre |
| Verificar depois do handler | Faria valer a ordem de commit, e não a de chegada, e gastaria o trabalho do handler à toa |
| Gravar o registro do inbox na regressão | A reentrega sairia `Duplicate` e seria confirmada: a regressão sumiria |
| Sequência igual sempre regressão | Mandaria à DLX a reentrega da última aplicada depois da retenção |
| Sequência igual sempre duplicata | Um produtor restaurado de backup perderia uma mensagem em silêncio |
| Motivo num cabeçalho, republicando na DLX | Publish e ack não atômicos; resultado mais rico só para isso |
| Membro default na interface | Quem implementa `IInbox` por fora descobriria em produção, não na compilação |
| Opção global em vez de método | A verificação é por handler; um handler sem ordem no mesmo serviço pagaria por ela |
| Recusar a chave vazia no `MessageOrder` | Transformaria em veneno uma mensagem que o próprio Waybill produz |

## Consequências

- **Membro novo no `IInbox`:** quebra quem implementa a interface fora do Waybill. O `FakeInbox` do `Waybill.Testing` tem o mesmo comportamento.
- **Uma escrita a mais por mensagem ordenada:** o upsert em `inbox_keys`. Um handler lento segura a sua chave, e a espera conta contra o timeout de comando e o `consumer_timeout`.
- **`inbox_keys` cresce:** uma linha por handler e chave já verificada.
- **Produtor recomeçado:** se o banco do produtor for recriado, tudo vira regressão até rodar o SQL `inbox-forget-keys` do `OPERATIONS.md`, com os consumidores parados.
- **A DLX do consumidor é evidência, não fila de reprocessamento:** uma regressão pode ser cópia de uma mensagem já aplicada.
- **Efeito colateral na G3:** para mensagens ordenadas, a cópia tardia depois da retenção do inbox deixa de ser reaplicada (testado). Mensagens sem `sequence` ficam com a fronteira de sempre.
- **Falha no exemplo:** uma falha do handler que dure mais que as tentativas do consumidor deixa gap na chave.

## Testes que provam

- **Integração, `G4_Inbox_*`:**
  - em ordem;
  - gap;
  - dois handlers;
  - reentrega depois da seguinte;
  - regressão depois da retenção, com os campos do log;
  - igual com o mesmo id;
  - sequência reusada;
  - liberada tardia;
  - verificação desligada e mensagem sem ordem, com `inbox_keys` travada por outra sessão;
  - handler que falha;
  - commit ambíguo, também com a seguinte no meio;
  - concorrentes da mesma chave, nas duas ordens, com chave nova e existente;
  - primeira de chave nova desfeita;
  - handlers que enfileiram ordenadas sem deadlock.
- **Broker real:**
  - `G4_RabbitMq_RequeueDepoisDeFalha`: a devolvida volta antes, e o `nack` não conta entregas;
  - `G4_RabbitMq_RegressaoChegaADlx`: a regressão chega à fila de mortas, e com ela cheia fica retida na origem.
- **Caos:** `G4_Propriedade_RegressaoNaDlx`, com zumbis, retenção quase zero e cópias injetadas: nada fora de ordem, toda mensagem aplicada uma vez, a DLX com exatamente as regressões.
- **Unidade e equivalência:**
  - `MessageOrderTests` e `GetWaybillOrderTests`, com ida e volta pelo transporte e chave vazia;
  - `FakeInboxEquivalenceTests`.
- **Schema e operação:**
  - `Schema_*` (`inbox_keys`, o `Down` com e sem a tabela);
  - `G4_Retencao_NaoApagaOutboxKeysNemReleased`;
  - `Operacao_EsquecerSequenciasDoHandler`.
- **Mutações registradas nos commits:**
  - ordem dos testes;
  - trava antes do handler;
  - `<` no `WHERE`;
  - `last_message_id`;
  - rollback;
  - log;
  - `Lower` e `Reused`;
  - `to_regclass`;
  - as três do fake;
  - at-least-once;
  - a verificação inteira, pelo teste de propriedade.
