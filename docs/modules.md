# Modules

`Shirubasoft.Aspire.ModularAppHosts` lets a shared C# contract describe an Aspire resource graph. Each receiving AppHost chooses whether to materialize that graph locally. Repository-backed modules can instead use a managed checkout.

## Define and materialize a module

`ExportModule` registers a definition. It does not add resources until the AppHost calls one of the materialization APIs:

- `builder.Add(definition)` uses the definition in the current application.
- `builder.ImportModule(name)` uses a managed checkout when the module configures a repository.

Keep the definition in a project referenced by every participating AppHost:

```csharp
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ModularAppHosts;

[GenerateDistributedApplicationModule(Name)]
public static partial class OrdersModule
{
    public const string Name = "orders";
    public const string ApiResourceName = "orders-api";
    public const string CacheResourceName = "orders-cache";

    public static IDistributedApplicationModule Register(
        IDistributedApplicationBuilder builder,
        string repository) =>
        builder.ExportModule(Name, module =>
        {
            module.WithRepository(repository);
            module.AddResource<ProjectResource>(ApiResourceName, context =>
                context.ApplicationBuilder
                    .AddProject(
                        context.ResourceName,
                        Path.Combine(
                            context.RepositoryPath,
                            "src/Orders.Api/Orders.Api.csproj"))
                    .WithHttpEndpoint(name: "http"));
            module.AddContainer(CacheResourceName, "redis", "8-alpine");
        });
}
```

An AppHost using the local definition adds it directly:

```csharp
var definition = OrdersModule.Register(builder, sourcePath);
var orders = OrdersModule.AddModule(builder, definition);
```

An importing AppHost registers the contract and imports by name:

```csharp
OrdersModule.Register(builder, "https://github.com/example/orders.git");
var orders = OrdersModule.ImportModule(builder);
```

Both paths return the same generated `OrdersModule.Module` API:

```csharp
builder.AddContainer("consumer", "example/consumer", "latest")
    .WithReference(orders.Api.GetEndpoint("http"))
    .WaitFor(orders.Api)
    .WaitFor(orders.Cache);
```

## Generated resource API

`GenerateDistributedApplicationModule` generates `AddModule`, `ImportModule`, and a `Module` wrapper with one typed property per declared resource. A constant ending in `ResourceName` becomes a property without that suffix, so `ApiResourceName` produces `Api`.

The annotated type must be a top-level, non-generic, static partial class. The generator recognizes `AddProject`, `AddContainer`, and `AddResource<TResource>` calls whose resource names are compile-time strings. Invalid declarations, unsupported names, and generated-member collisions are reported as build diagnostics.

The untyped API remains available when a generated contract is unnecessary. Generated `AddProject` properties use `IResourceWithEndpoints` because configuration can select a `ProjectResource` or `ContainerResource` at run time:

```csharp
var orders = builder.ImportModule("orders");
var api = orders.GetResource<ContainerResource>("orders-api");
```

## Resource kinds

### Existing container images

Use `AddContainer` for an image that already exists in a registry or the local container runtime:

```csharp
module.AddContainer("orders-cache", "redis", "8-alpine")
    .Configure(container => container.WithEndpoint(targetPort: 6379, name: "tcp"));
```

### Projects and repository-aware factories

Use `AddResource<TResource>` when a resource should run directly from the local or imported repository. Build paths from `context.RepositoryPath`, as in the module contract above.

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
        ])
    {
        ImageTag = "dev"
    });
```

`ConfigureProject` applies when run-mode configuration selects the project for debugging. The existing `ExportAsContainer` callback applies to its container representation.

### Any Aspire resource

`AddResource<TResource>` accepts a lazy factory for first-party integrations, community integrations, and custom resource types:

```csharp
module.AddResource<PostgresServerResource>("postgres", context =>
    context.ApplicationBuilder.AddPostgres(context.ResourceName));
```

Factories run in declaration order when the module is materialized. The context provides the receiving builder, required resource name, repository path, import state, and `GetResource<TResource>` for earlier resources in the same module. The returned resource must use `context.ResourceName`.

Modules containing only repository-independent resources, such as existing images or parameters, can be imported without `WithRepository`.

## Publishing module images

The library does not infer how an image is built. `ExportAsContainer` publishes a project image, while `WithImagePublishCommand` attaches an explicit build command to an `AddContainer` resource:

```csharp
module.AddContainer("orders-static", "orders-static", "dev")
    .WithImagePublishCommand(new ModuleContainerExportOptions(
        imageName: "orders-static",
        publishCommand: "podman",
        publishArguments:
        [
            "build",
            "--tag",
            ModuleContainerExportOptions.ImageReferencePlaceholder,
            "."
        ])
    {
        ImageTag = "dev"
    });
```

In run mode, a one-shot installer invokes the configured executable before the container starts when the image needs publishing:

- A clean repository uses `ImageName:ImageTag` and reuses that image when it already exists locally.
- A dirty repository uses `ImageName:ImageTag-dirty` and rebuilds it for every AppHost session.
- The container waits for its installer to complete successfully.
- Installers are run-only resources and are excluded from deployment manifests.

Publish arguments can use the `{image}`, `{image-name}`, and `{image-tag}` constants on `ModuleContainerExportOptions`. The effective image reference is also available to the command as `ASPIRE_MODULE_IMAGE`.

`WorkingDirectory` is relative to the repository root. It defaults to the project directory for `ExportAsContainer` and to the repository root for `WithImagePublishCommand`. The command and arguments are executed directly without a shell.

## AppHost configuration

Materialization policy is bound from `Aspire:ModularAppHosts` and registered as `IOptions<ModularAppHostsOptions>`. Every key is optional. Resource-specific values override module values, which override global defaults:

```json
{
  "Aspire": {
    "ModularAppHosts": {
      "RepositoryBasePath": "/work/aspire-repositories",
      "UpdateImportedRepositories": true,
      "RunProjectsAsContainers": true,
      "PublishImages": true,
      "Modules": {
        "orders": {
          "Repository": "https://github.com/example/orders.git",
          "UpdateRepository": false,
          "RunProjectsAsContainers": true,
          "PublishImages": true,
          "Projects": {
            "orders-api": {
              "RunAsContainer": false,
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

`RunAsContainer` is honored only in Aspire run mode. Publish mode always uses the declared container representation. Running an imported project directly requires its managed checkout to exist when the AppHost model is built.

Image and command settings override a publish command declared by `ExportAsContainer` or `WithImagePublishCommand`; configuration cannot introduce an undeclared publisher. `PublishImage: false` skips the run-only installer and leaves image acquisition to the configured pull policy.

The same options can be changed in code before materializing a module:

```csharp
builder.ConfigureModularAppHosts(options =>
    options.RunProjectsAsContainers = false);
```

## Repository imports

When an imported module needs repository content, the library clones or fast-forward-pulls its configured Git repository before Aspire starts the resources. A dirty imported checkout is never pulled or reset.

Managed repositories default to:

```text
<AppHost directory>/.aspire/module-repositories/<module name>
```

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

When an imported project has no repository in its module definition or options, the library adds a required Aspire parameter such as `module-orders-repository`. Aspire's interaction service asks for it in interactive development environments; non-interactive runs and deployment pipelines supply it through the normal `Parameters:<name>` configuration key. Use `DistributedApplicationModuleExtensions.GetRepositoryParameterName(moduleName)` when code needs the exact parameter name.

Repository synchronization is shared by resources in the same module. Repeated export, add, and import calls are deduplicated by the AppHost's module registry.

See the [Two-AppHost sample](../samples/README.md) for a complete local and imported module.
