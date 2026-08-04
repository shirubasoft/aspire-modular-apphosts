# Shirubasoft.Aspire.ModularAppHosts

`Shirubasoft.Aspire.ModularAppHosts` adds reusable, named modules to a C# Aspire AppHost. A module is defined once, then either added from the current worktree or imported into a managed Git clone. Its C# APIs use the `Aspire.Hosting.ModularAppHosts` namespace.

```bash
dotnet add package Shirubasoft.Aspire.ModularAppHosts
```

The extension deliberately does not infer how an application image should be produced. Every `ExportAsContainer` call supplies the image name and the exact publish executable and arguments. The one-shot installer runs that command before the corresponding container starts.

## Usage

```csharp
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ModularAppHosts;

var builder = DistributedApplication.CreateBuilder(args);

var orders = builder.ExportModule("orders", module =>
{
    module.WithRepository("https://github.com/example/orders.git");

    module.AddProject<Projects.Orders_Api>("orders-api")
        .ExportAsContainer(
            new ModuleContainerExportOptions(
                imageName: "orders-api",
                publishCommand: "dotnet",
                publishArguments:
                [
                    "publish",
                    "Orders.Api.csproj",
                    "-t:PublishContainer",
                    "-p:ContainerRepository=orders-api",
                    "-p:ContainerImageTag=dev"
                ])
            {
                ImageTag = "dev"
            },
            container => container.WithHttpEndpoint(targetPort: 8080));

    module.AddContainer("orders-cache", "redis", "8-alpine")
        .Configure(container => container.WithEndpoint(targetPort: 6379, name: "tcp"));

    module.AddResource<ParameterResource>("orders-region", context =>
        context.ApplicationBuilder.AddParameter(
            context.ResourceName,
            "local",
            publishValueAsDefault: true));
});

// Use projects from the current worktree.
builder.Add(orders);

// Or, in an AppHost that should use the managed clone:
// builder.ImportModule("orders");

builder.Build().Run();
```

An importing AppHost can retrieve materialized resources for ordinary Aspire wiring:

```csharp
var orders = builder.ImportModule("orders");
var api = orders.GetResource<ContainerResource>("orders-api");
var cache = orders.GetResource<ContainerResource>("orders-cache");
var region = orders.GetResource<ParameterResource>("orders-region");

builder.AddContainer("consumer", "example/consumer")
    .WithReference(api.GetEndpoint("http"))
    .WithEnvironment("ORDERS_REGION", region)
    .WaitFor(api)
    .WaitFor(cache);
```

## Exporting any Aspire resource

`AddResource<TResource>` accepts a lazy factory for any type implementing Aspire's `IResource`, including resources returned by first-party integrations, community integrations, and custom extensions:

```csharp
module.AddResource<PostgresServerResource>("postgres", context =>
    context.ApplicationBuilder.AddPostgres(context.ResourceName));

module.AddResource<TalkingClockResource>("clock", context =>
    context.ApplicationBuilder.AddTalkingClock(context.ResourceName));
```

Factories run in declaration order only when the module is added or imported. `IDistributedApplicationModuleResourceContext` exposes the receiving AppHost builder, the required resource name, the local or managed repository path, import state, and `GetResource<TResource>` for referring to earlier exports in the same module. The factory must return a resource with `context.ResourceName`; mismatches fail during materialization.

Modules made entirely from repository-independent generic resources or existing images can be imported without `WithRepository`. Configure a repository when factories use source files or when the module exports projects.

The publish command must create the exact `ImageName:ImageTag` configured on `ModuleContainerExportOptions`. `ASPIRE_MODULE_IMAGE` is also provided to the command as an environment variable for scripts that prefer to consume the image identity that way.

`WorkingDirectory` is relative to the repository root and defaults to the project directory. Arguments are passed directly to the executable; the extension does not invoke a shell or parse a command line string.

## Repository behavior

- `ExportModule` registers an inert definition in a catalog held by `IServiceCollection`.
- `Add(module)` materializes local resources and does not pull the developer's worktree.
- `ImportModule(name)` clones or fast-forward-pulls the configured repository before Aspire starts resources.
- A dirty imported worktree is never pulled or reset. Its service installer still runs the supplied publish command, so dirty images are always rebuilt.
- The publish installer runs on every AppHost session, including clean worktrees. Build caching is entirely controlled by the supplied command.
- The container waits for its installer to exit successfully. Installers are excluded from deployment manifests.
- Repeated export, add, and import calls are deduplicated by the service-collection-backed module registry.

Repository synchronization is shared across services in the same module, while each service has its own publish installer.

## Two-AppHost sample

[`samples`](samples/README.md) contains a complete runnable example:

- AppHost A exports a .NET project as `modular-sample-api:dev` using an explicit Podman command and also exports an existing `nginx:alpine` container.
- AppHost B imports both resources and adds a gateway container with references and health dependencies on both.
- The gateway's `/health` endpoint probes both imported services, so it becomes healthy only when both dependencies respond successfully.

## Repository base location parameter

Imported repositories default to:

```text
<AppHost directory>/.aspire/module-repositories/<module name>
```

Override the Aspire parameter in configuration:

```json
{
  "Parameters": {
    "module-repository-base-location": "/work/aspire-repositories"
  }
}
```

`ImportModule` adds the non-secret `module-repository-base-location` parameter resource once and publishes its effective value as the manifest default.

## Build and test

```bash
dotnet restore Aspire.ModularAppHosts.slnx
dotnet build Aspire.ModularAppHosts.slnx --no-restore
dotnet test Aspire.ModularAppHosts.slnx --no-build --no-restore
```

## Publishing

GitHub Actions builds, tests, and packs every pull request. Publishing uses the Shirubasoft organization-level `NUGET_API_KEY` Actions secret, which is available to this repository. Publish a GitHub release whose tag is a semantic version prefixed with `v`, such as `v1.0.0`; the publishing workflow can also be started manually with an explicit package version.
