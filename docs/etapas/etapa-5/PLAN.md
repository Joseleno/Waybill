# Etapa 5 — Plano e critérios de aceite

## Desenho

| Tema | Como |
| --- | --- |
| Limpeza do outbox | `DELETE … WHERE id IN (SELECT id … LIMIT @lote FOR UPDATE SKIP LOCKED)`, repetido enquanto o lote vier cheio. O filtro é `status = 'published' AND published_at < clock_timestamp() - @retencao`. Para varrer pela PK sem índice novo, ele soma `id < uuid_do_corte`: o UUIDv7 nunca é posterior ao commit, então o filtro só estreita a busca. Quem decide é o `published_at`. Um relógio do cliente adiantado só atrasa a limpeza; um atrasado não a antecipa, porque o `published_at` barra |
| Limpeza do inbox | `processed_at < clock_timestamp() - @retencao`, em lotes, com `SKIP LOCKED`. O `message_id` pode não ser UUIDv7 (mensagem de outro sistema), então entra o índice `ix_inbox_processed_at` numa migration nova do pacote. O inbox só recebe INSERT, e o índice custa uma entrada por linha |
| Serviço de limpeza | `AddWaybillRetention(options)` registra um `BackgroundService` próprio, com data source próprio. Opções: `ConnectionString` (obrigatória), `OutboxRetention` (7 dias), `InboxRetention` (30 dias), `Interval` (5 min), `BatchSize` (1000). Todas são validadas na partida, positivas, sem piso mínimo. Falha de banco gera log e espera o próximo intervalo, nunca derruba o host |
| Métrica | Um amostrador registrado por `AddWaybillDispatcher` roda a cada `MetricsInterval` (15 s, nova opção do dispatcher): `SELECT clock_timestamp() - created_at … WHERE status IN ('pending','claimed') ORDER BY id LIMIT 1`. Usa o índice `ix_outbox_claimable`, que já está na ordem do claim. A idade sai do relógio do banco, sem misturar com o da aplicação. O `Meter` vem do `IMeterFactory` quando registrado, senão é criado e descartado pelo amostrador. Se a consulta falha, gera log e o gauge mantém o último valor |
| Estado do dispatcher | O `WaybillDispatcherService` atualiza um `DispatcherStatus` interno e singleton com o resultado do último ciclo, as falhas de banco consecutivas e a hora do último ciclo concluído |
| Health check | `IHealthChecksBuilder.AddWaybillDispatcher(name = "waybill-dispatcher")`. As regras, avaliadas na ordem: (1) laço fora de execução, ou último ciclo mais antigo que `Lease + MaxBackoff`: `Unhealthy`; (2) 3 ou mais falhas de banco seguidas: `Unhealthy`; (3) breaker aberto ou último ciclo com falha de conexão: `Degraded`; (4) senão, `Healthy`. O `data` traz a idade da pendente mais antiga e o motivo |
| Carga longa | Teste `Category=Long` no projeto de integração. Transporte falso que confirma, produtores em ritmo constante e a limpeza ligada com retenção curta. A duração vem de `WAYBILL_LONG_DURATION` (default 5 h). Fases: aquecimento, linha de base, transação longa aberta (40% do tempo, segurando o horizonte do vacuum), recuperação. Grava um CSV por minuto com p95 do claim, tamanho das tabelas e `n_dead_tup`, publicado como artefato do workflow |

## Cenários

| Teste | Cenário | Resultado esperado | Onde roda |
| --- | --- | --- | --- |
| `G2_BrokerParadoAlemDaRetencao_NenhumaPendenteApagada` | Transporte com falha de conexão (breaker aberto), linhas `pending`, `claimed` e `dlq`, e `published` antigas; retenção de 2 s; várias passadas da limpeza | Só as `published` antigas somem; `pending`, `claimed` e `dlq` intactas; com o transporte de volta, todas publicam | PR |
| `Retencao_Outbox_ApagaSoPublicadasAlemDaRetencao` | Mistura de estados e idades | Só `published` com `published_at` além da retenção somem | PR |
| `Retencao_Outbox_PublicadaTarde_ContaDoPublishedAt` | `id` e `created_at` antigos, `published_at` recente | Fica até `published_at` passar da retenção | PR |
| `Retencao_Inbox_ApagaSoExpiradas` | Linhas do inbox de idades diferentes | Só as além da retenção somem | PR |
| `Retencao_MaisLinhasQueOLote_DrenaNaMesmaPassada` | 3× o lote em linhas elegíveis | Uma passada apaga todas, em lotes | PR |
| `Retencao_DuasInstanciasAoMesmoTempo_SemErroNemSobra` | Dois serviços de limpeza no mesmo banco | Nenhuma exceção, nada elegível sobra, nada inelegível some | PR |
| `Retencao_LinhasTravadasPorOutraTransacao_PulaSemEsperar` | Outra transação segura parte das linhas elegíveis | A passada apaga as outras sem esperar; as travadas saem na passada seguinte. É o que prova o `SKIP LOCKED`: sem ele, duas instâncias ainda terminam, só que em série | PR |
| `Retencao_BancoFora_LogaESegue` | Banco inacessível durante a passada | Log de erro e backoff; volta a limpar quando o banco volta | PR |
| `Inbox_ReentregaDepoisDaLimpeza_ProcessaDeNovo` | Mensagem processada, linha do inbox limpa, mensagem reentregue | Handler roda de novo. Caracteriza a fronteira da G3 que o `OPERATIONS.md` documenta; não é garantia | PR |
| `Metrica_SemPendentes_Zero` | Outbox vazio ou só `published` | Gauge em 0 | PR |
| `Metrica_BrokerParadoEReligado_CresceEVoltaAZero` | Transporte falso cai com pendentes e depois volta | Idade cresce a cada amostra; volta a 0 depois de drenar | PR |
| `Metrica_ConsultaFalha_MantemUltimoValor` | Banco cai entre amostras | Último valor mantido, log de erro; dispatcher segue | PR |
| `HealthCheck_DispatcherRodando_Healthy` | Transporte confirma | `Healthy` | PR |
| `HealthCheck_BrokerFora_Degraded` | Falha de conexão no transporte | `Degraded` com motivo; o outbox continua aceitando `Enqueue` + `SaveChanges` | PR |
| `HealthCheck_BancoFora_Unhealthy` | Três ciclos seguidos com falha de banco | `Unhealthy` | PR |
| `HealthCheck_LacoParado_Unhealthy` | Host parado | `Unhealthy` | PR |
| `BrokerParadoEReligado_MetricaEHealthCheckAcompanham` | RabbitMQ real atrás do Toxiproxy, caminho cortado com backlog e depois religado | Health check vai a `Degraded` e volta a `Healthy`; a métrica cresce e volta a 0; nada na DLQ | PR (caos curto) |
| `CargaLonga_TransacaoLongaAberta_LatenciaDoClaimEstabiliza` | Ver Desenho | Na última janela de 15 min da recuperação, depois de um autovacuum posterior ao fechamento: p95 do claim ≤ max(2× o da linha de base, linha de base + 5 ms), e o tamanho do outbox cresce no máximo 5% dentro da janela. O tamanho não volta ao da linha de base: o vacuum comum libera espaço para reuso, mas não encolhe o arquivo; o que se exige é que pare de crescer | Agendado, 5 h |

Opções inválidas, como retenção zero ou negativa, falham na partida. Isso fica num teste de unidade por opção.

## Documentos

- ADR 0004: as quatro decisões do SPEC mais o filtro por `id` da limpeza e o índice do inbox.
- `docs/OPERATIONS.md` (inglês):
  - defaults;
  - o que monitorar: idade da pendente, DLQ e health check;
  - autovacuum;
  - dimensionamento da retenção do inbox.
- Nos documentos de design, "24 h" vira "carga longa (5 h)":
  - `docs/plano.md`;
  - `docs/escopo-e-fronteiras.md` (linha da limpeza);
  - `docs/analise-de-negocio.md`;
  - comentários de `.github/workflows/scheduled.yml`.
- `scheduled.yml`:
  - a carga longa ganha um job próprio, porque o limite de 6 h vale por job e os longos atuais já somam mais de 1 h;
  - o job `long` passa a excluir a carga longa;
  - input `long_duration` no `workflow_dispatch`;
  - upload do CSV como artefato.
- README e CHANGELOG: limpeza, métrica, health check e a fronteira da G3. Nenhuma frase sem o teste correspondente.

**Pronto quando** os cenários de PR passam contra PostgreSQL 15 e 18, a carga longa tem uma execução verde no job agendado, e o ADR 0004 e o `OPERATIONS.md` existem.
