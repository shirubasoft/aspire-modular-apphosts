using System.Net.Http.Json;
using Aspire.Hosting;
using Aspire.Hosting.ModularAppHosts;
using Aspire.Hosting.Testing;
using EShop.Modules;
using Xunit;

namespace EShop.E2E.Tests;

public sealed class EShopScenarioTests
{
    private const string AppHostMode = "apphost";
    private const string ComposeMode = "compose";
    private const string ModeEnvironmentVariableName = "ESHOP_E2E_MODE";
    private const string OrdersApiKey = "e2e-orders-key";
    private static readonly TimeSpan TestTimeout = Environment.GetEnvironmentVariable("CI") is null
        ? TimeSpan.FromMinutes(2)
        : TimeSpan.FromMinutes(5);

    [Fact]
    public async Task Customer_can_order_a_product_from_the_catalog()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await CreateEnvironmentAsync(cancellationToken);
        using var catalog = environment.CreateHttpClient(CatalogModule.ApiResourceName);
        using var orders = environment.CreateHttpClient(OrdersModule.ApiResourceName);

        var product = await catalog.GetFromJsonAsync<Product>("/products/coffee-mug", cancellationToken);
        Assert.NotNull(product);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/orders")
        {
            Content = JsonContent.Create(new CreateOrderRequest(product.Id, Quantity: 2))
        };
        request.Headers.Add("X-Orders-Api-Key", environment.OrdersApiKey);

        using var response = await orders.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var order = await response.Content.ReadFromJsonAsync<Order>(cancellationToken);

        Assert.NotNull(order);
        Assert.Equal(product.Id, order.ProductId);
        Assert.Equal(product.Name, order.ProductName);
        Assert.Equal(37.00m, order.Total);
    }

    private static async Task<ITestEnvironment> CreateEnvironmentAsync(CancellationToken cancellationToken)
    {
        var mode = Environment.GetEnvironmentVariable(ModeEnvironmentVariableName) ?? AppHostMode;
        return mode switch
        {
            AppHostMode => await RunningAppHostEnvironment.CreateAsync(cancellationToken),
            ComposeMode => await DeployedComposeEnvironment.CreateAsync(cancellationToken),
            _ => throw new InvalidOperationException(
                $"Unsupported {ModeEnvironmentVariableName} value '{mode}'. Use '{AppHostMode}' or '{ComposeMode}'.")
        };
    }

    private interface ITestEnvironment : IAsyncDisposable
    {
        string OrdersApiKey { get; }

        HttpClient CreateHttpClient(string resourceName);
    }

    private sealed class RunningAppHostEnvironment : ITestEnvironment
    {
        private readonly DistributedApplication _application;

        private RunningAppHostEnvironment(DistributedApplication application)
        {
            _application = application;
        }

        public string OrdersApiKey => EShopScenarioTests.OrdersApiKey;

        public static async Task<RunningAppHostEnvironment> CreateAsync(CancellationToken cancellationToken)
        {
            var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.EShop_E2E_AppHost>(
                [$"Parameters:orders-api-key={EShopScenarioTests.OrdersApiKey}"]);
            var application = await appHost.BuildAsync(cancellationToken)
                .WaitAsync(TestTimeout, cancellationToken);

            try
            {
                await application.StartAsync(cancellationToken)
                    .WaitAsync(TestTimeout, cancellationToken);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TestTimeout);
                await application.ResourceNotifications.WaitForResourceHealthyAsync(
                    CatalogModule.ApiResourceName,
                    timeout.Token);
                await application.ResourceNotifications.WaitForResourceHealthyAsync(
                    OrdersModule.ApiResourceName,
                    timeout.Token);
                return new RunningAppHostEnvironment(application);
            }
            catch
            {
                await application.DisposeAsync();
                throw;
            }
        }

        public HttpClient CreateHttpClient(string resourceName) =>
            _application.CreateHttpClient(resourceName, "http");

        public ValueTask DisposeAsync() => _application.DisposeAsync();
    }

    private sealed class DeployedComposeEnvironment : ITestEnvironment
    {
        private readonly AspireDeploymentTestConfiguration _configuration;

        private DeployedComposeEnvironment(AspireDeploymentTestConfiguration configuration)
        {
            _configuration = configuration;
        }

        public string OrdersApiKey => _configuration.GetValue("orders-api-key");

        public static async Task<DeployedComposeEnvironment> CreateAsync(CancellationToken cancellationToken)
        {
            var configuration = AspireDeploymentTestConfiguration.LoadFromEnvironment();
            await WaitForHealthyAsync(configuration, CatalogModule.ApiResourceName, cancellationToken);
            await WaitForHealthyAsync(configuration, OrdersModule.ApiResourceName, cancellationToken);
            return new DeployedComposeEnvironment(configuration);
        }

        public HttpClient CreateHttpClient(string resourceName) =>
            _configuration.CreateHttpClient(resourceName);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static async Task WaitForHealthyAsync(
            AspireDeploymentTestConfiguration configuration,
            string resourceName,
            CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TestTimeout);
            using var client = configuration.CreateHttpClient(resourceName);

            while (true)
            {
                try
                {
                    using var response = await client.GetAsync("/health", timeout.Token);
                    if (response.IsSuccessStatusCode)
                    {
                        return;
                    }
                }
                catch (HttpRequestException)
                {
                    // The Compose service may still be starting.
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250), timeout.Token);
            }
        }
    }

    private sealed record Product(string Id, string Name, decimal Price);

    private sealed record CreateOrderRequest(string ProductId, int Quantity);

    private sealed record Order(
        Guid Id,
        string ProductId,
        string ProductName,
        int Quantity,
        decimal Total);
}
