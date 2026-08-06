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

- `await builder.AddOrdersModuleAsync()` uses the definition in the current application.
- `await builder.ImportOrdersModuleAsync()` uses a managed checkout when the module configures a repository.

Keep the definition in a project referenced by every participating AppHost:

```csharp
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ModularAppHosts;

namespace Orders.Modules;

[GenerateDistributedApplicationModule(Name, Version = "1")]
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
var orders = await builder.AddOrdersModuleAsync();
```

An importing AppHost registers the contract and imports by name:

```csharp
builder.Configuration[
    DistributedApplicationModuleExtensions.GetRepositoryConfigurationKey(OrdersModule.Name)] =
    "https://github.com/example/orders.git";
var orders = await builder.ImportOrdersModuleAsync();
```

Both paths return the same generated `OrdersModule.Module` API:

```csharp
builder.AddContainer("consumer", "example/consumer", "latest")
    .WithReference(orders.Api.GetEndpoint("http"))
    .WaitFor(orders.Api)
    .WaitFor(orders.Cache);
```

## Generated resource API

`GenerateDistributedApplicationModule` generates module-specific builder extensions such as `AddOrdersModuleAsync` and `ImportOrdersModuleAsync`, plus a `Module` wrapper with one typed property per declared resource. The wrapper inherits the shared module contract delegation, so generated code only contains contract-specific resource properties. A constant ending in `ResourceName` becomes a property without that suffix, so `ApiResourceName` produces `Api`. The optional attribute `Version` identifies the contract with an exact, ordinal string comparison; defining the same module name with another version fails with both versions in the diagnostic. Bump it when resource names, exposed resource types, required configuration, endpoints, or materialization semantics change incompatibly. A repository branch, commit, or image rebuild does not by itself change the contract version. Publish the updated contract package and update participating AppHosts together when a version changes.

Advanced contracts that need inputs beyond configuration can omit the conventional `Define` method, register with `DefineModuleAsync`/`ExportModuleAsync`, and pass the resulting definition to the generated `builder.AddOrdersModuleAsync(definition)` overload.

The annotated type must be a top-level, non-generic, static partial class. The generator recognizes `AddProject`, `AddContainer`, and `AddResource<TResource>` calls whose resource names are compile-time strings inside the conventional `Define` method. Advanced contracts are scanned in module-builder definition methods or a lambda passed directly to `DefineModuleAsync`/`ExportModuleAsync`. Calls in unrelated helpers are ignored so the typed API cannot advertise resources the selected definition never materializes. Invalid declarations, unsupported names, generated-member collisions, and custom resource types that are less accessible than the generated module API are reported as build diagnostics.

The untyped API remains available when a generated contract is unnecessary. Generated `AddProject` properties use `IResourceWithEndpoints` because configuration can select a `ProjectResource` or `ContainerResource` at run time:

```csharp
await builder.DefineModuleAsync(OrdersModule.Name, "1", OrdersModule.Define);
var orders = await builder.ImportModuleAsync("orders");
var api = orders.GetResource<ProjectResource>("orders-api");
```

Unlike the generated `builder.ImportOrdersModuleAsync()` extension, the raw untyped import does not register the definition for you; call `DefineModuleAsync` or `ExportModuleAsync` first.

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
var catalog = await builder.AddCatalogModuleAsync();
var orders = await builder.AddOrdersModuleAsync();
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
        .Configure(container => container.WithEnvironment("REGION", options.Region));
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

var orders = await builder.ImportOrdersModuleAsync(import);
// orders.Api resolves the Aspire resource "sales-orders-api".
// orders.Cache resolves "shared-cache".
```

Unknown aliases, aliases that map multiple resources to the same name, installer-name collisions, and collisions with resources already in the AppHost fail before any module resource is added.

## Resource kinds

### Existing container images

Use `AddContainer` for an image that already exists in a registry or the local container runtime:

```csharp
module.AddContainer("orders-cache", "redis", "8-alpine")
    .Configure(container => container.WithEndpoint(targetPort: 6379, name: "tcp"));
```

### Projects and repository-aware factories

Use `AddResource<TResource>` when a resource should run directly from the local or imported repository. Call `RequiresRepository()` once on the module and build paths from `context.RepositoryPath`, as in the module contract above. This makes imports request, discover, or synchronize repository content even when the module has no specialized `AddProject` declaration.

```csharp
module.RequiresRepository();
module.AddResource<ProjectResource>("orders-api", context =>
    context.ApplicationBuilder.AddProject(
        context.ResourceName,
        Path.Combine(context.RepositoryPath, "src", "Orders.Api", "Orders.Api.csproj")));
```

Repository-backed generic factories run while Aspire constructs the application model, so their repository must come from configuration, programmatic options, or `WithRepository`. A missing managed checkout is cloned before the factory runs. An interactive repository parameter cannot be used for this case because Aspire presents that input only after model construction.

Use the specialized `AddProject` API when the project must be represented as a portable container image. These project declarations require the exact command that produces their image:

```csharp
module.AddProject<Projects.Orders_Api>("orders-api")
    .ConfigureProject(project => project.WithHttpHealthCheck("/health"))
    .ExportAsContainer(new ModuleContainerExportOptions(
        imageName: "orders-api",
        publishCommand: "dotnet",
        publishArguments:
        [
            "publish",
            "Orders.Api.csproj",
            "-t:PublishContainer",
            $"-p:ContainerRepository={ModuleContainerExportOptions.ImageNamePlaceholder}",
            $"-p:ContainerImageTag={ModuleContainerExportOptions.ImageTagPlaceholder}"
        ]));
```

`ConfigureProject` applies when run-mode configuration selects the project for debugging. The existing `ExportAsContainer` callback applies to its container representation.

### Any Aspire resource

`AddResource<TResource>` accepts a lazy factory for first-party integrations, community integrations, and custom resource types:

```csharp
module.AddResource<PostgresServerResource>("postgres", context =>
    context.ApplicationBuilder.AddPostgres(context.ResourceName));
```

Omit `RequiresRepository()` when every generic factory is independent of source files. A `WithImagePublishCommand` declaration marks its module as repository-backed automatically when the command uses the module repository. A publisher with an explicit `BuildRepository` can keep the module definition repository-independent.

A generic factory can also own an image built by an explicit command. Pass `ModuleContainerExportOptions` to `AddResource<TResource>` when the resource is not a plain container—an Aspire integration such as `AddSqlServer` or `AddPostgres`—but its image still has to be built:

```csharp
module.AddResource<SqlServerServerResource>(
    ServerResourceName,
    context => context.ApplicationBuilder
        .AddSqlServer(context.ResourceName, password)
        // AddSqlServer brings its own registry and WithImage only replaces the name, so a registry-qualified
        // module image also has to replace the registry.
        .WithImage("orders-database", context.Image!.Tag)
        .WithImageRegistry("ghcr.io/example"),
    new ModuleContainerExportOptions(
        imageName: "ghcr.io/example/orders-database",
        publishCommand: "pwsh",
        publishArguments: ["build-docker.ps1"])
    {
        ImageTag = "production",
        BuildRepository = "https://github.com/example/orders-database.git",
        WorkingDirectory = "."
    });
```

The factory receives the resolved image through `context.Image` and must apply it to the resource it creates; the name and tag already include configuration overrides and the `-dirty` suffix. The resource waits for the same one-shot installer a declared container gets, so `TResource` must support waiting, and it is configured under the module's `Containers` section like any other module image. Because the factory owns the resource, `ImagePullPolicy` and `ImageSHA256` overrides do not apply—set those in the factory itself.

Factories run in declaration order when the module is materialized. The context provides the receiving builder, required resource name, repository path, import state, and `GetResource<TResource>` for earlier resources in the same module. The returned resource must use `context.ResourceName`.

Modules containing only repository-independent resources, such as existing images or parameters, can be imported without `WithRepository`.

## Publishing module images

The library does not infer how an image is built. `ExportAsContainer` publishes a project image, while `WithImagePublishCommand` attaches an explicit build command to an `AddContainer` resource. Resolve the command with `ContainerRuntimeResolver` when the module should follow Aspire's Docker or Podman selection:

```csharp
var containerRuntime = await ContainerRuntimeResolver.ResolveAsync(cancellationToken);

module.AddContainer("orders-static", "orders-static")
    .WithImagePublishCommand(new ModuleContainerExportOptions(
        imageName: "orders-static",
        publishCommand: containerRuntime,
        publishArguments:
        [
            "build",
            "--tag",
            ModuleContainerExportOptions.ImageReferencePlaceholder,
            "."
        ]));
```

`ASPIRE_CONTAINER_RUNTIME` takes precedence over the legacy `DOTNET_ASPIRE_CONTAINER_RUNTIME` variable. Without an explicit value, the resolver probes Docker and Podman in parallel, prefers a running runtime over one that is merely installed, and uses Docker as its tie-breaker and fallback.

In run mode, a one-shot installer invokes the configured executable before the container starts when the image needs publishing:

- When `ImageTag` is omitted, the module repository branch is lowercased and sanitized and its 12-character commit is appended (`feature/orders` becomes `feature-orders-a1b2c3d4e5f6`). Detached checkouts use `sha-a1b2c3d4e5f6`. A known managed repository is synchronized before this tag is selected. For repository-independent or still-unresolved definitions, the AppHost branch and commit are used, then CI branch variables, then `latest`.
- A clean repository uses `ImageName:ImageTag` and reuses that image when it already exists locally.
- A dirty repository uses `ImageName:ImageTag-dirty` and rebuilds it for every AppHost session. The tag remains within the 128-character distribution limit.
- The container waits for its installer to complete successfully.
- Installers are run-only resources and are excluded from deployment manifests.

Publish arguments can use the `{image}`, `{image-name}`, and `{image-tag}` constants on `ModuleContainerExportOptions`. The effective image reference is also available to the command as `ASPIRE_MODULE_IMAGE`.

`WorkingDirectory` is relative to the effective build repository root. It defaults to the project directory for `ExportAsContainer` when the module repository also builds the image. A separate build repository and `WithImagePublishCommand` both default to the build repository root. The command and arguments are executed directly without a shell.

### Build a resource from another repository

The repository that defines a resource and the repository that builds its image are independent. Set `BuildRepository` on the resource's export options when, for example, an application contract declares a custom database container but the Dockerfile and build script belong to the database repository:

```csharp
module.AddContainer("orders-database", "example/orders-database")
    .WithImagePublishCommand(new ModuleContainerExportOptions(
        imageName: "example/orders-database",
        publishCommand: "./build-image.sh",
        publishArguments: [ModuleContainerExportOptions.ImageReferencePlaceholder])
    {
        BuildRepository = "https://github.com/example/orders-database.git",
        BuildRepositoryRevision = "main",
        WorkingDirectory = "."
    });
```

The AppHost resolves and synchronizes that checkout independently of the module repository, runs the publisher from it, and uses the build checkout's branch, commit, and dirty state for the default image tag. The container's module annotation still points to the definition repository; the generated installer points to the build repository. An exact `BuildRepositoryRevision` that differs from the definition checkout is checked out in a separate managed worktree, so selecting a database commit never changes the application checkout or a sibling source worktree.

The receiving AppHost can replace the declaration for one environment without changing the shared contract:

```csharp
builder.ConfigureModularAppHosts(options =>
{
    options.Modules[OrdersModule.Name] = new DistributedApplicationModuleOptions
    {
        Containers =
        {
            ["orders-database"] = new DistributedApplicationModuleContainerOptions
            {
                BuildRepository = "/work/orders-database",
                BuildRepositoryRevision = "feature/new-schema",
                AutoCloneBuildRepository = false,
                UpdateBuildRepository = false
            }
        }
    };
});
```

Relative local build-repository paths are resolved from the AppHost directory. `AutoCloneBuildRepository` selects the same direct-sibling convention as module auto-cloning; otherwise the checkout is placed under `RepositoryBasePath`. `UpdateBuildRepository` controls updates independently of the definition checkout. If publishing is disabled and the resource has an explicit tag or `ImageSHA256`, no build checkout is required—the origin repository's already-built image can be pulled directly.

## AppHost configuration

Materialization policy is bound from `Aspire:ModularAppHosts` and registered as `IOptions<ModularAppHostsOptions>`. Every key is optional. Resource-specific values override module values, which override global defaults:

```json
{
  "Aspire": {
    "ModularAppHosts": {
      "RepositoryBasePath": "/work/aspire-repositories",
      "AutoCloneRepositories": false,
      "GitHubCliPath": "gh",
      "GitExecutablePath": "git",
      "RepositoryCommandTimeout": "00:02:00",
      "UpdateImportedRepositories": true,
      "ProjectMode": "Auto",
      "PublishImages": false,
      "Modules": {
        "orders": {
          "Repository": "https://github.com/example/orders.git",
          "RepositoryRevision": "release/2026-08",
          "AutoCloneRepository": true,
          "UpdateRepository": false,
          "ProjectMode": "Container",
          "PublishImages": true,
          "Projects": {
            "orders-api": {
              "ProjectMode": "Project",
              "LaunchProfileName": "https",
              "ExcludeLaunchProfile": false,
              "ExcludeKestrelEndpoints": false,
              "ImageName": "example/orders-api",
              "ImageTag": "debug",
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
              "AutoCloneBuildRepository": false,
              "UpdateBuildRepository": true,
              "ImagePullPolicy": "Never"
            }
          },
          "Containers": {
            "orders-cache": {
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

`ProjectMode` is honored only in Aspire run mode. Its safe `Auto` default runs modules added from local source as projects and imported modules as containers; publish mode always uses the declared container representation. Running an imported project directly requires its managed checkout to exist when the AppHost model is built.

Existing clean imported repositories update by default. Set `UpdateRepository` or `UpdateImportedRepositories` to `false` where a checkout must remain fixed. Resource-level `UpdateBuildRepository` and `AutoCloneBuildRepository` independently override those policies for a separate image-build checkout. Image build commands remain opt-in through `PublishImage`/`PublishImages`. Image, command, build-repository, and build-revision settings override a publisher declared by `ExportAsContainer` or `WithImagePublishCommand`; configuration cannot introduce an undeclared publisher. `PublishImage: false` skips the run-only installer and leaves image acquisition to the configured pull policy; an explicit tag or digest also avoids resolving an unused build repository.

Configured module, project, and container names are validated against exported definitions. A typo fails with the missing name and the available names instead of being silently ignored. With sibling discovery enabled, every specialized `AddProject` path is also checked after discovery or cloning; an absent service project fails with its module name, resource name, and expected path.

The same options can be changed in code before materializing a module:

```csharp
builder.UseLocalModuleProjects();
builder.UseModuleContainers();
builder.BuildModuleImages();
```

Use `ConfigureModularAppHosts` when several policies should be set together or computed in code:

```csharp
builder.ConfigureModularAppHosts(options =>
{
    options.RepositoryBasePath = Path.Combine(builder.AppHostDirectory, ".aspire", "modules");
    options.UpdateImportedRepositories = true;
    options.Modules[OrdersModule.Name] = new DistributedApplicationModuleOptions
    {
        Repository = "https://github.com/example/orders.git",
        RepositoryRevision = "v2.0.0",
        PublishImages = false
    };
});
```

Managed repository synchronization buffers clone, fetch, checkout, and pull progress and replays it through Aspire's resource logging service so it appears with the module resource in the dashboard. Discovery and cloning that must finish while constructing the application model continue to stream to the AppHost output. Deferred synchronization before startup honors startup cancellation, and every repository operation is bounded by `RepositoryCommandTimeout`. `GitExecutablePath`, `GitHubCliPath`, and `RepositoryCommandTimeout` configure the processes without changing module contracts.

## Repository imports

When an imported module needs repository content, the library clones or fast-forward-pulls its configured Git repository before Aspire starts the resources. Existing managed checkouts are synchronized before image tags and build decisions are selected, preventing a locally cached image from masking a newer checkout. A dirty imported checkout is never pulled or reset.

Pin a branch, tag, or commit with `WithRepository(repository, revision)` or `Modules:<name>:RepositoryRevision`. A pinned clean checkout fetches that revision, checks out the resolved commit in detached-head mode, and updates submodules. A dirty checkout must already be at the requested commit. Existing managed and sibling checkouts must have an `origin` matching the configured repository; a mismatched or missing origin fails instead of running unrelated source.

For cross-repository feature testing, prefer a versioned module preview request over passing a
branch name directly. The consumer-owned policy verifies requested repositories, contract packages,
and immutable image digests; `ApplyModulePreviewResolutionAsync` then applies the trusted result
before imports are materialized. A complete image resolution uses Aspire-native SHA-256 pins and can
avoid a producer runtime checkout. The [cross-repository preview guide](module-previews.md) covers the
.NET tool, GitHub workflow dispatch, source fallbacks, dependency pins, and the runnable fixture.

Managed repositories default to:

```text
<AppHost directory>/.aspire/module-repositories/<repository slug>-<module slug>
```

For example, module `orders` from `acme/orders` uses `acme-orders-orders`. The repository owner and name keep repositories with the same final path segment distinct, while the module component keeps multiple contracts from one repository distinct. Names that would collapse to the same filesystem/resource slug receive a stable suffix instead of sharing a checkout or parameter accidentally.

Override the base directory through the options section:

```json
{
  "Aspire": {
    "ModularAppHosts": {
      "RepositoryBasePath": "/work/aspire-repositories"
    }
  }
}
```

Repository values supplied through `Aspire:ModularAppHosts:Modules:<module>:Repository` are modeled with `AddParameterFromConfiguration`. If that key, the module definition, and programmatic options are all missing, the same required parameter uses Aspire's interaction service to ask for the repository in interactive environments, except for repository-backed generic factories as noted above. Non-interactive runs and deployment pipelines provide the options key through normal configuration, such as `Aspire__ModularAppHosts__Modules__orders__Repository`. Use `builder.GetRepositoryParameterName(repository, moduleName)` when the repository is known, the one-argument fallback for unresolved interactive imports, and `GetRepositoryConfigurationKey(moduleName)` for the configuration key. The builder is required so relative repository identities use `builder.AppHostDirectory`, matching materialization even when Aspire was launched elsewhere with `--apphost`.

`RepositoryBasePath` remains an AppHost option because its value is needed while the resource model is being constructed, before unresolved parameters are presented by the interaction service.

Repository synchronization is keyed by the canonical Git root and shared across modules. When multiple modules belong to the same checkout, the AppHost's module registry executes one synchronization task and routes its buffered progress to the first module resource's log stream.

Repository-relative project and publish paths are compared with the operating system's path rules. Parent traversal and symbolic links that escape the repository are rejected.

### Optional sibling discovery and cloning

Set `AutoCloneRepositories` to `true` globally, or `Modules:<name>:AutoCloneRepository` for one module, to use the local sibling convention:

```text
<workspace>/consumer/   # current AppHost Git root
<workspace>/orders/     # module repository inferred from …/orders.git or owner/orders
```

The library first resolves the AppHost Git root. A module whose configured local root belongs to that same worktree is reused as-is, including a nested logical module root, and GitHub CLI is not invoked. Local paths are compared by their actual Git roots, so a nested logical root remains valid even when that worktree has a remote `origin`. Otherwise, the only accepted location is one direct sibling named from the module repository. An existing sibling must be a Git worktree with the configured origin. A missing sibling is cloned during model construction with:

```text
gh repo clone <repository> <sibling-path> -- --recurse-submodules
```

This feature is off by default, so `gh` is only a runtime dependency when it is enabled and a sibling is missing. `GitHubCliPath` can select another executable path. Authentication, host selection, and credentials remain GitHub CLI concerns; clone failures retain its diagnostic output.

Because sibling cloning must finish before repository-backed Aspire resources are added to the model, an enabled missing sibling needs `Repository` from configuration, programmatic options, or `WithRepository`. It cannot wait for an interactive parameter response. Existing managed imports continue to use `RepositoryBasePath` when sibling discovery is disabled. Synchronization happens during model construction when a repository-backed factory or default image identity needs the checkout immediately; other imports can defer it until before startup.

See the [Two-AppHost sample](../samples/README.md) for a complete local and imported module.
