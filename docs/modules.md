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

`GenerateDistributedApplicationModule` generates module-specific builder extensions such as `AddOrdersModuleAsync` and `ImportOrdersModuleAsync`, plus a `Module` wrapper with one typed property per declared resource. The wrapper inherits the shared module contract delegation, so generated code only contains contract-specific resource properties. A constant ending in `ResourceName` becomes a property without that suffix, so `ApiResourceName` produces `Api`. The optional attribute `Version` identifies the contract with an exact, ordinal string comparison; defining the same module name with another version fails with both versions in the diagnostic. Bump it when resource names, exposed resource types, required configuration, endpoints, or materialization semantics change incompatibly. A repository branch, commit, or image rebuild does not by itself change the contract version. `PackageId` identifies the NuGet package that publishes the contract. Publish the updated contract package and update participating AppHosts together when a version changes.

Advanced contracts that need inputs beyond configuration can omit the conventional `Define` method, register with `DefineModuleAsync`/`ExportModuleAsync`, and pass the resulting definition to the generated `builder.AddOrdersModuleAsync(definition)` overload. Use the overload whose third argument is the package ID when the contract is distributed as a package.

The annotated type must be a top-level, non-generic, static partial class. The generator recognizes `AddProject`, `AddContainer`, and `AddResource<TResource>` calls whose resource names are compile-time strings inside the conventional `Define` method. Advanced contracts are scanned in module-builder definition methods or a lambda passed directly to `DefineModuleAsync`/`ExportModuleAsync`. Calls in unrelated helpers are ignored so the typed API cannot advertise resources the selected definition never materializes. Invalid declarations, unsupported names, generated-member collisions, and custom resource types that are less accessible than the generated module API are reported as build diagnostics.

The generator supports .NET SDK 10.0.100 and later. Pin at least that version in `global.json`; patch releases and later .NET 10 feature bands are supported.

Use the untyped API for dynamic contracts. Generated `AddProject` properties use `IResourceWithEndpoints` because configuration can select a `ProjectResource` or `ContainerResource` at run time:

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

var orders = await builder.ImportOrdersModuleAsync(import);
// orders.Api resolves the Aspire resource "sales-orders-api".
// orders.Cache resolves "shared-cache".
```

Unknown aliases, aliases that map multiple resources to the same name, installer-name collisions, and collisions with resources already in the AppHost fail before any module resource is added.

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
module.AddContainer("orders-cache", "redis");
module.AddProject<Projects.Orders_Api>("orders-api")
    .ConfigureProject((context, project) => project
        .WaitFor(context.GetResource<ContainerResource>("orders-cache"))
        .WithHttpHealthCheck("/health"))
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

`ConfigureProject` applies when run-mode configuration selects the project for debugging. The existing
`ExportAsContainer` callback applies to its container representation. Those callbacks and declared
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
    new ModuleContainerExportOptions(
        imageName: "example/orders-postgres",
        publishCommand: "docker",
        publishArguments:
        [
            "build",
            "--tag",
            ModuleContainerExportOptions.ImageReferencePlaceholder,
            "."
        ])
    {
        ImageRegistry = "ghcr.io"
    });
```

The overload is constrained to `ContainerResource`; integration server resources derived from it retain their typed APIs. Before the factory runs, `context.Image` contains the resolved registry, name, tag, optional digest, repository, and full effective reference. When a digest is configured, the reference uses the immutable `repository@sha256:...` form. After the factory returns, the library replaces any integration-default image and registry, applies configured `ImageSHA256` and `ImagePullPolicy` values from the module's `Containers` section, and attaches the same one-shot installer used by declared containers.

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

## Publishing module images

Declare how each module image is built. `ExportAsContainer` publishes a project image, `WithImagePublishCommand` attaches an explicit build command to an `AddContainer` resource, and the image-publishing `AddResource` overload does the same for a factory-created container resource. Resolve the command with `ContainerRuntimeResolver` when the module should follow Aspire's Docker or Podman selection:

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

The resolver reads `ASPIRE_CONTAINER_RUNTIME` first and also accepts `DOTNET_ASPIRE_CONTAINER_RUNTIME`. Without an explicit value, it probes Docker and Podman in parallel, prefers a running runtime over one that is merely installed, and uses Docker as its tie-breaker and fallback.

In run mode, a one-shot installer invokes the configured executable before the container starts when the image needs publishing:

- When `ImageTag` is omitted, the module repository branch is lowercased and sanitized and its 12-character commit is appended (`feature/orders` becomes `feature-orders-a1b2c3d4e5f6`). Detached checkouts use `sha-a1b2c3d4e5f6`. A known managed repository is synchronized before this tag is selected. For repository-independent or still-unresolved definitions, the AppHost branch and commit are used, then CI branch variables, then `latest`.
- A clean repository uses `[ImageRegistry/]ImageName:ImageTag` and reuses that image when it already exists locally.
- With `PullBeforeBuild = true`, a missing clean image is pulled from its registry before the build command is considered. A successful pull skips the build; a missing or failed pull falls back to the declared command. Dirty repositories always build and never use this pull shortcut. When an explicit tag and a separate `BuildRepository` are configured but its checkout is absent, inspect/pull happens first, so a successful acquisition can avoid an unnecessary clone entirely. Existing local or managed build checkouts are resolved first so uncommitted source changes still produce a dirty image.
- A dirty repository uses `[ImageRegistry/]ImageName:ImageTag-dirty` and rebuilds it for every AppHost session. The tag remains within the 128-character distribution limit.
- The container waits for its installer to complete successfully.
- Installers are run-only resources and are excluded from deployment manifests.

`ImageRegistry` explicitly separates a registry host such as `ghcr.io` from an `ImageName` repository path such as `example/orders-api`. Leave it unset for local or otherwise unqualified images. Publish arguments can use the `{image}`, `{image-registry}`, `{image-repository}`, `{image-name}`, and `{image-tag}` constants on `ModuleContainerExportOptions`. The effective image reference is also available to the command as `ASPIRE_MODULE_IMAGE`.

### Push module images

In publish mode, every project, declared container, or factory-created container that has an image publisher contributes a `build-<resource>` step to Aspire's `build` pipeline. The step executes the effective `ModuleContainerExportOptions` command and arguments in its resolved build working directory with `ASPIRE_MODULE_IMAGE` set. `PullBeforeBuild` first attempts to reuse or pull a clean image, dirty repositories always build, and `ProducedImageReference` is retagged after a successful command. Failures and cancellation stop the pipeline.

Build every publisher or scope the operation by declared or effective resource name:

```bash
aspire do build
aspire do build orders-api orders-worker
```

Every registry-backed module image also contributes a `push-<resource>` step. That push depends on its matching build step, so `aspire do push` builds a missing image from the module-owned command before pushing it. This keeps the build command in the module contract and the workflow focused on orchestration. An explicit `ImageRegistry` pushes the effective image reference directly. A resource associated with `AddContainerRegistry` through `WithContainerRegistry` uses Aspire's registry-aware image manager instead. Authenticate the selected container runtime to the destination registry before invoking the step.

After pushing the effective image, a clean publisher also tags and pushes the same image with its sanitized source branch (`feature/orders` becomes `feature-orders`). This branch alias is published even when configuration or `manifest publish --tag` selects a separate canonical tag, so a default-branch consumer can follow a stable name while workflow manifests remain pinned to the exact canonical image. Detached AppHost checkouts use `GITHUB_HEAD_REF` and then `GITHUB_REF_NAME`. Dirty repositories and detached managed revisions never update a mutable alias. If the canonical remote tag already equals the branch alias, the duplicate tag and push are skipped.

Remote identity resolution uses one precedence order for describe, pull, and push: an explicit pull mapping (pull only), a per-resource `WithContainerRegistry`, the qualified registry declared by the module, and finally a deployment or default registry. Registries with an endpoint participate in remote pull and push. Empty-endpoint registries, such as the local registry supplied by a Docker Compose environment, preserve the module-owned registry host.

Project exports retain the `ImageRegistry` from `ModuleContainerExportOptions` when they are represented as containers. When the destination is an Aspire registry resource instead, configure that registry and any remote-image options on the existing container-export callback:

```csharp
#pragma warning disable ASPIRECOMPUTE003, ASPIREPIPELINES003
var registry = builder.AddContainerRegistry("ghcr", "ghcr.io", "example/orders");

module.AddProject("orders-api", projectPath)
    .ExportAsContainer(
        new ModuleContainerExportOptions(
            "orders-api",
            "dotnet",
            "publish",
            "-t:PublishContainer"),
        (_, container) => container
            .WithContainerRegistry(registry)
            .WithRemoteImageName("api")
            .WithRemoteImageTag("candidate"));
#pragma warning restore ASPIRECOMPUTE003, ASPIREPIPELINES003
```

Push every eligible image, one or more resources, one or more modules, or a mixture by adding
positional selectors:

```bash
aspire do push
aspire do push orders-api orders-worker
aspire do push orders catalog/api
aspire do push module:orders resource:catalog-worker
```

Plain selectors match a module, declared resource, or effective resource when the name is
unambiguous. Use `module:<name>` or `resource:<name>` when a module and resource share a name, and
use `<module>/<resource>` for one declared resource identity. Ambiguous and unknown selectors fail
with the available identities instead of broadening the operation. Mixed selectors are unioned and
deduplicated.

When selectors are present, non-selected image push steps are detached from the `push` aggregate,
including ordinary Aspire project and Dockerfile steps. Only matching module build dependencies run.
Directly invoking a resource step such as `aspire do push-orders-api` remains supported by Aspire.

### Describe module images

Generate a machine-readable inventory from the same effective identity resolver used by the pull and push pipelines:

```bash
aspire do describe-images --output-path artifacts
aspire do describe-images orders-api orders-worker --output-path artifacts
```

The command writes deterministic schema-version-3 JSON to `artifacts/module-images.json` and logs a concise reference summary. Its `modules` collection contains every materialized module name and declared contract package ID, including modules without container images. Each `images` entry contains the declared resource name, effective prefixed or aliased Aspire name, resource kind, registry, repository without tag, effective tag or digest, complete run and pull references, a structured registry/repository/tag push target when a push step exists, and the resolved build command and source when the module publishes that image. Resource selection accepts both declared and effective names. This file gives automation the module and image identities without duplicating contract configuration.

### Pull module images

The same registry-backed modular project exports, declared containers, and factory-created containers contribute a `pull-<resource>` step to the module-provided `pull` pipeline. An explicit `ImageRegistry` pulls the effective image reference directly. A resource associated with an Aspire registry resolves its remote image name and tag, pulls that reference, and tags it back to the local image reference used by the container resource. Resolution starts from the configured module image name and tag, preserving an owner-qualified repository such as `example/orders-api`.

Use `WithImagePullMapping` when the pull source must be declared independently of the resource image. The pipeline pulls the supplied complete remote reference and tags it as the resource's effective local image, even when the two references use different registries:

```csharp
module.AddContainer("api", "ghcr.io/api", "1-0")
    .WithImagePullMapping("mycustomregistry.io/images:api-1-0");
```

In this example, `aspire do pull api` executes `pull mycustomregistry.io/images:api-1-0` followed by `tag mycustomregistry.io/images:api-1-0 ghcr.io/api:1-0`. The explicit mapping takes precedence over `WithContainerRegistry`, remote push-name callbacks, and default registry targets for pull resolution. The mapping applies to pulls; `aspire do push` retains the resource's configured push target. The resource's local image must be tag-based because the pipeline retags the pulled image.

Pull and re-tag lifecycle messages are written both to the Aspire pipeline-step logger and to the pulled resource's `ResourceLoggerService` stream. The pipeline output therefore records the exact remote and local references in CI, while the same structured messages remain associated with the resource for dashboard and programmatic log consumers.

Pull every eligible module image or scope the operation to effective Aspire resource names:

```bash
aspire do pull
aspire do pull orders-api orders-worker
```

When resource arguments are present, only the selected pull steps run. An unknown name fails with the available image resources. A resource step can also be invoked directly, for example `aspire do pull-orders-api`. Registry authentication for every referenced registry is supplied by the selected Docker or Podman runtime in the same way as push authentication.

Pull and describe-only commands skip a separate image build repository whenever a configured tag or immutable digest makes the image identity independent of that checkout. When neither is present, the repository is still resolved because its branch and commit determine the effective default tag. Build and push commands resolve only the repositories selected by declared or effective resource name.

Build commands that choose their own output tag can set `ProducedImageReference`. After the build command succeeds, the module adds a second one-shot resource that invokes the selected container runtime as `tag <produced> <effective>`, and the target container waits for that retag step. The value can be a fixed reference or use the same image placeholders. The module invokes the runtime directly:

```csharp
new ModuleContainerExportOptions("example/orders-database", "pwsh", "./build-image.ps1")
{
    ImageRegistry = "ghcr.io",
    ProducedImageReference = "orders-database:production",
    PullBeforeBuild = true
};
```

`ProducedImageReference` and `PullBeforeBuild` apply when image publishing is enabled. With `PublishImage: false`, Aspire's configured `ImagePullPolicy` acquires the target image.

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
              "AutoCloneBuildRepository": false,
              "UpdateBuildRepository": true,
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

`ProjectMode` is honored only in Aspire run mode. Its safe `Auto` default runs modules added from local source as projects and imported modules as containers; publish mode always uses the declared container representation. Running an imported project directly requires its managed checkout to exist when the AppHost model is built.

Existing clean imported repositories with a configured upstream update by default. Clean local branches without an upstream and dirty checkouts are left unchanged. Set `UpdateRepository` or `UpdateImportedRepositories` to `false` where a checkout must remain fixed. Resource-level `UpdateBuildRepository` and `AutoCloneBuildRepository` independently override those policies for a separate image-build checkout. Image build commands remain opt-in through `PublishImage`/`PublishImages`. Image, command, build-repository, and build-revision settings override a publisher declared by `ExportAsContainer` or `WithImagePublishCommand`; configuration cannot introduce an undeclared publisher. `PublishImage: false` skips the run-only installer and leaves image acquisition to the configured pull policy; an explicit tag or digest also avoids resolving an unused build repository.

A complete external image identity—registry, repository name, and exactly one tag or digest with `PublishImage: false`—also removes that resource's source dependency. When every source-backed image publisher in an imported module is external-image-only, the remaining resources are repository-independent, and the module does not call `RequiresRepository()`, a declared definition repository remains contract metadata: the AppHost does not discover, prepare, or synchronize it, and `RepositoryBasePath` remains untouched. `manifest apply` configures this mode for every listed publisher. Projects running in `Project` mode, uncovered image publishers, and factories explicitly marked with `RequiresRepository()` still materialize the source they consume.

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

Managed repository synchronization buffers clone, fetch, checkout, and pull progress and replays it through Aspire's resource logging service so it appears with the module resource in the dashboard. Discovery and cloning that must finish while constructing the application model continue to stream to the AppHost output. Deferred synchronization before startup honors startup cancellation, and every repository operation is bounded by `RepositoryCommandTimeout`. `GitExecutablePath`, `GitHubCliPath`, and `RepositoryCommandTimeout` configure the processes without changing module contracts. GitHub HTTPS clones use `gh repo clone`; subsequent fetch, pull, and submodule commands use the configured `gh` as a process-scoped Git credential helper. Credentials stay within that helper process.

## Repository imports

When an imported module needs repository content, the library clones or fast-forward-pulls its configured Git repository before Aspire starts the resources. Existing managed checkouts are synchronized before image tags and build decisions are selected, preventing a locally cached image from masking a newer checkout. A dirty imported checkout is never pulled or reset.

Pin a branch, tag, or commit with `WithRepository(repository, revision)` or `Modules:<name>:RepositoryRevision`. A pinned imported module always uses a library-owned checkout under `RepositoryBasePath`, even when a matching sibling repository or the AppHost worktree is available. That managed checkout fetches the revision, checks out the resolved commit in detached-head mode, and updates submodules. This isolation prevents materialization commands from detaching or moving a developer's active branch. Existing managed checkouts must have an `origin` matching the configured repository; a mismatched or missing origin fails instead of running unrelated source.

Managed repositories default to:

```text
<AppHost directory>/.aspire/module-repositories/<repository slug>-<module slug>
```

For example, module `orders` from `acme/orders` uses `acme-orders-orders`. The repository owner and name keep repositories with the same final path segment distinct, while the module component keeps multiple contracts from one repository distinct. Names that would collapse to the same filesystem/resource slug receive a stable suffix instead of sharing a checkout or parameter accidentally.

When a managed checkout is actually materialized, its base receives minimal `Directory.Build.props`, `Directory.Build.targets`, and `Directory.Packages.props` files plus an inert `Directory.Build.rsp`. These atomically published boundaries prevent a consumer repository's MSBuild and central package policy from leaking into independently owned checkouts while preserving any nearer files committed by each producer repository. Existing boundary files supplied at a custom base path are left unchanged. Move shared build policy that producers still require into each producer repository, or pre-provision custom boundary files that explicitly import that policy before mounting the base read-only.

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

Existing repository synchronization is keyed by the canonical Git root and shared across modules, including nested logical roots and symbolic-link aliases. A not-yet-created managed checkout is keyed by its collision-free destination path. When multiple modules belong to the same checkout, the AppHost's module registry executes one synchronization task and routes its buffered progress to the first module resource's log stream. Automatic fast-forward updates run only for branches that track an upstream; a clean local-only branch is preserved just like a dirty checkout.

Project paths declared with `ModuleProjectPathBase.Repository` and repository-relative publish paths are compared with the operating system's path rules. Parent traversal and symbolic links that escape the repository are rejected.

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

This feature is off by default. `GitHubCliPath` can select another executable path. Authentication, host selection, and credentials remain GitHub CLI concerns; clone failures retain its diagnostic output. For private GitHub HTTPS repositories, authenticate locally with `gh auth login`. In CI, pass the job token to the AppHost process as `GH_TOKEN` or `GITHUB_TOKEN`; `gh auth git-credential` supplies it only to the Git process that needs it.

Sibling cloning finishes before repository-backed Aspire resources are added to the model. For a missing sibling, supply `Repository` through configuration, programmatic options, or `WithRepository`. Existing managed imports use `RepositoryBasePath` when sibling discovery is disabled. Synchronization happens during model construction when a repository-backed factory or default image identity needs the checkout immediately; other imports can defer it until before startup.

See the [Two-AppHost sample](../samples/README.md) for a complete local and imported module.
