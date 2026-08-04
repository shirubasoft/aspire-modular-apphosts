using Aspire.Hosting.ModularAppHosts;
using Xunit;

namespace Aspire.Hosting.ModularAppHosts.Tests;

public sealed class AspireDeploymentTestConfigurationTests
{
    [Fact]
    public void Load_imports_endpoints_and_values_from_an_aspire_environment_file()
    {
        var filePath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(
                filePath,
                """
                # External test endpoint catalog-api
                ASPIRE_TEST_ENDPOINT__CATALOG_API=http://localhost:5101/

                # External test configuration value orders-api-key
                ASPIRE_TEST_VALUE__ORDERS_API_KEY=secret=with=separators
                """);

            var configuration = AspireDeploymentTestConfiguration.Load(filePath);

            Assert.Equal(new Uri("http://localhost:5101/"), configuration.GetEndpoint("catalog-api"));
            Assert.Equal("secret=with=separators", configuration.GetValue("orders-api-key"));
            using var client = configuration.CreateHttpClient("catalog-api");
            Assert.Equal(new Uri("http://localhost:5101/"), client.BaseAddress);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public void GetEndpoint_rejects_an_invalid_exported_uri()
    {
        var filePath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(filePath, "ASPIRE_TEST_ENDPOINT__CATALOG=not-an-endpoint");
            var configuration = AspireDeploymentTestConfiguration.Load(filePath);

            var exception = Assert.Throws<InvalidOperationException>(() => configuration.GetEndpoint("catalog"));

            Assert.Contains("invalid absolute URI", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public void GetValue_reports_a_missing_export_by_name()
    {
        var filePath = Path.GetTempFileName();
        try
        {
            var configuration = AspireDeploymentTestConfiguration.Load(filePath);

            var exception = Assert.Throws<InvalidOperationException>(() => configuration.GetValue("missing"));

            Assert.Contains("missing", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(filePath);
        }
    }
}
