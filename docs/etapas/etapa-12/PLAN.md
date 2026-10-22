# Etapa 12 — Plano e critérios de aceite

Escrito com as recomendações do SPEC como premissa; o autor confirmou todas em Oct 22, 2026. As linhas que dependiam disso levam (D).

## Desenho

| Tema | Como |
| --- | --- |
| G4 no `GUARANTEES.md` (D) | Título `## G4. Messages of one aggregate key reach the broker first in commit order, and a consumer that verifies sequences never applies one out of order`. Abre com "Holds when ordering is on (`OrderByKey`)" e duas listas. **Publication:** primeira cópia em ordem de commit, a partir da primeira `sequence` (propriedades `G4_Propriedade_PrimeiraEntregaEmOrdem_*`, `G4_OrdenacaoLigadaComBacklog_OrdemValeAPartirDaPrimeiraSequence`, `G4_SequenciaContigua_*` e as mesmas chaves numa transação); a troca de dono de partição e o zumbi fenceados no banco; as rodadas com M ≥ 2; uma mensagem na DLQ para só a sua chave. **Consumer:** `ProcessInOrderAsync` deduplica e depois compara; regressão volta como `Regressed`, sem efeito nem registro, e chega à DLX (`G4_Propriedade_RegressaoNaDlx_*`, `G4_RabbitMq_*`); reentrega depois da seguinte é duplicata; assinatura parcial com gap não prende; regressão depois da retenção do inbox não é aplicada. Depois, "Requires from the broker" (um consumidor ativo por fila, dead-lettering at-least-once, com os testes do RabbitMQ) e "Not covered" (cópias repetidas depois da seguinte, a lacuna da mensagem liberada, a ordem estrita, mensagens sem `sequence`). Cada item com o teste pelo nome e link para o arquivo. A fonte é a linha G4 e o Contrato técnico do escopo e os ADRs 0007, 0008 e 0010 |
| Rastreabilidade | `Garantias_CadaGarantiaTemTeste` espera `["G1", "G2", "G3", "G4"]`. A regra "a garantia cita ao menos um teste com o próprio prefixo, e todo teste `Gn_` citado na seção é dela" já serve à G4. Os testes de outra família citados nas condições (`Schema_*`, `Operacao_*`) passam como hoje |
| "What Waybill does not promise" e "Operating over time" (D) | A linha "Order" vira as quatro do escopo, com o porquê de cada uma. "Operating over time" ganha um parágrafo da ordenação com os dois testes do SPEC; os números ficam no `OPERATIONS.md` |
| README | Status `0.2.0-alpha`. Garantias com a G4 em uma frase e a ressalva da primeira chegada. Seção nova "Ordering by key", depois de "Consuming with the inbox": o que promete, como ligar (`OrderByKey`, chave no `Enqueue`, `ProcessInOrderAsync` no consumidor, single active consumer e dead-lettering at-least-once na fila), quando não ligar (sem necessidade de ordem; custo no claim, com link para "What it costs"), e o que fazer com uma chave parada (link para o `OPERATIONS.md`). Os trechos de código da seção compilam num teste, como o da API em dez linhas. "When not to use" e a comparação sem "planned for 0.2" |
| Trecho da ordenação compila | `Readme_Ordenacao_Compila`: o trecho entre marcadores do README (como `<!-- api -->`) é copiado num teste e compila contra o `FakeOutbox` e o `FakeInbox` |
| Comparação (D) | Projeto descartável com os pacotes do feed local da `0.2.0-alpha`; `dotnet list package --include-transitive` dá a contagem do Waybill. Wolverine e CAP: reconferir versões e células nas fontes; mudou, atualiza com link; o "as of" leva a data nova |
| Revisão dos ADRs 0006 a 0011 | Ler cada um contra o código. Texto divergente: corrigir o ADR. Comportamento divergente: teste que falha, depois a correção. O 0009 só confere que o escopo, o plano e o README dizem o mesmo sobre o Kafka |
| Escopo | Linha G4 sem "vai para o `GUARANTEES.md` na etapa 12"; "Latência fixa" sem o benchmark da v0.2; o que a revisão dos ADRs pedir |
| `OPERATIONS.md` | Releitura contra o código final e contra o README novo; nenhuma seção nova prevista |
| CHANGELOG | `[0.2.0-alpha] - <data>`, agrupado por tema: "Upgrading from 0.1.0-alpha" no topo (ordem: migrar o schema antes de subir quem enfileira; a quebra do `IInbox`), depois "Ordering by key", "Sequence verification in the inbox", "Tracing and metrics", "Unroutable messages", "Fixed". Saem as notas entre builds não publicados. Links de comparação no pé |
| PublicAPI (D) | Conteúdo dos `Unshipped` acrescentado aos `Shipped`, os `Unshipped` ficam só com `#nullable enable`. O build com `TreatWarningsAsErrors` confere |
| Release pipeline | Sem mudança prevista. A tag `v0.2.0-alpha-test.1` valida o caminho inteiro com o código da release |
| Estranho (D) | Agente `general-purpose` sem contexto, com o README, o `OPERATIONS.md` e um feed local (`0.2.0-alpha.local.N`, um N por pack, `globalPackagesFolder` próprio). Três tarefas do SPEC; a 3 instala a `0.1.0-alpha` do nuget.org primeiro |
| Release (D) | Branch `release/0.2.0-alpha` da `develop` com o commit que data o CHANGELOG e move o PublicAPI; PR para a `main`. Antes da tag: um agendado verde com o código da release (se o das 08:00 UTC não cobrir, `gh workflow run scheduled.yml --ref release/0.2.0-alpha`). Tag assinada por SSH, `git tag -v`, push só com ok do autor. Validação dos pacotes do nuget.org com `meziantou.validate-nuget-package`. Back-merge por `chore/back-merge-v0.2.0-alpha`. Releases no GitHub com ok do autor |

## Cenários

| Teste ou verificação | Cenário | Resultado esperado | Onde roda |
| --- | --- | --- | --- |
| `Garantias_CadaGarantiaTemTeste` | `GUARANTEES.md` com G1 a G4; antes do texto, uma G4 sem teste citado | Falha nomeando a G4; passa com o texto final | Unit (PR) |
| `Garantias_CadaTesteCitadoExiste` | Cada nome citado na G4; antes do texto final, um nome inventado | Falha com o nome que falta; passa com o texto final | Unit (PR) |
| `Readme_ApiEmDezLinhas_Compila` | O trecho da API depois da reescrita do README | Continua compilando, com as linhas do Waybill em dez | Unit (PR) |
| `Readme_Ordenacao_Compila` | O trecho da seção "Ordering by key" | Compila e enfileira com chave contra o `FakeOutbox`; processa em ordem contra o `FakeInbox` | Unit (PR) |
| `Schema_UpgradeDaV01ComBacklog_LinhasContinuamReivindicaveis` | Banco da última migration da `0.1.0-alpha` com 20 pendentes recebe a migration final | Nenhuma linha reescrita; o backlog drena | Integration (PR) |
| Revisão dos ADRs | 0006 a 0011 contra o código | Cada divergência corrigida no ADR, ou com teste e correção | Manual, registrada no PR |
| Tag `-test` | `v0.2.0-alpha-test.1` | Pipeline verde; pacotes instalam do feed local; Source Link e símbolos validam | `release.yml` |
| Estranho | As três tarefas do SPEC | Primeiro evento, ordenação ligada e upgrade da `0.1.0-alpha` feitos só com a documentação; fricção registrada | Manual, antes do PR |
| Agendado | Caos longo e carga longa de 5 h com o código da release | Verde | `scheduled.yml` |
| Release real | Tag assinada `v0.2.0-alpha` | Quatro pacotes no nuget.org; tag "Verified"; Source Link resolve o commit da tag | `release.yml`, com ok do autor |

## Pronto quando (do plano)

- [ ] Cada linha nova do `GUARANTEES.md` aponta para um teste nomeado, e a rastreabilidade garante isso no CI
- [ ] O upgrade de um banco da `0.1.0-alpha` com backlog pendente passa, agora com a migration final
- [ ] A `0.2.0-alpha` está no nuget.org com Source Link e símbolos funcionando, em todos os pacotes

## Ordem

1. Rastreabilidade esperando G4 (falha) e a G4 no `GUARANTEES.md` (passa), com "does not promise" e "Operating over time".
2. Revisão dos ADRs 0006 a 0011 e do escopo. Pode mudar o texto da G4, por isso vem antes do README.
3. README, com a seção da ordenação, `Readme_Ordenacao_Compila` e a comparação remedida.
4. Releitura do `OPERATIONS.md`; CHANGELOG.
5. Verificação completa, revisão independente, tag `-test`, estranho, PR para a `develop`.
6. Depois do merge: branch de release (CHANGELOG datado, PublicAPI Shipped), PR para a `main`, agendado verde, e com o ok do autor a tag, a validação do nuget.org, o back-merge e as releases no GitHub.
