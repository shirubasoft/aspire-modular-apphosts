using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Publishing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Hosting.ModularAppHosts.Tests;

public sealed class FullControlModulePreviewTests
{
    private const string SourceRepository = "https://github.com/acme/preview-source.git";

    [Fact]
    public async Task Save_load_and_resolve_preserve_sparse_overrides_and_sanitize_the_source_ref()
    {
        using var directory = TemporaryDirectory.Create();
        var firstPath = Path.Combine(directory.Path, "first.json");
        var secondPath = Path.Combine(directory.Path, "second.json");
        var manifest = CreateManifest();

        await manifest.SaveAsync(firstPath, TestContext.Current.CancellationToken);
        var loaded = await FullControlModulePreviewManifest.LoadAsync(
            firstPath,
            TestContext.Current.CancellationToken);
        await loaded.SaveAsync(secondPath, TestContext.Current.CancellationToken);

        Assert.Equal(
            await File.ReadAllTextAsync(firstPath, TestContext.Current.CancellationToken),
            await File.ReadAllTextAsync(secondPath, TestContext.Current.CancellationToken));
        var tags = loaded.ResolveContainerTags("feat/module-preview#42");
        Assert.Equal("feat-module-preview-42", tags["catalog-api"]);
        Assert.Equal("feat-module-preview-42", tags["catalog-worker"]);
        Assert.Equal("7.2.0", tags["shared-cache"]);
        Assert.Equal(3, tags.Count);
    }

    [Theory]
    [InlineData("-invalid")]
    [InlineData("contains space")]
    [InlineData("tag@digest")]
    public void Validate_rejects_invalid_explicit_container_tags(string tag)
    {
        var manifest = CreateManifest();
        manifest.ContainerTags["shared-cache"] = tag;

        Assert.Contains(
            "valid container tag",
            Assert.Throws<InvalidDataException>(manifest.Validate).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_rejects_overlapping_source_ref_and_explicit_resources()
    {
        var manifest = CreateManifest();
        manifest.ContainerTags.Add("CATALOG-API", "explicit");

        Assert.Contains(
            "both",
            Assert.Throws<InvalidDataException>(manifest.Validate).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Full_control_tags_force_container_mode_and_elide_repository_checkout_with_effective_aliases()
    {
        using var directory = TemporaryDirectory.Create();
        var builder = CreateBuilder(directory.Path);
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:GitExecutablePath"] =
            Path.Combine(directory.Path, "missing-git");
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:GitHubCliPath"] =
            Path.Combine(directory.Path, "missing-gh");
        builder.ApplyFullControlModulePreview(
            CreateManifest(),
            CreateSource("feat/catalog-v4"));

        await builder.DefineModuleAsync("catalog", "1", definition =>
        {
            definition.WithRepository(SourceRepository);
            definition.AddProject(
                    "api",
                    "src/Catalog.Api/Catalog.Api.csproj",
                    ModuleProjectPathBase.Repository)
                .ExportAsContainer("ghcr.io/acme/catalog-api", "dotnet", ["publish"]);
            definition.AddContainer("worker", "ghcr.io/acme/catalog-worker", "main")
                .WithImagePublishCommand(new ModuleContainerExportOptions(
                    "ghcr.io/acme/catalog-worker",
                    "dotnet",
                    ["publish"]));
        }, TestContext.Current.CancellationToken);
        await builder.ImportModuleAsync(
            "catalog",
            new ModuleImportOptions
            {
                ResourceAliases =
                {
                    ["api"] = "catalog-api",
                    ["worker"] = "catalog-worker"
                }
            },
            TestContext.Current.CancellationToken);

        Assert.Empty(builder.Resources.OfType<ProjectResource>());
        Assert.Collection(
            builder.Resources.OfType<ContainerResource>().OrderBy(resource => resource.Name),
            resource => AssertContainerTag(resource, "catalog-api", "feat-catalog-v4"),
            resource => AssertContainerTag(resource, "catalog-worker", "feat-catalog-v4"));
        Assert.DoesNotContain(builder.Resources, resource => resource is ParameterResource);
        Assert.DoesNotContain(builder.Resources, resource => resource is ModuleRepositoryInstallerResource);
    }

    [Fact]
    public async Task Trusted_source_and_tag_overrides_are_enforced_at_model_finalization_and_publish()
    {
        using var directory = TemporaryDirectory.Create();
        var builder = CreateBuilder(directory.Path);
        builder
            .AddContainer("shared-cache", "redis", "main")
            .WithImageSHA256(new string('a', 64));
        builder.ApplyFullControlModulePreview(CreateManifest(), CreateSource("feat/catalog-v4"));
        var configuredSharedCache = Assert.Single(
            builder.Resources.OfType<ContainerResource>(),
            resource => resource.Name == "shared-cache");
        AssertContainerTag(configuredSharedCache, "shared-cache", "7.2.0", expectedSha256: null);
        await AddCatalogModuleAsync(builder);

        await using var application = builder.Build();
        var model = application.Services.GetRequiredService<DistributedApplicationModel>();
        await builder.Eventing.PublishAsync(
            new AfterResourcesCreatedEvent(application.Services, model),
            TestContext.Current.CancellationToken);
        var sharedCache = Assert.Single(
            model.Resources.OfType<ContainerResource>(),
            resource => resource.Name == "shared-cache");
        AssertContainerTag(sharedCache, "shared-cache", "7.2.0", expectedSha256: null);

        var image = Assert.Single(sharedCache.Annotations.OfType<ContainerImageAnnotation>());
        image.Tag = "drifted";
        await builder.Eventing.PublishAsync(
            new BeforePublishEvent(application.Services, model),
            TestContext.Current.CancellationToken);
        AssertContainerTag(sharedCache, "shared-cache", "7.2.0", expectedSha256: null);
    }

    [Fact]
    public async Task Lifecycle_rejects_a_source_repository_not_declared_by_any_AppHost_module()
    {
        using var directory = TemporaryDirectory.Create();
        var builder = CreateBuilder(directory.Path);
        var manifest = new FullControlModulePreviewManifest();
        manifest.ContainerTags.Add("shared-cache", "7.2.0");
        builder.ApplyFullControlModulePreview(
            manifest,
            CreateSource("feat/catalog-v4", "https://github.com/acme/untrusted.git"));
        await AddCatalogModuleAsync(builder);
        builder.AddContainer("shared-cache", "redis", "main");

        await using var application = builder.Build();
        var model = application.Services.GetRequiredService<DistributedApplicationModel>();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            builder.Eventing.PublishAsync(
                new AfterResourcesCreatedEvent(application.Services, model),
                TestContext.Current.CancellationToken));

        Assert.Contains("not declared", exception.Message, StringComparison.Ordinal);
        Assert.Contains(SourceRepository, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lifecycle_rejects_unknown_effective_resource_names()
    {
        using var directory = TemporaryDirectory.Create();
        var builder = CreateBuilder(directory.Path);
        var manifest = new FullControlModulePreviewManifest();
        manifest.ContainerTags.Add("missing-resource", "preview");
        builder.ApplyFullControlModulePreview(manifest, CreateSource("feat/catalog-v4"));
        await AddCatalogModuleAsync(builder);

        await using var application = builder.Build();
        var model = application.Services.GetRequiredService<DistributedApplicationModel>();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            builder.Eventing.PublishAsync(
                new BeforePublishEvent(application.Services, model),
                TestContext.Current.CancellationToken));

        Assert.Contains("unknown AppHost resource 'missing-resource'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lifecycle_rejects_non_container_targets()
    {
        using var directory = TemporaryDirectory.Create();
        var builder = CreateBuilder(directory.Path);
        var manifest = new FullControlModulePreviewManifest();
        manifest.ContainerTags.Add("runtime-setting", "preview");
        builder.ApplyFullControlModulePreview(manifest, CreateSource("feat/catalog-v4"));
        await AddCatalogModuleAsync(builder);
        builder.AddParameter("runtime-setting");

        await using var application = builder.Build();
        var model = application.Services.GetRequiredService<DistributedApplicationModel>();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            builder.Eventing.PublishAsync(
                new AfterResourcesCreatedEvent(application.Services, model),
                TestContext.Current.CancellationToken));

        Assert.Contains("not a container resource", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Configuration_is_optional_but_requires_trusted_source_values_when_enabled()
    {
        using var directory = TemporaryDirectory.Create();
        var builder = CreateBuilder(directory.Path);

        Assert.Same(
            builder,
            await builder.ApplyFullControlModulePreviewFromConfigurationAsync(
                TestContext.Current.CancellationToken));

        builder.Configuration[$"{FullControlModulePreviewOptions.ConfigurationSectionName}:ManifestPath"] =
            "preview.json";
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            builder.ApplyFullControlModulePreviewFromConfigurationAsync(
                TestContext.Current.CancellationToken));
        Assert.Contains("trusted CI context", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Configuration_loads_a_relative_manifest_and_applies_trusted_source_context()
    {
        using var directory = TemporaryDirectory.Create();
        var manifestPath = Path.Combine(directory.Path, "full-control-preview.json");
        await CreateManifest().SaveAsync(manifestPath, TestContext.Current.CancellationToken);
        var builder = CreateBuilder(directory.Path);
        var sharedCache = builder.AddContainer("shared-cache", "redis", "main").Resource;
        builder.Configuration[$"{FullControlModulePreviewOptions.ConfigurationSectionName}:ManifestPath"] =
            Path.GetFileName(manifestPath);
        builder.Configuration[$"{FullControlModulePreviewOptions.ConfigurationSectionName}:SourceRepository"] =
            SourceRepository;
        builder.Configuration[$"{FullControlModulePreviewOptions.ConfigurationSectionName}:SourceRef"] =
            "feat/configured-preview";

        var returned = await builder.ApplyFullControlModulePreviewFromConfigurationAsync(
            TestContext.Current.CancellationToken);

        Assert.Same(builder, returned);
        AssertContainerTag(sharedCache, "shared-cache", "7.2.0");
    }

    private static async Task AddCatalogModuleAsync(IDistributedApplicationBuilder builder)
    {
        var module = await builder.ExportModuleAsync("catalog", definition =>
        {
            definition.WithRepository(SourceRepository);
            definition.AddContainer("catalog-api", "ghcr.io/acme/catalog-api", "main");
            definition.AddContainer("catalog-worker", "ghcr.io/acme/catalog-worker", "main");
        }, TestContext.Current.CancellationToken);
        await builder.AddAsync(module, TestContext.Current.CancellationToken);
    }

    private static FullControlModulePreviewManifest CreateManifest()
    {
        var manifest = new FullControlModulePreviewManifest();
        manifest.SourceRefResources.Add("catalog-api");
        manifest.SourceRefResources.Add("catalog-worker");
        manifest.ContainerTags.Add("shared-cache", "7.2.0");
        return manifest;
    }

    private static FullControlModulePreviewSource CreateSource(
        string sourceRef,
        string repository = SourceRepository) =>
        new()
        {
            Repository = repository,
            Ref = sourceRef
        };

    private static void AssertContainerTag(
        ContainerResource resource,
        string expectedResource,
        string expectedTag,
        string? expectedSha256 = null)
    {
        Assert.Equal(expectedResource, resource.Name);
        var image = Assert.Single(resource.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal(expectedTag, image.Tag);
        Assert.Equal(expectedSha256, image.SHA256);
    }

    private static IDistributedApplicationBuilder CreateBuilder(string projectDirectory) =>
        DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = [],
            DisableDashboard = true,
            ProjectDirectory = projectDirectory
        });
}
