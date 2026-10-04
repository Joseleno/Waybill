# Etapa 1 — Plano e critérios de aceite

| Critério (plano, "Pronto quando") | Como se verifica |
| --- | --- |
| `dotnet pack` gera os três pacotes com metadados completos | Job `pack` do CI; nuspec com licença, README, repositório e commit, dependências |
| O CI roda os três projetos de teste, ainda que quase vazios, e fica verde | Workflow `ci` verde no `main`: `test (postgres 15)`, `test (postgres 18)`, `chaos (short)`, `pack` |
| Uma tag de teste publica num feed local ou privado | Tag `v0.0.0-test.1`: workflow `release` empacota, publica num feed local do runner e instala os três pacotes num projeto novo |

Testes que já carregam significado nesta etapa:

| Teste | O que prova |
| --- | --- |
| `Nucleo_DependeSoDeSystemEMicrosoftExtensions` | A regra de dependências do núcleo vale desde o primeiro commit |
| `Postgres_VersaoDentroDaMatrizDeSuporte` | A matriz do CI realmente sobe o PostgreSQL 15 e o 18 |
