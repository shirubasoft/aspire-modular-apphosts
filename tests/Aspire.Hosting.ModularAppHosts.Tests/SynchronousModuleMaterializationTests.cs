#pragma warning disable ASPIRECOMMAND001

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Hosting.ModularAppHosts.Tests;

public sealed class SynchronousModuleMaterializationTests
{
    [Fact]
    public async Task Explicit_start_image_preparation_is_scoped_to_the_target_resource_event()
    {
        using var appHost = CreateGitAppHost();
        var builder = CreateBuilder(appHost.Path);
        builder.Configuration["DcpPublisher:CliPath"] = Environment.ProcessPath;
        builder.Configuration["DcpPublisher:DashboardPath"] = Environment.ProcessPath;
        var module = builder.ExportModule("orders", definition =>
        {
            definition.WithRepository(appHost.Path);
            definition.AddContainer("orders-api", "orders-api")
                .WithImagePublishCommand(new ModuleImageCommandOptions(
                    "orders-api",
                    "publisher-that-must-remain-lazy",
                    "build"))
                .Configure((_, container) => container.WithExplicitStart());
        });
        builder.AddModule(module);
        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        Assert.Single(container.Annotations.OfType<ExplicitStartupAnnotation>());
        var publisher = Assert.Single(container.Annotations.OfType<ModuleImagePublisherAnnotation>());

        await using var application = builder.Build();
        var model = application.Services.GetRequiredService<DistributedApplicationModel>();
        await builder.Eventing.PublishAsync(
            new BeforeStartEvent(application.Services, model),
            TestContext.Current.CancellationToken);
        Assert.False(publisher.TryGetPreparedImage(out _));

        await builder.Eventing.PublishAsync(
            new BeforeResourceStartedEvent(new ContainerResource("unrelated"), application.Services),
            TestContext.Current.CancellationToken);
        Assert.False(publisher.TryGetPreparedImage(out _));

        using var emptyServices = new ServiceCollection().BuildServiceProvider();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            builder.Eventing.PublishAsync(
                new BeforeResourceStartedEvent(container, emptyServices),
                TestContext.Current.CancellationToken));
        Assert.Contains(nameof(ResourceLoggerService), exception.Message, StringComparison.Ordinal);
        Assert.False(publisher.TryGetPreparedImage(out _));
    }

    [Fact]
    public void Import_registers_initialize_steps_without_invoking_git_or_reading_checkout_content()
    {
        using var appHost = CreateGitAppHost();
        var builder = CreateBuilder(appHost.Path);
        builder.ConfigureModularAppHosts(options =>
            options.GitExecutablePath = "git-must-not-run-during-materialization");
        builder.ExportModule("orders", definition =>
        {
            definition.WithRepository("https://example.test/acme/orders.git");
            definition.RequiresRepository();
            definition.AddResource<ContainerResource>("orders-api", context =>
                context.ApplicationBuilder.AddContainer(context.ResourceName, "busybox"));
        });

        var imported = builder.ImportModule("orders");

        Assert.Equal("orders", imported.Name);
        Assert.Single(builder.Resources.OfType<ContainerResource>());
        var registry = GetRegistry(builder);
        var requirement = Assert.Single(registry.RepositoryPlans!.Requirements);
        Assert.Equal("example.test/acme/orders", requirement.NormalizedRepository);
        Assert.False(Directory.Exists(requirement.RepositoryPath));
        var requiredCommands = Assert.Single(builder.Resources.OfType<ContainerResource>())
            .Annotations
            .OfType<RequiredCommandAnnotation>()
            .Select(annotation => annotation.Command)
            .ToArray();
        Assert.Equal(["git-must-not-run-during-materialization"], requiredCommands);
    }

    [Fact]
    public void GitHub_https_repository_advertises_Git_and_the_selected_credential_provider()
    {
        using var appHost = CreateGitAppHost();
        var builder = CreateBuilder(appHost.Path);
        builder.ConfigureModularAppHosts(options =>
        {
            options.GitExecutablePath = "custom-git";
            options.GitHubCliPath = "custom-gh";
        });
        builder.ExportModule("orders", definition =>
        {
            definition.WithRepository("https://github.com/acme/orders.git");
            definition.RequiresRepository();
            definition.AddContainer("orders-api", "busybox");
        });

        builder.ImportModule("orders");

        var requiredCommands = Assert.Single(builder.Resources.OfType<ContainerResource>())
            .Annotations
            .OfType<RequiredCommandAnnotation>()
            .Select(annotation => annotation.Command)
            .ToArray();
        Assert.Equal(["custom-git", "custom-gh"], requiredCommands);
    }

    [Fact]
    public async Task Normal_run_preflight_reports_the_initialize_command_for_planned_checkout()
    {
        using var appHost = CreateGitAppHost();
        var builder = CreateBuilder(appHost.Path);
        builder.ExportModule("orders", definition =>
        {
            definition.WithRepository("https://example.test/acme/orders.git");
            definition.RequiresRepository();
            definition.AddContainer("orders-api", "busybox");
        });
        builder.ImportModule("orders");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            GetRegistry(builder).ValidateRepositoryPreflightAsync(
                new InMemoryModuleRepositoryStateStore(),
                CreateInitializationSettings(),
                appHost.Path,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("orders", exception.Message, StringComparison.Ordinal);
        Assert.Contains(ModuleRepositoryPreflight.CreateInitializeCommand(appHost.Path), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void External_image_override_remains_checkout_free()
    {
        using var appHost = CreateGitAppHost();
        var builder = CreateBuilder(appHost.Path);
        builder.ConfigureModularAppHosts(options =>
        {
            options.Modules["orders"] = new DistributedApplicationModuleOptions
            {
                Containers =
                {
                    ["orders-api"] = new DistributedApplicationModuleContainerOptions
                    {
                        PublishImage = false,
                        ImageRegistry = "registry.example.test",
                        ImageName = "external/orders-api",
                        ImageTag = "2026.08"
                    }
                }
            };
        });
        builder.ExportModule("orders", definition =>
        {
            definition.WithRepository("https://example.test/acme/orders.git");
            definition.AddContainer("orders-api", "orders-api")
                .WithImagePublishCommand(new ModuleImageCommandOptions(
                    "orders-api",
                    "publisher-that-must-not-run",
                    "build"));
        });

        builder.ImportModule("orders");

        var registry = GetRegistry(builder);
        Assert.Null(registry.RepositoryPlans);
        Assert.Single(builder.Resources);
        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var image = Assert.Single(container.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal("registry.example.test", image.Registry);
        Assert.Equal("external/orders-api", image.Image);
        Assert.Equal("2026.08", image.Tag);
    }

    [Fact]
    public void External_image_override_requires_a_complete_image_identity()
    {
        using var appHost = CreateGitAppHost();
        var builder = CreateBuilder(appHost.Path);
        var exception = Assert.Throws<InvalidOperationException>(() =>
            builder.ConfigureModularAppHosts(options =>
            {
                options.Modules["orders"] = new DistributedApplicationModuleOptions
                {
                    Containers =
                    {
                        ["orders-api"] = new DistributedApplicationModuleContainerOptions
                        {
                            PublishImage = false,
                            ImageRegistry = "registry.example.test",
                            ImageTag = "2026.08"
                        }
                    }
                };
            }));

        Assert.Contains("Containers:orders-api:ImageName", exception.Message, StringComparison.Ordinal);
        Assert.Contains("PublishImage", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Local_image_publisher_declares_stable_alias_without_a_synthetic_resource()
    {
        using var appHost = CreateGitAppHost();
        var builder = CreateBuilder(appHost.Path);
        builder.ConfigureModularAppHosts(options =>
            options.GitExecutablePath = "git-must-not-run-during-materialization");
        var module = builder.ExportModule("orders", definition =>
        {
            definition.WithRepository(appHost.Path);
            definition.AddContainer("orders-api", "orders-api")
                .WithImagePublishCommand(new ModuleImageCommandOptions(
                    "orders-api",
                    ModuleImageCommandOptions.ContainerRuntimePlaceholder,
                    ModuleImageCommandOptions.ImageReferencePlaceholder));
        });

        builder.AddModule(module);

        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var image = Assert.Single(container.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal(ModuleImageBuildRecipe.LocalRunTag, image.Tag);
        var publisher = Assert.Single(container.Annotations.OfType<ModuleImagePublisherAnnotation>());
        Assert.False(publisher.TryGetPreparedImage(out _));
        Assert.Equal("orders-api:aspire-run", publisher.Recipe.LocalImageReference);
        Assert.Equal(ModuleImageCommandOptions.ContainerRuntimePlaceholder, publisher.Options.PublishCommand);
        Assert.Single(builder.Resources);
    }

    [Fact]
    public async Task Project_image_publisher_uses_the_project_directory_for_preflight_and_builds()
    {
        using var appHost = CreateGitAppHost();
        var projectDirectory = Path.Combine(appHost.Path, "Api");
        Directory.CreateDirectory(projectDirectory);
        var projectPath = Path.Combine(projectDirectory, "Api.csproj");
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var builder = CreateBuilder(appHost.Path);
        builder.ConfigureModularAppHosts(options => options.ProjectMode = ModuleProjectMode.Container);
        var module = builder.ExportModule("orders", definition =>
        {
            definition.WithRepository(appHost.Path);
            definition.AddProject("orders-api", projectPath)
                .ExportAsContainerWithCommand(new ModuleImageCommandOptions(
                    "orders-api",
                    "dotnet",
                    "publish"));
        });

        builder.AddModule(module);

        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var publisher = Assert.Single(container.Annotations.OfType<ModuleImagePublisherAnnotation>());
        Assert.Equal(projectDirectory, publisher.WorkingDirectory);
        await GetRegistry(builder).ValidateRepositoryPreflightAsync(
            new InMemoryModuleRepositoryStateStore(),
            CreateInitializationSettings(),
            appHost.Path,
            cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Containerized_project_preflight_requires_the_declared_project_file()
    {
        using var appHost = CreateGitAppHost();
        var projectDirectory = Path.Combine(appHost.Path, "Api");
        Directory.CreateDirectory(projectDirectory);
        var projectPath = Path.Combine(projectDirectory, "Missing.Api.csproj");
        var builder = CreateBuilder(appHost.Path);
        builder.ConfigureModularAppHosts(options => options.ProjectMode = ModuleProjectMode.Container);
        var module = builder.ExportModule("orders", definition =>
        {
            definition.WithRepository(appHost.Path);
            definition.AddProject("orders-api", projectPath)
                .ExportAsContainerWithCommand(new ModuleImageCommandOptions(
                    "orders-api",
                    "dotnet",
                    "publish"));
        });

        builder.AddModule(module);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            GetRegistry(builder).ValidateRepositoryPreflightAsync(
                new InMemoryModuleRepositoryStateStore(),
                CreateInitializationSettings(),
                appHost.Path,
                cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains(projectPath, exception.Message, StringComparison.Ordinal);
        Assert.Contains(ModuleRepositoryPreflight.CreateInitializeCommand(appHost.Path), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Managed_project_container_and_factory_images_win_over_resource_callbacks()
    {
        using var appHost = CreateGitAppHost();
        var projectPath = Path.Combine(appHost.Path, "Api.csproj");
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var builder = CreateBuilder(appHost.Path).UseModuleContainers();
        ModuleResourceImage? factoryImage = null;
        var module = builder.ExportModule("orders", definition =>
        {
            definition.WithRepository(appHost.Path);
            definition.AddProject("project", projectPath)
                .ExportAsContainerWithCommand(
                    Publisher("project"),
                    (_, container) => OverrideImage(container));
            definition.AddContainer("declared", "acme/declared", "candidate")
                .WithImagePublishCommand(Publisher("declared"))
                .Configure((_, container) => OverrideImage(container));
            definition.AddResource<ContainerResource>(
                "factory",
                context =>
                {
                    factoryImage = context.Image;
                    return OverrideImage(
                        context.ApplicationBuilder.AddContainer(context.ResourceName, "placeholder"));
                },
                Publisher("factory"));
        });

        builder.AddModule(module);

        Assert.Equal("registry.example.test/acme/factory:aspire-run", factoryImage?.Reference);
        var containers = builder.Resources.OfType<ContainerResource>().ToDictionary(resource => resource.Name);
        foreach (var resourceName in new[] { "project", "declared", "factory" })
        {
            var image = Assert.Single(containers[resourceName].Annotations.OfType<ContainerImageAnnotation>());
            Assert.Equal("registry.example.test", image.Registry);
            Assert.Equal($"acme/{resourceName}", image.Image);
            Assert.Equal(ModuleImageBuildRecipe.LocalRunTag, image.Tag);
            Assert.Null(image.SHA256);
        }
    }

    [Fact]
    public void Factory_external_image_override_is_checkout_free_and_applied_after_materialization()
    {
        using var appHost = CreateGitAppHost();
        var builder = CreateBuilder(appHost.Path);
        builder.ConfigureModularAppHosts(options =>
        {
            options.Modules["orders"] = new DistributedApplicationModuleOptions
            {
                Containers =
                {
                    ["factory"] = new DistributedApplicationModuleContainerOptions
                    {
                        PublishImage = false,
                        ImageRegistry = "registry.example.test",
                        ImageName = "external/factory",
                        ImageTag = "2026.08"
                    }
                }
            };
        });
        ModuleResourceImage? factoryImage = null;
        builder.ExportModule("orders", definition =>
        {
            definition.WithRepository("https://example.test/acme/orders.git");
            definition.AddResource<ContainerResource>(
                "factory",
                context =>
                {
                    factoryImage = context.Image;
                    return context.ApplicationBuilder.AddContainer(context.ResourceName, "placeholder");
                },
                Publisher("factory"));
        });

        builder.ImportModule("orders");

        Assert.Equal("registry.example.test/external/factory:2026.08", factoryImage?.Reference);
        var registry = GetRegistry(builder);
        Assert.Null(registry.RepositoryPlans);
        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var image = Assert.Single(container.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal("registry.example.test", image.Registry);
        Assert.Equal("external/factory", image.Image);
        Assert.Equal("2026.08", image.Tag);
        Assert.Empty(container.Annotations.OfType<ModuleImagePublisherAnnotation>());
    }

    [Fact]
    public void Registry_uses_one_immutable_model_shaping_options_snapshot()
    {
        using var appHost = CreateGitAppHost();
        var builder = CreateBuilder(appHost.Path);
        builder.ConfigureModularAppHosts(options => options.GitExecutablePath = "first-git");
        builder.ExportModule("orders", definition =>
            definition.AddContainer("orders-api", "busybox"));
        var registry = GetRegistry(builder);
        var registeredOptions = registry.Options;

        var exception = Assert.Throws<InvalidOperationException>(() =>
            builder.ConfigureModularAppHosts(options => options.GitExecutablePath = "second-git"));

        Assert.Same(registeredOptions, registry.Options);
        Assert.Equal("first-git", registeredOptions.GitExecutablePath);
        Assert.Contains("before defining", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("project")]
    [InlineData("declared")]
    [InlineData("factory")]
    public void Published_images_reject_external_digest_pins(string resourceKind)
    {
        using var appHost = CreateGitAppHost();
        var projectPath = Path.Combine(appHost.Path, "Api.csproj");
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var builder = CreateBuilder(appHost.Path).UseModuleContainers();
        builder.ConfigureModularAppHosts(options =>
        {
            var module = new DistributedApplicationModuleOptions();
            var image = new DistributedApplicationModuleContainerOptions
            {
                ImageSHA256 = $"sha256:{new string('a', 64)}"
            };
            if (resourceKind == "project")
            {
                module.Projects[resourceKind] = new DistributedApplicationModuleProjectOptions
                {
                    ImageSHA256 = image.ImageSHA256
                };
            }
            else
            {
                module.Containers[resourceKind] = image;
            }

            options.Modules["orders"] = module;
        });
        var module = builder.ExportModule("orders", definition =>
        {
            definition.WithRepository(appHost.Path);
            switch (resourceKind)
            {
                case "project":
                    definition.AddProject(resourceKind, projectPath)
                        .ExportAsContainerWithCommand(Publisher(resourceKind));
                    break;
                case "declared":
                    definition.AddContainer(resourceKind, $"acme/{resourceKind}", "candidate")
                        .WithImagePublishCommand(Publisher(resourceKind));
                    break;
                case "factory":
                    definition.AddResource<ContainerResource>(
                        resourceKind,
                        context => context.ApplicationBuilder.AddContainer(context.ResourceName, "placeholder"),
                        Publisher(resourceKind));
                    break;
            }
        });

        var exception = Assert.Throws<InvalidOperationException>(() => builder.AddModule(module));

        Assert.Contains(nameof(DistributedApplicationModuleImageOptions.ImageSHA256), exception.Message);
        Assert.Contains(nameof(DistributedApplicationModuleImageOptions.PublishImage), exception.Message);
        Assert.Contains("external immutable image", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Factory_publisher_requires_a_container_image_annotation()
    {
        using var appHost = CreateGitAppHost();
        var builder = CreateBuilder(appHost.Path);
        var module = builder.ExportModule("orders", definition =>
        {
            definition.WithRepository(appHost.Path);
            definition.AddResource<ContainerResource>(
                "factory",
                context => context.ApplicationBuilder.AddResource(new ContainerResource(context.ResourceName)),
                Publisher("factory"));
        });

        var exception = Assert.Throws<InvalidOperationException>(() => builder.AddModule(module));

        Assert.Contains("created a container without an image", exception.Message, StringComparison.Ordinal);
        Assert.Contains("context.Image", exception.Message, StringComparison.Ordinal);
    }

    private static ModuleImageCommandOptions Publisher(string resource) =>
        new($"acme/{resource}", "publisher", "build")
        {
            ImageRegistry = "registry.example.test",
            ImageTag = "candidate"
        };

    private static IResourceBuilder<ContainerResource> OverrideImage(
        IResourceBuilder<ContainerResource> container) =>
        container
            .WithImage("callback-override")
            .WithImageRegistry("wrong.example.test")
            .WithImageTag("wrong")
            .WithImageSHA256(new string('f', 64));

    private static TemporaryDirectory CreateGitAppHost()
    {
        var appHost = TemporaryDirectory.Create();
        Directory.CreateDirectory(Path.Combine(appHost.Path, ".git"));
        return appHost;
    }

    private static IDistributedApplicationBuilder CreateBuilder(string projectDirectory) =>
        DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = [],
            AssemblyName = typeof(SynchronousModuleMaterializationTests).Assembly.FullName,
            ProjectDirectory = projectDirectory,
            DisableDashboard = true
        });

    private static ModuleRepositoryInitializationSettings CreateInitializationSettings() =>
        new("git", "gh", TimeSpan.FromMinutes(2));

    private static ModuleApplicationRegistry GetRegistry(IDistributedApplicationBuilder builder) =>
        Assert.IsType<ModuleApplicationRegistry>(builder.Services
            .Last(descriptor => descriptor.ServiceType == typeof(IDistributedApplicationModuleCatalog))
            .ImplementationInstance);
}
