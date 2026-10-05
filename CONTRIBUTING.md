# Contributing

Thanks for your interest. Waybill is in an early, experimental stage, and the design is still moving. Opening an
issue to discuss a change before writing code saves everyone time.

## Ground rules

- **No guarantee without a test.** Any behavior claimed in the README or in `GUARANTEES.md` must be backed by a
  concurrency test that proves it. If a sentence describes behavior that no test covers, it goes.
- **Tests that prove a guarantee are named after it**, for example `G1_RetryDuranteCommit_NaoDuplica`.
- **Design decisions are recorded as ADRs** in [`docs/adr/`](docs/adr), in the change that makes the decision.
- **The core package (`src/Waybill`) depends only on `Microsoft.Extensions.*` and the BCL.** Anything else belongs in
  a transport or persistence package. A unit test enforces this.
- **The public API is tracked.** New public members must be listed in the project's `PublicAPI.Unshipped.txt`; the
  build fails otherwise.
- Avoid marketing words such as "production-ready", "exactly-once" or "blazing fast" in code, docs and commits.

## Development

Requirements: .NET 10 SDK and Docker.

```
dotnet build
dotnet test --project tests/Waybill.Tests.Unit
dotnet test --project tests/Waybill.Tests.Integration --filter-not-trait "Category=Long"
dotnet test --project tests/Waybill.Tests.Chaos --filter-not-trait "Category=Long"
```

Long-running scenarios (`[Trait("Category", "Long")]`) run in the scheduled CI workflow, not on pull requests.

## Branching (git flow)

- `main` holds released code only; every release is a tag on `main`.
- `develop` is the integration branch.
- Work happens on `feature/<short-name>` branches created from `develop`, and reaches it only through a pull request.
- Releases go through `release/<version>` (from `develop`, merged into `main` and back into `develop`); urgent fixes
  through `hotfix/<version>` (from `main`, merged into `main` and `develop`).
- Nobody pushes directly to `main` or `develop`.

## Commits and pull requests

- Pull requests target `develop` (or `main` for `release/*` and `hotfix/*`), and CI must be green before merging.
- Commit messages follow [Conventional Commits](https://www.conventionalcommits.org/) (`feat:`, `fix:`, `test:`, `docs:`, `build:`, `ci:`).
- One commit per proven test scenario, with the test name in the message.
- By contributing, you agree that your contributions are licensed under the [Apache-2.0 license](LICENSE).
