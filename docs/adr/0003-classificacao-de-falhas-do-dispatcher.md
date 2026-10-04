# ADR 0003 — Classificação de falhas do dispatcher

- Status: aceito
- Data: 2026-10-04
- Etapa: 3 (dispatcher e RabbitMQ, G2)

## Contexto

G2 promete que todo evento persistido é publicado pelo menos uma vez, ou vai para a DLQ do outbox por um defeito da lista fechada, com o motivo registrado, e nunca é descartado em silêncio. Para isso o dispatcher precisa separar o que é defeito da mensagem (não adianta tentar de novo) do que é falha de transporte (tentar de novo resolve), e, dentro das falhas de transporte, separar o broker fora do ar (não adianta insistir agora) do broker lento (adianta, com menos carga). Errar a classificação viola G2 nos dois sentidos: mandar à DLQ o que só esperava o broker voltar, ou repetir para sempre o que nunca vai passar.

## Decisão

Cada mensagem de um lote termina num de quatro resultados (`PublishStatus`), e cada `Retry` traz a causa (`TransportFailure`).

| Falha | Exemplos | Trata como | Efeito |
| --- | --- | --- | --- |
| Tipo não registrado, serialização, payload acima do `MaxPayloadBytes`, nome ou `correlation_id` acima de 255 bytes | Erro de programação ou de configuração | Recusada no `Enqueue` | Nunca chega à tabela (ADR 0002) |
| Linha gravada sob um limite maior que o atual | Limite reduzido depois | Defeito (`Defect`) | DLQ com motivo, sem tocar o broker |
| Mensagem que o broker não consegue carregar | Propriedade AMQP impossível; mensagem que, sozinha, faz o broker fechar o canal com 406 (`max_message_size`) | Defeito (`Defect`) | DLQ com motivo |
| `basic.return` (sem rota, com `mandatory`) | Binding ausente | `Returned` | Gasta uma de `MaxReturns`; na última, DLQ com o `ReplyText` |
| Conexão ou canal | Broker fora, rede, canal fechado | `Retry` / `Connection` | Devolve sem gastar tentativa; **abre o circuit breaker** |
| Confirmação que não chega no `PublishTimeout` | Broker lento | `Retry` / `ConfirmTimeout` | Devolve sem gastar tentativa; **lote cai à metade**; breaker fechado |
| Nack | Back-pressure do broker | `Retry` / `Nacked` | Igual ao timeout |
| Resultado inválido do transporte (`default`, status desconhecido) | Bug no transporte | `Retry` | Nunca conta como confirmado |

**Circuit breaker.** Só falha de conexão ou de canal abre (também `Unspecified`, por segurança). Aberto, o dispatcher não reivindica nada: o backlog fica intacto na tabela, em vez de ciclar por claims e devoluções. Passado o período, fica meio aberto e sonda com uma mensagem; se ela passa, fecha; se falha, reabre pelo dobro do tempo, de `PollingInterval` até 30 s.

**Redução de lote.** Timeout de confirmação e nack são pressão, não queda: o lote cai à metade (até 1) sem abrir o breaker, e dobra de volta a cada lote saudável até o `BatchSize`. Assim um broker lento nunca faz o breaker oscilar.

**Isolamento da mensagem que fecha o canal.** Quando o broker fecha o canal com 406 no meio de um lote, com a conexão ainda de pé, todas as publicações não confirmadas falham juntas e o cliente não diz qual mensagem causou. O transporte republica essas mensagens uma a uma, em canal novo; só a que fecha o canal sozinha vira `Defect`. Outros códigos de fechamento e quedas de conexão continuam `Retry`, porque uma falha transitória confundida com defeito mandaria à DLQ uma mensagem boa.

## Consequências

- Broker fora do ar por horas: nada vai à DLQ e nenhuma tentativa é gasta; a tabela cresce (documentado em "O que o pacote não resolve") e drena sozinha quando o broker volta. Provado com 15 s no PR e 1 h no job agendado (Toxiproxy).
- Broker lento: o lote cai até 1 e volta; o breaker não abre. Provado com latência acima do `PublishTimeout`.
- A republicação um a um pode duplicar as mensagens que chegaram ao broker sem confirmação antes do fechamento do canal, o que G2 permite.
- O breaker e o lote são por instância de dispatcher; várias instâncias decidem cada uma por si.
- Falha do banco (não do broker) segue com o backoff próprio do serviço hospedado, também até 30 s.

## Testes que provam

`Breaker_*`, `Lote_*` (unidade, relógio falso); `G2_FalhaDeTransporte_ReabreSemGastarTentativa`, `G2_TimeoutDeConfirmacaoOuNack_ReduzLoteSemAbrirBreaker`, `G2_ResultadoInvalidoDoTransporte_NuncaContaComoConfirmado`, `G2_PayloadAcimaDoLimiteAtual_SoElaVaiParaDlq`, `G2_Returned_OrcamentoProprioDepoisDlq`, `G2_RabbitMq_SemRota_ReturnedAteDlq`, `G2_RabbitMq_MensagemInexprimivelEmAmqp_VaiParaDlq`, `G2_MensagemAcimaDoMaxMessageSizeDoBroker_IsoladaUmAUm` (integração); `G2_BrokerParado_NadaNaDlqEDrenaSozinho`, `G2_LatenciaAlta_BreakerFechadoLoteReduzido`, `G2_ConexaoDerrubadaNoMeioDaPublicacao_NadaSePerde` (caos).
