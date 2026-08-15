# Contributing

## Prerequisites

- .NET 10 SDK pinned by [`global.json`](global.json).
- Aspire CLI restored from the local tool manifest.
- Docker or Podman for container-backed samples and deployment tests.

## Validate the repository

The build entry point restores pinned tools and dependencies, verifies formatting, builds, runs non-container tests, and packs all public packages.

| Task | Linux/macOS | Windows |
| --- | --- | --- |
| Standard validation | `./build.sh` | `./build.ps1` |
| Include container tests | `./build.sh --containers` | `./build.ps1 -Containers` |
| Validate a release version | `./build.sh --package-version 1.2.3` | `./build.ps1 -PackageVersion 1.2.3` |

The package contract suite inspects the core library, testing library, workflow tool, and template packages, then builds temporary consumers from those packages.

For the full Docker-backed multi-repository scenario, use the command and runtime guidance in the [test harness README](tests/Spire.MultiRepo.E2E.Tests/README.md).

## Repository layout

- `src/Aspire.Hosting.ModularAppHosts`: core module APIs and packaged source generator.
- `src/Aspire.Hosting.ModularAppHosts.Testing`: optional Docker Compose test support.
- `src/Aspire.Hosting.ModularAppHosts.Tool`: workflow-document and cross-repository dispatch CLI.
- `tests/Spire.MultiRepo.E2E.*`: isolated repository lifecycle and handoff coverage.
- `samples`: runnable AppHosts and focused usage examples.
- `templates`: packaged `dotnet new aspire-module` item template.
- `docs`: user guides too detailed for package READMEs.

## Commits and pull requests

Keep changes focused and document user-visible behavior. Releases are calculated from [Conventional Commits](https://www.conventionalcommits.org/):

- `fix:` and `perf:` produce a patch release.
- `feat:` produces a minor release.
- `!` after the type or scope, or a `BREAKING CHANGE:` footer, produces a major release.
- Documentation, tests, refactoring, and build-only changes do not release on their own.

When squash-merging, ensure the resulting commit still follows this convention.

## Documentation

Lead with the task a reader can perform. Keep prerequisites, security boundaries, and option constraints beside the command or API they govern. Breaking-change history belongs in commit messages and generated release notes; reference guides describe the current contract.

## Releases

After complete CI succeeds on `main`, release automation derives the next version, promotes the exact packages produced by that CI run, publishes them to NuGet, and creates the GitHub release. Set the repository variable `NUGET_PUBLISH_ENABLED` to `true` to enable publishing from successful `main` builds.

SDK, AppHost SDK, dependency, and local Aspire CLI versions are pinned centrally by `global.json`, `Directory.Packages.props`, and `.config/dotnet-tools.json`.
