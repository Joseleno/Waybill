# Etapa 10 — Verificação de sequência no inbox

Derivado de `docs/plano-v0.2.md`, seção "Etapa 10", do Contrato técnico de `docs/escopo-e-fronteiras.md` (linhas "Verificação de sequência no inbox" e "Inbox (consumidor)") e dos ADRs 0008 e 0009. Revisto em Oct 21, 2026, depois de uma revisão adversarial.

## Objetivo

Fechar a G4 do RabbitMQ no consumidor: **um handler com a verificação ligada nunca aplica, para uma chave, uma mensagem cuja `sequence` não seja posterior à última que ele aplicou nessa chave; toda mensagem recusada assim volta à aplicação como regressão, com o motivo registrado, e a aplicação a manda à DLX.** Nada é descartado em silêncio, desde que o broker cumpra as condições da seção "Condições do broker".

## Por que ainda vale fazer (premissa revista na 8c-1)

Com a etapa 8, a primeira cópia de cada mensagem chega na ordem de commit, e as cópias repetidas trazem o mesmo `message_id`, que o inbox já reconhece. Uma regressão que passa pela deduplicação só aparece em casos estreitos:

1. **Cópia tardia depois da retenção do inbox.** A retenção apagou o registro `(handler, message_id)` da original, e a cópia passa pela deduplicação.
2. **Mensagem liberada da DLQ.** Uma tentativa anterior dela ficou sem confirmação e chega depois das seguintes da chave (`OPERATIONS.md`, "A key stopped by the DLQ").
3. ~~**Janela com a ordenação desligada.**~~ Não é caso de detecção (achado na implementação, Oct 21): o claim sem ordenação não devolve a `sequence` (`OutboxStore`), então as mensagens publicadas com a ordenação desligada, inclusive as numeradas antes, saem sem `waybill-sequence`, e o consumidor só as deduplica. Desligar a ordenação é abrir mão da ordem; o `OPERATIONS.md` diz isso.
4. **Produtor restaurado de backup.** `outbox_keys` volta no tempo, e mensagens novas reusam sequências que o consumidor já aplicou.

São raros, mas sem a verificação a G4 teria de dizer "nesses casos uma mensagem pode ser aplicada fora de ordem sem ninguém saber". Com ela, nenhuma regressão é aplicada e toda regressão chega à DLX. A etapa é pequena, não traz dependência nova e é o que resta do núcleo da v0.2 depois do ADR 0009.

## Decisões de desenho

| Tema | Decisão |
| --- | --- |
| Onde fica o estado | Tabela `waybill.inbox_keys (handler, key, last_seq, last_message_id)`, chave primária `(handler, key)`, com a última `sequence` aplicada e o `message_id` dela. Gravada na mesma transação do handler, na migration única da v0.2 (`SchemaV0_2`, ainda sem tag). Nunca limpa pela retenção (regra do `CLAUDE.md`). O `Down` da migration recusa voltar quando `inbox_keys` tem linhas, como já faz com `outbox_keys` |
| Opcional, por handler | Método novo no `IInbox<TContext>`, ao lado do `ProcessAsync`. Quem chama o `ProcessAsync` de hoje tem o comportamento da v0.1 e nunca lê `inbox_keys`. É membro comum da interface, não método default: quem a implementa por fora descobre na compilação, não em produção (aceitável na 0.x). O `FakeInbox` do `Waybill.Testing` ganha o mesmo comportamento |
| Resultado | Tipo próprio para o método novo, com `Processed`, `Duplicate` e `Regressed`. O `InboxResult` do `ProcessAsync` não muda, e a frase "either way the message may be acknowledged" continua verdadeira para ele. Um `else ack` copiado do exemplo antigo não compila contra o tipo novo sem tratar `Regressed` |
| Ordem dos testes | Primeiro a deduplicação por `(handler, message_id)`, depois a sequência. Ao contrário, a reentrega legítima de uma mensagem já aplicada viraria regressão. Faz parte do contrato |
| Como a sequência é testada | Depois do `INSERT` do inbox e antes do handler: `INSERT ... ON CONFLICT (handler, key) DO UPDATE ... WHERE inbox_keys.last_seq < EXCLUDED.last_seq`. Sem linha afetada, lê a linha e decide entre duplicata e regressão (abaixo). A trava da linha da chave vale até o commit: duas entregas concorrentes da mesma chave no mesmo handler se serializam, e a segunda compara com o que a primeira gravou. Testar antes do handler faz valer a ordem de chegada. Custo: um handler lento segura a chave inteira e conta contra o `consumer_timeout`; vai no XML doc |
| Sequência igual | Com o mesmo `message_id`: duplicata (a retenção apagou o registro da original). Com outro `message_id`: regressão, motivo "sequência reusada" (produtor restaurado de backup, dois produtores no mesmo handler, `outbox_keys` apagada) |
| Regressão | O handler não roda, e a transação inteira volta, inclusive o registro do inbox: uma reentrega da mesma mensagem é de novo regressão, nunca duplicata. Quem manda à DLX é a aplicação, como no ack (o Waybill não tem laço de consumo). O exemplo faz `basic.nack` sem requeue |
| O motivo da regressão | O escopo pede "vai à DLX com motivo". O `nack` deixa na DLX só `x-death reason=rejected`, igual a qualquer recusa. O motivo fica num log `Warning` do inbox, com `EventId` estável e campos estruturados: handler, chave, `sequence` recebida, última aplicada, `message_id` e o tipo da regressão (menor, ou igual com outro id). O operador cruza pelo `message_id`. O ADR 0010 emenda a linha do escopo |
| Regressão pode já ter sido aplicada | No caso 1 da premissa, a mensagem recusada é cópia de uma que já foi aplicada. Por isso a DLX do consumidor não é fila de reprocessamento: é evidência para investigar. Reaplicar uma mensagem dela é decisão da aplicação, sabendo disso. Fica no ADR e no `OPERATIONS.md` |
| Mensagem sem `sequence` | Processada só com a deduplicação, sem ler `inbox_keys`: sem chave, escrita antes de ligar a ordenação ou publicada com ela desligada |
| Tipo da ordem no núcleo | `struct` em `src/Waybill` com a chave e a `sequence`, validada na construção: `sequence >= 1`, chave não nula e até 255 bytes UTF-8, exatamente a regra do `Enqueue`, que aceita chave vazia (revisão do PLAN: recusar a vazia transformaria em veneno uma mensagem que o próprio Waybill produz). Fica no núcleo para o pacote do RabbitMQ não depender do de EF |
| Gap | Não é erro, e o inbox não reordena. Um consumidor que assina só parte dos tipos de uma chave vê gaps legítimos, e uma liberada deixa gap. A justificativa vai para o ADR |
| Escopo da `sequence` | Contada por chave no banco do produtor (`outbox_keys`). Um handler com a verificação ligada consome as chaves de um produtor só; dois produtores pedem dois nomes de handler. A verificação é por nome de handler: um nome por tipo de mensagem na mesma chave não tem ordem entre os tipos. Os dois avisos vão no XML doc e no `OPERATIONS.md` |
| Recomeço do produtor | Se as sequências do produtor recomeçarem (banco recriado), tudo vira regressão. O `OPERATIONS.md` ganha o SQL que esquece as sequências de um handler, marcado para um teste que o executa (como `<!-- dlq-requeue -->`), com os consumidores do handler parados |
| RabbitMQ | Extensão de leitura ao lado de `GetWaybillMessageId`, que devolve a ordem dos cabeçalhos `waybill-key` e `waybill-sequence`, ou nada quando a mensagem não é ordenada. Cabeçalho malformado, ou `sequence` sem chave, é erro, como o `message_id` inválido |

## Condições do broker (parte da garantia)

"Toda regressão chega à DLX" depende de configuração que o Waybill não controla. Vão para o `OPERATIONS.md` e para o `definitions.json` do exemplo:

- **A fila tem DLX.** `nack` sem requeue numa fila sem `x-dead-letter-exchange` descarta a mensagem.
- **Dead-lettering at-least-once.** Na fila quorum, o padrão é at-most-once. Exige `x-dead-letter-strategy: at-least-once` com `x-overflow: reject-publish`.
- **A fila de mortas não descarta.** O exemplo tem `x-max-length: 10000` com o overflow padrão (`drop-head`), que apaga as mais antigas. Passa para `reject-publish`.
- **Um consumidor ativo por fila, processando em sequência.** `x-single-active-consumer: true` no exemplo, que hoje tem prefetch 1, mas nada impede uma segunda instância. Com consumidores concorrentes, o RabbitMQ já não entrega em ordem. A verificação continua segura (nada é aplicado fora de ordem), mas uma mensagem válida pode ir à DLX.

## Falha do handler

Medido na revisão do PLAN contra `rabbitmq:4-alpine` (4.3.6), com prefetch 1:

- **Requeue depois de exceção.** A mensagem devolvida com `basic.nack` e requeue voltou antes da seguinte em todas as tentativas. A documentação diz "na posição original, se possível", sem garantia, e a imagem dos testes é flutuante: um teste fixa o comportamento e avisa se ele mudar.
- **Limite de entregas.** `basic.nack` com requeue **não conta** para o `x-delivery-limit`. Só a devolução por canal fechado (queda do consumidor, `consumer_timeout`) conta. Uma mensagem que sempre falha e volta com requeue gira para sempre e, com um consumidor ativo por fila, para a fila inteira. O exemplo da v0.1 confia nesse limite, e o comentário dele está errado.
- **Política do exemplo.** Repetir dentro do consumidor, com espera crescente. Esgotadas as tentativas, `nack` sem requeue para a DLX, com log de erro. Isso deixa um gap na chave: a seguinte é aplicada, e reenviar a morta depois dá `Regressed`. É a mesma troca da DLQ do outbox, mas sem bloquear a chave, porque o consumidor não tem como bloqueá-la sem parar a fila. O `OPERATIONS.md` explica a troca e o gap, que também aparece quando o broker devolve por `consumer_timeout` até o limite.

## API candidata (o desenho final fecha no PLAN)

```csharp
// Waybill.RabbitMQ
var messageId = delivery.BasicProperties.GetWaybillMessageId();
var order = delivery.BasicProperties.GetWaybillOrder();   // MessageOrder? ; null quando não é ordenada

// Waybill.EntityFrameworkCore.PostgreSql
var result = await inbox.ProcessInOrderAsync("receipts.invoice-paid", messageId, order, handle, ct);
switch (result)
{
    case OrderedInboxResult.Processed or OrderedInboxResult.Duplicate:
        await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, ct);
        break;
    case OrderedInboxResult.Regressed:   // o inbox já registrou o motivo no log
        await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: false, ct);
        break;
}
```

## Cenários de aceite

| Cenário | Resultado esperado |
| --- | --- |
| **Propriedade no RabbitMQ**, só mensagens com chave: N dispatchers, um deles zumbi (publicação atrasada além do lease), retenção do inbox em zero, e cópias tardias injetadas depois da drenagem; um consumidor ativo, sequencial, sem falhas do handler | Por handler e chave, as sequências aplicadas crescem estritamente; o conteúdo da DLX é exatamente o conjunto das `Regressed`; nenhuma é aplicada; toda mensagem é aplicada uma vez; cada cópia injetada sai `Regressed`; regressões naturais (do zumbi) contadas à parte, e maiores que zero no job agendado. As liberadas têm teste próprio (revisão do PLAN: no teste de propriedade, seriam injeção disfarçada) |
| ~~Janela com a ordenação desligada~~ | Fora: as mensagens dessa janela saem sem `sequence` (premissa, item 3). Documentado no `OPERATIONS.md` |
| Consumidor que assina só parte dos tipos de uma chave | Não fica preso em gap |
| Reentrega de uma mensagem já aplicada, depois da seguinte | `Duplicate` |
| Regressão de mensagem cujo registro no inbox já foi limpo pela retenção | `Regressed`; o handler não roda; nada é gravado; a reentrega dá `Regressed` de novo |
| Sequência igual à última aplicada, mesmo `message_id`, sem registro no inbox | `Duplicate`; o handler não roda |
| Sequência igual à última aplicada, outro `message_id` (produtor restaurado de backup) | `Regressed`, com o motivo "sequência reusada" no log |
| Cópia tardia de uma mensagem liberada, depois das seguintes | `Regressed` |
| Duas entregas concorrentes da mesma chave no mesmo handler | Nunca a maior aplicada antes da menor; a que chega depois da maior sai como `Regressed`. É segurança, não vivacidade: o caso fora das condições do broker |
| Primeira entrega concorrente de uma chave nova, com a primeira transação desfeita | A segunda aplica normalmente |
| Commit ambíguo no método novo (perda do ack do commit, repetição da estratégia) | `Duplicate`; a chave avançou uma vez |
| Handler lança exceção | Nada gravado; a reentrega da mesma mensagem é processada |
| Handler que enfileira mensagens ordenadas no outbox na mesma transação | Sem deadlock entre `inbox_keys` e `outbox_keys` |
| Handler falha e a mensagem volta com requeue, com a seguinte da chave na fila | A devolvida volta antes da seguinte; `nack` com requeue não conta entregas, o canal fechado conta (ver "Falha do handler") |
| Cabeçalho `waybill-sequence` malformado; `sequence` sem chave | Erro na leitura, como o `message_id` inválido |
| Verificação desligada (`ProcessAsync`) | Comportamento da v0.1, sem leitura nem escrita em `inbox_keys` |
| Mensagem sem `sequence` na verificação ligada | Só deduplicação; `inbox_keys` intocada |
| Retenção | Nunca apaga `inbox_keys` |
| `Down` da migration com linhas em `inbox_keys` | Recusado |
| SQL do `OPERATIONS.md` que esquece as sequências de um handler | Depois dele, a sequência 1 volta a ser aplicada |
| `FakeInbox` | Mesmos resultados do inbox real nos cenários acima que não dependem de transação |

**Pronto quando:**
- os cenários passam;
- o exemplo mostra o consumidor mandando a regressão à DLX, com o `definitions.json` nas condições do broker;
- o ADR 0010 registra três pontos: por que o inbox detecta e não reordena, a emenda do "com motivo" e as condições do broker.

## Fora da etapa

- Reordenar ou segurar mensagens à espera de um gap: o inbox não reordena.
- API para reprocessar a DLX do consumidor: v1.0, como a da DLQ do outbox.
- Métricas de regressão: etapa 11 (OpenTelemetry), se ela ficar na v0.2.
- A `GUARANTEES.md` com a G4: etapa 12.
- Um efeito colateral provável: para mensagens ordenadas, a cópia tardia depois da retenção deixa de ser reaplicada, o que estreita a fronteira da G3. Não entra na promessa sem teste próprio.
