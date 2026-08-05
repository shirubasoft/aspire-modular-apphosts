# Shirubasoft.Aspire.ModularAppHosts

Define an Aspire resource graph once and reuse it across AppHosts. A module can be added from the current application, imported from a managed Git checkout, and exposed through a generated strongly typed API. Importing does not load a module definition from Git: every consumer references the producer-owned C# contract package, while the configured repository supplies only the source and build context needed to materialize that contract.

## Packages

| Package | Use it for |
| --- | --- |
| `Shirubasoft.Aspire.ModularAppHosts` | Defining, exporting, importing, and consuming modules in an AppHost. |
| `Shirubasoft.Aspire.ModularAppHosts.Testing` | Running the same E2E tests against an AppHost or an Aspire-managed Docker Compose deployment. |
| `Shirubasoft.Aspire.ModularAppHosts.Templates` | Scaffolding a runnable module contract with `dotnet new aspire-module`. |

Install the core package in AppHosts and shared module contracts:

```bash
dotnet add package Shirubasoft.Aspire.ModularAppHosts
```

The testing package is optional. Keeping it separate means regular AppHosts do not acquire `Aspire.Hosting.Testing` or Docker hosting dependencies. Add it to both the AppHost that declares the test deployment environment and the test project that creates the deployment builder.

```bash
dotnet add package Shirubasoft.Aspire.ModularAppHosts.Testing
```

Both packages target .NET 10 and Aspire 13.4.6 or later. Their APIs use the `Aspire.Hosting.ModularAppHosts` namespace.
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

var catalog = await CatalogModule.AddModuleAsync(builder);

builder.AddContainer("storefront", "nginx", "alpine")
    .WithReference(catalog.Api.GetEndpoint("http"))
    .WaitFor(catalog.Api);

await builder.Build().RunAsync();
```

From the AppHost directory, run `aspire run`, open the dashboard URL printed by Aspire, and use the `catalog-api` endpoint to verify the module is running.

For a repository-backed module, supply its repository through configuration or `WithRepository(...)` and materialize it with `await CatalogModule.ImportModuleAsync(builder)`. Import options can prefix or alias resources when a receiving AppHost already uses the contract names. The module guide covers repository-aware factories, project/container selection, identity, and image publishing.

By default, local modules run as projects, imported modules run as containers, and existing clean imported repositories are fast-forwarded before startup. Image build commands remain opt-in. Set `UpdateImportedRepositories` or a module's `UpdateRepository` to `false` to keep a checkout fixed, and use `UseLocalModuleProjects()`, `UseModuleContainers()`, or `BuildModuleImages()` for AppHost-wide intent.

Module image build commands can follow Aspire's Docker or Podman selection by awaiting `ContainerRuntimeResolver.ResolveAsync()`. It honors `ASPIRE_CONTAINER_RUNTIME` and the legacy `DOTNET_ASPIRE_CONTAINER_RUNTIME` variable, otherwise probes both runtimes in parallel and prefers one that is running.

For a sibling-repository workflow, opt into `AutoCloneRepositories`. Same-worktree modules are discovered without a clone; a missing direct sibling is cloned with GitHub CLI. Published module images default to a branch-and-commit tag and add `-dirty` when their source worktree has changes. Repositories can be pinned to a branch, tag, or commit, and existing checkouts are verified against the configured origin. The module guide documents the layout, configuration, and validation behavior.

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
- [Two-AppHost sample](https://github.com/Shirubasoft/aspire-modular-apphosts/tree/main/samples): one AppHost exports a mixed module and another imports it.
- [eShop E2E sample](https://github.com/Shirubasoft/aspire-modular-apphosts/tree/main/samples/E2ETesting): `catalog` and `orders` modules tested in both modes in CI.
- [Multi-repository E2E sample](https://github.com/Shirubasoft/aspire-modular-apphosts/tree/main/samples/MultiRepoE2E): an isolated consumer clones and runs a project from `Shirubasoft/spire` through the real GitHub CLI.

For repository setup, validation commands, and the release workflow, see [Contributing](https://github.com/Shirubasoft/aspire-modular-apphosts/blob/main/CONTRIBUTING.md).
