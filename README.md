# Shirubasoft.Aspire.ModularAppHosts

Define an Aspire resource graph once and reuse it across AppHosts. A module can be added from the current application, imported from a managed Git checkout, and exposed through a generated strongly typed API.

## Packages

| Package | Use it for |
| --- | --- |
| `Shirubasoft.Aspire.ModularAppHosts` | Defining, exporting, importing, and consuming modules in an AppHost. |
| `Shirubasoft.Aspire.ModularAppHosts.Testing` | Running the same E2E tests against an AppHost or an Aspire-managed Docker Compose deployment. |

Install the core package in AppHosts and shared module contracts:

```bash
dotnet add package Shirubasoft.Aspire.ModularAppHosts
```

The testing package is optional. Keeping it separate means regular AppHosts do not acquire `Aspire.Hosting.Testing` or Docker hosting dependencies.

```bash
dotnet add package Shirubasoft.Aspire.ModularAppHosts.Testing
```

Both packages target .NET 10 and Aspire 13.4.6 or later. Their APIs use the `Aspire.Hosting.ModularAppHosts` namespace.
They are licensed under the [MIT License](https://github.com/Shirubasoft/aspire-modular-apphosts/blob/main/LICENSE).

## Quick start

Install the repository's item template and scaffold a contract into an existing project that references the core package:

```bash
dotnet new install ./templates/aspire-module
dotnet new aspire-module --name CatalogModule --moduleName catalog
```

Declare a module in a shared contract. The generator creates typed properties for its resources:

```csharp
using Aspire.Hosting;
using Aspire.Hosting.ModularAppHosts;

[GenerateDistributedApplicationModule(Name, Version = "1")]
public static partial class CatalogModule
{
    public const string Name = "catalog";
    public const string ApiResourceName = "catalog-api";

    public static void Define(IDistributedApplicationModuleBuilder module)
    {
        module.AddContainer(ApiResourceName, "example/catalog-api", "latest")
            .Configure(container =>
                container.WithHttpEndpoint(targetPort: 8080, name: "http"));
    }
}
```

Add the module and use its generated resources like ordinary Aspire resource builders:

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var catalog = CatalogModule.AddModule(builder);

builder.AddContainer("storefront", "example/storefront", "latest")
    .WithReference(catalog.Api.GetEndpoint("http"))
    .WaitFor(catalog.Api);

builder.Build().Run();
```

For a repository-backed module, supply its repository through configuration or `WithRepository(...)` and materialize it with `CatalogModule.ImportModule(builder)`. Import options can prefix or alias resources when a receiving AppHost already uses the contract names. The module guide covers repository-aware factories, project/container selection, identity, and image publishing.

The defaults are side-effect safe: local modules run as projects, imported modules run as containers, and repository updates and image build commands require an explicit opt-in. Use `UseLocalModuleProjects()`, `UseModuleContainers()`, or `BuildModuleImages()` for AppHost-wide intent, with finer configuration available per module and resource.

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
