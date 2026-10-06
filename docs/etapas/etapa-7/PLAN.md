# Etapa 7 — Plano e critérios de aceite

Escrito com as cinco recomendações do SPEC como premissa. Se o autor decidir diferente, mudam as linhas marcadas com (D).

## Desenho

| Tema | Como |
| --- | --- |
| `GUARANTEES.md` | Na raiz, em inglês. Para G1, G2 e G3: a frase da garantia, "Holds when" (as condições), e "Proven by" (os testes, com link para o arquivo). Depois, "What Waybill does not promise" e "Operating conditions" (operação sustentável, só com a carga longa verde). A fonte é a seção Garantias de `docs/escopo-e-fronteiras.md`, sem as condições que dependem de API inexistente (reprocessamento da DLQ, ordenação) |
| Rastreabilidade viva | Teste unitário `Garantias_CadaTesteCitadoExiste`. Acha a raiz pelo `Waybill.slnx`, extrai do `GUARANTEES.md` os nomes no formato `G<n>_...`, e falha se algum não aparecer como método em `tests/**/*.cs`. Também falha se alguma das três garantias ficar sem nenhum teste citado |
| README | Ordem: o que é, status (alpha), install, a API em dez linhas, link para o exemplo, guarantees (resumo de três linhas e link), when not to use, comparison (D), what it does not solve, operations, building, license. Links absolutos para `https://github.com/Joseleno/Waybill/blob/main/...`. O estado "What exists today" sai: o conteúdo vai para a API e para o CHANGELOG |
| API em dez linhas | Contadas do registro ao `Enqueue`, no estilo do `Program.cs` do Billing (`AddMessage`, `AddWaybillDispatcher`, `AddWaybillRabbitMQ`, `MapWaybillOutbox`, `Enqueue`). O mesmo trecho compila num teste, então o README não pode envelhecer sem o build quebrar |
| Comparação (D) | Tabela Waybill, Wolverine e CAP, com dependências transitivas, runtime exigido no host, geração de código em runtime, inbox ou idempotência do consumidor, ordem por chave e licença. Cada célula de terceiro tem link para a fonte, e a tabela leva "as of" com a data e as versões. As células do Waybill vêm de medição: `dotnet list package --include-transitive` num projeto descartável que instala os quatro pacotes |
| Revisão dos ADRs | Ler 0001 a 0005 contra o código. Divergência de texto: corrigir o ADR. Divergência de comportamento: teste que falha, depois a correção. No escopo, tirar as marcas de *provisório* do claim e decidir `lock_timeout`, `statement_timeout` e `idle_in_transaction_session_timeout`: viram configuração com teste ou saem |
| CHANGELOG | `[Unreleased]` vira `[0.1.0-alpha] - <data>`, agrupada por pacote ("Outbox", "Dispatcher", "RabbitMQ transport", "Inbox", "Operations", "Testing"). O "Changed" de antes da release some, porque não houve versão anterior. Links de comparação no pé |
| Pacotes | Corrigir a descrição do `Waybill.Testing` (outbox e inbox). Mover `PublicAPI.Unshipped.txt` para `PublicAPI.Shipped.txt` no commit da release (D) |
| Release pipeline | Integração com `--filter-not-trait "Category=Long"` e `timeout-minutes` no job. O caminho `-test` passa a validar também Source Link e símbolos: `dotnet-validate package local` ou a checagem equivalente do `.snupkg` |
| Validação pelo estranho | Um agente sem contexto recebe só o README e um feed local com os pacotes da tag `-test` (ou de `dotnet pack`). Pedido: criar um projeto novo, rodar Postgres e RabbitMQ em Docker e publicar o primeiro evento. A fricção vai para `FRICCAO.md`. O autor repete antes do merge |
| Release (D) | Depois do merge na `develop` e da `develop` na `main`, a tag assinada com SSH `v0.1.0-alpha` dispara o `release.yml`. O push da tag espera o ok explícito do autor. Em seguida: baixar o pacote do nuget.org num projeto novo e conferir o Source Link (step into no Rider/VS, ou `dotnet-validate`) |
| Prefixo (D) | Pedido de reserva de `Waybill.*` ao NuGet, enviado pelo autor, sem bloquear a release |

## Cenários

| Teste ou verificação | Cenário | Resultado esperado | Onde roda |
| --- | --- | --- | --- |
| `Garantias_CadaTesteCitadoExiste` | `GUARANTEES.md` cita um nome inexistente (verificado com um nome inventado antes do texto final) | Falha com o nome que falta | Unit (PR) |
| `Garantias_CadaGarantiaTemTeste` | Uma garantia sem nenhum teste citado | Falha com a garantia | Unit (PR) |
| `Readme_ApiEmDezLinhas_Compila` | O trecho da API do README, copiado num teste | Compila e enfileira contra o `FakeOutbox`. O trecho tem no máximo dez linhas | Unit (PR) |
| Release com tag `-test` | `v0.1.0-alpha-test.1` | Pipeline verde em minutos, sem a carga longa. Os pacotes instalam de um feed local, e Source Link e símbolos validam | `release.yml` |
| Estranho com o README | Agente sem contexto, só com o README e um feed local | Publica o primeiro evento num projeto novo; cada fricção registrada | Manual, antes do PR |
| Autor com o README | O mesmo, feito pelo autor | Idem | Manual, antes do merge |
| Release real | Tag assinada `v0.1.0-alpha` | Pacotes no nuget.org; o GitHub mostra a tag como "Verified"; Source Link resolve o código do commit da tag | `release.yml`, com ok do autor |

## Pronto quando (do plano)

- o pacote instala num projeto novo e publica o primeiro evento seguindo só o README;
- cada linha do `GUARANTEES.md` aponta para um teste nomeado, e o teste da rastreabilidade garante isso no CI;
- a v0.1.0-alpha está no NuGet com Source Link e símbolos funcionando.

## Ordem

1. A correção do pipeline de release vem primeiro, porque é bug e independe das decisões.
2. `GUARANTEES.md` e o teste da rastreabilidade.
3. Revisão dos ADRs e do escopo. Ela pode mudar o texto das garantias, por isso vem antes do README.
4. README e o teste da API em dez linhas.
5. CHANGELOG, metadados e PublicAPI.
6. Tag `-test`, o estranho, o autor.
7. PR. Depois do merge e com o ok do autor: a tag real.
