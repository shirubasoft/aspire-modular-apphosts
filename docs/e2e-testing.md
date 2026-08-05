# E2E testing with AppHost and Docker Compose

`Shirubasoft.Aspire.ModularAppHosts.Testing` lets one test suite use Aspire's standard `IDistributedApplicationTestingBuilder` contract in two modes:

- start the AppHost in the test process with `DistributedApplicationTestingBuilder`;
- deploy the AppHost to Docker Compose with Aspire and represent the deployed services as a testing application.

The package is separate so regular AppHosts do not acquire `Aspire.Hosting.Testing` or Docker hosting dependencies.

```bash
dotnet add package Shirubasoft.Aspire.ModularAppHosts.Testing
```

## Export test configuration from the AppHost

Add a Docker Compose environment and export only the endpoints and values that external tests require:

```csharp
using Aspire.Hosting.ModularAppHosts;

var compose = builder.AddDockerComposeEnvironment("e2e")
    .WithDashboard(false);
var apiKey = builder.AddParameter("orders-api-key", secret: true);

compose
    .WithTestEndpoint(
        "catalog-api",
        catalog.Api.GetEndpoint("http"),
        healthCheckPath: "/health")
    .WithTestEndpoint(
        "orders-api",
        orders.Api.GetEndpoint("http"),
        healthCheckPath: "/health")
    .WithTestValue("Parameters:orders-api-key", apiKey.Resource)
    .WithTestConnectionString("catalog", catalogDatabase);
```

`WithTestEndpoint` requires an external endpoint with an explicit host port. It preserves the endpoint name, so multiple endpoints on one resource and calls such as `CreateHttpClient("catalog-api", "admin")` behave the same in both modes. The optional health path becomes Aspire resource health in the imported testing model. `WithTestValue` accepts any Aspire `IValueProvider`, including secret parameters; `WithTestConnectionString` imports a resource under the standard `ConnectionStrings:<name>` configuration key.

## Use one test lifecycle

Choose the builder at test startup and share the remaining test code:

```csharp
await using IDistributedApplicationTestingBuilder builder = mode switch
{
    "apphost" => await DistributedApplicationTestingBuilder
        .CreateAsync<Projects.EShop_E2E_AppHost>(),
    "compose" => await DockerComposeDeploymentTestingBuilder
        .DeployAsync<Projects.EShop_E2E_AppHost>(),
    _ => throw new InvalidOperationException($"Unknown E2E mode '{mode}'.")
};

await using var app = await builder.BuildAsync();
await app.StartAsync();
await app.ResourceNotifications.WaitForResourceHealthyAsync("orders-api");

using var orders = app.CreateHttpClient("orders-api", "http");
```

In AppHost mode, Aspire starts the project resources and allocates their endpoints. In Compose mode, `DeployAsync` first runs `aspire deploy`, imports the resolved endpoints and configuration, and returns a builder for the already deployed services. Disposing that builder runs `aspire destroy`.

## Deployment options

`DeployAsync<TEntryPoint>()` uses these optional environment variables:

| Variable | Purpose | Default |
| --- | --- | --- |
| `ASPIRE_TEST_DEPLOYMENT_ENVIRONMENT` | Aspire deployment environment name. | `Tests` |
| `ASPIRE_TEST_DEPLOYMENT_OUTPUT_PATH` | Directory for generated Compose files and environment configuration. | A temporary directory removed during disposal. |

Use an explicit output path in CI when an emergency teardown step needs to locate deployment state after a cancelled test process.

For code-owned configuration, pass `DockerComposeDeploymentOptions`:

```csharp
var builder = await DockerComposeDeploymentTestingBuilder
    .DeployAsync<Projects.EShop_E2E_AppHost>(new DockerComposeDeploymentOptions
    {
        EnvironmentName = "CI",
        OutputPath = artifactsPath,
        AspireCliPath = toolPath,
        DeploymentTimeout = TimeSpan.FromMinutes(15),
        CleanupTimeout = TimeSpan.FromMinutes(3)
    });
```

The Aspire CLI output is streamed while deploy and destroy run. A timed-out deploy still receives a best-effort destroy, and a temporary output directory is removed.

When another system owns deployment, use `DockerComposeDeploymentTestingBuilder.Create<TEntryPoint>(filePath)` or set `ASPIRE_TEST_CONFIGURATION_FILE` and call `CreateFromEnvironment<TEntryPoint>()`. These modes import configuration without deploying or destroying the external environment.

Aspire's environment-specific file can contain resolved secrets. Keep it out of source control and do not publish it as a CI artifact.

See the [eShop E2E sample](../samples/E2ETesting/README.md) for a complete AppHost, test suite, and CI workflow.
