# Etapa 8 — Plano e critérios de aceite

## 8a — espaçamento do `basic.return` (PostgreSQL real, transporte falso salvo indicação)

| Teste | Cenário | Resultado esperado | Onde roda |
| --- | --- | --- | --- |
| `G2_Returned_EspacamentoCrescenteAteOTeto` | Transporte devolve `Returned` sempre; `ReturnBackoff` e `MaxReturnBackoff` pequenos; a espera é pulada por SQL entre os ciclos | Depois do k-ésimo retorno, `next_attempt_at − clock_timestamp()` ≈ `min(base × 2^(k−1), teto)`; na `MaxReturns`ª, DLQ com o motivo | PR |
| `G2_Returned_LinhaEmEsperaNaoEReivindicada` | Uma linha em espera e outras pendentes | O claim leva as outras e não leva a que espera; vencida a espera, ela é reivindicada | PR |
| `G2_FalhaDeTransporteDepoisDeUmReturn_NaoReiniciaAProgressao` | `Returned`, espera vencida, `Retry` (timeout de confirmação, para não abrir o breaker), depois `Returned` de novo | O `Retry` não gasta tentativa e reabre na hora; o segundo retorno espera `2 × base`, não `base` | PR |
| `G2_Returned_MuitasTentativas_IntervaloNaoEstoura` | `MaxReturns` = 1000, linha com 998 tentativas gastas, `ReturnBackoff` de um dia | Nenhum erro de `interval out of range`; a espera fica no teto | PR |
| `G2_DezMilEmEspera_ClaimContinuaFluindo` | Dez mil linhas em espera com ids menores que cem pendentes | As pendentes são reivindicadas; latência do claim registrada na saída do teste. Se o p50 passar de 50 ms, a 8a ganha índice próprio antes do PR (decisão registrada no ADR) | PR |
| `G2_Returned_OrcamentoProprioDepoisDlq_ComMotivo` (existente) | Ajustado: `ReturnBackoff` = 0 | Orçamento inalterado; a linha volta reivindicável na hora, como na v0.1 | PR |
| `G2_RabbitMq_SemRota_ReturnedAteDlq_ComNoRoute` (existente) | Ajustado: pula a espera por SQL | `NO_ROUTE` até a DLQ, agora espaçado | PR |
| `Operacao_ReenfileirarDaDlq` (existente) | Ajustado: pula a espera entre os retornos. O SQL do `OPERATIONS.md` não muda, porque a linha na DLQ tem `next_attempt_at` nula | A mensagem reenfileirada sai na hora | PR |
| `G2_OitoDispatchers_NenhumaLinhaComDoisLeasesValidos` (existente, caos) | Ampliado: o transporte também devolve `Returned`, e o oráculo registra claim de linha ainda em espera | Nenhum claim durante a espera; só retornos gastam tentativa | PR: curto; agendado: 10 min |
| `Schema_UpgradeDaV01ComBacklog_LinhasContinuamReivindicaveis` | Banco com o schema da `0.1.0-alpha` e pendentes; aplica a migration da v0.2 | Migration aditiva; `next_attempt_at` nula; o backlog drena | PR |
| `Opcoes_*` (unidade) | `ReturnBackoff` negativo; `MaxReturnBackoff` menor que `ReturnBackoff` | O host não sobe, com mensagem que diz o que mudar | PR |

**Armadilhas da 8a**

- `interval × 2^k` estoura com k grande: o expoente é limitado antes da multiplicação, no SQL. "Intervalos sem teto" está na lista de bugs que não podem voltar.
- A espera vem do relógio do banco, como o lease. Os testes não esperam o relógio passar: avançam a linha por SQL (`UPDATE … SET next_attempt_at = clock_timestamp()`).
- O predicado novo vai nos dois níveis do claim. Verificado por mutação: tirar só do CTE é pego pelo teste de dez mil linhas em espera; tirar dos dois níveis, pelo oráculo de caos; tirar só do `UPDATE` externo não tem teste determinístico (janela de corrida), como o predicado de status (ADR 0006).

## 8b — lease por partição

**Desenho**

| Tema | Decisão |
| --- | --- |
| Opções | `WaybillOptions.OrderByKey` (desligada por padrão), lida pelo dispatcher nesta PR e pelo enfileiramento na 8c. `WaybillDispatcherOptions.Partitions` (P, default 16, de 1 a 1024) e `PartitionLease` (default 60 s, pelo menos o dobro do lease da linha, no máximo um dia) |
| Configuração global | Tabela `waybill.settings`, de uma linha, que existe só enquanto a ordenação está ligada: P e `PartitionLease`. A primeira instância com a ordenação ligada cria a linha e as P linhas de `outbox_partitions` num comando só (`INSERT … ON CONFLICT DO NOTHING`); as outras comparam. Divergência é erro crítico, como a verificação de `READ COMMITTED`: o laço para e o health check acusa. Uma instância com a ordenação desligada que encontra a linha também para, no startup e a cada `PartitionLease`, porque reivindicaria sem filtro de partição ao lado de quem ordena. Ligar, desligar ou mudar P exige parar todos os dispatchers; o SQL fica no `OPERATIONS.md` |
| Tabelas | `outbox_partitions (partition, owner, epoch, lease_until)` e `outbox_instances (owner, heartbeat_at)` |
| Ciclo | No início de cada ciclo, inclusive com o breaker aberto: heartbeat da instância; renovação das partições que ainda são dela (`owner = eu`, lease não vencido); cálculo da fatia justa, `ceil(P / instâncias vivas)`; aquisição de partições livres ou vencidas até a fatia (`FOR UPDATE SKIP LOCKED`, condição repetida no `UPDATE` externo, `epoch + 1`); devolução das que passam da fatia. Instância viva = heartbeat mais novo que `PartitionLease`; a coleta apaga as mais velhas que dez vezes isso. A devolução só acontece entre lotes, sem nada em voo |
| Claim | Ganha `key IS NULL OR EXISTS (posse válida da partição key_hash % P por esta instância, com lease_until > clock_timestamp() + lease da linha)`. Mensagem sem chave não é ordenada e não espera partição |
| Por que um `EXISTS` na tabela de posse | O SPEC previa só `key_hash % P = ANY(@minhas)`, sem tocar a tabela de posse. Não basta: uma instância pausada (GC, VM congelada) depois de renovar pode acordar com `@minhas` velho, depois de outra ter assumido a partição, e as duas reivindicariam a mesma chave ao mesmo tempo, que é a inversão do spike com M ≥ 2. O `EXISTS` lê a posse no snapshot do comando. A armadilha do ADR 0001 (a reavaliação do `FOR UPDATE` usa a tupla antiga das outras tabelas) não morde aqui: nenhuma outra instância pode assumir a partição antes do `lease_until`, e o claim exige que ele dure mais que o lease da linha a partir do relógio do próprio comando |
| `epoch` | Identifica um mandato: sobe a cada troca de dono. Não entra no fencing da linha, porque o fencing da linha (`owner`, `fence`) e o `EXISTS` acima já impedem claim fora do mandato e marcação atrasada. Serve ao transporte (o producer por partição da etapa 9 é recriado quando o `epoch` muda) e ao diagnóstico. Vai para o ADR 0007 com essa prova |
| Shutdown | Devolve as partições e apaga a própria linha de instância depois de devolver as linhas; a fatia é redistribuída no ciclo seguinte de quem fica, sem esperar o lease |

**Testes que provam**

| Teste | Cenário | Resultado esperado | Onde roda |
| --- | --- | --- | --- |
| `G4_OrdenacaoDesligada_ClaimDaV01SemTabelasDePosse` | Ordenação desligada | Nenhuma linha em `settings`, `outbox_partitions` ou `outbox_instances`; claim como antes | PR |
| `G4_PrimeiraInstancia_CriaConfiguracaoEParticoes` | Primeira instância com a ordenação ligada | `settings` com P e lease; P partições; a instância fica com todas | PR |
| `G4_PDiferenteDoBanco_ErroCriticoENaoReivindica` | Segunda instância com P ou `PartitionLease` diferente | Erro crítico que diz o que mudar; nenhuma linha reivindicada; health check `Unhealthy` | PR |
| `G4_OrdenacaoDesligadaComConfiguracaoNoBanco_Para` | Instância sem ordenação diante de `settings` existente, no startup e depois de criada com ela rodando | Para com erro crítico nos dois casos | PR |
| `G4_ClaimSoDasParticoesDaInstancia` | Duas instâncias, chaves espalhadas, mais mensagens sem chave | Cada uma publica só as chaves das próprias partições; as sem chave saem por qualquer uma | PR |
| `G4_DonoAntigoDaParticao_NaoReivindica` | A instância renova, a partição vence por SQL, outra a assume, e a primeira reivindica com a lista velha | Nenhuma linha daquela partição vai para a primeira | PR |
| `G4_FatiaJusta_ConvergeAoEntrarESair` | P = 16; três instâncias entram, uma sai com shutdown, outra morre | Cada uma com no máximo `ceil(P / N)` e todas as P com dono (6/6/4); depois 8/8 já no ciclo seguinte ao shutdown; as partições da morta voltam depois do `PartitionLease`; a morta sai de `outbox_instances` pela coleta | PR |
| `G4_EntradaESaidaSobCarga_NenhumaParticaoComDoisDonos` | Oito instâncias entrando, saindo e morrendo durante a carga, lease curto | Oráculo por trigger em `outbox_partitions`: nenhum par de mandatos válidos sobrepostos na mesma partição; nada perdido | PR: 20 s; agendado: 10 min (caos) |
| `Operacao_ReiniciarOrdenacao` | O SQL de limpeza do `OPERATIONS.md`, executado como está escrito | Depois dele, sobem uma instância com outro P e uma sem ordenação | PR |
| `G4_InstanciasSobemJuntas_TodasLeemAConfiguracao` (da revisão) | Dezesseis instâncias criam a configuração ao mesmo tempo, cinco rodadas | Todas leem a mesma configuração; nenhuma falha | PR |
| `G4_EsperaEntreCiclosMaiorQueOLease_NaoPerdeParticoes` (da revisão) | `PollingInterval` de 5 s com `PartitionLease` de 700 ms, ocioso | As partições seguem no primeiro mandato | PR |
| `G4_LotesCheiosEmSequencia_ManutencaoEspacada` (da revisão) | Dez lotes cheios em sequência | Uma manutenção só | PR |
| `Dispatcher_PartitionLease_SoValidadoComOrdenacao`, `Dispatcher_IntervaloForaDoLimite_*` (unidade) | P fora de 1 a 1024; `PartitionLease` menor que o dobro do lease da linha, só com a ordenação ligada | O host não sobe, com mensagem que diz o que mudar | PR |

**Fora da 8b:** filtro de cabeça, contador, `sequence`, M, liberação de chave e o teste de propriedade de ordem (8c). Com a ordenação ligada e só a 8b, as partições já valem, mas a ordem ainda não é garantida; nada disso sai em release antes da 8c.

## 8c

Detalhado no início do PR, a partir de `docs/plano-v0.2.md`, Etapa 8, e das decisões do `SPEC.md`.

## Pronto quando (8a)

- Os cenários acima passam contra PostgreSQL real (15 e 18 no CI).
- ADR 0006 escrito: espaçamento do `basic.return` e a interação com a cabeça por chave.
- `OPERATIONS.md` com as opções novas e o tempo total até a DLQ; escopo (G2 e Contrato técnico), ADR 0003 e `GUARANTEES.md` atualizados; API pública declarada; CHANGELOG atualizado.
