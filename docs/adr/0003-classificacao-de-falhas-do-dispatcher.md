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
| Tipo não registrado, serialização, payload acima do `MaxPayloadBytes`, nome, `correlation_id`, chave de agregado ou `tenant_id` acima de 255 bytes | Erro de programação ou de configuração | Recusada no `Enqueue` | Nunca chega à tabela (ADR 0002) |
| Linha gravada sob um limite maior que o atual | Limite reduzido depois | Defeito (`Defect`) | DLQ com motivo, sem tocar o broker |
| Mensagem que o broker não consegue carregar | Propriedade AMQP impossível; mensagem que, sozinha e num canal só dela, faz o broker fechar o canal com 406 (`max_message_size`); publicação que falha com a conexão e o canal ainda de pé | Defeito (`Defect`) | DLQ com motivo |
| `basic.return` (sem rota, com `mandatory`) | Binding ausente | `Returned` | Gasta uma de `MaxReturns`; na última, DLQ com o `ReplyText` |
| Conexão ou canal | Broker fora, rede, canal fechado | `Retry` / `Connection` | Devolve sem gastar tentativa; **abre o circuit breaker** |
| Confirmação que não chega no `PublishTimeout` | Broker lento | `Retry` / `ConfirmTimeout` | Devolve sem gastar tentativa; **lote cai à metade**; breaker fechado |
| Nack, ou `Retry` sem causa (`Unspecified`) | Back-pressure do broker; transporte que não diz a causa | `Retry` / `Nacked` ou `Unspecified` | Igual ao timeout |
| Pressão seguida com o lote já em 1 (timeout, nack ou `Retry` sem causa; `SilentOutageThreshold` = 3) | Rede que descarta pacotes em silêncio, sem erro de conexão | Queda | **Abre o circuit breaker** |
| Resultado inválido do transporte (`default`, status desconhecido) | Bug no transporte | `Retry` | Nunca conta como confirmado; conta como pressão |
| Exceção do transporte, ou número de resultados diferente do lote | Bug no transporte, ou falha que ele não classificou | `Retry` / `Connection` | Igual à falha de conexão: **abre o circuit breaker** |

**Circuit breaker.** Só falha de conexão ou de canal abre, e também a queda silenciosa (três ciclos seguidos de pressão com lote 1). Aberto, o dispatcher não reivindica nada: o backlog fica intacto na tabela, em vez de ciclar por claims e devoluções. Passado o período, fica meio aberto e sonda com uma mensagem; se ela passa, fecha; se falha, por conexão, timeout ou nack, reabre pelo dobro do tempo, de `PollingInterval` até 30 s. Um ciclo que não chegou ao broker (só defeitos locais) não fecha nem abre o breaker, nem mexe no lote.

**Redução de lote.** Timeout de confirmação, nack e `Retry` sem causa são pressão, não queda: o lote cai à metade (até 1) sem abrir o breaker, e dobra de volta a cada lote saudável até o `BatchSize`. Assim um broker lento nunca faz o breaker oscilar.

**Isolamento da mensagem que fecha o canal.** Quando o broker fecha o canal com 406 no meio de um lote, com a conexão ainda de pé, todas as publicações não confirmadas falham juntas e o cliente não diz qual mensagem causou. O transporte republica essas mensagens uma a uma, cada uma num canal novo só dela (nunca o canal compartilhado, que pode carregar o fechamento de outra mensagem); só a que fecha o canal sozinha vira `Defect`. O isolamento respeita o `PublishTimeout`: o que não couber volta como `ConfirmTimeout`. Outros códigos de fechamento e quedas de conexão continuam `Retry`, porque uma falha transitória confundida com defeito mandaria à DLQ uma mensagem boa.

## Consequências

- Broker fora do ar por horas: nada vai à DLQ e nenhuma tentativa é gasta; a tabela cresce (documentado em "O que o pacote não resolve") e drena sozinha quando o broker volta. Provado com 15 s no PR e 1 h no job agendado (Toxiproxy).
- Broker lento: o lote cai até 1 e volta; o breaker não abre. Provado com latência acima do `PublishTimeout`.
- A republicação um a um pode duplicar as mensagens que chegaram ao broker sem confirmação antes do fechamento do canal, o que G2 permite.
- O breaker e o lote são por instância de dispatcher; várias instâncias decidem cada uma por si.
- Falha do banco (não do broker) segue com o backoff próprio do serviço hospedado, também até 30 s.

## Testes que provam

`Breaker_*`, `Lote_*` (unidade, relógio falso); `G2_FalhaDeTransporte_ReabreSemGastarTentativa`, `G2_TimeoutDeConfirmacaoOuNack_ReduzLoteSemAbrirBreaker`, `G2_ResultadoInvalidoDoTransporte_NuncaContaComoConfirmado`, `G2_PayloadAcimaDoLimiteAtual_SoElaVaiParaDlq`, `G2_Returned_OrcamentoProprioDepoisDlq`, `G2_RabbitMq_SemRota_ReturnedAteDlq`, `G2_RabbitMq_MensagemInexprimivelEmAmqp_VaiParaDlq`, `G2_MensagemAcimaDoMaxMessageSizeDoBroker_IsoladaUmAUm`, `G2_SondaSoComDefeitoLocal_NaoFechaOBreaker`, `G2_QuedaSilenciosa_TimeoutsComLoteDe1AbremOBreaker`, `G2_QuedaSilenciosa_SondaQueEstouraOTimeout_ReabrePeloDobro` (integração); `G2_BrokerParado_NadaNaDlqEDrenaSozinho`, `G2_LatenciaAlta_BreakerFechadoLoteReduzido`, `G2_ConexaoDerrubadaNoMeioDaPublicacao_NadaSePerde`, `G2_BuracoNegro_TimeoutsAbremOBreakerSemDlq` (caos).

## Revisão (2026-10-04)

A revisão de código independente desta etapa encontrou e motivou:

- **Bloqueio pela cabeça da fila.** A sonda do breaker meio aberto é sempre a linha mais antiga. Uma linha que falhasse de forma determinística classificada como `Connection` manteria o breaker reabrindo para sempre e nada sairia. Por isso uma falha de publicação com a conexão e o canal de pé é `Defect`, e chave de agregado e `tenant_id` têm o limite de 255 bytes no `Enqueue`.
- **Breaker fechado sem falar com o broker** num ciclo só de defeitos locais: corrigido (o ciclo não mexe no breaker).
- **Queda silenciosa** nunca abria o breaker: regra dos três timeouts com lote 1, provada com o toxic de buraco negro do Toxiproxy.
- **`Retry` sem causa** abria o breaker sem aviso: passou a ser pressão; só `Connection` explícito abre.

## Revisão da etapa 7 (2026-10-05)

Revisado contra o código final.

- **Sonda que falhava por timeout fechava o breaker.** Numa queda silenciosa, a sonda meio aberta dava timeout e o dispatcher chamava `RecordSuccess`: o breaker fechava, o período nunca dobrava e o backlog voltava a ciclar por claims e devoluções a cada três timeouts. Corrigido: qualquer `Retry` da sonda reabre pelo dobro. Teste: `G2_QuedaSilenciosa_SondaQueEstouraOTimeout_ReabrePeloDobro`.
- **Texto:** a queda silenciosa conta qualquer pressão com lote 1, não só timeout; exceção do transporte e contagem errada de resultados abrem o breaker como falha de conexão.
