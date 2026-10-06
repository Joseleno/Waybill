# Etapa 1 — Tarefas

- [x] `git init`, `.gitignore`, `.editorconfig`, `LICENSE` Apache-2.0
- [x] `global.json` (SDK 10, runner Microsoft.Testing.Platform), `Directory.Build.props`, `Directory.Packages.props`
- [x] Três pacotes com metadados, Source Link, `.snupkg`, MinVer e analisador de API pública
- [x] Três projetos de teste com xUnit v3; teste da regra de dependências do núcleo; teste da matriz de PostgreSQL
- [x] Workflows `ci`, `scheduled` e `release`
- [x] `README.md` (estado experimental), `SECURITY.md`, `CONTRIBUTING.md`, `CHANGELOG.md`
- [x] ADR 0001 em `docs/adr/`; spike arquivado em `spike/`
- [x] Repositório público `Joseleno/Waybill` no GitHub; `main` e `develop` com o commit inicial (licença)
- [x] Git flow: `main` (releases), `develop` (integração), `feature/*` com PR para `develop`; documentado no `CONTRIBUTING.md`
- [x] PR #1 `feature/etapa-1-repositorio-e-ci` → `develop` revisada e mergeada pelo autor
- [x] CI verde na PR e no `develop` (PostgreSQL 15 e 18, caos curto, pack)
- [x] Tag `v0.0.0-test.1`: release empacotou `0.0.0-test.1`, publicou num feed local e instalou os três pacotes num projeto novo; passos do nuget.org pulados
- [x] Private vulnerability reporting habilitado no GitHub
