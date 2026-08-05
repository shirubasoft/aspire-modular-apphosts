# Multi-repository E2E sample

This sample proves that an Aspire module contract can define a container whose Docker build inputs
live in a different Git repository. CI packs `Spire.ModuleContract`, removes its source and the
build fixture from an isolated consumer clone, and restores only the package. The local project
reference remains as a development convenience.

The contract declares:

- the `multi-repo-api` container and HTTP health check;
- `bash build-image.sh <resolved-image-reference>` as its image build command;
- module-scoped `IOptions<SpireModuleOptions>` for the build repository and optional revision; and
- an `appsettings.json` default that uses the checked-in build fixture without environment variables.

[`ResourceBuildRepository`](ResourceBuildRepository) is source material for the independent
repository used by the fixture. It owns only a Dockerfile, its build script, and the HTTP health and
marker files copied into the nginx image.

## What CI proves

CI creates this layout outside the checked-out source tree:

```text
<temporary-root>/
├── consumer/                 # isolated clone containing only the AppHost
├── resource-build-source/    # separately initialized producer Git repository
├── packages/                 # packed runtime and module contract
└── consumer/.../.aspire/module-repositories/
    └── <managed-checkout>/    # detached checkout used by the image builder
```

The producer repository gets two commits. CI records the first commit, then changes `marker.txt` in
the second. The AppHost requests the first SHA. It must clone the producer repository into its
managed checkout, detach at that exact SHA, execute the checked-in build script and Dockerfile, wait
for the resulting container to become healthy, and return `multi-repo-resource-pinned-revision`
from `/marker.txt`. Building the producer's latest commit would return a different marker and fail
the job.

The validation also confirms that the isolated consumer no longer contains the contract source or
build fixture, the managed checkout's origin is the independent producer repository, the expected
image exists in Docker, `/health.txt` returns the producer-owned health marker, and the AppHost
stops cleanly.

## Run manually

From the repository root, start the AppHost. Its normal configuration points the module at the
checked-in build fixture, and the build script selects a running Docker or Podman installation:

```bash
cd samples/MultiRepoE2E/Spire.Consumer.AppHost
aspire run
```

For an independent checkout or pinned build, set `BuildRepository` and
`BuildRepositoryRevision` under
`Aspire:ModularAppHosts:Modules:multi-repo-resource-build` through JSON, command-line, or another
standard .NET configuration provider.

The sample requires the .NET 10 SDK, Aspire CLI 13.4 or later, Git, Bash, Docker, `curl`, and `jq`.
