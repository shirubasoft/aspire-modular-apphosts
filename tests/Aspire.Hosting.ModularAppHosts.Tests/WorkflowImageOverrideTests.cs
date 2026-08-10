using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ModularAppHosts;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Aspire.Hosting.ModularAppHosts.Tests;

public sealed class WorkflowImageOverrideTests
{
    [Fact]
    public void Complete_identity_wins_last_and_forces_remote_container_consumption()
    {
        var configuration = CreateConfiguration(
            ("Module", "orders"),
            ("Resource", "api"),
            ("ResourceKind", nameof(ModuleResourceKind.Project)),
            ("Registry", "workflow.example.test"),
            ("Repository", "acme/orders"),
            ("Tag", "candidate"));
        var project = new DistributedApplicationModuleProjectOptions
        {
            ImageRegistry = "configured.example.test",
            ImageName = "configured/orders",
            ImageTag = "configured",
            PublishImage = true,
            ProjectMode = ModuleProjectMode.Project,
            ImagePullPolicy = ImagePullPolicy.Missing
        };
        var options = new ModularAppHostsOptions();
        options.Modules.Add("orders", new DistributedApplicationModuleOptions
        {
            Projects = { ["api"] = project }
        });

        WorkflowImageOverrideLoader.Apply(configuration, options);

        Assert.Equal("workflow.example.test", project.ImageRegistry);
        Assert.Equal("acme/orders", project.ImageName);
        Assert.Equal("candidate", project.ImageTag);
        Assert.Null(project.ImageSHA256);
        Assert.False(project.PublishImage);
        Assert.Equal(ModuleProjectMode.Container, project.ProjectMode);
        Assert.Equal(ImagePullPolicy.Always, project.ImagePullPolicy);
        Assert.True(project.HasFullWorkflowImageOverride);
    }

    [Fact]
    public void Tag_only_override_preserves_build_and_push_configuration_and_replaces_a_digest()
    {
        var configuration = CreateConfiguration(
            ("Module", "orders"),
            ("Resource", "api"),
            ("ResourceKind", nameof(ModuleResourceKind.Container)),
            ("Tag", "branch-42"));
        var container = new DistributedApplicationModuleContainerOptions
        {
            ImageRegistry = "registry.example.test",
            ImageName = "acme/orders",
            ImageSHA256 = $"sha256:{new string('a', 64)}",
            PublishImage = true
        };
        var options = new ModularAppHostsOptions();
        options.Modules.Add("orders", new DistributedApplicationModuleOptions
        {
            Containers = { ["api"] = container }
        });

        WorkflowImageOverrideLoader.Apply(configuration, options);

        Assert.Equal("branch-42", container.ImageTag);
        Assert.Null(container.ImageSHA256);
        Assert.True(container.PublishImage);
        Assert.Equal("registry.example.test", container.ImageRegistry);
        Assert.Equal("acme/orders", container.ImageName);
        Assert.False(container.HasFullWorkflowImageOverride);
    }

    [Theory]
    [InlineData("Registry", "", "complete image identity")]
    [InlineData("Tag", "invalid tag", "distribution tag")]
    public void Invalid_overrides_fail_before_materialization(
        string property,
        string value,
        string expected)
    {
        var values = new Dictionary<string, string?>
        {
            ["Module"] = "orders",
            ["Resource"] = "api",
            ["ResourceKind"] = nameof(ModuleResourceKind.Project),
            ["Registry"] = "registry.example.test",
            ["Repository"] = "acme/orders",
            ["Tag"] = "candidate"
        };
        values[property] = value;
        var configuration = CreateConfiguration(values.Select(pair => (pair.Key, pair.Value!)).ToArray());

        var exception = Assert.Throws<InvalidOperationException>(() =>
            WorkflowImageOverrideLoader.Apply(configuration, new ModularAppHostsOptions()));

        Assert.Contains(expected, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Duplicate_identities_are_case_insensitive()
    {
        var configuration = new ConfigurationManager();
        Configure(configuration, 0, "orders", "api");
        Configure(configuration, 1, "ORDERS", "API");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            WorkflowImageOverrideLoader.Apply(configuration, new ModularAppHostsOptions()));

        Assert.Contains("duplicate", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static ConfigurationManager CreateConfiguration(params (string Name, string Value)[] values)
    {
        var configuration = new ConfigurationManager();
        var section = $"{ModularAppHostsOptions.ConfigurationSectionName}:{WorkflowImageOverrideLoader.SectionName}:0";
        foreach (var (name, value) in values)
        {
            configuration[$"{section}:{name}"] = value;
        }

        return configuration;
    }

    private static void Configure(
        ConfigurationManager configuration,
        int index,
        string module,
        string resource)
    {
        var section =
            $"{ModularAppHostsOptions.ConfigurationSectionName}:{WorkflowImageOverrideLoader.SectionName}:{index}";
        configuration[$"{section}:Module"] = module;
        configuration[$"{section}:Resource"] = resource;
        configuration[$"{section}:ResourceKind"] = nameof(ModuleResourceKind.Container);
        configuration[$"{section}:Tag"] = "candidate";
    }
}
