# E2E tests for modular AppHosts

This sample uses two common eShop business modules:

```text
catalog module ──> catalog-api <── orders-api <── scenario tests
                                      ^
                                      |
                               orders module
```

`EShop.E2E.AppHost` adds both modules, wires the orders API to the catalog endpoint, supplies the orders API key as an Aspire secret parameter, and declares a Docker Compose deployment environment. The test project runs one checkout scenario against either a test-managed AppHost or a builder-managed Compose deployment. Both modes produce an `IDistributedApplicationTestingBuilder`, so all test lifecycle and client code is shared.

The E2E AppHost and test project reference `Shirubasoft.Aspire.ModularAppHosts.Testing`, which contains the Compose export extensions and deployment builder. The ordinary modular AppHosts package remains free of `Aspire.Hosting.Testing` and Docker hosting dependencies.

## Run through the AppHost

The default mode starts and disposes the AppHost with `DistributedApplicationTestingBuilder`:

```bash
ESHOP_E2E_MODE=apphost \
dotnet test EShop.E2E.Tests/EShop.E2E.Tests.csproj
```

Aspire allocates the runtime addresses, waits for both project resources to become healthy, and creates the test HTTP clients.

## Run through Docker Compose

Install the Aspire CLI, ensure Docker or Podman is running, and run the test:

```bash
Parameters__orders_api_key=e2e-orders-key \
ESHOP_E2E_MODE=compose \
dotnet test EShop.E2E.Tests/EShop.E2E.Tests.csproj --no-build
```

`DockerComposeDeploymentTestingBuilder.DeployAsync` runs `aspire deploy`, imports the generated `.env.<environment>` file, and returns the same testing-builder contract used by AppHost mode. Disposing the builder runs `aspire destroy` and removes its temporary output directory.

CI uses a known output path so its fallback teardown can locate deployment state if the test process is interrupted. Local runs use an automatically cleaned temporary directory.

Environment-specific files can contain resolved secrets. `aspire-output` is ignored by Git and should not be retained as a CI artifact.

For endpoint requirements, externally owned deployments, and CI options, see the [E2E testing guide](../../docs/e2e-testing.md).
