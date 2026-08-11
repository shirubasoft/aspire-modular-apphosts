using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting;
using Xunit;

namespace Aspire.Hosting.ModularAppHosts.Tests;

public sealed class ModuleImageWorkflowConfigurationTests
{
    [Fact]
    public void Manifest_projects_to_standard_configuration_keys()
    {
        var document = new ModuleImageManifestDocument();
        document.Images.Add(new ModuleImageManifestEntry
        {
            Module = "orders",
            Resource = "api",
            ResourceKind = ModuleResourceKind.Project,
            Registry = "registry.example.test",
            Repository = "acme/orders-api",
            Tag = "candidate"
        });

        var values = ModuleImageWorkflowConfiguration.Create(document);

        const string prefix = "Aspire:ModularAppHosts:Modules:orders:Projects:api";
        Assert.Equal("registry.example.test", values[$"{prefix}:ImageRegistry"]);
        Assert.Equal("acme/orders-api", values[$"{prefix}:ImageName"]);
        Assert.Equal("candidate", values[$"{prefix}:ImageTag"]);
        Assert.Equal(string.Empty, values[$"{prefix}:ImageSHA256"]);
        Assert.Equal(bool.FalseString, values[$"{prefix}:PublishImage"]);
        Assert.Equal(nameof(ImagePullPolicy.Always), values[$"{prefix}:ImagePullPolicy"]);
        Assert.Equal(nameof(ModuleProjectMode.Container), values[$"{prefix}:ProjectMode"]);
    }

    [Theory]
    [InlineData("orders:shadow")]
    [InlineData("orders__shadow")]
    [InlineData("orders/shadow")]
    [InlineData("orders=shadow")]
    public void Manifest_rejects_names_that_can_escape_identity_or_configuration_segments(string module)
    {
        var document = new ModuleImageManifestDocument();
        document.Images.Add(new ModuleImageManifestEntry
        {
            Module = module,
            Resource = "api",
            ResourceKind = ModuleResourceKind.Container,
            Registry = "registry.example.test",
            Repository = "acme/orders-api",
            Tag = "candidate"
        });

        var exception = Assert.Throws<InvalidDataException>(document.Validate);

        Assert.Contains("reserved", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Description_selection_uses_the_shared_module_resource_and_identity_grammar()
    {
        var descriptions = new[]
        {
            CreateDescription("orders", "api", "orders-api"),
            CreateDescription("orders", "worker", "orders-worker"),
            CreateDescription("catalog", "api", "catalog-api")
        };

        Assert.Equal(2, new ModuleImageSelection(["orders"])
            .ResolveDescriptions(descriptions, "test images").Count);
        Assert.Equal("catalog-api", Assert.Single(new ModuleImageSelection(["catalog/api"])
            .ResolveDescriptions(descriptions, "test images")).EffectiveResource);
        Assert.Throws<InvalidOperationException>(() => new ModuleImageSelection(["api"])
            .ResolveDescriptions(descriptions, "test images"));
    }

    private static ModuleImageDescription CreateDescription(
        string module,
        string resource,
        string effectiveResource) => new()
        {
            Module = module,
            Resource = resource,
            EffectiveResource = effectiveResource,
            ResourceKind = ModuleResourceKind.Project,
            Repository = $"acme/{module}-{resource}",
            Reference = $"acme/{module}-{resource}:candidate",
            PullReference = $"acme/{module}-{resource}:candidate"
        };
}
