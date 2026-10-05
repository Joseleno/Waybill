# Etapa 7 — Documentação e release

Derivado de `docs/plano.md`, seção "Etapa 7".

## Objetivo

Escrever a promessa da v0.1 só agora, quando cada linha dela tem um teste atrás, e publicar a v0.1.0-alpha. Pronto quando alguém instala o pacote num projeto novo e publica o primeiro evento seguindo só o README, cada linha do `GUARANTEES.md` aponta para um teste nomeado, e a versão está no NuGet com Source Link e símbolos funcionando.

A etapa depende do fechamento da etapa 5: a linha de operação sustentável só entra com a carga longa verde.

## Decisões pendentes (com recomendação)

| Tema | Recomendação | Por quê |
| --- | --- | --- |
| Tom da comparação com Wolverine e CAP | Tabela de fatos sem adjetivos, cada célula com fonte e data ("as of Oct 2026"). Critérios da análise de negócio: dependências transitivas, se exige runtime no host, geração de código em runtime, tamanho da API pública, idempotência do consumidor. Antes da tabela, a seção "When not to use Waybill", que aponta Wolverine e CAP para quem quer um framework | Uma comparação sem fonte é uma promessa sem teste. Os números do Waybill saem de um projeto descartável medido na própria etapa; os dos outros, da documentação deles, com link |
| Publicar no NuGet agora ou só preparar | Preparar tudo e validar com uma tag `-test` e com o estranho instalando de um feed local. O push da tag `v0.1.0-alpha` é o último passo, com ok explícito do autor naquele momento | Publicar no NuGet é irreversível: dá para deslistar, mas não para apagar. Preparar e publicar são decisões separadas |
| Reserva do prefixo `Waybill.*` | Pedir, em paralelo, sem bloquear a release | Custa um e-mail. Se for recusada por ser palavra comum, o plano já diz para seguir sem a reserva |
| Assinatura da tag | SSH | Não há chave GPG nem SSH configurada na máquina, então as duas opções começam do zero. SSH é uma chave `ed25519`, cadastrada no GitHub como chave de assinatura, que aparece como "Verified". GPG exige gerenciar chaveiro e expiração |
| `PublicAPI.Unshipped.txt` na release | Mover tudo para `PublicAPI.Shipped.txt` no commit da release | Daí em diante, toda mudança da API pública aparece no diff como mudança sobre algo publicado |

## Entregas

- **`GUARANTEES.md`** (inglês, na raiz), escrito a partir das seções Garantias, Contrato técnico e O que o pacote não promete de `docs/escopo-e-fronteiras.md`.
  - Traz as três garantias, com as condições de cada uma.
  - Cada linha cita o teste ou os testes que a provam, pelo nome e com link para o arquivo.
  - Um teste automatizado lê o `GUARANTEES.md` e falha se um nome citado não existir no repositório, para a rastreabilidade não envelhecer.
- **README** reescrito para a release, com:
  - a API em dez linhas;
  - quickstart a partir do pacote do NuGet, não do código-fonte;
  - "When not to use Waybill";
  - a comparação com Wolverine e CAP;
  - "What Waybill does not solve", da seção de mesmo nome do escopo;
  - links para `GUARANTEES.md`, `OPERATIONS.md` e o exemplo.

  O README vai dentro do pacote, então os links precisam ser absolutos: links relativos quebram no nuget.org.
- **Revisão dos ADRs 0001 a 0005** contra o código final. Nenhum ADR novo nesta etapa: uma divergência vira correção no ADR, ou no código com teste.
  - Tirar as marcas de *provisório* do Contrato técnico, que diziam "se confirmam na etapa 3".
  - Decidir as três configurações que ficaram "a medir na etapa 3": `lock_timeout`, `statement_timeout` e `idle_in_transaction_session_timeout`. Cada uma vira configuração ou sai do texto.
- **`CHANGELOG`**: a seção `[Unreleased]` vira `[0.1.0-alpha]`, reorganizada por tema e não por ordem de entrega.
- **Pipeline de release corrigido e validado** com uma tag `v0.1.0-alpha-test.N`.
- **Validação pelo estranho**: um agente sem contexto recebe só o README e um feed local com os pacotes. Ele cria um projeto novo e publica o primeiro evento. Cada fricção vai para `FRICCAO.md`. Depois, o autor faz o mesmo antes do merge.
- **Tag assinada `v0.1.0-alpha`**, publicação no NuGet e conferência de Source Link e símbolos a partir do pacote baixado do nuget.org.

## Achados de partida

Levantados ao escrever este SPEC; cada um vira tarefa.

| Achado | Onde | Consequência |
| --- | --- | --- |
| O `release.yml` roda os testes de integração sem filtro, então inclui a carga longa (`Category=Long`), que tem 5 h por padrão | `.github/workflows/release.yml` | Uma tag de release ficaria 5 h presa, ou estouraria o limite de 6 h do job. Corrigir com `--filter-not-trait "Category=Long"`, como no `ci.yml` |
| A condição de G3 cita uma "API de reprocessamento" que só chega na v1.0 | `docs/escopo-e-fronteiras.md`, Garantias | Pela regra de corte, a condição sai do `GUARANTEES.md` e fica como roadmap |
| A descrição do `Waybill.Testing` só menciona o outbox, mas o pacote também tem o `FakeInbox` | `src/Waybill.Testing/Waybill.Testing.csproj` | Corrigir os metadados do pacote |
| O README do pacote é o README da raiz, que hoje usa links relativos | `src/Directory.Build.props`, `README.md` | Usar links absolutos no README novo |
| A linha "Operação sustentável" depende da carga longa verde, que ainda está em investigação na etapa 5 | `docs/plano.md`, Rastreabilidade | Sem run verde, a linha não entra. Só pode ir para o `OPERATIONS.md` como observação com números, sem promessa |

## Regra de corte (do plano)

Se uma frase do README ou do `GUARANTEES.md` descreve comportamento sem teste correspondente, ela sai ou vira item de roadmap marcado como não implementado.

## Fora do escopo

Código novo de funcionalidade, Kafka e ordenação (v0.2), benchmark de latência (v0.2), API de operação da DLQ (v1.0) e ADRs novos.
