# Multi-repository and workflow-image E2E sample

This sample proves that an Aspire module contract can define a container whose Docker build inputs
live in a different Git repository. CI packs `Spire.ModuleContract`, removes its source and the
build fixture from an isolated consumer repository, and restores only the package. The local project
reference remains as a development convenience.

The contract declares:

- the `multi-repo-api` container and HTTP health check;
- `bash build-image.sh <resolved-image-reference>` as its image build command;
- module-scoped `IOptions<SpireModuleOptions>` for the build repository and optional revision; and
- an `appsettings.json` default that uses the checked-in build fixture without environment variables.

It also provides two AppHosts for the cross-repository workflow:

- `Spire.Producer.AppHost` represents Repo B, builds the module image, and exposes its remote
  registry identity to `modular-apphosts manifest publish`.
- `Spire.Consumer.AppHost` represents Repo A. A full workflow manifest makes it pull the producer's
  image even when the producer build repository is unavailable.

[`ResourceBuildRepository`](ResourceBuildRepository) is source material for the independent
repository used by the fixture. It owns only a Dockerfile, its build script, and the HTTP health and
marker files copied into the nginx image.

## What CI proves

The .NET E2E driver creates this layout outside the checked-out source tree:

```text
<temporary-root>/
├── consumer/                 # isolated Git repository containing only the AppHost
├── resource-build-source/    # separately initialized producer Git repository
├── packages/                 # packed runtime and module contract
├── <remote-hash>/            # initializer-owned unpinned sibling
├── <remote-hash>-rev-.../    # initializer-owned detached revision sibling
└── consumer/.aspire/modular-apphosts/repositories/
    └── *.json                # credential-free initialization receipts
```

The driver packs the contract, removes its source and the build fixture from the isolated consumer,
and creates independent local producer repositories. It then exercises the real Aspire CLI and
verifies all of these behaviors in one locally reproducible command:

1. `aspire start` fails fast before initialization with the exact initialization command.
2. `aspire do initialize --non-interactive` creates direct sibling checkouts and credential-free receipts.
3. Repeated initialization is idempotent.
4. A configured local source plus a revision uses a detached initializer-owned sibling without moving
   the developer checkout.
5. Another initialization fast-forwards a clean unpinned checkout.
6. Default run performs only read-only Git inspection for image state; it never clones, fetches, pulls,
   checks out, or otherwise moves a repository.
7. Opt-in runtime refresh fast-forwards a clean build checkout.
8. A dirty checkout is preserved and rebuilt, including when runtime refresh is enabled.
9. Repository lifecycle output is emitted and credential-bearing command output and receipts are redacted.
10. The requested Docker or Podman executable is selected through the existing runtime resolver.

The build command and Dockerfile are executed from the initialized build checkout. Each run waits for
the resulting container to become healthy and verifies the exact `/marker.txt` content, so using the
wrong revision, reusing a dirty image, or moving the wrong checkout fails the scenario.

The validation restores the contract package into an isolated consumer, verifies the initializer-owned
checkout's independent producer origin, checks the expected Docker image and producer-owned
`/health.txt` marker, and stops the AppHost cleanly.

A separate CI job starts an ordinary local registry service and uses only the packed tool's commands:

1. `manifest publish` runs the producer AppHost pipeline and writes its fully qualified tagged
   reference plus GitHub step outputs.
2. `manifest apply` launches `Spire.Consumer.Tests` with the consumer configuration. The tests start
   the consumer AppHost with deliberately missing definition and build repositories plus a missing
   initialization siblings. They verify `/marker.txt` from the image that Repo B published and prove
   that complete manifest identities do not prepare or clone source checkouts.

## Run manually

Run the complete initialization scenario from the repository root. This is the same command CI uses;
add `--keep-temporary` to preserve its isolated repositories for inspection after a successful run:

```bash
dotnet tool restore
dotnet run \
  --project samples/MultiRepoE2E/Spire.MultiRepo.E2E.Driver/Spire.MultiRepo.E2E.Driver.csproj \
  --configuration Release \
  -- \
  --repository-root . \
  --container-runtime docker
```

Use `--container-runtime podman` to validate the same scenario with Podman.

From the repository root, start the AppHost. Its normal configuration points the module at the
checked-in build fixture, and the build script selects a running Docker or Podman installation:

```bash
cd samples/MultiRepoE2E/Spire.Consumer.AppHost
aspire run
```

The producer AppHost is independently runnable in the same way:

```bash
cd samples/MultiRepoE2E/Spire.Producer.AppHost
aspire run
```

To reproduce the image handoff from the repository root, start the same local registry used by CI:

```bash
docker run --detach --rm \
  --name modular-apphosts-sample-registry \
  --publish 5000:5000 \
  registry:2
```

Restore the sample, then run the tool project directly to publish the producer image and manifest:

```bash
dotnet restore samples/MultiRepoE2E/Spire.Consumer.Tests/Spire.Consumer.Tests.csproj
dotnet run \
  --project src/Aspire.Hosting.ModularAppHosts.Tool/Aspire.Hosting.ModularAppHosts.Tool.csproj \
  -- manifest publish \
  --apphost samples/MultiRepoE2E/Spire.Producer.AppHost \
  --all \
  --tag manual-e2e \
  --output artifacts/manual-workflow-image-manifest.json
```

Run the consumer test through `manifest apply`. This is the same command shape used in CI; no shell
environment setup is required:

```bash
dotnet run \
  --project src/Aspire.Hosting.ModularAppHosts.Tool/Aspire.Hosting.ModularAppHosts.Tool.csproj \
  -- manifest apply \
  --file artifacts/manual-workflow-image-manifest.json \
  -- \
  dotnet test \
  samples/MultiRepoE2E/Spire.Consumer.Tests/Spire.Consumer.Tests.csproj \
  --configuration Release
```

Inspect `artifacts/manual-workflow-image-manifest.json` to see the exact contract passed between
the two AppHosts. Stop the registry when finished:

```bash
docker stop modular-apphosts-sample-registry
```

For an independent checkout or pinned build, set `BuildRepository` and
`BuildRepositoryRevision` under
`Aspire:ModularAppHosts:Modules:multi-repo-resource-build` through JSON, command-line, or another
standard .NET configuration provider.

The sample requires the .NET 10 SDK, Aspire CLI 13.4 or later, Git, Bash, Docker, and `curl`.
