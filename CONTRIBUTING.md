# Contributing

## Prerequisites

- .NET 10 SDK, pinned by [`global.json`](global.json).
- Aspire CLI 13.4.6 or later for sample and deployment E2E tests.
- Docker or Podman for container-backed samples and Compose E2E tests.

## Restore and build

From the repository root:

```bash
dotnet restore Aspire.ModularAppHosts.slnx
dotnet build Aspire.ModularAppHosts.slnx --configuration Release --no-restore
dotnet format Aspire.ModularAppHosts.slnx --no-restore --verify-no-changes
```

## Tests

Run the core, testing-package, and packed-package contract suites:

```bash
dotnet test tests/Aspire.Hosting.ModularAppHosts.Tests/Aspire.Hosting.ModularAppHosts.Tests.csproj \
  --configuration Release --no-build --no-restore

dotnet test tests/Aspire.Hosting.ModularAppHosts.Testing.Tests/Aspire.Hosting.ModularAppHosts.Testing.Tests.csproj \
  --configuration Release --no-build --no-restore

dotnet test tests/Aspire.Hosting.ModularAppHosts.PackageTests/Aspire.Hosting.ModularAppHosts.PackageTests.csproj \
  --configuration Release --no-build --no-restore
```

The package contract suite packs both projects, inspects their dependency boundaries, and builds temporary consumers against the resulting packages.

Run the sample E2E scenario through the AppHost:

```bash
ESHOP_E2E_MODE=apphost \
dotnet test samples/E2ETesting/EShop.E2E.Tests/EShop.E2E.Tests.csproj \
  --configuration Release --no-build --no-restore
```

To exercise the real Compose deployment lifecycle, start a supported container runtime and run:

```bash
Parameters__orders_api_key=e2e-orders-key \
ESHOP_E2E_MODE=compose \
dotnet test samples/E2ETesting/EShop.E2E.Tests/EShop.E2E.Tests.csproj \
  --configuration Release --no-build --no-restore
```

## Repository layout

- `src/Aspire.Hosting.ModularAppHosts`: core module APIs.
- `src/Aspire.Hosting.ModularAppHosts.Generators`: source generator packaged with the core library.
- `src/Aspire.Hosting.ModularAppHosts.Testing`: optional Docker Compose testing support.
- `tests`: unit, lifecycle, generator, and package contract tests.
- `samples`: runnable modular AppHost and E2E examples.
- `docs`: user guides that are too detailed for the package README.

## Commits and pull requests

Keep changes focused and include tests or documentation for user-visible behavior. Use [Conventional Commits](https://www.conventionalcommits.org/) because releases are calculated from commit history:

- `fix:` and `perf:` produce a patch release.
- `feat:` produces a minor release.
- A `!` after the type or scope, or a `BREAKING CHANGE:` footer, produces a major release.
- Documentation, tests, refactoring, and build-only changes do not release on their own.

When squash-merging, ensure the resulting commit message still follows this convention.

## Releases

After changes reach `main`, the release workflow calculates the next version, publishes both NuGet packages, and creates the corresponding GitHub release. Versions are derived from semantic commit history rather than edited manually.
