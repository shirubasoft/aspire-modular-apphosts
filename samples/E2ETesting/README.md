# E2E tests for modular AppHosts

This sample uses two common eShop business modules:

```text
catalog module ──> catalog-api <── orders-api <── scenario tests
                                      ^
                                      |
                               orders module
```

`EShop.E2E.AppHost` adds both modules, wires the orders API to the catalog endpoint, supplies the orders API key as an Aspire secret parameter, and declares a Docker Compose deployment environment. The test project runs one checkout scenario against either a test-managed AppHost or an already deployed Compose environment. Both modes produce an `IDistributedApplicationTestingBuilder`, so all test lifecycle and client code is shared.

## Run through the AppHost

The default mode starts and disposes the AppHost with `DistributedApplicationTestingBuilder`:

```bash
ESHOP_E2E_MODE=apphost \
dotnet test EShop.E2E.Tests/EShop.E2E.Tests.csproj
```

Aspire allocates the runtime addresses, waits for both project resources to become healthy, and creates the test HTTP clients.

## Run through Docker Compose

Deploy the same AppHost model through Aspire:

```bash
Parameters__orders_api_key=e2e-orders-key \
aspire deploy \
  --apphost EShop.E2E.AppHost/EShop.E2E.AppHost.csproj \
  --output-path EShop.E2E.AppHost/aspire-output \
  --environment CI \
  --non-interactive
```

Run the same test against the deployment:

```bash
ESHOP_E2E_MODE=compose \
ASPIRE_TEST_CONFIGURATION_FILE="$PWD/EShop.E2E.AppHost/aspire-output/.env.CI" \
dotnet test EShop.E2E.Tests/EShop.E2E.Tests.csproj --no-build
```

Finally, remove the temporary deployment:

```bash
aspire destroy \
  --apphost EShop.E2E.AppHost/EShop.E2E.AppHost.csproj \
  --output-path EShop.E2E.AppHost/aspire-output \
  --environment CI \
  --yes \
  --non-interactive
```

`WithTestEndpoint` requires an external endpoint with an explicit host port, because an external test process needs a stable address after Compose deployment. Its optional `healthCheckPath` is imported into Aspire resource health, so the same `WaitForResourceHealthyAsync` calls work in both modes. `WithTestValue` resolves any Aspire `IValueProvider`, including secret parameters, into the supplied configuration key during deployment preparation. Both are written into `.env.<environment>` and loaded by `DockerComposeDeploymentTestingBuilder`; CI does not parse generated YAML or copy individual values into the test command.

The Compose builder represents deployed services as already allocated Aspire endpoint resources. Starting its `DistributedApplication` starts only the local testing model and health monitoring; the Compose deployment continues to own the service processes.

Environment-specific files can contain resolved secrets. `aspire-output` is ignored by Git and should not be retained as a CI artifact.
