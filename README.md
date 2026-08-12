# Shirubasoft.Aspire.ModularAppHosts

Define an Aspire resource graph once and reuse it across AppHosts. A module can be added from the current application, imported from a managed Git checkout, and exposed through a generated strongly typed API. Every consumer references the producer-owned C# contract package, while the configured repository supplies the source and build context used to materialize that contract.

## Packages

| Package | Use it for |
| --- | --- |
| `Shirubasoft.Aspire.ModularAppHosts` | Defining, exporting, importing, and consuming modules in an AppHost. |
| `Shirubasoft.Aspire.ModularAppHosts.Testing` | Running the same E2E tests against an AppHost or an Aspire-managed Docker Compose deployment. |
| `Shirubasoft.Aspire.ModularAppHosts.Templates` | Scaffolding a runnable module contract with `dotnet new aspire-module`. |
| `Shirubasoft.Aspire.ModularAppHosts.Tool` | Publishing/applying workflow image manifests and dispatching cross-repository E2E workflows. |

Install the core package in AppHosts and shared module contracts:

```bash
dotnet add package Shirubasoft.Aspire.ModularAppHosts
```

The optional testing package carries `Aspire.Hosting.Testing` and Docker hosting dependencies. Add it to both the AppHost that declares the test deployment environment and the test project that creates the deployment builder.

```bash
dotnet add package Shirubasoft.Aspire.ModularAppHosts.Testing
```

The runtime packages target .NET 10, the source generator supports .NET SDK 10.0.100 or later, and the Aspire-facing packages require Aspire 13.4.6 or later. The core APIs extend Aspire's existing `Aspire.Hosting` namespace, while deployment-testing APIs use `Aspire.Hosting.Testing`.
They are licensed under the [MIT License](https://github.com/Shirubasoft/aspire-modular-apphosts/blob/main/LICENSE).

## Quick start

Prerequisites are .NET SDK 10.0.100 or later, Aspire CLI 13.4.6 or later, and a running Docker 28+ or Podman 5+ container runtime. In an existing AppHost or shared contract project, install the core package, install the item template, and scaffold a contract:

```bash
dotnet add package Shirubasoft.Aspire.ModularAppHosts
dotnet new install Shirubasoft.Aspire.ModularAppHosts.Templates
dotnet new aspire-module --name CatalogModule --moduleName catalog --namespace Catalog.Modules
```

Replace the generated `CatalogModule.cs` content with this runnable contract. The generator creates typed properties for its resources:

```csharp
using Aspire.Hosting;

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
using Aspire.Hosting;
using Catalog.Modules;

var builder = DistributedApplication.CreateBuilder(args);

var catalog = builder.AddCatalogModule();

builder.AddContainer("storefront", "nginx", "alpine")
    .WithReference(catalog.Api.GetEndpoint("http"))
    .WaitFor(catalog.Api);

await builder.Build().RunAsync();
```

From the AppHost directory, run `aspire run`, open the dashboard URL printed by Aspire, and use the `catalog-api` endpoint to verify the module is running.

For a repository-backed module, supply its repository through configuration or `WithRepository(...)` and materialize it with `builder.ImportCatalogModule()`. Packaged contracts can declare specialized projects with `ModuleProjectPathBase.Repository`, preserving local project debugging without coupling the contract to the consumer's source-tree layout. Import options can prefix or alias resources when a receiving AppHost already uses the contract names. The module guide covers repository-aware factories, project/container selection, identity, and image publishing.

Inside another module's `Define` method, `CatalogModule.Reference(module)` returns the same strongly typed API and validates the required contract version. Module definitions can read the AppHost's `IConfiguration`, use their conventional `ConfigurationSection`, or call `GetOptions<T>()` to bind `IOptions<T>` from `Aspire:ModularAppHosts:Modules:<module-name>`.

By default, local modules run as projects and imported modules run as containers. Module declaration is synchronous and performs no Git or image operations. Remote and pinned repositories are acquired from the AppHost directory with `aspire do initialize --apphost . --non-interactive`; normal run fails fast with the exact AppHost-aware recovery command when a sibling checkout, initialization state record, project, or build directory is missing. Use `UseLocalModuleProjects()` or `UseModuleContainers()` for AppHost-wide project-mode intent.

Module image build commands can follow Aspire's Docker or Podman selection by using `ModuleContainerExportOptions.ContainerRuntimePlaceholder` as their publish command. The command resolves through Aspire's `IContainerRuntimeResolver` only when it runs, keeping AppHost declaration synchronous and consistent with Aspire's configured runtime. In publish mode, image publishers contribute `build-<resource>` steps and registry-backed images participate in `aspire do push` and `aspire do pull`; push depends on build, so CI can delegate module-owned build commands to Aspire. A clean push publishes the canonical image plus a sanitized source-branch alias, allowing default-branch consumers to use a stable tag while workflow image manifests retain the exact canonical tag. Use Aspire's named resource steps, such as `aspire do pull-catalog-api`, for one resource; use the tool's `manifest publish --selector` option for validated multi-resource workflow selection. `aspire do describe-images --output-path artifacts` writes the effective run, pull, push, and build identities to `artifacts/module-images.json` without preparing images. A resource-level `WithImagePullMapping` can pull a remote reference from one registry and re-tag it as the resource image in another registry while retaining its push behavior.

Initialization places remote checkouts in collision-resistant directories beside the AppHost Git root; pinned revisions receive distinct siblings that protect developer worktrees. Immediately before a published container starts, its Aspire resource callback inspects branch, commit, and dirty state, reuses or optionally pulls a clean canonical image, builds when needed, and retags the result to a deterministic `aspire-run` alias. Explicit-start resources remain lazy. Dirty source always rebuilds. Each publisher can select a separate `BuildRepository` and revision, and an explicit refresh option may fast-forward only clean unpinned build checkouts. The module guide documents the layout, configuration, and validation behavior.

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

## Cross-repository E2E in three commands

Pin the tool in both repos with a committed .NET tool manifest. Repo B publishes its selected
module images and writes a strict workflow image manifest:

```bash
dotnet tool run modular-apphosts -- manifest publish \
  --apphost src/RepoB.AppHost --selector orders --tag "$GITHUB_SHA"
```

Repo A runs its ordinary E2E command with that manifest, using the same invocation locally and in
GitHub Actions:

```bash
dotnet tool run modular-apphosts -- manifest apply \
  --json "$IMAGE_MANIFEST" \
  -- \
  dotnet test tests/RepoA.E2E.Tests/RepoA.E2E.Tests.csproj --configuration Release
```

When Repo B needs a separate Repo A run, one command dispatches the exact run, waits for it, and
returns its status:

```bash
dotnet tool run modular-apphosts -- workflow dispatch \
  --repository your-org/repo-a --workflow external-e2e.yml \
  --manifest module-image-manifest.json
```

See the [tool reference](https://github.com/Shirubasoft/aspire-modular-apphosts/blob/main/src/Aspire.Hosting.ModularAppHosts.Tool/README.md)
and [cross-repository guide](https://github.com/Shirubasoft/aspire-modular-apphosts/blob/main/docs/external-e2e-workflows.md)
for pinned setup, tag precedence, complete workflow files, permissions, and troubleshooting.

## Guides and samples

- [Module guide](https://github.com/Shirubasoft/aspire-modular-apphosts/blob/main/docs/modules.md): module contracts, generated resources, imports, repository behavior, and image publishing.
- [E2E testing guide](https://github.com/Shirubasoft/aspire-modular-apphosts/blob/main/docs/e2e-testing.md): one test suite for AppHost and Docker Compose modes.
- [Cross-repository E2E workflow guide](https://github.com/Shirubasoft/aspire-modular-apphosts/blob/main/docs/external-e2e-workflows.md): workflow image manifest publication, application, reusable-workflow handoff, and script-free GitHub CLI dispatch.
- [Upgrade guide](https://github.com/Shirubasoft/aspire-modular-apphosts/blob/main/docs/upgrading.md): migration steps for the namespace, synchronous contract, repository, image, and workflow API redesign.
- [Two-AppHost sample](https://github.com/Shirubasoft/aspire-modular-apphosts/tree/main/samples): one AppHost exports a mixed module and another imports it.
- [eShop E2E sample](https://github.com/Shirubasoft/aspire-modular-apphosts/tree/main/samples/E2ETesting): `catalog` and `orders` modules tested in both modes in CI.
- [Image pipeline sample](https://github.com/Shirubasoft/aspire-modular-apphosts/tree/main/samples/ImagePushE2E): effective image descriptions plus real local-registry build, push, pull, and mapping validation.
- [Multi-repository E2E sample](https://github.com/Shirubasoft/aspire-modular-apphosts/tree/main/samples/MultiRepoE2E): an isolated consumer plus a two-AppHost local-registry workflow image handoff.

For repository setup, validation commands, and the release workflow, see [Contributing](https://github.com/Shirubasoft/aspire-modular-apphosts/blob/main/CONTRIBUTING.md).
