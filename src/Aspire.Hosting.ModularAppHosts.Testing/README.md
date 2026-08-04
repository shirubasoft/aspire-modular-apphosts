# Shirubasoft.Aspire.ModularAppHosts.Testing

`Shirubasoft.Aspire.ModularAppHosts.Testing` adds Docker Compose deployment support to Aspire E2E tests without adding `Aspire.Hosting.Testing` or Docker hosting dependencies to regular modular AppHosts.

```bash
dotnet add package Shirubasoft.Aspire.ModularAppHosts.Testing
```

The package uses the same `Aspire.Hosting.ModularAppHosts` namespace as the core package. In the E2E AppHost, export the endpoints and values that external tests need:

```csharp
compose
    .WithTestEndpoint("catalog-api", catalog.Api.GetEndpoint("http"), healthCheckPath: "/health")
    .WithTestValue("Parameters:api-key", apiKey.Resource);
```

Then choose either Aspire's in-process testing builder or a builder-owned Compose deployment in the test project:

```csharp
await using IDistributedApplicationTestingBuilder builder = mode switch
{
    "apphost" => await DistributedApplicationTestingBuilder.CreateAsync<Projects.EShop_E2E_AppHost>(),
    "compose" => await DockerComposeDeploymentTestingBuilder.DeployAsync<Projects.EShop_E2E_AppHost>()
};

await using var app = await builder.BuildAsync();
await app.StartAsync();
await app.ResourceNotifications.WaitForResourceHealthyAsync("catalog-api");
```

`DeployAsync` runs `aspire deploy`, imports the generated environment-specific configuration, and returns the standard Aspire testing-builder contract. Disposing the builder runs `aspire destroy`. `Create` and `CreateFromEnvironment` remain available when another system owns the deployment lifecycle.

See the repository's [E2E testing sample](https://github.com/Shirubasoft/aspire-modular-apphosts/tree/main/samples/E2ETesting) for the complete catalog-and-orders example and CI workflow.
