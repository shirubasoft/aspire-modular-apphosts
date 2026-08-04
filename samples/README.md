# Two modular AppHosts

This sample demonstrates one AppHost exporting a mixed module and another AppHost importing it.

```text
AppHost A
├── sample-api project (development) / branch-tagged modular-sample-api container
├── sample-project (ProjectResource, explicit start)
├── sample-csharp-app (CSharpAppResource, explicit start)
├── sample-static (ContainerResource, nginx:alpine)
├── sample-generated-static ── podman build ──> branch-tagged modular-sample-static
├── sample-executable (ExecutableResource, explicit start)
├── sample-dotnet-tool (DotnetToolResource, explicit start)
├── sample-message (ParameterResource)
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
podman build --tag modular-sample-api:<sanitized-branch> .
podman build --tag modular-sample-static:<sanitized-branch> .
```

For a dirty repository, the exact image-reference argument is changed to its `-dirty` tag. The extension does not generate the executable or the rest of the command.

`AppHostAModule` opts into the source generator with `GenerateDistributedApplicationModule`. The generated `Module` exposes every declared resource as a strongly typed property, including `Api`, `Static`, `GeneratedStatic`, `Message`, and `Custom`. Both AppHosts consume these properties instead of repeating resource types and string names through `GetResource<TResource>(name)`.

## Prerequisites

- .NET 10 SDK
- Aspire CLI 13.4 or later
- A running Podman-compatible container runtime

## Run AppHost A

```bash
cd samples/AppHostA
aspire run
```

AppHost A materializes its local module. Development configuration sets `sample-api`'s `RunAsContainer` option to `false`, so Aspire runs the project directly for debugging while publish mode retains its container representation. `sample-generated-static-installer` builds the Dockerfile-based static image before its container starts, and `sample-static` runs directly from `nginx:alpine`. Clean images are reused after their first build; dirty worktrees always rebuild the sanitized branch tag with `-dirty`. The additional project, C# app, executable, and .NET tool resources use explicit start so they demonstrate their model types without adding duplicate services or package downloads to the default run.

## Run AppHost B

Stop AppHost A, then run the importing host:

```bash
cd samples/AppHostB
aspire run
```

AppHost B sets `Aspire:ModularAppHosts:RepositoryBasePath` to the sample source directory and supplies the AppHost A repository through its configuration-backed Aspire parameter. It imports the complete module, injects the exported message parameter, and starts its own `dependency-gateway` container. In another terminal, verify readiness through Aspire:

```bash
aspire wait sample-api
aspire wait sample-static
aspire wait sample-generated-static
aspire wait sample-message
aspire wait dependency-gateway
aspire describe --include-hidden
```

The dashboard graph shows `Reference` and `WaitFor` relationships from `dependency-gateway` to all three imported containers. Open the gateway endpoint shown by the dashboard; `/health` returns HTTP 200 only while all three upstreams respond successfully.

## E2E testing sample

[`E2ETesting`](E2ETesting/README.md) contains a separate eShop example with `catalog` and `orders` modules. Its E2E AppHost supports both Aspire's in-process testing builder and an Aspire-deployed Docker Compose environment. The same test scenario runs against both modes in CI.

## Multi-repository E2E sample

[`MultiRepoE2E`](MultiRepoE2E/README.md) contains a consumer AppHost that imports and runs Spire's
sample API from the separate `Shirubasoft/spire` repository. CI runs it from an isolated Git root,
so the real GitHub CLI must discover and clone the missing sibling repository before Aspire can
start the service.
