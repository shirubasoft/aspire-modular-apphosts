# Shirubasoft.Aspire.ModularAppHosts

`Shirubasoft.Aspire.ModularAppHosts` adds reusable, named modules to a C# Aspire AppHost. A module is defined once, then either added from the current worktree or imported into a managed Git clone. Its C# APIs use the `Aspire.Hosting.ModularAppHosts` namespace.

```bash
dotnet add package Shirubasoft.Aspire.ModularAppHosts
```

The extension deliberately does not infer how an application image should be produced. Every image export supplies the image name and the exact publish executable and arguments. A one-shot installer runs that command before the corresponding container starts when the repository is dirty or the clean image tag does not exist locally.

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
                    $"-p:ContainerRepository={ModuleContainerExportOptions.ImageNamePlaceholder}",
                    $"-p:ContainerImageTag={ModuleContainerExportOptions.ImageTagPlaceholder}"
                ])
            {
                ImageTag = "dev"
            },
            container => container.WithHttpEndpoint(targetPort: 8080));

    module.AddContainer("orders-cache", "redis", "8-alpine")
        .Configure(container => container.WithEndpoint(targetPort: 6379, name: "tcp"));

    module.AddContainer("orders-static", "orders-static", "dev")
        .WithImagePublishCommand(new ModuleContainerExportOptions(
            imageName: "orders-static",
            publishCommand: "podman",
            publishArguments: ["build", "--tag", "orders-static:dev", "."])
        {
            ImageTag = "dev"
        })
        .Configure(container => container.WithHttpEndpoint(targetPort: 80, name: "http"));

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

## Strongly typed generated modules

Annotate a static partial module class to generate named properties for every resource it declares:

```csharp
[GenerateDistributedApplicationModule(Name)]
public static partial class OrdersModule
{
    public const string Name = "orders";
    public const string ApiResourceName = "orders-api";
    public const string CacheResourceName = "orders-cache";

    public static IDistributedApplicationModule Register(
        IDistributedApplicationBuilder builder) =>
        builder.ExportModule(Name, module =>
        {
            module.AddContainer(ApiResourceName, "example/orders-api")
                .Configure(container => container.WithHttpEndpoint(targetPort: 8080, name: "http"));
            module.AddContainer(CacheResourceName, "redis", "8-alpine");
        });
}
```

The generator adds a typed `Module`, plus `AddModule` and `ImportModule` methods. A resource name constant ending in `ResourceName` becomes a property with that suffix removed, so consumers discover resources through IntelliSense without repeating their names or types:

```csharp
// In an AppHost using the current worktree:
var exported = OrdersModule.Register(builder);
var orders = OrdersModule.AddModule(builder, exported);
```

```csharp
// In an importing AppHost:
OrdersModule.Register(builder);
var orders = OrdersModule.ImportModule(builder);
```

Either path returns the same generated API for ordinary Aspire wiring:

```csharp
builder.AddContainer("consumer", "example/consumer")
    .WithReference(orders.Api.GetEndpoint("http"))
    .WaitFor(orders.Api)
    .WaitFor(orders.Cache);
```

The annotated class must be a top-level, non-generic, static partial class. Supported resource declarations are `AddProject`, `AddContainer`, and `AddResource<TResource>` calls inside that class, and their names must be compile-time string constants. Literal names are converted to PascalCase property names. Generator diagnostics report invalid declarations and property-name collisions at build time.

The untyped API remains available when a generated contract is not appropriate:

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

`ExportAsContainer` publishes project images, while `WithImagePublishCommand` adds the same behavior to an `AddContainer` resource. In both cases, clean repositories use `ImageName:ImageTag`; dirty repositories use `ImageName:ImageTag-dirty` and always rebuild.

Publish arguments can use `{image}`, `{image-name}`, and `{image-tag}` through the constants on `ModuleContainerExportOptions`. The extension resolves those placeholders to the effective clean or dirty image identity. For compatibility with direct container-runtime commands, an argument exactly matching the configured clean image reference is also changed to the dirty reference. `ASPIRE_MODULE_IMAGE` contains the same effective reference for scripts that prefer an environment variable.

The publish command must create the effective image reference. Its executable and all arguments other than the documented image substitutions remain caller-supplied; the extension does not infer how .NET or any other application type should be published.

`WorkingDirectory` is relative to the repository root. It defaults to the project directory for `ExportAsContainer` and to the repository root for `WithImagePublishCommand`. Arguments are passed directly to the executable; the extension does not invoke a shell or parse a command line string.

## Repository behavior

- `ExportModule` registers an inert definition in a catalog held by `IServiceCollection`.
- `Add(module)` materializes local resources and does not pull the developer's worktree.
- `ImportModule(name)` clones or fast-forward-pulls the configured repository before Aspire starts resources.
- A dirty imported worktree is never pulled or reset. Its service installers use the `-dirty` image-tag suffix and run on every AppHost session.
- A clean image is published only when its configured local tag does not already exist.
- The container waits for its installer to exit successfully. Installers are excluded from deployment manifests.
- Repeated export, add, and import calls are deduplicated by the service-collection-backed module registry.

Repository synchronization is shared across services in the same module, while each service has its own publish installer.

## Two-AppHost sample

[`samples`](samples/README.md) contains a complete runnable example:

- AppHost A exports a .NET project and a Dockerfile-backed container image using explicit Podman commands, and includes one of every public core top-level Aspire resource type.
- AppHost B imports the complete module and adds a gateway container with references and health dependencies on all three running containers.
- The gateway's `/health` endpoint probes all three imported services, so it becomes healthy only when every dependency responds successfully.

## E2E tests against AppHost or Docker Compose

[`samples/E2ETesting`](samples/E2ETesting/README.md) composes `catalog` and `orders` modules in one E2E AppHost and runs the same checkout scenario in two ways:

- start the AppHost in the test process with `DistributedApplicationTestingBuilder`;
- let the Compose testing builder deploy the AppHost through Aspire, run the tests, and tear the deployment down.

The Docker Compose AppHost exports the test-facing endpoints and parameter-backed values into Aspire's environment-specific file:

```csharp
compose
    .WithTestEndpoint("catalog-api", catalog.Api.GetEndpoint("http"), healthCheckPath: "/health")
    .WithTestEndpoint("orders-api", orders.Api.GetEndpoint("http"), healthCheckPath: "/health")
    .WithTestValue("Parameters:orders-api-key", ordersApiKey.Resource);
```

The Compose builder runs `aspire deploy`, imports the resolved addresses and configuration, and presents them through the standard Aspire testing contract. The same test can therefore build, start, wait for resources, and create clients through `DistributedApplication` in either mode:

```csharp
await using IDistributedApplicationTestingBuilder testBuilder = mode switch
{
    "apphost" => await DistributedApplicationTestingBuilder.CreateAsync<Projects.EShop_E2E_AppHost>(),
    "compose" => await DockerComposeDeploymentTestingBuilder
        .DeployAsync<Projects.EShop_E2E_AppHost>()
};

var app = await testBuilder.BuildAsync();
await app.StartAsync();
await app.ResourceNotifications.WaitForResourceHealthyAsync("orders-api");
using var orders = app.CreateHttpClient("orders-api", "http");
```

`DeployAsync` uses `ASPIRE_TEST_DEPLOYMENT_ENVIRONMENT` when set and otherwise deploys to `Tests`. It uses `ASPIRE_TEST_DEPLOYMENT_OUTPUT_PATH` when set; otherwise it creates and removes a temporary output directory. Disposing the builder runs `aspire destroy`. `CreateFromEnvironment` remains available when CI or another system deliberately owns an existing deployment.

Environment-specific files can contain resolved secrets. They are ignored by this repository; treat them as sensitive CI workspace data and do not upload them as artifacts.

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

GitHub Actions builds, tests, and packs every pull request. After a change reaches `main`, semantic-release analyzes the semantic commit messages since the latest `v`-prefixed version tag, chooses the next version, publishes the package to NuGet.org, and creates a GitHub release with the package attached. The workflow uses the Shirubasoft organization-level `NUGET_API_KEY` Actions secret.

Use [Conventional Commits](https://www.conventionalcommits.org/) for commits that affect consumers:

- `fix:` and `perf:` produce a patch release.
- `feat:` produces a minor release.
- A `!` after the type or scope, or a `BREAKING CHANGE:` footer, produces a major release.
- Other commit types, including `build:`, `chore:`, `ci:`, `docs:`, `refactor:`, `style:`, and `test:`, do not produce a release on their own.

For example, `feat(modules): export container resources` produces a minor release. When squash-merging, make sure the resulting commit message follows the same convention. The publishing workflow can be started manually to retry any unreleased commits, but versions are always calculated from the commit history rather than entered by hand.
