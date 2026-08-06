using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Options;
using Xunit;

namespace Aspire.Hosting.ModularAppHosts.Tests;

public sealed class ModulePreviewManifestTests
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private const string OtherCommit = "89abcdef0123456789abcdef0123456789abcdef";
    private const string RequestSha256 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string PackageSha256 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string ImageSha256 =
        "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    [Fact]
    public async Task Save_and_load_round_trip_deterministic_validated_json()
    {
        using var directory = TemporaryDirectory.Create();
        var firstPath = Path.Combine(directory.Path, "first.json");
        var secondPath = Path.Combine(directory.Path, "second.json");
        var manifest = CreateManifest();

        await manifest.SaveAsync(firstPath, TestContext.Current.CancellationToken);
        var loaded = await ModulePreviewManifest.LoadAsync(
            firstPath,
            TestContext.Current.CancellationToken);
        await loaded.SaveAsync(secondPath, TestContext.Current.CancellationToken);

        Assert.Equal(
            await File.ReadAllTextAsync(firstPath, TestContext.Current.CancellationToken),
            await File.ReadAllTextAsync(secondPath, TestContext.Current.CancellationToken));
        Assert.False(loaded.Producer.Dirty);
        var module = Assert.Single(loaded.Modules);
        Assert.Equal("orders", module.Name);
        Assert.Equal(Commit, module.Commit);
        Assert.Equal("Acme.Orders.Contract", Assert.Single(loaded.Contracts).PackageId);
        Assert.Equal(ModulePreviewResourceKind.Container, Assert.Single(loaded.Images).ResourceKind);
    }

    [Fact]
    public void Validate_rejects_unknown_schema_duplicates_secrets_and_non_immutable_content()
    {
        var schema = CreateManifest();
        schema.SchemaVersion = 2;
        Assert.Contains("schema version", Assert.Throws<InvalidDataException>(schema.Validate).Message);

        var duplicate = CreateManifest();
        duplicate.Modules.Add(new ModulePreviewSelection
        {
            Name = "ORDERS",
            Repository = "https://github.com/acme/orders.git",
            Commit = OtherCommit
        });
        Assert.Contains("duplicate", Assert.Throws<InvalidDataException>(duplicate.Validate).Message);

        var secret = CreateManifest();
        secret.Modules[0].Repository = "https://token@github.com/acme/orders.git";
        Assert.Contains("credentials", Assert.Throws<InvalidDataException>(secret.Validate).Message);

        var abbreviated = CreateManifest();
        abbreviated.Modules[0].Commit = "0123456";
        Assert.Contains("40- or 64-character", Assert.Throws<InvalidDataException>(abbreviated.Validate).Message);

        var dirty = CreateManifest();
        dirty.Producer.Dirty = true;
        Assert.Contains("must be false", Assert.Throws<InvalidDataException>(dirty.Validate).Message);
    }

    [Fact]
    public void Validate_rejects_unselected_duplicate_and_malformed_artifacts()
    {
        var unselectedContract = CreateManifest();
        unselectedContract.Contracts[0].Module = "catalog";
        Assert.Contains("not selected", Assert.Throws<InvalidDataException>(unselectedContract.Validate).Message);

        var duplicateContract = CreateManifest();
        duplicateContract.Contracts.Add(new ModulePreviewContractRequest
        {
            Module = "ORDERS",
            PackageId = "Acme.Orders.Other",
            Version = "1.0.0"
        });
        Assert.Contains("duplicate contract", Assert.Throws<InvalidDataException>(duplicateContract.Validate).Message);

        var taggedImage = CreateManifest();
        taggedImage.Images[0].Repository = "ghcr.io/acme/orders:preview";
        Assert.Contains("tag", Assert.Throws<InvalidDataException>(taggedImage.Validate).Message);

        var uppercaseDigest = CreateManifest();
        uppercaseDigest.Images[0].Sha256 =
            "sha256:CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC";
        Assert.Contains("lowercase", Assert.Throws<InvalidDataException>(uppercaseDigest.Validate).Message);

        var invalidKind = CreateManifest();
        invalidKind.Images[0].ResourceKind = (ModulePreviewResourceKind)42;
        Assert.Contains("resource kind", Assert.Throws<InvalidDataException>(invalidKind.Validate).Message);
    }

    [Fact]
    public async Task Resolution_round_trips_verified_provenance_and_rejects_invalid_identity()
    {
        using var directory = TemporaryDirectory.Create();
        var firstPath = Path.Combine(directory.Path, "first-resolution.json");
        var secondPath = Path.Combine(directory.Path, "second-resolution.json");
        var resolution = CreateResolution();
        resolution.Contracts.Add(new ModulePreviewResolvedContract
        {
            Module = "orders",
            PackageId = "Acme.Orders.Contract",
            Version = "1.2.3-preview.1",
            Sha256 = PackageSha256,
            Source = "https://api.nuget.org/v3/index.json",
            PackagePath = Path.Combine(directory.Path, "Acme.Orders.Contract.nupkg")
        });

        await resolution.SaveAsync(firstPath, TestContext.Current.CancellationToken);
        var loaded = await ModulePreviewResolution.LoadAsync(
            firstPath,
            TestContext.Current.CancellationToken);
        await loaded.SaveAsync(secondPath, TestContext.Current.CancellationToken);

        Assert.Equal(
            await File.ReadAllTextAsync(firstPath, TestContext.Current.CancellationToken),
            await File.ReadAllTextAsync(secondPath, TestContext.Current.CancellationToken));
        Assert.Equal(RequestSha256, loaded.RequestSha256);
        Assert.Equal(OtherCommit, loaded.Consumer.Commit);
        Assert.Equal(PackageSha256, Assert.Single(loaded.Contracts).Sha256);
        Assert.Equal(ImageSha256, Assert.Single(loaded.Images).Sha256);

        loaded.RequestSha256 = RequestSha256.ToUpperInvariant();
        Assert.Contains("lowercase", Assert.Throws<InvalidDataException>(loaded.Validate).Message);
    }

    [Fact]
    public async Task Apply_manifest_sets_authoritative_programmatic_module_options()
    {
        using var directory = TemporaryDirectory.Create();
        var manifestPath = Path.Combine(directory.Path, "preview.json");
        await CreateManifest().SaveAsync(manifestPath, TestContext.Current.CancellationToken);
        var builder = CreateBuilder(directory.Path);
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:Modules:orders:Repository"] =
            "https://github.com/acme/configured.git";

        var returned = await builder.ApplyModulePreviewManifestAsync(
            manifestPath,
            TestContext.Current.CancellationToken);
        builder.ConfigureModularAppHosts(options =>
        {
            options.Modules["orders"].Repository = "https://github.com/acme/later-override.git";
            options.Modules["orders"].RepositoryRevision = OtherCommit;
        });

        Assert.Same(builder, returned);
        var options = Assert.Single(
            builder.Services,
            descriptor => descriptor.ServiceType == typeof(IOptions<ModularAppHostsOptions>));
        var configured = Assert.IsType<OptionsWrapper<ModularAppHostsOptions>>(options.ImplementationInstance).Value;
        Assert.Equal("https://github.com/acme/orders.git", configured.Modules["orders"].Repository);
        Assert.Equal(Commit, configured.Modules["orders"].RepositoryRevision);
    }

    [Fact]
    public async Task Preview_repository_bypasses_conflicting_repository_configuration_at_materialization()
    {
        using var directory = TemporaryDirectory.Create();
        var builder = CreateBuilder(directory.Path);
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:Modules:orders:Repository"] =
            "https://github.com/acme/configured.git";
        builder.ApplyModulePreviewManifest(CreateManifest());

        await builder.DefineModuleAsync("orders", "1", definition =>
        {
            definition.WithRepository("https://github.com/acme/orders.git");
            definition.AddContainer("api", "nginx");
        }, TestContext.Current.CancellationToken);
        await builder.ImportModuleAsync("orders", TestContext.Current.CancellationToken);

        Assert.DoesNotContain(builder.Resources, resource => resource is ParameterResource);
        var image = Assert.Single(
            Assert.Single(builder.Resources.OfType<ContainerResource>())
                .Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal("nginx", image.Image);
        Assert.Null(image.SHA256);
    }

    [Fact]
    public void Apply_resolution_makes_verified_image_options_authoritative()
    {
        using var directory = TemporaryDirectory.Create();
        var builder = CreateBuilder(directory.Path);
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:Modules:orders:Containers:api:ImageName"] =
            "untrusted.example/orders";
        builder.ApplyModulePreviewResolution(CreateResolution());
        builder.ConfigureModularAppHosts(options =>
        {
            var configured = options.Modules["orders"].Containers["api"];
            configured.ImageName = "later.example/orders";
            configured.ImageSHA256 = $"sha256:{new string('d', 64)}";
            configured.PublishImage = true;
            options.Modules["orders"].Containers["worker"] =
                new DistributedApplicationModuleContainerOptions { PublishImage = true };
            options.Modules["orders"].PublishImages = true;
        });

        var descriptor = Assert.Single(
            builder.Services,
            candidate => candidate.ServiceType == typeof(IOptions<ModularAppHostsOptions>));
        var options = Assert.IsType<OptionsWrapper<ModularAppHostsOptions>>(
            descriptor.ImplementationInstance).Value;
        var image = options.Modules["orders"].Containers["api"];
        Assert.Equal("ghcr.io", image.ImageRegistry);
        Assert.Equal("acme/orders", image.ImageName);
        Assert.Equal(ImageSha256, image.ImageSHA256);
        Assert.False(image.PublishImage);
        Assert.True(options.Modules["orders"].Containers["worker"].PublishImage);
        Assert.True(options.Modules["orders"].PublishImages);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    public async Task Mixed_module_elides_checkout_only_when_all_repository_dependent_resources_are_pinned(
        bool pinProject,
        bool pinPublishableContainer,
        bool expected)
    {
        using var directory = TemporaryDirectory.Create();
        var builder = CreateBuilder(directory.Path);
        var resolution = CreateResolution();
        resolution.Images.Clear();
        if (pinProject)
        {
            resolution.Images.Add(CreateImage("api", ModulePreviewResourceKind.Project));
        }

        if (pinPublishableContainer)
        {
            resolution.Images.Add(CreateImage("worker", ModulePreviewResourceKind.Container));
        }

        builder.ApplyModulePreviewResolution(resolution);
        var module = await builder.DefineModuleAsync("orders", "1", definition =>
        {
            definition.WithRepository("https://github.com/acme/orders.git");
            definition.AddProject(
                    "api",
                    Path.Combine(directory.Path, "missing-checkout", "Orders.Api.csproj"))
                .ExportAsContainer("declared.example/orders", "dotnet", ["publish"]);
            definition.AddContainer("worker", "declared.example/worker")
                .WithImagePublishCommand(new ModuleContainerExportOptions(
                    "declared.example/worker",
                    "dotnet",
                    ["publish"]));
        }, TestContext.Current.CancellationToken);

        var registry = DistributedApplicationModuleExtensions.GetOrCreateRegistryForPreview(builder);
        Assert.Equal(
            expected,
            registry.CanMaterializePreviewWithoutRepository(
                Assert.IsType<DistributedApplicationModule>(module)));
    }

    [Fact]
    public void Module_image_option_rejects_noncanonical_digest()
    {
        using var directory = TemporaryDirectory.Create();
        var builder = CreateBuilder(directory.Path);
        var exception = Assert.Throws<InvalidOperationException>(() =>
            builder.ConfigureModularAppHosts(options =>
                options.Modules["orders"] = new DistributedApplicationModuleOptions
                {
                    Containers =
                    {
                        ["api"] = new DistributedApplicationModuleContainerOptions
                        {
                            ImageSHA256 = $"sha256:{new string('A', 64)}"
                        }
                    }
                }));

        Assert.Contains("lowercase", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolution_materializes_prebuilt_container_without_repository_checkout()
    {
        using var directory = TemporaryDirectory.Create();
        var builder = CreateBuilder(directory.Path);
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:GitExecutablePath"] =
            Path.Combine(directory.Path, "missing-git");
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:AutoCloneRepositories"] = "true";
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:GitHubCliPath"] =
            Path.Combine(directory.Path, "missing-gh");
        builder.ApplyModulePreviewResolution(CreateResolution());

        await builder.DefineModuleAsync("orders", "1", definition =>
        {
            definition.WithRepository("https://github.com/acme/orders.git");
            definition.AddContainer("api", "declared.example/orders", "branch");
        }, TestContext.Current.CancellationToken);
        await builder.ImportModuleAsync("orders", TestContext.Current.CancellationToken);

        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var image = Assert.Single(container.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal("ghcr.io", image.Registry);
        Assert.Equal("acme/orders", image.Image);
        Assert.Equal(ImageSha256["sha256:".Length..], image.SHA256);
        Assert.DoesNotContain(builder.Resources, resource => resource is ParameterResource);
        Assert.DoesNotContain(builder.Resources, resource => resource is ModuleRepositoryInstallerResource);
    }

    [Fact]
    public async Task Resolution_materializes_factory_created_container_without_repository_checkout()
    {
        using var directory = TemporaryDirectory.Create();
        var builder = CreateBuilder(directory.Path);
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:GitExecutablePath"] =
            Path.Combine(directory.Path, "missing-git");
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:AutoCloneRepositories"] = "true";
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:GitHubCliPath"] =
            Path.Combine(directory.Path, "missing-gh");
        builder.ApplyModulePreviewResolution(CreateResolution());
        ModuleResourceImage? resolvedImage = null;

        await builder.DefineModuleAsync("orders", "1", definition =>
        {
            definition.WithRepository("https://github.com/acme/orders.git");
            definition.AddResource<ContainerResource>(
                "api",
                context =>
                {
                    resolvedImage = context.Image;
                    return context.ApplicationBuilder
                        .AddContainer(context.ResourceName, "library/declared")
                        .WithImageRegistry("docker.io");
                },
                new ModuleContainerExportOptions("orders", "dotnet", "publish")
                {
                    ImageRegistry = "declared.example"
                });
        }, TestContext.Current.CancellationToken);
        await builder.ImportModuleAsync("orders", TestContext.Current.CancellationToken);

        Assert.NotNull(resolvedImage);
        Assert.Equal("ghcr.io", resolvedImage.Registry);
        Assert.Equal("acme/orders", resolvedImage.Name);
        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var image = Assert.Single(container.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal("ghcr.io", image.Registry);
        Assert.Equal("acme/orders", image.Image);
        Assert.Equal(ImageSha256["sha256:".Length..], image.SHA256);
        Assert.DoesNotContain(builder.Resources, resource => resource is ParameterResource);
        Assert.DoesNotContain(builder.Resources, resource => resource is ModuleRepositoryInstallerResource);
    }

    [Fact]
    public async Task Resolution_project_image_forces_container_mode_and_native_digest()
    {
        using var directory = TemporaryDirectory.Create();
        var projectPath = Path.Combine(directory.Path, "missing-checkout", "Orders.Api.csproj");
        var builder = CreateBuilder(directory.Path);
        var resolution = CreateResolution(ModulePreviewResourceKind.Project);
        builder.ApplyModulePreviewResolution(resolution);

        await builder.DefineModuleAsync("orders", "1", definition =>
        {
            definition.WithRepository("https://github.com/acme/orders.git");
            definition.AddProject("api", projectPath)
                .ExportAsContainer("declared.example/orders", "dotnet", ["publish"]);
        }, TestContext.Current.CancellationToken);
        await builder.ImportModuleAsync("orders", TestContext.Current.CancellationToken);

        Assert.Empty(builder.Resources.OfType<ProjectResource>());
        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var image = Assert.Single(container.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal("ghcr.io", image.Registry);
        Assert.Equal("acme/orders", image.Image);
        Assert.Equal(ImageSha256["sha256:".Length..], image.SHA256);
        Assert.DoesNotContain(builder.Resources, resource => resource is ModuleRepositoryInstallerResource);
    }

    [Theory]
    [InlineData("missing", ModulePreviewResourceKind.Container, "does not declare")]
    [InlineData("api", ModulePreviewResourceKind.Project, "declares kind")]
    public async Task Resolution_rejects_missing_or_wrong_contract_resource(
        string resource,
        ModulePreviewResourceKind resourceKind,
        string expectedMessage)
    {
        using var directory = TemporaryDirectory.Create();
        var builder = CreateBuilder(directory.Path);
        var resolution = CreateResolution();
        resolution.Images[0].Resource = resource;
        resolution.Images[0].ResourceKind = resourceKind;
        builder.ApplyModulePreviewResolution(resolution);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            builder.DefineModuleAsync("orders", "1", definition =>
            {
                definition.WithRepository("https://github.com/acme/orders.git");
                definition.AddContainer("api", "nginx");
            }, TestContext.Current.CancellationToken));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preview_repository_must_match_the_exported_module_contract()
    {
        using var directory = TemporaryDirectory.Create();
        var builder = CreateBuilder(directory.Path);
        builder.ApplyModulePreviewManifest(CreateManifest());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            builder.DefineModuleAsync("orders", "1", definition =>
            {
                definition.WithRepository("https://github.com/acme/different.git");
                definition.AddContainer("api", "nginx");
            }, TestContext.Current.CancellationToken));

        Assert.Contains("different.git", exception.Message, StringComparison.Ordinal);
        Assert.Contains("orders.git", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preview_manifest_must_be_applied_before_module_materialization()
    {
        using var directory = TemporaryDirectory.Create();
        var builder = CreateBuilder(directory.Path);
        await builder.DefineModuleAsync("orders", "1", definition =>
            definition.AddContainer("api", "nginx"), TestContext.Current.CancellationToken);
        await builder.ImportModuleAsync("orders", TestContext.Current.CancellationToken);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            builder.ApplyModulePreviewManifest(CreateManifest()));

        Assert.Contains("before importing", exception.Message, StringComparison.Ordinal);
    }

    private static ModulePreviewManifest CreateManifest()
    {
        var manifest = new ModulePreviewManifest
        {
            Producer = new ModulePreviewProducer
            {
                Repository = "https://github.com/acme/orders.git",
                Commit = Commit,
                Dirty = false,
                Branch = "feat/preview",
                BaseRef = "refs/heads/main",
                BaseCommit = OtherCommit
            }
        };
        manifest.Modules.Add(new ModulePreviewSelection
        {
            Name = "orders",
            Repository = "https://github.com/acme/orders.git",
            Commit = Commit,
            Branch = "feat/preview",
            BaseRef = "refs/heads/main",
            BaseCommit = OtherCommit
        });
        manifest.Contracts.Add(new ModulePreviewContractRequest
        {
            Module = "orders",
            PackageId = "Acme.Orders.Contract",
            Version = "1.2.3-preview.1"
        });
        manifest.Images.Add(CreateImage(ModulePreviewResourceKind.Container));
        return manifest;
    }

    private static ModulePreviewResolution CreateResolution(
        ModulePreviewResourceKind resourceKind = ModulePreviewResourceKind.Container)
    {
        var resolution = new ModulePreviewResolution
        {
            RequestSha256 = RequestSha256,
            Consumer = new ModulePreviewConsumerIdentity
            {
                Repository = "https://github.com/acme/e2e.git",
                Commit = OtherCommit
            }
        };
        resolution.Modules.Add(new ModulePreviewSelection
        {
            Name = "orders",
            Repository = "https://github.com/acme/orders.git",
            Commit = Commit
        });
        resolution.Images.Add(CreateImage(resourceKind));
        return resolution;
    }

    private static ModulePreviewImageArtifact CreateImage(ModulePreviewResourceKind resourceKind) =>
        CreateImage("api", resourceKind);

    private static ModulePreviewImageArtifact CreateImage(
        string resource,
        ModulePreviewResourceKind resourceKind) =>
        new()
        {
            Module = "orders",
            Resource = resource,
            ResourceKind = resourceKind,
            Repository = resource == "api"
                ? "ghcr.io/acme/orders"
                : $"ghcr.io/acme/orders-{resource}",
            Sha256 = ImageSha256
        };

    private static IDistributedApplicationBuilder CreateBuilder(string projectDirectory) =>
        DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = [],
            DisableDashboard = true,
            ProjectDirectory = projectDirectory
        });
}
