# Multi-repository and workflow-image E2E sample

This sample proves that an Aspire module contract can define a container whose Docker build inputs
live in a different Git repository. CI packs `Spire.ModuleContract`, removes its source and the
build fixture from an isolated consumer repository, and restores only the package. The local project
reference remains as a development convenience.

The contract declares:

- the `multi-repo-api` container and HTTP health check;
- a Dockerfile build command that uses the standard `{container-runtime}` and `{image}` placeholders;
- standard module `Repository` and per-container `BuildRepository`/`BuildRepositoryRevision` settings; and
- an `appsettings.json` default that uses the checked-in build fixture without environment variables.

It also provides two AppHosts for the cross-repository workflow:

- `Spire.Producer.AppHost` represents Repo B, builds the module image, and exposes its remote
  registry identity to `modular-apphosts images publish`.
- `Spire.Consumer.AppHost` represents Repo A. A module image workflow document makes it pull the producer's
  image even when the producer build repository is unavailable.

[`ResourceBuildRepository`](ResourceBuildRepository) is source material for the independent
repository used by the fixture. It owns only a Dockerfile and the HTTP health and marker files copied
into the nginx image.

## Run the sample

The checked-in configuration uses the local build fixture. From either AppHost directory, the sample
works with the ordinary Aspire command and Aspire's configured Docker or Podman runtime:

```bash
cd samples/MultiRepoE2E/Spire.Consumer.AppHost
aspire
```

```bash
cd samples/MultiRepoE2E/Spire.Producer.AppHost
aspire
```

For an independent checkout or pinned build, set `BuildRepository` and
`BuildRepositoryRevision` under
`Aspire:ModularAppHosts:Modules:multi-repo-resource-build:Containers:multi-repo-api` through JSON,
command-line, or another standard .NET configuration provider. Set the definition checkout with the
module-level `Repository` option. If either repository needs initialization, run the exact AppHost-aware
command reported by preflight.

## What CI proves

The xUnit E2E suite creates this layout outside the checked-out source tree:

```text
<temporary-root>/
├── consumer/                 # isolated Git repository containing only the AppHost
├── resource-build-source/    # separately initialized producer Git repository
├── packages/                 # packed runtime and module contract
├── <remote-hash>/            # initializer-owned unpinned sibling
└── <remote-hash>-rev-.../    # initializer-owned detached revision sibling
```

Credential-free repository initialization state is stored independently of the AppHost environment at
`~/.aspire/deployments/<apphost-sha>/modular-apphosts.json`.

The suite packs the contract, removes its source and the build fixture from the isolated consumer,
and creates independent local producer repositories. It then exercises the real Aspire CLI and
verifies all of these behaviors in one locally reproducible command:

1. Both checked-in AppHosts start and become healthy using only their default configuration.
2. `aspire start` fails fast before initialization with the exact `--apphost` recovery command.
3. `aspire do initialize --apphost <path> --non-interactive` creates direct sibling checkouts and normalized state.
4. Repeated initialization is idempotent.
5. A configured local source plus a revision uses a detached initializer-owned sibling without moving
   the developer checkout.
6. Another initialization fast-forwards a clean unpinned checkout.
7. Default run permits only explicitly allowlisted read-only Git inspection; every other Git command
   shape is rejected and recorded.
8. Opt-in runtime refresh fast-forwards a clean build checkout.
9. A dirty checkout is preserved and rebuilt, including when runtime refresh is enabled.
10. Repository lifecycle and command output are emitted without project-owned filtering.
11. The requested Docker or Podman executable is selected through Aspire's runtime resolver.
12. An explicit-tag publisher can describe, build, and run from a registry image while its separate
    build checkout is absent.

The build command and Dockerfile are executed from the initialized build checkout. Each run waits for
the resulting container to become healthy and verifies the exact `/marker.txt` content, so using the
wrong revision, reusing a dirty image, or moving the wrong checkout fails the scenario.

The validation restores the contract package into an isolated consumer, verifies the initializer-owned
checkout's independent producer origin, checks the expected Docker image and producer-owned
`/health.txt` marker, and stops the AppHost cleanly.

A separate CI job starts an ordinary local registry service and uses only the packed tool's commands:

1. `images publish` runs the producer AppHost pipeline and writes its fully qualified tagged
   reference plus GitHub step outputs.
2. `PublisherFallbackTests` removes the local producer tag, points the consumer publisher at a
   deliberately missing build checkout, and validates `describe-images`, the generated build step,
   and an `Aspire.Hosting.Testing` run against the pulled tag without cloning or building. It also
   proves that a missing tag reports the exact initialization recovery command.
3. `images apply` launches `Spire.Consumer.Tests` with the consumer configuration. The tests start
   the consumer AppHost with deliberately missing definition and build repositories plus missing
   initialization siblings. They verify `/marker.txt` from the image that Repo B published and prove
   that complete workflow-document identities do not prepare or clone source checkouts.

## Run the maintainer validation

Run the complete initialization scenario from the repository root. This is the exact test command CI
uses:

```bash
dotnet tool restore
MULTI_REPO_E2E=true ASPIRE_CONTAINER_RUNTIME=docker \
  dotnet test tests/Spire.MultiRepo.E2E.Tests/Spire.MultiRepo.E2E.Tests.csproj \
  --configuration Release
```

Set `ASPIRE_CONTAINER_RUNTIME=podman` to run the same suite with Podman. Docker is validated in CI;
Podman selection is covered through Aspire's resolver and the runtime proxy but is not currently run
end to end on a hosted runner. See the [test harness README](../../tests/Spire.MultiRepo.E2E.Tests/README.md)
for fixture, proxy, cleanup, and failure-diagnostic details.

To reproduce the image handoff from the repository root, start the same local registry used by CI:

```bash
docker run --detach --rm \
  --name modular-apphosts-sample-registry \
  --publish 5000:5000 \
  registry:2
```

Restore the sample, then run the tool project directly to publish the producer image and workflow document:

```bash
dotnet restore samples/MultiRepoE2E/Spire.Consumer.Tests/Spire.Consumer.Tests.csproj
dotnet run \
  --project src/Aspire.Hosting.ModularAppHosts.Tool/Aspire.Hosting.ModularAppHosts.Tool.csproj \
  -- images publish \
  --apphost samples/MultiRepoE2E/Spire.Producer.AppHost \
  --all \
  --tag manual-e2e \
  --output artifacts/manual-module-image-workflow.json
```

Run the consumer test through `images apply`. This is the same command shape used in CI; no shell
environment setup is required:

```bash
dotnet run \
  --project src/Aspire.Hosting.ModularAppHosts.Tool/Aspire.Hosting.ModularAppHosts.Tool.csproj \
  -- images apply \
  --file artifacts/manual-module-image-workflow.json \
  -- \
  dotnet test \
  samples/MultiRepoE2E/Spire.Consumer.Tests/Spire.Consumer.Tests.csproj \
  --configuration Release
```

Inspect `artifacts/manual-module-image-workflow.json` to see the exact contract passed between
the two AppHosts. Stop the registry when finished:

```bash
docker stop modular-apphosts-sample-registry
```

The sample requires the .NET 10 SDK, Aspire CLI 13.4.6 or later, Git, and Docker or Podman.
