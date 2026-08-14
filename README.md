# Shirubasoft.Aspire.ModularAppHosts

Define an Aspire resource graph once and reuse it across AppHosts. A module can be added from the current application, imported from a managed Git checkout, and exposed through a generated strongly typed API. Every consumer references the producer-owned C# contract package, while the configured repository supplies the source and build context used to materialize that contract.

## Packages

| Package | Use it for |
| --- | --- |
| `Shirubasoft.Aspire.ModularAppHosts` | Defining, exporting, importing, and consuming modules in an AppHost. |
| `Shirubasoft.Aspire.ModularAppHosts.Testing` | Running the same E2E tests against an AppHost or an Aspire-managed Docker Compose deployment. |
| `Shirubasoft.Aspire.ModularAppHosts.Templates` | Scaffolding a runnable module contract with `dotnet new aspire-module`. |
| `Shirubasoft.Aspire.ModularAppHosts.Tool` | Publishing/applying module image workflow documents and dispatching cross-repository E2E workflows. |

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

### Required host tools

Use a required-tool resource when another resource cannot start until a host command is available.
Unlike Aspire's `WithRequiredCommand`, which adds a startup warning to an existing resource,
`AddRequiredTool` creates a local-only resource with a live health check. Any wait-capable resource
can depend on it with `WaitFor`:

```csharp
var git = builder.AddRequiredTool("git-cli", "git")
    .WithWebsite("https://git-scm.com/downloads")
    .WithInstallCommand("brew", "install", "git");

builder.AddProject<Projects.Catalog_Api>("catalog-api")
    .WaitFor(git);
```

The dashboard shows the website and install commands. `aspire do initialize` also runs each missing
tool's installer before every other initialization prerequisite.
The installer command is passed directly as an executable and argument list; no shell is implied.
Select the command with normal AppHost configuration or platform checks when installation differs
between local environments. Required-tool resources are excluded from deployment manifests because
they describe tools on the AppHost machine.

For a repository-backed module, supply its repository through configuration or `WithRepository(...)` and materialize it with `builder.ImportCatalogModule()`. Packaged contracts can declare specialized projects with `ModuleProjectPathBase.Repository`, preserving local project debugging without coupling the contract to the consumer's source-tree layout. Import options can prefix or alias resources when a receiving AppHost already uses the contract names. The module guide covers repository-aware factories, project/container selection, identity, and image publishing.

Inside another module's `Define` method, `CatalogModule.Reference(module)` returns the same strongly typed API and validates the required contract version. Module definitions can read the AppHost's `IConfiguration`, use their conventional `ConfigurationSection`, or call `GetOptions<T>()` to bind `IOptions<T>` from `Aspire:ModularAppHosts:Modules:<module-name>`.

By default, local modules run as projects and imported modules run as containers. Module declaration is synchronous and performs no Git or image operations. Remote and pinned repositories are acquired from the AppHost directory with `aspire do initialize --apphost . --non-interactive`; their machine-local state is stored independently of the AppHost environment. Normal run fails fast with the exact state path and AppHost-aware recovery command when a required sibling checkout, initialization record, project, or build directory is missing. Optional tagged-image build repositories are not inspected at run time. Use `UseLocalModuleProjects()` or `UseModuleContainers()` for checked-in AppHost-wide intent. AppHosts with a `UserSecretsId` also expose `aspire do use-projects`, `use-containers`, and per-resource steps such as `use-project-catalog-api`; these persist a developer-local override and take effect after the next AppHost start. Run `aspire do use-configured-modes` to remove every temporary override.

Projects use Aspire's native container publisher through `ExportAsContainer(imageName)` by default,
and module-owned `AddDockerfile` resources retain Aspire's Dockerfile build and push operations.
Advanced image commands can follow Aspire's Docker or Podman selection by using
`ModuleImageCommandOptions.ContainerRuntimePlaceholder` with `ExportAsContainerWithCommand(...)` or
`WithImagePublishCommand(...)`. Registry-backed images participate in `aspire do build`, `push`, and
`pull`; dirty source may build and run locally but cannot be pushed. A clean advanced publisher also
pushes a sanitized source-branch alias, while module image workflow documents retain the exact
canonical tag. Named Aspire steps operate on one resource; repeatable `images publish --module` and
`--resource` options select a validated graph. `aspire do describe-images --output-path artifacts`
writes effective identities without preparing images.

Initialization places an unpinned remote in a human-readable sibling named from the normalized
repository, such as `<workspace>/orders`. Before creating that slug, planning adopts an existing sibling
whose directory name normalizes to the same slug, so names such as `Repo_A` remain usable by default.
A matching existing sibling is adopted as developer-owned and is never updated by initialization;
a missing sibling is cloned as initializer-managed and may be fast-forwarded on later initialization
runs. Name or origin conflicts fail with the exact
`CheckoutDirectoryName` key instead of falling back to a hash. Pinned revisions continue to receive
collision-resistant hashed siblings that protect developer worktrees. Advanced command
publishers inspect branch, commit, and dirty state immediately before their container starts, reuse
or optionally pull a clean canonical image, build when needed, and retag the result to a deterministic
`aspire-run` alias. Explicit-start resources remain lazy. Each advanced publisher can select a
separate `BuildRepository` and revision, and an explicit refresh may fast-forward only a clean,
unpinned build checkout.

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
module images and writes a strict module image workflow document:

```bash
dotnet tool run modular-apphosts -- images publish \
  --apphost src/RepoB.AppHost --module orders --tag "$GITHUB_SHA"
```

Repo A runs its ordinary E2E command with that workflow document, using the same invocation locally and in
GitHub Actions:

```bash
dotnet tool run modular-apphosts -- images apply \
  --json "$IMAGE_WORKFLOW" \
  -- \
  dotnet test tests/RepoA.E2E.Tests/RepoA.E2E.Tests.csproj --configuration Release
```

When Repo B needs a separate Repo A run, one command dispatches the exact run, waits for it, and
returns its status:

```bash
dotnet tool run modular-apphosts -- workflow dispatch \
  --repository your-org/repo-a --workflow external-e2e.yml \
  --workflow-document module-image-workflow.json
```

See the [tool reference](https://github.com/Shirubasoft/aspire-modular-apphosts/blob/main/src/Aspire.Hosting.ModularAppHosts.Tool/README.md)
and [cross-repository guide](https://github.com/Shirubasoft/aspire-modular-apphosts/blob/main/docs/external-e2e-workflows.md)
for pinned setup, tag precedence, complete workflow files, permissions, and troubleshooting.

## Guides and samples

- [Module guide](https://github.com/Shirubasoft/aspire-modular-apphosts/blob/main/docs/modules.md): module contracts, generated resources, imports, initialization, and configuration.
- [Module image workflow guide](https://github.com/Shirubasoft/aspire-modular-apphosts/blob/main/docs/module-images.md): native and advanced image publishers, lifecycle, pipeline steps, and workflow documents.
- [E2E testing guide](https://github.com/Shirubasoft/aspire-modular-apphosts/blob/main/docs/e2e-testing.md): one test suite for AppHost and Docker Compose modes.
- [Cross-repository E2E workflow guide](https://github.com/Shirubasoft/aspire-modular-apphosts/blob/main/docs/external-e2e-workflows.md): module image workflow document publication, application, reusable-workflow handoff, and script-free GitHub CLI dispatch.
- [Two-AppHost sample](https://github.com/Shirubasoft/aspire-modular-apphosts/tree/main/samples): one AppHost exports a mixed module and another imports it.
- [eShop E2E sample](https://github.com/Shirubasoft/aspire-modular-apphosts/tree/main/samples/E2ETesting): `catalog` and `orders` modules tested in both modes in CI.
- [Image pipeline sample](https://github.com/Shirubasoft/aspire-modular-apphosts/tree/main/samples/ImagePushE2E): effective image descriptions plus real local-registry build, push, pull, and mapping validation.
- [Multi-repository E2E sample](https://github.com/Shirubasoft/aspire-modular-apphosts/tree/main/samples/MultiRepoE2E): an isolated consumer plus a two-AppHost local-registry workflow image handoff.
- [Remote initialization sample](https://github.com/Shirubasoft/aspire-modular-apphosts/tree/main/samples/RemoteInitialization): a Git required-tool resource installed before repository initialization and used as a health-gated service dependency.

For repository setup, validation commands, and the release workflow, see [Contributing](https://github.com/Shirubasoft/aspire-modular-apphosts/blob/main/CONTRIBUTING.md).
