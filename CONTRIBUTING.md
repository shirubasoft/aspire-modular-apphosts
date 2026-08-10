# Contributing

## Prerequisites

- .NET 10 SDK, pinned by [`global.json`](global.json).
- Aspire CLI restored from the repository's local tool manifest for sample and deployment E2E tests.
- Docker or Podman for container-backed samples and Compose E2E tests.

## Validate the repository

From the repository root, one command restores pinned tools and dependencies, verifies formatting, builds, runs all non-container tests, and packs all three public packages:

```bash
./build.sh
# Windows:
./build.ps1
```

Release automation runs the same validation path with an explicit package version:

```bash
./build.sh --package-version 1.2.3
# Windows:
./build.ps1 -PackageVersion 1.2.3
```

Include the real Docker Compose deployment lifecycle when Docker or Podman is running:

```bash
./build.sh --containers
# Windows:
./build.ps1 -Containers
```

The package contract suite packs the library, testing, and template projects; inspects their package contracts; and builds temporary consumers against the resulting packages.

## Repository layout

- `src/Aspire.Hosting.ModularAppHosts`: core module APIs.
- `src/Aspire.Hosting.ModularAppHosts.Generators`: source generator packaged with the core library.
- `src/Aspire.Hosting.ModularAppHosts.Testing`: optional Docker Compose testing support.
- `src/Aspire.Hosting.ModularAppHosts.Tool`: reserved empty project.
- `tests`: unit, lifecycle, generator, and package contract tests.
- `samples`: runnable modular AppHost and E2E examples.
- `templates`: the packaged `dotnet new` item template for the first module contract.
- `docs`: user guides that are too detailed for the package README.

## Commits and pull requests

Keep changes focused and include tests or documentation for user-visible behavior. Use [Conventional Commits](https://www.conventionalcommits.org/) because releases are calculated from commit history:

- `fix:` and `perf:` produce a patch release.
- `feat:` produces a minor release.
- A `!` after the type or scope, or a `BREAKING CHANGE:` footer, produces a major release.
- Documentation, tests, refactoring, and build-only changes do not release on their own.

When squash-merging, ensure the resulting commit message still follows this convention.

## Documentation

Describe the current supported workflow and lead with the task a reader can perform. Keep prerequisites,
security boundaries, and option constraints beside the command or API they govern. Release notes and
upgrade guides own migration history, leaving reference guides focused on the current contract. Avoid
comparisons with unrelated or unsupported concepts in reference guides.

## Releases

After the complete CI workflow succeeds on `main`, the release workflow calculates the next version, publishes the core, testing, and template NuGet packages plus symbol packages, and creates the corresponding GitHub release. Versions are derived from semantic commit history rather than edited manually. The .NET SDK, AppHost SDK, NuGet dependencies, and local Aspire CLI are pinned centrally by `global.json`, `Directory.Packages.props`, and `.config/dotnet-tools.json`.

For publish-enabled pushes to `main`, CI calculates that version before its Linux build and stores the resulting NuGet packages as a workflow artifact. The release workflow promotes the packages from that exact successful CI run rather than rebuilding or rerunning tests.

Set the repository variable `NUGET_PUBLISH_ENABLED` to `true` to enable publishing from successful
`main` builds.
