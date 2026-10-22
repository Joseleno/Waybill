# Etapa 12 — Documentação e release

Derivado de `docs/plano-v0.2.md`, seção "Etapa 12", da linha G4 da seção Garantias de `docs/escopo-e-fronteiras.md` e do molde da etapa 7 (release da `0.1.0-alpha`). Rascunho de Oct 22, 2026.

## Objetivo

Escrever a G4 só agora, quando cada linha dela tem um teste atrás, e publicar a `0.2.0-alpha`. Pronto quando cada linha nova do `GUARANTEES.md` aponta para um teste nomeado, o upgrade de um banco da `0.1.0-alpha` com backlog pendente passa com a migration final, e a `0.2.0-alpha` está no nuget.org com Source Link e símbolos funcionando, em todos os pacotes.

A etapa não tem código de funcionalidade. Código só entra como teste de rastreabilidade ou como correção de uma divergência achada na revisão dos ADRs, com o teste que falha antes.

## O que já existe

- `docs/OPERATIONS.md` já cobre o que o plano pede dele: a ordenação (como ligar, o custo medido, P e lease, upgrade e rollback), a liberação de chave, as cabeças sem marca, a verificação de sequência no inbox, o espaçamento do `basic.return` e o rastro. Nesta etapa ele só é relido contra o código final.
- Os testes da G4 existem: 139 métodos `G4_*`, entre eles as propriedades `G4_Propriedade_PrimeiraEntregaEmOrdem_{Curto,DezMinutos}` e `G4_Propriedade_RegressaoNaDlx_{Curto,DezMinutos}`, que rodam no caos, e os de inbox `G4_Inbox_*`.
- O upgrade da v0.1 com backlog tem teste: `Schema_UpgradeDaV01ComBacklog_LinhasContinuamReivindicaveis`, que parte da última migration da `0.1.0-alpha`.
- O `release.yml` da v0.1 continua de pé (Trusted Publishing, validação de Source Link e símbolos, caminho `-test` com feed local).

## Decisões pendentes (com recomendação)

| Tema | Recomendação | Por quê |
| --- | --- | --- |
| Forma da G4 | Uma seção `## G4.`, com duas metades e a redação assimétrica do escopo. **Publicação:** por chave, a partir da primeira mensagem com `sequence`, a primeira cópia de cada mensagem chega ao broker depois da primeira cópia de todas as anteriores, na ordem de commit. **Consumidor:** com `ProcessInOrderAsync`, uma regressão nunca é aplicada nem descartada em silêncio; volta para a aplicação mandar à DLX. "Holds when" com as condições (`OrderByKey`, um consumidor ativo por fila, dead-lettering at-least-once); "Not covered" com as cópias repetidas, a lacuna da mensagem liberada e a ordem estrita | É a frase que os testes provam. Prometer "ordem por chave" sem a assimetria seria prometer o que só o Kafka, adiado, daria |
| O que não promete | A linha "Order" sai e entram as quatro do escopo: ordem estrita de ponta a ponta no RabbitMQ, reordenar no consumidor, ordem global entre agregados e recuperar a mensagem de uma chave liberada | O escopo já diz por que cada uma não vale; o `GUARANTEES.md` copia a lista única |
| "Operating over time" | Ganha só o que tem teste com asserção: uma chave parada pela DLQ, com 100 mil linhas à frente, não segura as outras (`G4_ChaveBloqueadaComCemMilAFrente_OutrasChavesFluem_CustoMedido`), e o SQL de liberação faz a chave voltar a fluir em ordem (`Operacao_SqlDoOperationsLiberaAChave_EElaVoltaAFluirEmOrdem`). As latências do backlog de 500 mil e o custo da ordenação ficam como números no `OPERATIONS.md`, sem promessa | Pela regra de corte, medida sem critério de aprovação não vira promessa |
| Comparação no README | Remedir a contagem de pacotes do Waybill com os pacotes da `0.2.0-alpha`, atualizar a linha "Ordering by key" e reconferir cada célula de Wolverine e CAP nas fontes, com data e versões novas | Uma tabela com "as of" velho e o Waybill mudado deixa de ser fato |
| PublicAPI | Mover os `PublicAPI.Unshipped.txt` para os `Shipped` no commit da release, como na v0.1 | Daí em diante, toda mudança aparece como mudança sobre a `0.2.0-alpha` |
| Validação pelo estranho | Um agente sem contexto recebe o README e um feed local com versão nova a cada pack. Três tarefas: (1) o primeiro evento num projeto novo, para pegar regressão do README; (2) ligar a ordenação e consumir com `ProcessInOrderAsync`, só com o README e o `OPERATIONS.md`; (3) partir de um projeto com a `0.1.0-alpha` do nuget.org e backlog pendente com o broker parado, subir para o feed local, migrar e drenar. Fricção em `FRICCAO.md` | A tarefa 3 é o mesmo upgrade do teste, feito como um usuário faria, pela documentação |
| Quando pôr a tag | Só depois de (a) merge na `develop` e na `main`, (b) um run agendado verde com o código da release (caos longo com as propriedades G4 e a carga longa de 5 h) e (c) o ok explícito do autor naquele momento | O agendado de Oct 21 falhou no custo pelo critério antigo, e o de Oct 22 é o primeiro com as etapas 10 e 11. Publicar no nuget.org não se desfaz |
| Release no GitHub | Criar a release da `v0.2.0-alpha` com as notas do CHANGELOG e, junto, a da `v0.1.0-alpha` que está pendente, ambas com o ok do autor | Custa um comando, e quem chega pelo GitHub vê o que mudou sem abrir o CHANGELOG |

## Entregas

- **`GUARANTEES.md`**: a G4 (forma acima), com cada condição apontando para teste; introdução e matriz de teste atualizadas para a v0.2 ("four promises"); "What Waybill does not promise" e "Operating over time" como acima.
- **Rastreabilidade viva**: `Garantias_CadaGarantiaTemTeste` passa a esperar G1 a G4.
- **README**: status `0.2.0-alpha`; o resumo das garantias com a G4; uma seção sobre a ordenação (o que promete em uma frase, quando ligar e quando não ligar, o custo com link para o `OPERATIONS.md`, a verificação no consumidor); "When not to use" sem "ordering is planned for 0.2"; comparação remedida. Links continuam absolutos.
- **Revisão dos ADRs 0006 a 0011** contra o código final. Divergência de texto corrige o ADR; de comportamento, teste que falha e depois a correção. Nenhum ADR novo.
- **Escopo**: a linha da G4 deixa de dizer "vai para o `GUARANTEES.md` na etapa 12"; o que a revisão dos ADRs mudar.
- **`CHANGELOG`**: `[Unreleased]` vira `[0.2.0-alpha] - <data>`, reorganizado por tema; saem as notas internas entre builds não publicados ("a database that applied the unreleased v0.2 migration must be recreated"); o caminho de upgrade da `0.1.0-alpha` fica explícito; links de comparação no pé.
- **PublicAPI Shipped** no commit da release.
- **Tag de teste** `v0.2.0-alpha-test.1` verde.
- **Validação pelo estranho** e `FRICCAO.md`.
- **Tag assinada `v0.2.0-alpha`**, publicação por Trusted Publishing, pacotes baixados do nuget.org validados depois que os símbolos indexarem; back-merge por branch temporária, nunca com a `main` como origem do PR.

## Achados de partida

Levantados ao escrever este SPEC; cada um vira tarefa.

| Achado | Onde | Consequência |
| --- | --- | --- |
| O teste de rastreabilidade fixa as garantias em G1, G2 e G3 | `tests/Waybill.Tests.Unit/RastreabilidadeTests.cs` | A G4 entra no texto e falha o teste até ele esperar G1 a G4: é o teste que vem primeiro |
| O `GUARANTEES.md` diz "Waybill 0.1 makes three promises" e "Version 0.1 publishes in parallel batches, with no ordering promise" | `GUARANTEES.md` | Reescrever a introdução e a linha "Order" |
| O README diz `0.1.0-alpha` e "planned for 0.2" em três lugares (garantias, when not to use, comparação), e não tem seção sobre ligar a ordenação, que o plano pede | `README.md` | Entra na tarefa do README |
| O escopo promete "números de referência com o benchmark da v0.2", e o plano da v0.2 não tem benchmark de latência | `docs/escopo-e-fronteiras.md`, "O que o pacote não promete" | Pela regra de corte, a frase sai ou vira roadmap sem versão |
| O `[Unreleased]` está em ordem de entrega e tem notas que só valem entre builds não publicados | `CHANGELOG.md` | Reorganizar na tarefa do CHANGELOG |
| 38 entradas nos `PublicAPI.Unshipped.txt` dos quatro projetos | `src/*/PublicAPI.Unshipped.txt` | Mover no commit da release |
| O agendado da `develop` ainda não rodou verde com as etapas 10 e 11 (Oct 21 falhou no custo pelo critério antigo) | `scheduled.yml` | A tag espera um agendado verde (decisão acima) |
| A mudança em `IInbox<TContext>` (`ProcessInOrderAsync`) quebra implementações de fora | `CHANGELOG.md`, "Changed" | Já marcada como **Breaking**; numa `0.x` o SemVer admite. Fica em destaque no topo da seção da versão |

## Regra de corte (do plano)

Se uma frase do README ou do `GUARANTEES.md` descreve comportamento sem teste correspondente, ela sai ou vira item de roadmap marcado como não implementado.

## Fora do escopo

Código novo de funcionalidade, transporte Kafka (ADR 0009), API de operação da DLQ e de liberação de chave (v1.0), ADRs novos e benchmark de latência.
