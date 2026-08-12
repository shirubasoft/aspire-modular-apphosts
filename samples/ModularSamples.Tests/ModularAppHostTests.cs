using Aspire.Hosting;
using Aspire.Hosting.Testing;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace ModularSamples.Tests;

public sealed class ModularAppHostTests
{
    private const string ExpectedMessage = "Hello from an arbitrary exported Aspire resource.";
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(15);

    [Fact]
    public async Task Both_AppHosts_apply_module_callbacks_and_run_the_exported_resources()
    {
        await VerifyAppHostAsync<Projects.ModularSample_AppHostA>(hasGateway: false);
        await VerifyAppHostAsync<Projects.ModularSample_AppHostB>(hasGateway: true);
    }

    private static async Task VerifyAppHostAsync<TEntryPoint>(bool hasGateway)
        where TEntryPoint : class
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TestTimeout);
        await using var builder = await DistributedApplicationTestingBuilder
            .CreateAsync<TEntryPoint>([], timeout.Token);
        await using var application = await builder.BuildAsync(timeout.Token)
            .WaitAsync(TestTimeout, timeout.Token);

        await application.StartAsync(timeout.Token).WaitAsync(TestTimeout, timeout.Token);
        await WaitForHealthyAsync(application, "sample-api", timeout.Token);
        await WaitForHealthyAsync(application, "sample-static", timeout.Token);
        await WaitForHealthyAsync(application, "sample-generated-static", timeout.Token);
        if (hasGateway)
        {
            await WaitForHealthyAsync(application, "dependency-gateway", timeout.Token);
        }

        using var apiClient = application.CreateHttpClient("sample-api", "http");
        var api = await apiClient.GetFromJsonAsync<JsonElement>("/", timeout.Token);
        Assert.Equal("sample-api", api.GetProperty("service").GetString());
        Assert.Equal(ExpectedMessage, api.GetProperty("message").GetString());

        using var staticClient = application.CreateHttpClient("sample-static", "http");
        Assert.Equal(ExpectedMessage, await staticClient.GetStringAsync("/", timeout.Token));

        using var generatedClient = application.CreateHttpClient("sample-generated-static", "http");
        var generated = await generatedClient.GetStringAsync("/", timeout.Token);
        Assert.Contains("published by a modular AppHost", generated, StringComparison.Ordinal);

        if (hasGateway)
        {
            using var gatewayClient = application.CreateHttpClient("dependency-gateway", "http");
            var gateway = await gatewayClient.GetFromJsonAsync<JsonElement>("/", timeout.Token);
            Assert.Equal("dependency-gateway", gateway.GetProperty("service").GetString());
            Assert.Equal(ExpectedMessage, gateway.GetProperty("message").GetString());
            Assert.StartsWith("http", gateway.GetProperty("api").GetString(), StringComparison.Ordinal);
            Assert.StartsWith("http", gateway.GetProperty("staticSite").GetString(), StringComparison.Ordinal);
            Assert.StartsWith(
                "http",
                gateway.GetProperty("generatedStaticSite").GetString(),
                StringComparison.Ordinal);
        }
    }

    private static async Task WaitForHealthyAsync(
        DistributedApplication application,
        string resourceName,
        CancellationToken cancellationToken)
        => await application.ResourceNotifications.WaitForResourceHealthyAsync(
            resourceName,
            cancellationToken).WaitAsync(TestTimeout, cancellationToken);
}
