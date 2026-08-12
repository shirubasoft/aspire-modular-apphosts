# Modules

`Shirubasoft.Aspire.ModularAppHosts` lets a shared C# contract describe an Aspire resource graph. Each receiving AppHost chooses whether to materialize that graph locally. Repository-backed modules can instead use a managed checkout. Consumers still reference the producer-owned C# contract assembly; the Git repository provides source and build context, not executable module-definition code.

## Define and materialize a module

To scaffold the first contract into an existing project that references the core package, install the item-template package and choose the C# type name and stable module identity:

```bash
dotnet new install Shirubasoft.Aspire.ModularAppHosts.Templates
dotnet new aspire-module --name OrdersModule --moduleName orders --namespace Orders.Modules
```

The generated contract starts at version `1` with one container-backed API. Replace that example resource graph with the resources owned by the module.

The generated API defines and materializes a conventional `Define(IDistributedApplicationModuleBuilder)` contract in one call:

- `builder.AddOrdersModule()` uses the definition in the current application.
- `builder.ImportOrdersModule()` uses a managed checkout when the module configures a repository.

Keep the definition in a project referenced by every participating AppHost:

```csharp
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace Orders.Modules;

[GenerateDistributedApplicationModule(Name, Version = "1", PackageId = "Orders.Modules")]
public static partial class OrdersModule
{
    public const string Name = "orders";
    public const string ApiResourceName = "orders-api";
    public const string CacheResourceName = "orders-cache";

    public static void Define(IDistributedApplicationModuleBuilder module)
    {
        module.RequiresRepository();
        module.AddResource<ProjectResource>(ApiResourceName, context =>
            context.ApplicationBuilder
                .AddProject(
                    context.ResourceName,
                    Path.Combine(
                        context.RepositoryPath,
                        "src/Orders.Api/Orders.Api.csproj"))
                .WithHttpEndpoint(name: "http"));
        module.AddContainer(CacheResourceName, "redis", "8-alpine");
    }
}
```

An AppHost using the local definition adds it directly:

```csharp
builder.Configuration[
    DistributedApplicationModuleExtensions.GetRepositoryConfigurationKey(OrdersModule.Name)] = sourcePath;
var orders = builder.AddOrdersModule();
```

An importing AppHost registers the contract and imports by name:

```csharp
builder.Configuration[
    DistributedApplicationModuleExtensions.GetRepositoryConfigurationKey(OrdersModule.Name)] =
    "https://github.com/example/orders.git";
var orders = builder.ImportOrdersModule();
```

Both paths return the same generated `OrdersModule.Module` API:

```csharp
builder.AddContainer("consumer", "example/consumer", "latest")
    .WithReference(orders.Api.GetEndpoint("http"))
    .WaitFor(orders.Api)
    .WaitFor(orders.Cache);
```

## Initialize repository-backed imports

When an imported module needs remote repository content, module declaration registers an `initialize` pipeline step without invoking Git. From the AppHost directory run:

```bash
aspire do initialize --apphost . --non-interactive
aspire run
```

Initialization locates the AppHost Git root without executing Git and assigns managed checkouts as direct siblings of that root:

```text
<workspace>/consumer/                         # AppHost Git root
<workspace>/orders-<remote-hash>/             # unpinned checkout
<workspace>/orders-<remote-hash>-rev-.../     # isolated pinned checkout
~/.aspire/deployments/<apphost-sha>/<environment>.json
```

Equivalent repositories share one initialization step. Pinned revisions receive distinct paths and are checked out detached after fetch. Initialization validates existing origins, preserves dirty worktrees, fast-forwards clean unpinned branches when enabled, updates submodules, and writes credential-free per-repository sections through Aspire's deployment-state API. Repeating the command is idempotent.

An existing unpinned local repository path is used directly and is excluded from initialization. A local repository paired with a revision is treated as a clone source for an initializer-owned sibling, protecting the developer checkout. Repository values can come from `WithRepository`, `DistributedApplicationModuleOptions.Repository`, or the standard `Aspire:ModularAppHosts:Modules:<module>:Repository` configuration key. Use `GetRepositoryConfigurationKey(moduleName)` to construct that key.

Normal `aspire run` validates sibling directories, `.git` metadata, initialization state, project files, and build directories without cloning, fetching, pulling, or checking out. It ends an aggregate failure with an exact `aspire do initialize --apphost <path> --non-interactive` recovery command. Read-only Git inspection is used only when an image recipe evaluates source state. Set `RefreshBuildRepositoriesOnRun` globally or `RefreshBuildRepositoryOnRun` per resource to explicitly permit a clean unpinned build checkout to fast-forward during image preparation.

Project paths declared with `ModuleProjectPathBase.Repository` and repository-relative publish paths are compared with the operating system's path rules. Parent traversal and symbolic links that escape the repository are rejected.

See the [Two-AppHost sample](../samples/README.md) for a complete local and imported module.

## Generated resource API

`GenerateDistributedApplicationModule` generates synchronous module-specific builder extensions such as `AddOrdersModule` and `ImportOrdersModule`, plus a `Module` wrapper with one typed property per declared resource. The wrapper inherits the shared module contract delegation, so generated code only contains contract-specific resource properties. A constant ending in `ResourceName` becomes a property without that suffix, so `ApiResourceName` produces `Api`. The optional attribute `Version` identifies the contract with an exact, ordinal string comparison; defining the same module name with another version fails with both versions in the diagnostic. Bump it when resource names, exposed resource types, required configuration, endpoints, or materialization semantics change incompatibly. A repository branch, commit, or image rebuild does not by itself change the contract version. `PackageId` identifies the NuGet package that publishes the contract. Publish the updated contract package and update participating AppHosts together when a version changes.

| Contract declaration | Generated member |
| --- | --- |
| `[GenerateDistributedApplicationModule("orders")]` on `OrdersModule` | Nested `OrdersModule.Module` typed wrapper and module identity validation. |
| Conventional `Define(IDistributedApplicationModuleBuilder)` | `builder.AddOrdersModule()` and both `builder.ImportOrdersModule(...)` overloads. |
| An exported definition without conventional `Define` | `builder.AddOrdersModule(IDistributedApplicationModule)` advanced overload. |
| `OrdersModule.Reference(moduleBuilder)` | Version-checked typed reference for another module definition. |
| `ApiResourceName` passed to a recognized `Add...` call | `OrdersModule.Module.Api` with the declared Aspire resource type. |

Contracts need `using Aspire.Hosting;`; add `using Aspire.Hosting.ApplicationModel;` when the
definition names application-model resource types. Consuming AppHosts also import the contract's own
namespace, such as `using Orders.Modules;`. IDEs expose generated members under the analyzer's
generated files. The hint name is `<contract-namespace>_<type>.Module.g.cs`; to inspect a physical
copy, enable `EmitCompilerGeneratedFiles` and set `CompilerGeneratedFilesOutputPath` in the contract
project, conventionally to `$(BaseIntermediateOutputPath)generated`.

Advanced contracts that need inputs beyond configuration can omit the conventional `Define` method, register with `DefineModule`/`ExportModule`, and pass the resulting definition to the generated `builder.AddOrdersModule(definition)` overload. Use the overload whose third argument is the package ID when the contract is distributed as a package.

The annotated type must be a top-level, non-generic, static partial class. The generator recognizes `AddProject`, `AddContainer`, and `AddResource<TResource>` calls whose resource names are compile-time strings inside the conventional `Define` method. Advanced contracts are scanned in module-builder definition methods or a lambda passed directly to `DefineModule`/`ExportModule`. Calls in unrelated helpers are ignored so the typed API cannot advertise resources the selected definition never materializes. Invalid declarations, unsupported names, generated-member collisions, and custom resource types that are less accessible than the generated module API are reported as build diagnostics.

The generator supports .NET SDK 10.0.100 and later. Pin at least that version in `global.json`; patch releases and later .NET 10 feature bands are supported.

Use the untyped API for dynamic contracts. Generated `AddProject` properties use `IResourceWithEndpoints` because configuration can select a `ProjectResource` or `ContainerResource` at run time:

```csharp
builder.DefineModule(OrdersModule.Name, "1", OrdersModule.Define);
var orders = builder.ImportModule("orders");
var api = orders.GetResource<ProjectResource>("orders-api");
```

Unlike the generated `builder.ImportOrdersModule()` extension, the raw untyped import does not register the definition for you; call `DefineModule` or `ExportModule` first.

### Reference another module

Generated contracts expose `Reference`, which resolves another module by its generated name and contract version and returns its strongly typed resource API. Request dependencies in `Define` and use them exactly as an AppHost would:

```csharp
public static void Define(IDistributedApplicationModuleBuilder module)
{
    var catalog = CatalogModule.Reference(module);

    module.AddResource<ProjectResource>(ApiResourceName, context =>
        context.ApplicationBuilder
            .AddProject(context.ResourceName, "src/Orders.Api/Orders.Api.csproj")
            .WithEnvironment("Catalog__Endpoint", catalog.Api.GetEndpoint("http"))
            .WaitFor(catalog.Api));
}
```

Add or import dependencies before the dependent module so their definitions and resources are available:

```csharp
var catalog = builder.AddCatalogModule();
var orders = builder.AddOrdersModule();
```

A missing definition or incompatible contract version fails while the dependent module is defined. The generated reference preserves resource aliases and prefixes because it delegates to the dependency's materialized module.

### Module configuration and options

Definitions receive the AppHost's complete `IConfiguration` through `module.Configuration`. `module.ConfigurationSection` is the conventional `Aspire:ModularAppHosts:Modules:<module-name>` section, and `module.GetOptions<T>()` binds that section to an `IOptions<T>` value while preserving property defaults:

```csharp
public sealed class OrdersModuleOptions
{
    public string Region { get; set; } = "local";
}

public static void Define(IDistributedApplicationModuleBuilder module)
{
    var options = module.GetOptions<OrdersModuleOptions>().Value;
    module.AddContainer("orders-api", "example/orders-api")
        .Configure((_, container) => container.WithEnvironment("REGION", options.Region));
}
```

Configure it through any normal .NET configuration provider:

```json
{
  "Aspire": {
    "ModularAppHosts": {
      "Modules": {
        "orders": {
          "Region": "south-america"
        }
      }
    }
  }
}
```

Use `DistributedApplicationModuleExtensions.GetModuleConfigurationKey(moduleName)` when constructing the same section key programmatically. Options are bound when the module definition is first registered, so configure the builder before adding or importing it.

### Resource prefixes and aliases

An import can adapt contract names without changing typed lookups:

```csharp
var import = new ModuleImportOptions { ResourcePrefix = "sales-" };
import.ResourceAliases[OrdersModule.CacheResourceName] = "shared-cache";

var orders = builder.ImportOrdersModule(import);
// orders.Api resolves the Aspire resource "sales-orders-api".
// orders.Cache resolves "shared-cache".
```

Unknown aliases, aliases that map multiple resources to the same name, and collisions with resources already in the AppHost fail before any module resource is added.

## Resource kinds

### Existing container images

Use `AddContainer` for an image that already exists in a registry or the local container runtime. Its
`Configure` callback receives the same materialization context as project callbacks, so a declared
container can resolve resources declared earlier in the same module:

```csharp
module.AddResource<ParameterResource>("cache-password", context =>
    context.ApplicationBuilder.AddParameter(context.ResourceName, secret: true));

module.AddContainer("orders-cache", "redis", "8-alpine")
    .Configure((context, container) => container
        .WithEnvironment(
            "REDIS_PASSWORD",
            context.GetResource<ParameterResource>("cache-password"))
        .WithEndpoint(targetPort: 6379, name: "tcp"));
```

### Projects and repository-aware factories

Use `AddResource<TResource>` when a resource should run directly from the local or imported repository. Call `RequiresRepository()` once on the module and build paths from `context.RepositoryPath`, as in the module contract above. This makes imports request or discover repository content even when the module has no specialized `AddProject` declaration; synchronization occurs only through `initialize` or an explicit run-time refresh.

```csharp
module.RequiresRepository();
module.AddResource<ProjectResource>("orders-api", context =>
    context.ApplicationBuilder.AddProject(
        context.ResourceName,
        Path.Combine(context.RepositoryPath, "src", "Orders.Api", "Orders.Api.csproj")));
```

Repository-backed generic factories run while Aspire constructs the application model. They may compose paths from `context.RepositoryPath`, but they must not read repository content at declaration time. Missing checkout and file validation is deferred to normal-run preflight so `aspire do initialize` can construct the pipeline before the checkout exists.

Use the specialized `AddProject` API when the project must also have a portable container image. The simple overload delegates build and push behavior to Aspire:

```csharp
module.AddContainer("orders-cache", "redis");
module.AddProject<Projects.Orders_Api>("orders-api")
    .ConfigureProject((context, project) => project
        .WaitFor(context.GetResource<ContainerResource>("orders-cache"))
        .WithHttpHealthCheck("/health"))
    .ExportAsContainer("orders-api");
```

`ConfigureProject` applies when the project runs or when Aspire derives its container representation. The
`ExportAsContainer` callback applies additional container-only configuration. Those callbacks and declared
containers' `Configure` callbacks receive the materialization context, so they can call
`GetResource<TResource>` for resources declared earlier in the same module. The context also reports the
effective resource name, repository path, import state, and resolved image identity. This keeps resource
configuration aligned without mutable variables in the contract. A callback that asks for a later
resource fails during materialization; move that dependency before the consumer.

Contracts distributed as packages should declare the project relative to the module repository so the receiving AppHost does not need the same source-tree layout:

```csharp
module.AddProject(
        "orders-api",
        "src/Orders.Api/Orders.Api.csproj",
        ModuleProjectPathBase.Repository)
    .ExportAsContainer("orders-api");
```

Repository-relative paths are resolved only after the local source tree or imported checkout is selected. The two-argument `AddProject(name, projectPath)` overload remains relative to the defining AppHost, and generated `AddProject<TProject>` metadata follows that existing behavior.

### Any Aspire resource

`AddResource<TResource>` accepts a lazy factory for first-party integrations, community integrations, and custom resource types:

```csharp
module.AddResource<PostgresServerResource>("postgres", context =>
    context.ApplicationBuilder.AddPostgres(context.ResourceName));
```

Omit `RequiresRepository()` when every generic factory is independent of source files. A `WithImagePublishCommand` declaration marks its module as repository-backed automatically when the command uses the module repository. A publisher with an explicit `BuildRepository` can keep the module definition repository-independent.

Container-backed integrations can use the three-argument overload to publish a custom image while keeping their specialized resource type:

```csharp
module.AddResource<PostgresServerResource>(
    "postgres",
    context => context.ApplicationBuilder.AddPostgres(context.ResourceName),
    new ModuleImageCommandOptions(
        imageName: "example/orders-postgres",
        publishCommand: "docker",
        publishArguments:
        [
            "build",
            "--tag",
            ModuleImageCommandOptions.ImageReferencePlaceholder,
            "."
        ])
    {
        ImageRegistry = "ghcr.io"
    });
```

The overload is constrained to `ContainerResource`; integration server resources derived from it retain their typed APIs. Before the factory runs, `context.Image` contains the resolved registry, name, tag, optional digest, repository, and full effective reference. When a digest is configured, the reference uses the immutable `repository@sha256:...` form. After the factory returns, the library replaces any integration-default image and registry, applies configured `ImageSHA256` and `ImagePullPolicy` values from the module's `Containers` section, and attaches the same just-in-time image preparation callback used by declared containers.

Factory-created containers remain in the module's complete `Resources` collection, where `ResourceType` identifies the declared `ContainerResource` subtype. The narrower `Containers` collection contains only resources declared through `AddContainer` because a lazy integration factory does not have an image identity until materialization.

Factories run in declaration order when the module is materialized. The context provides the receiving builder, effective resource name, repository path, import state, and `GetResource<TResource>` for earlier resources in the same module. The returned resource must use `context.ResourceName`. That name already includes the import prefix or alias, so runtime container names can follow it when a fixed name is unavoidable:

```csharp
module.AddResource<ContainerResource>("cache", context =>
    context.ApplicationBuilder
        .AddContainer(context.ResourceName, "redis")
        .WithContainerName(context.ResourceName));
```

Prefixes and aliases do not rewrite arbitrary string configuration or fixed host ports. Prefer resource references and dynamically allocated host ports; when an integration requires literal values, derive every related name from `context.ResourceName` and make host ports configurable by the receiving AppHost.

Modules containing only repository-independent resources, such as existing images or parameters, can be imported without `WithRepository`.

## Module image workflows

Projects use Aspire's native container publisher through `ExportAsContainer(imageName)`. Module-owned
`AddDockerfile` resources also keep Aspire's standard Dockerfile build and push behavior. Use
`ExportAsContainerWithCommand` or `WithImagePublishCommand` only when an image requires an arbitrary
publisher command.

The [module image workflow guide](module-images.md) covers lazy run preparation, native and advanced
publishers, canonical identities, build/push/pull steps, read-only descriptions, workflow documents,
external-image overrides, and independent build repositories.
## AppHost configuration

Materialization policy is bound from `Aspire:ModularAppHosts` and registered as `IOptions<ModularAppHostsOptions>`. Every key is optional. Resource-specific values override module values, which override global defaults:

```json
{
  "Aspire": {
    "ModularAppHosts": {
      "GitHubCliPath": "gh",
      "GitExecutablePath": "git",
      "RepositoryCommandTimeout": "00:02:00",
      "ImageBuildTimeout": "00:15:00",
      "ImageTransferTimeout": "00:10:00",
      "UpdateRepositoriesOnInitialize": true,
      "RefreshBuildRepositoriesOnRun": false,
      "ProjectMode": "Auto",
      "Modules": {
        "orders": {
          "Repository": "https://github.com/example/orders.git",
          "RepositoryRevision": "release/2026-08",
          "UpdateRepositoryOnInitialize": false,
          "ProjectMode": "Container",
          "Projects": {
            "orders-api": {
              "ProjectMode": "Project",
              "LaunchProfileName": "https",
              "ExcludeLaunchProfile": false,
              "ExcludeKestrelEndpoints": false,
              "ImageRegistry": "ghcr.io",
              "ImageName": "example/orders-api",
              "ImageTag": "debug",
              "ProducedImageReference": "orders-api:build-output",
              "PullBeforeBuild": true,
              "PublishImage": true,
              "PublishCommand": "dotnet",
              "PublishArguments": [
                "publish",
                "Orders.Api.csproj",
                "-t:PublishContainer",
                "-p:ContainerRepository={image-name}",
                "-p:ContainerImageTag={image-tag}"
              ],
              "PublishWorkingDirectory": "src/Orders.Api",
              "BuildRepository": "https://github.com/example/orders-api-images.git",
              "BuildRepositoryRevision": "release/2026-08",
              "RefreshBuildRepositoryOnRun": true,
              "ImagePullPolicy": "Never"
            }
          },
          "Containers": {
            "orders-cache": {
              "ImageRegistry": "docker.io",
              "ImageName": "redis",
              "ImageTag": "8-alpine",
              "PublishImage": false,
              "ImagePullPolicy": "Missing"
            }
          }
        }
      }
    }
  }
}
```

`ProjectMode` is honored only in Aspire run mode. Its `Auto` default runs modules added from local source as projects and imported modules as containers; publish mode always uses the declared container representation. Image, command, build-repository, and build-revision settings can override an already-declared publisher, but configuration cannot introduce a publisher that is absent from the module contract.

A complete external image identity—registry, repository name, and exactly one tag or digest with `PublishImage: false`—removes that resource's source dependency. When every source-backed publisher is overridden and the module has no project or repository-backed factory, the imported module remains checkout-free. `images apply` configures this mode for workflow images.

Configured module, project, and container names are validated against exported definitions. A typo fails synchronously with the missing name and available names. Missing initialized repositories, initialization state records, project files, and build directories are aggregated by normal-run preflight into one actionable error.

The same options can be changed in code before materializing a module:

```csharp
builder.UseLocalModuleProjects();
builder.UseModuleContainers();
```

Use `ConfigureModularAppHosts` when several policies should be set together or computed in code:

```csharp
builder.ConfigureModularAppHosts(options =>
{
    options.UpdateRepositoriesOnInitialize = true;
    options.RefreshBuildRepositoriesOnRun = false;
    options.Modules[OrdersModule.Name] = new DistributedApplicationModuleOptions
    {
        Repository = "https://github.com/example/orders.git",
        RepositoryRevision = "v2.0.0",
        UpdateRepositoryOnInitialize = false
    };
});
```

Repository inspection and synchronization are bounded by `RepositoryCommandTimeout`; image builds use `ImageBuildTimeout`; image pulls, pushes, and tags use `ImageTransferTimeout`. `GitExecutablePath` and `GitHubCliPath` configure the repository tools without changing module contracts. Every remote is cloned through Git; GitHub HTTPS repositories use the configured `gh` only as a process-scoped credential provider. Lifecycle logs are structured, and raw command output stays with the relevant operation or resource. The library does not transform command output; configure secret masking in the execution environment.
