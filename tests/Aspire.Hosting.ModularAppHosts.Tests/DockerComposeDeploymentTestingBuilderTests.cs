using System.Text;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ModularAppHosts;
using Aspire.Hosting.Testing;
using Xunit;

namespace Aspire.Hosting.ModularAppHosts.Tests;

public sealed class DockerComposeDeploymentTestingBuilderTests
{
    [Fact]
    public async Task Create_imports_endpoints_and_configuration_into_an_Aspire_testing_builder()
    {
        var endpointName = Encode("catalog-api");
        var configurationKey = Encode("Parameters:orders-api-key");
        var filePath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(
                filePath,
                $$"""
                # External test endpoint catalog-api
                ASPIRE_TEST_ENDPOINT__{{endpointName}}=http://localhost:5101/
                ASPIRE_TEST_ENDPOINT_HEALTH_PATH__{{endpointName}}=/health

                # External test configuration value Parameters:orders-api-key
                ASPIRE_TEST_VALUE__{{configurationKey}}=secret=with=separators
                """);

            var builder = DockerComposeDeploymentTestingBuilder
                .Create<DockerComposeDeploymentTestingBuilderTests>(filePath);

            Assert.IsAssignableFrom<IDistributedApplicationTestingBuilder>(builder);
            Assert.Equal("secret=with=separators", builder.Configuration["Parameters:orders-api-key"]);
            Assert.True(builder.Resources.TryGetByName("catalog-api", out var resource));
            Assert.IsAssignableFrom<IResourceWithEndpoints>(resource);

            var endpoint = Assert.Single(resource.Annotations.OfType<EndpointAnnotation>());
            Assert.Equal("http", endpoint.Name);
            Assert.Equal("localhost", endpoint.AllocatedEndpoint?.Address);
            Assert.Equal(5101, endpoint.AllocatedEndpoint?.Port);
            Assert.Contains(resource.Annotations, annotation => annotation is HealthCheckAnnotation);

            await using var application = await builder.BuildAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public void Create_rejects_an_invalid_exported_uri()
    {
        var endpointName = Encode("catalog");
        var filePath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(filePath, $"ASPIRE_TEST_ENDPOINT__{endpointName}=not-an-endpoint");

            var exception = Assert.Throws<InvalidOperationException>(() =>
                DockerComposeDeploymentTestingBuilder
                    .Create<DockerComposeDeploymentTestingBuilderTests>(filePath));

            Assert.Contains("invalid HTTP URI", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public void Create_rejects_an_invalid_encoded_name()
    {
        var filePath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(filePath, "ASPIRE_TEST_VALUE__NOT_HEX=value");

            var exception = Assert.Throws<InvalidOperationException>(() =>
                DockerComposeDeploymentTestingBuilder
                    .Create<DockerComposeDeploymentTestingBuilderTests>(filePath));

            Assert.Contains("invalid encoded name", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    private static string Encode(string value) => Convert.ToHexString(Encoding.UTF8.GetBytes(value));
}
