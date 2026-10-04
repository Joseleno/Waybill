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
