# Contributing

## Prerequisites

- .NET 10 SDK, pinned by [`global.json`](global.json).
- Aspire CLI restored from the repository's local tool manifest for sample and deployment E2E tests.
- Docker or Podman for container-backed samples and Compose E2E tests.

## Validate the repository

From the repository root, one command restores pinned tools and dependencies, verifies formatting, builds, runs all non-container tests, and packs both public packages:

```bash
./build.sh
# Windows:
./build.ps1
```

Include the real Docker Compose deployment lifecycle when Docker or Podman is running:

```bash
./build.sh --containers
# Windows:
./build.ps1 -Containers
```

The package contract suite packs both projects, inspects their dependency boundaries, and builds temporary consumers against the resulting packages.

## Repository layout

- `src/Aspire.Hosting.ModularAppHosts`: core module APIs.
- `src/Aspire.Hosting.ModularAppHosts.Generators`: source generator packaged with the core library.
- `src/Aspire.Hosting.ModularAppHosts.Testing`: optional Docker Compose testing support.
- `tests`: unit, lifecycle, generator, and package contract tests.
- `samples`: runnable modular AppHost and E2E examples.
- `templates`: a `dotnet new` item template for the first module contract.
- `docs`: user guides that are too detailed for the package README.

## Commits and pull requests

Keep changes focused and include tests or documentation for user-visible behavior. Use [Conventional Commits](https://www.conventionalcommits.org/) because releases are calculated from commit history:

- `fix:` and `perf:` produce a patch release.
- `feat:` produces a minor release.
- A `!` after the type or scope, or a `BREAKING CHANGE:` footer, produces a major release.
- Documentation, tests, refactoring, and build-only changes do not release on their own.

When squash-merging, ensure the resulting commit message still follows this convention.

## Releases

After the complete CI workflow succeeds on `main`, the release workflow calculates the next version, publishes both NuGet packages and symbol packages, and creates the corresponding GitHub release. Versions are derived from semantic commit history rather than edited manually. The .NET SDK, AppHost SDK, NuGet dependencies, and local Aspire CLI are pinned centrally by `global.json`, `Directory.Packages.props`, and `.config/dotnet-tools.json`.

Publishing remains disarmed until the repository variable `NUGET_PUBLISH_ENABLED` is explicitly set to `true`. When no release tag exists, the workflow creates a local `v0.0.0` baseline so the first feature release stays in the `0.x` range. A breaking conventional commit advances the package to `1.0.0` when the public API is ready for that stability promise.
