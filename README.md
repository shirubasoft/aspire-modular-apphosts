# Shirubasoft.Aspire.ModularAppHosts

Define an Aspire resource graph once and reuse it across AppHosts. A module can be added from the current application, imported from a managed Git checkout, and exposed through a generated strongly typed API. Every consumer references the producer-owned C# contract package, while the configured repository supplies the source and build context used to materialize that contract.

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

The optional testing package carries `Aspire.Hosting.Testing` and Docker hosting dependencies. Add it to both the AppHost that declares the test deployment environment and the test project that creates the deployment builder.

```bash
dotnet add package Shirubasoft.Aspire.ModularAppHosts.Testing
```

The runtime packages target .NET 10, the source generator supports .NET SDK 10.0.100 or later, and the Aspire-facing packages require Aspire 13.4.6 or later. Their APIs use the `Aspire.Hosting.ModularAppHosts` namespace.
They are licensed under the [MIT License](https://github.com/Shirubasoft/aspire-modular-apphosts/blob/main/LICENSE).

## Quick start

Prerequisites are .NET SDK 10.0.100 or later, Aspire CLI 13.4 or later, and a running Docker 28+ or Podman 5+ container runtime. In an existing AppHost or shared contract project, install the core package, install the item template, and scaffold a contract:

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

[GenerateDistributedApplicationModule(Name, Version = "1", PackageId = "Catalog.Modules")]
public static partial class CatalogModule
{
    public const string Name = "catalog";
    public const string ApiResourceName = "catalog-api";

    public static void Define(IDistributedApplicationModuleBuilder module)
    {
        module.AddContainer(ApiResourceName, "nginx", "alpine")
            .Configure((_, container) =>
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

For a repository-backed module, supply its repository through configuration or `WithRepository(...)` and materialize it with `await builder.ImportCatalogModuleAsync()`. Packaged contracts can declare specialized projects with `ModuleProjectPathBase.Repository`, preserving local project debugging without coupling the contract to the consumer's source-tree layout. Import options can prefix or alias resources when a receiving AppHost already uses the contract names. The module guide covers repository-aware factories, project/container selection, identity, and image publishing.

Inside another module's `Define` method, `CatalogModule.Reference(module)` returns the same strongly typed API and validates the required contract version. Module definitions can read the AppHost's `IConfiguration`, use their conventional `ConfigurationSection`, or call `GetOptions<T>()` to bind `IOptions<T>` from `Aspire:ModularAppHosts:Modules:<module-name>`.

By default, local modules run as projects, imported modules run as containers, and clean imported repositories with a configured upstream are fast-forwarded before startup. Local branches keep their current commit when they lack an upstream or contain changes. Image build commands are opt-in. Set `UpdateImportedRepositories` or a module's `UpdateRepository` to `false` to keep a checkout fixed, and use `UseLocalModuleProjects()`, `UseModuleContainers()`, or `BuildModuleImages()` for AppHost-wide intent.

Module image build commands can follow Aspire's Docker or Podman selection by awaiting `ContainerRuntimeResolver.ResolveAsync()`. It reads `ASPIRE_CONTAINER_RUNTIME`, accepts `DOTNET_ASPIRE_CONTAINER_RUNTIME`, and otherwise probes both runtimes in parallel to prefer one that is running. In publish mode, image publishers contribute `build-<resource>` steps and registry-backed images participate in `aspire do push` and `aspire do pull`; push depends on build, so CI can delegate module-owned build commands to Aspire. Pass declared or effective resource names after any aggregate step to operate on that subset, for example `aspire do pull catalog-api catalog-worker`. `aspire do describe-images --output-path artifacts` writes the same effective run, pull, push, and build identities to `artifacts/module-images.json` for CI tooling. A resource-level `WithImagePullMapping` can pull a remote reference from one registry and re-tag it as the resource image in another registry while retaining its push behavior.

For a sibling-repository workflow, opt into `AutoCloneRepositories`. Same-worktree modules are discovered in place; a missing direct sibling is cloned with GitHub CLI. Published module images default to a branch-and-commit tag and add `-dirty` when their source worktree has changes. Registries can be modeled separately from image names, factory-created `ContainerResource` integrations can publish custom images while retaining their typed APIs, missing clean images can be pulled before building, and custom build outputs can be retagged directly. Each exported project or container publisher can select a separate `BuildRepository` and revision, so a resource may be defined in an application contract while its Dockerfile and build script remain in an owning repository. Imported modules pinned to a branch, tag, or commit use isolated managed checkouts that protect sibling and AppHost developer worktrees. The module guide documents the layout, configuration, and validation behavior.

Set an exported project's run mode to `Project` for local debugging while keeping its portable container representation for publishing:

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
- [Image pipeline sample](https://github.com/Shirubasoft/aspire-modular-apphosts/tree/main/samples/ImagePushE2E): effective image descriptions plus real local-registry build, push, pull, and mapping validation.
- [Multi-repository E2E sample](https://github.com/Shirubasoft/aspire-modular-apphosts/tree/main/samples/MultiRepoE2E): an isolated consumer builds a module image from an independently pinned Git repository without changing the producer worktree.

For repository setup, validation commands, and the release workflow, see [Contributing](https://github.com/Shirubasoft/aspire-modular-apphosts/blob/main/CONTRIBUTING.md).
