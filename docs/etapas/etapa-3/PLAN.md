# Etapa 3 — Plano e critérios de aceite

## 3a — dispatcher e claim (transporte falso, PostgreSQL real)

| Teste | Cenário | Resultado esperado | Onde roda |
| --- | --- | --- | --- |
| `G2_DispatcherPublicaEMarca` | Mensagens pendentes, transporte confirma | Todas `published`, `fence` 1, `attempts` 0, ordem de `id` respeitada no lote | PR |
| `G2_FalhaDeTransporte_ReabreSemGastarTentativa` | Transporte falha (exceção ou `Retry`), depois volta | Devolvidas na hora, `attempts` 0, publicadas depois | PR |
| `G2_PublicacaoAlemDoTimeout_ResultadoDesconhecidoDevolve` | Transporte não responde até o `PublishTimeout` | Lote devolvido antes do lease vencer; publicado de novo depois (duplicata permitida) | PR |
| `G2_PayloadAcimaDoLimiteAtual_SoElaVaiParaDlq` | Lote de cem; uma linha gravada sob limite maior que o atual | Só ela vai para a DLQ com motivo, sem tocar o transporte; as outras 99 publicam | PR |
| `G2_Returned_OrcamentoProprioDepoisDlq` | Transporte devolve `Returned` sempre | `attempts` sobe a cada retorno; na `MaxReturns`ª vai para a DLQ com motivo | PR |
| `G2_MesmaInstanciaReivindicaDeNovo_FenceBarraMarcacaoAntiga` | Lease vence com a marcação ainda por fazer; a mesma instância reivindica de novo | Marcação e devolução do ciclo antigo afetam 0 linhas | PR |
| `G2_KillDuranteAPublicacao_VoltaAposLease` | Processo do dispatcher morto com o lote reivindicado | Lote volta após o lease, nunca antes | PR (caos curto) |
| `G2_KillComClaimAberto_VoltaNaHora` | Processo morto com a transação de claim aberta | Linhas voltam assim que o backend aborta | PR (caos curto) |
| `G2_ShutdownGracioso_DevolveOQueNaoPublicou` | `StopAsync` com lote em voo e linhas reivindicadas | Nada fica `claimed` por este `owner` | PR |
| `G2_OitoDispatchers_NenhumaLinhaComDoisLeasesValidos` | 8 dispatchers, lease curto, transporte com atrasos e falhas | Todas as instâncias com trabalho; oráculo de auditoria (trigger) sem sobreposição; nada perdido | PR: 20 s; agendado: 10 min |

## 3b e 3c

Detalhados no início de cada PR, a partir de `docs/plano.md`, Etapa 3.

## Pronto quando (3a)

- Os cenários acima passam contra PostgreSQL real (15 e 18 no CI).
- O dispatcher roda como `BackgroundService` com o transporte falso.
- API pública declarada; CHANGELOG atualizado.

## 3b — transporte RabbitMQ (RabbitMQ e PostgreSQL reais)

| Teste | Cenário | Resultado esperado |
| --- | --- | --- |
| `G2_RabbitMq_PublicaComConfirmacaoEPropriedades` | Mensagens da outbox publicadas na exchange `events` (topic), fila quorum com bind `billing.#` | Chegam com routing key = nome, `message_id`, `type`, `content_type`, entrega persistente e headers de trace e correlação; linhas `published` |
| `G2_RabbitMq_SemRota_ReturnedAteDlq` | Nenhum bind casa com o nome (`mandatory`) | `basic.return` a cada ciclo; na `MaxReturns`ª, DLQ com o `ReplyText` (`NO_ROUTE`) |
| `G2_RabbitMq_ExchangeInexistente_RetrySemGastarTentativa` | A exchange configurada não existe | `Retry` sem gastar tentativa, log de erro; criada a exchange, publica |
| `G2_RabbitMq_PontaAPonta_ComOHost` | Só API pública: `AddWaybill`, `AddWaybillOutbox`, `AddWaybillDispatcher`, `AddWaybillRabbitMQ`, host rodando | O evento gravado com o dado chega à fila |

## 3c — classificação de falhas, breaker e caos

| Teste | Cenário | Resultado esperado | Onde roda |
| --- | --- | --- | --- |
| `Breaker_*`, `Lote_*` (unidade) | Sequências de falhas com relógio falso | Abre só com falha de conexão; meio aberto sonda com 1; lote cai à metade no timeout e volta dobrando | PR |
| `G2_MensagemAcimaDoMaxMessageSizeDoBroker_IsoladaUmAUm` | Lote de cem; uma mensagem entre o limite local e o `max_message_size` do broker | O canal fecha; o lote é republicado um a um em canal novo; só ela vai para a DLQ; as 99 publicam | PR |
| `G2_BrokerParado_NadaNaDlqEDrenaSozinho` | Toxiproxy derruba o caminho até o RabbitMQ com backlog | Breaker abre, nada vai para a DLQ, nenhuma tentativa gasta; voltando o broker, drena sozinho | PR: 20 s; agendado: 1 h |
| `G2_LatenciaAlta_BreakerFechadoLoteReduzido` | Latência acima do `PublishTimeout` | Breaker nunca abre; lote cai a 1; sem DLQ; sem a latência, publica tudo e o lote volta | PR |
| `G2_ConexaoDerrubadaNoMeioDaPublicacao_NadaSePerde` | Toxiproxy corta a conexão com um lote grande em voo | Nada se perde, no máximo duplica; nenhuma tentativa gasta | PR |
