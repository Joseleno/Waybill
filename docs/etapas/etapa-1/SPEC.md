# Etapa 1 — Repositório, solution e CI

Derivado de `docs/plano.md`, seção "Etapa 1".

## Objetivo

Esqueleto que empacota e roda testes, com o histórico público desde o primeiro commit e nenhuma promessa além do que existe.

## Escopo

- Solution `.slnx` com os três pacotes (`Waybill`, `Waybill.EntityFrameworkCore.PostgreSql`, `Waybill.RabbitMQ`) e os três projetos de teste (`Unit`, `Integration`, `Chaos`), em .NET 10.
- Licença Apache-2.0 no primeiro commit.
- MinVer (tags `vX.Y.Z`, pré-release `alpha` sem tag), Source Link, `.snupkg`, gestão central de pacotes.
- Analisador de API pública com `PublicAPI.Shipped.txt` e `PublicAPI.Unshipped.txt` por pacote.
- CI por PR: unitários e integração contra PostgreSQL 15 e 18, caos curto em job próprio, `dotnet pack`.
- Workflow agendado para os cenários longos (`Category=Long`).
- Release por tag: Trusted Publishing no nuget.org; tags `-test` validam o pacote num feed local, sem publicar.
- `SECURITY.md`, `CONTRIBUTING.md`, `CHANGELOG.md`, `docs/adr/` com o ADR 0001, spike arquivado em `spike/`.

## Fora do escopo

Qualquer API do pacote. O README descreve só o estado experimental.

## Decisões desta etapa

- Piso do PostgreSQL: 15 (Oct 4, 2026).
- Repositório público desde o primeiro commit; arquivos de repositório em inglês, design em português.
- Notas de trabalho locais (resumo de sessão e instruções do ambiente) ficam fora do git.
- Runners do GitHub têm limite de 6 h por job: o cenário de 24 h da etapa 5 precisa de runner próprio ou de duração menor. Decidir antes da etapa 5.
