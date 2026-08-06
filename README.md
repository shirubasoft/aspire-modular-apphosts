# Shirubasoft.Aspire.ModularAppHosts

Define an Aspire resource graph once and reuse it across AppHosts. A module can be added from the current application, imported from a managed Git checkout, and exposed through a generated strongly typed API. Importing does not load a module definition from Git: every consumer references the producer-owned C# contract package, while the configured repository supplies only the source and build context needed to materialize that contract.

## Packages

| Package | Use it for |
| --- | --- |
| `Shirubasoft.Aspire.ModularAppHosts` | Defining, exporting, importing, and consuming modules in an AppHost. |
| `Shirubasoft.Aspire.ModularAppHosts.Testing` | Running the same E2E tests against an AppHost or an Aspire-managed Docker Compose deployment. |
| `Shirubasoft.Aspire.ModularAppHosts.Templates` | Scaffolding a runnable module contract with `dotnet new aspire-module`. |
| `Shirubasoft.Aspire.ModularAppHosts.Tool` | Exporting immutable module preview manifests and dispatching cross-repository E2E workflows. |

Install the core package in AppHosts and shared module contracts:

```bash
dotnet add package Shirubasoft.Aspire.ModularAppHosts
```

The testing package is optional. Keeping it separate means regular AppHosts do not acquire `Aspire.Hosting.Testing` or Docker hosting dependencies. Add it to both the AppHost that declares the test deployment environment and the test project that creates the deployment builder.

```bash
dotnet add package Shirubasoft.Aspire.ModularAppHosts.Testing
```

The runtime packages and tool target .NET 10, and the Aspire-facing packages require Aspire 13.4.6 or later. Their APIs use the `Aspire.Hosting.ModularAppHosts` namespace.
They are licensed under the [MIT License](https://github.com/Shirubasoft/aspire-modular-apphosts/blob/main/LICENSE).

## Quick start

Prerequisites are the .NET 10 SDK, Aspire CLI 13.4 or later, and a running Docker 28+ or Podman 5+ container runtime. In an existing AppHost or shared contract project, install the core package, install the item template, and scaffold a contract:

```bash
dotnet add package Shirubasoft.Aspire.ModularAppHosts
dotnet new install Shirubasoft.Aspire.ModularAppHosts.Templates
dotnet new aspire-module --name CatalogModule --moduleName catalog --namespace Catalog.Modules
```

Replace the generated `CatalogModule.cs` content with this runnable contract. The generator creates typed properties for its resources:

```csharp
using Aspire.Hosting;
using Aspire.Hosting.ModularAppHosts;

namespace Catalog.Modules;

[GenerateDistributedApplicationModule(Name, Version = "1")]
public static partial class CatalogModule
{
    public const string Name = "catalog";
    public const string ApiResourceName = "catalog-api";

    public static void Define(IDistributedApplicationModuleBuilder module)
    {
        module.AddContainer(ApiResourceName, "nginx", "alpine")
            .Configure(container =>
                container.WithHttpEndpoint(targetPort: 80, name: "http"));
    }
}
```

Add the module and use its generated resources like ordinary Aspire resource builders:

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var catalog = await builder.AddCatalogModuleAsync();

builder.AddContainer("storefront", "nginx", "alpine")
    .WithReference(catalog.Api.GetEndpoint("http"))
    .WaitFor(catalog.Api);

await builder.Build().RunAsync();
```

From the AppHost directory, run `aspire run`, open the dashboard URL printed by Aspire, and use the `catalog-api` endpoint to verify the module is running.

For a repository-backed module, supply its repository through configuration or `WithRepository(...)` and materialize it with `await builder.ImportCatalogModuleAsync()`. Import options can prefix or alias resources when a receiving AppHost already uses the contract names. The module guide covers repository-aware factories, project/container selection, identity, and image publishing.

Inside another module's `Define` method, `CatalogModule.Reference(module)` returns the same strongly typed API and validates the required contract version. Module definitions can read the AppHost's `IConfiguration`, use their conventional `ConfigurationSection`, or call `GetOptions<T>()` to bind `IOptions<T>` from `Aspire:ModularAppHosts:Modules:<module-name>`.

By default, local modules run as projects, imported modules run as containers, and existing clean imported repositories with a configured upstream are fast-forwarded before startup. Clean local branches without an upstream and dirty checkouts are left unchanged. Image build commands remain opt-in. Set `UpdateImportedRepositories` or a module's `UpdateRepository` to `false` to keep a checkout fixed, and use `UseLocalModuleProjects()`, `UseModuleContainers()`, or `BuildModuleImages()` for AppHost-wide intent.

Module image build commands can follow Aspire's Docker or Podman selection by awaiting `ContainerRuntimeResolver.ResolveAsync()`. It honors `ASPIRE_CONTAINER_RUNTIME` and the legacy `DOTNET_ASPIRE_CONTAINER_RUNTIME` variable, otherwise probes both runtimes in parallel and prefers one that is running.

For a sibling-repository workflow, opt into `AutoCloneRepositories`. Same-worktree modules are discovered without a clone; a missing direct sibling is cloned with GitHub CLI. Published module images default to a branch-and-commit tag and add `-dirty` when their source worktree has changes. Each exported project or container publisher can select a separate `BuildRepository` and revision, so a resource may be defined in an application contract while its Dockerfile and build script remain in an owning repository. Repositories can be pinned to a branch, tag, or commit, and existing checkouts are verified against the configured origin. The module guide documents the layout, configuration, and validation behavior.

For an ongoing feature branch that must be exercised by another repository's CI, install the local
.NET tool, produce a request containing the clean pushed commit and any already-built image digests,
then dispatch the consumer's trusted workflow:

```bash
dotnet tool install --global Shirubasoft.Aspire.ModularAppHosts.Tool
dotnet modular-apphosts preview produce \
  --descriptor module-preview.producer.json \
  --contract-version 2.3.0-preview.7 \
  --image catalog-api=ghcr.io/example/catalog/api@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef \
  --output module-preview.json
dotnet modular-apphosts preview trigger \
  --manifest module-preview.json \
  --repo example/end-to-end-tests \
  --workflow module-preview-e2e.yml \
  --ref main \
  --wait \
  --github-output "$GITHUB_OUTPUT"
```

The request carries full commits and OCI digests rather than mutable branch names and image tags.
The producer descriptor may omit the contract for an image-only preview, or omit only its version
and receive the exact CI-computed version through `--contract-version`. The consumer policy decides
whether the contract is required and whether to restore its exact package from a reviewed HTTPS
NuGet source or pack it from a reviewed source fallback. Published-package materialization does not
check out or build the producer repository. A package feed is required only for requests that carry
a contract.

The consumer tool checks its own policy and writes a trusted resolution for
`ApplyModulePreviewResolutionAsync`. `preview trigger` prints `workflow_run_id` and
`workflow_run_url`; `--github-output` appends them to a GitHub Actions output file, while a bare
`--wait` returns the consumer run's final status. See the cross-repository preview guide for the
complete security model, package-source and source-fallback boundaries, and runnable two-repository
example.

Projects exported as containers can still run directly during local debugging. This changes run mode only; publishing continues to use the portable container representation:

```json
{
  "Aspire": {
    "ModularAppHosts": {
      "Modules": {
        "catalog": {
          "Projects": {
            "catalog-api": {
              "ProjectMode": "Project"
            }
          }
        }
      }
    }
  }
}
```

## Guides and samples

- [Module guide](https://github.com/Shirubasoft/aspire-modular-apphosts/blob/main/docs/modules.md): module contracts, generated resources, imports, repository behavior, and image publishing.
- [E2E testing guide](https://github.com/Shirubasoft/aspire-modular-apphosts/blob/main/docs/e2e-testing.md): one test suite for AppHost and Docker Compose modes.
- [Cross-repository preview guide](https://github.com/Shirubasoft/aspire-modular-apphosts/blob/main/docs/module-previews.md): exact-SHA manifests, workflow dispatch, preview contracts, and security boundaries.
- [Two-AppHost sample](https://github.com/Shirubasoft/aspire-modular-apphosts/tree/main/samples): one AppHost exports a mixed module and another imports it.
- [eShop E2E sample](https://github.com/Shirubasoft/aspire-modular-apphosts/tree/main/samples/E2ETesting): `catalog` and `orders` modules tested in both modes in CI.
- [Multi-repository E2E sample](https://github.com/Shirubasoft/aspire-modular-apphosts/tree/main/samples/MultiRepoE2E): an isolated consumer clones and runs a project from `Shirubasoft/spire` through the real GitHub CLI.
- [Cross-repository preview producer](https://github.com/Shirubasoft/aspire-modular-apphosts-preview-producer) and [consumer](https://github.com/Shirubasoft/aspire-modular-apphosts-preview-consumer): a producer feature branch changes source and its resource graph, then dispatches a trusted consumer E2E at the exact commit.

For repository setup, validation commands, and the release workflow, see [Contributing](https://github.com/Shirubasoft/aspire-modular-apphosts/blob/main/CONTRIBUTING.md).
