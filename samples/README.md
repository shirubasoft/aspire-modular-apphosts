# Two modular AppHosts

This sample demonstrates one AppHost exporting a mixed module and another AppHost importing it.

```text
AppHost A
├── sample-message parameter
├── sample-api project (development) / branch-tagged modular-sample-api container
│   └── both callbacks resolve sample-message from the same module context
├── sample-project (ProjectResource, explicit start)
├── sample-csharp-app (CSharpAppResource, explicit start)
├── sample-static (ContainerResource, busybox:1.37)
│   └── its Configure callback resolves sample-message from the same module context
├── sample-generated-static ── container build ──> branch-tagged modular-sample-static
├── sample-executable (ExecutableResource, explicit start)
├── sample-dotnet-tool (DotnetToolResource, explicit start)
├── sample-connection-string (ConnectionStringResource)
├── sample-external-service (ExternalServiceResource)
├── sample-container-registry (ContainerRegistryResource)
└── sample-custom (custom Resource)
             │
             └── exported module "AppHostA"
                         │
                         ▼
AppHost B imports every resource
└── dependency-gateway container
    ├── references all three HTTP endpoints
    ├── waits for all three resources to be healthy
    └── reports healthy only after probing all three upstreams
```

The shared module definition is in [`ModuleContract/AppHostAModule.cs`](ModuleContract/AppHostAModule.cs). It demonstrates every public core top-level Aspire resource type alongside the specialized project/container exports. Internal helper resources created by Aspire itself are intentionally excluded. Its project and generated-container exports supply the exact commands that produce their configured images:

```text
<docker|podman> build --tag modular-sample-api:<sanitized-branch>-<12-character-commit> .
<docker|podman> build --tag modular-sample-static:<sanitized-branch>-<12-character-commit> .
```

For a dirty repository, the exact image-reference argument is changed to its `-dirty` tag. Declare the executable and remaining arguments explicitly.

`AppHostAModule` opts into the source generator with `GenerateDistributedApplicationModule`. The generated `Module` exposes every declared resource as a strongly typed property, including `Api`, `Static`, `GeneratedStatic`, `Message`, and `Custom`. Both AppHosts consume these properties instead of repeating resource types and string names through `GetResource<TResource>(name)`.

The message parameter is declared before `sample-api` and `sample-static`. `ConfigureProject`, the
`ExportAsContainer` callback, and the declared container's `Configure` callback receive the module
materialization context, resolve that earlier parameter with `context.GetResource<ParameterResource>()`,
and inject it into their resource. `sample-static` serves the injected value with BusyBox's HTTP
server. CI runs AppHost A in project mode and AppHost B in container mode, then asserts that every
representation receives the same value.

## Prerequisites

- .NET 10 SDK
- Aspire CLI 13.4.6 or later
- A running Docker 28+ or Podman 5+ container runtime

The sample publishers use `ModuleImageCommandOptions.ContainerRuntimePlaceholder`, which resolves
through Aspire's `IContainerRuntimeResolver`. The build therefore follows the same configured Docker
or Podman runtime as the AppHost.

## Run AppHost A

```bash
cd samples/AppHostA
aspire
```

AppHost A materializes its local module and opts into the module-declared image publishers. Development
configuration sets `sample-api`'s `ProjectMode` to `Project`, so Aspire runs the project directly for
debugging while publish mode retains its container representation. Its AppHost project has a stable
`UserSecretsId`, so the mode can also be changed without editing the checked-in setting:

```bash
aspire do build-sample-api --apphost ModularSample.AppHostA.csproj --non-interactive
aspire do use-containers --apphost ModularSample.AppHostA.csproj --non-interactive
# Restart with `aspire`; sample-api is now a container.
aspire do use-project-sample-api --apphost ModularSample.AppHostA.csproj --non-interactive
aspire do use-configured-modes --apphost ModularSample.AppHostA.csproj --non-interactive
```

The native image is built explicitly before selecting container mode; the switch itself only persists
the next model choice. The `sample-generated-static`
resource builds its Dockerfile-based image immediately before it starts, and `sample-static` runs directly
from `busybox:1.37`, serving the message obtained from the module-owned parameter. Clean images are
reused after their first build; dirty worktrees always rebuild the branch-and-commit tag with `-dirty`.
The additional project, C# app, executable, and .NET tool resources use explicit start so they demonstrate
their model types without adding duplicate services or package downloads to the default run.

## Run AppHost B

Stop AppHost A, then run the importing host:

```bash
cd ../AppHostB
aspire
```

AppHost B supplies the existing local AppHost A repository through standard module configuration. Because that checkout is an explicit unpinned local path, no initialization step is needed. Its checked-in configuration keeps `sample-api` in project mode so a fresh checkout remains runnable without prebuilding the native image. It imports the complete module, injects the exported message parameter, and starts its own `dependency-gateway` container. In another terminal, verify readiness through Aspire:

```bash
aspire wait sample-api
aspire wait sample-static
aspire wait sample-generated-static
aspire wait sample-message
aspire wait dependency-gateway
aspire describe --include-hidden
```

The dashboard graph shows `Reference` and `WaitFor` relationships from `dependency-gateway` to all three imported services. Open the gateway endpoint shown by the dashboard; `/health` returns HTTP 200 only while all three upstreams respond successfully.

After confirming Docker or Podman is running, execute
`MODULAR_SAMPLES_E2E=true dotnet test samples/ModularSamples.Tests/ModularSamples.Tests.csproj` from
the repository root to exercise both AppHosts through Aspire's in-process testing builder exactly as
CI does. The test verifies project, exported-project, declared-container, generated-image, and gateway
behavior against the module-owned message.

## E2E testing sample

[`E2ETesting`](E2ETesting/README.md) contains a separate eShop example with `catalog` and `orders` modules. Its E2E AppHost supports both Aspire's in-process testing builder and an Aspire-deployed Docker Compose environment. The same test scenario runs against both modes in CI.

## Image-registry pipeline E2E sample

[`ImagePushE2E`](ImagePushE2E) starts a temporary local OCI registry and executes Aspire's real
`push` and `pull` pipelines for a declared container publisher, a project exported as a container,
an advanced factory-created publisher, and an Aspire-native Dockerfile resource. A second module proves image isolation through Aspire's
named resource steps while verifying that unselected publishers are not built. The pull fixture also maps an
image from one temporary registry to a local reference in a second registry. `test-image-describe.sh`
separately verifies the structured run, pull, push, and build identities consumed by CI tooling. See
the [sample README](ImagePushE2E/README.md) for commands.

## Multi-repository E2E sample

[`MultiRepoE2E`](MultiRepoE2E/README.md) contains a consumer AppHost that imports and runs Spire's
sample API from a contract package while its image build inputs live in a separately initialized
local Git repository derived from `ResourceBuildRepository`. CI validates a pinned managed checkout
plus adopted and initializer-created canonical remote siblings, and a second local-registry
producer-to-consumer module image workflow document handoff; it does not depend
on an external `Shirubasoft/spire` checkout.

## Remote initialization sample

[`RemoteInitialization`](RemoteInitialization/README.md) is the minimal user-facing initialization
flow. Its first `aspire` run fails with the exact `aspire do initialize` recovery command,
which clones an existing, unpinned `shirubasoft` repository. After initialization, plain `aspire`
starts the imported service from the human-readable `spire-external-repo-sample` sibling; later
initialization runs can fast-forward its clean `Created` checkout. The
imported `notification-service` is a specialized project export, so its native project/container
mode can also be selected through the generated `aspire do use-*-notification-service` steps.
