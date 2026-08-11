using Aspire.Hosting.ApplicationModel;
using Xunit;

namespace Aspire.Hosting.ModularAppHosts.Tests;

public sealed class SynchronousModuleMaterializationTests
{
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
    }

    [Fact]
    public void Normal_run_preflight_reports_the_initialize_command_for_planned_checkout()
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

        var exception = Assert.Throws<InvalidOperationException>(() =>
            GetRegistry(builder).ValidateRepositoryPreflight());

        Assert.Contains("orders", exception.Message, StringComparison.Ordinal);
        Assert.Contains(ModuleRepositoryPreflight.InitializeCommand, exception.Message, StringComparison.Ordinal);
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
                .WithImagePublishCommand(new ModuleContainerExportOptions(
                    "orders-api",
                    "publisher-that-must-not-run",
                    "build"));
        });

        builder.ImportModule("orders");

        var registry = GetRegistry(builder);
        Assert.Null(registry.RepositoryPlans);
        Assert.Empty(builder.Resources.OfType<ModuleRepositoryInstallerResource>());
        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var image = Assert.Single(container.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal("registry.example.test", image.Registry);
        Assert.Equal("external/orders-api", image.Image);
        Assert.Equal("2026.08", image.Tag);
    }

    [Fact]
    public void Local_image_publisher_declares_stable_alias_and_deferred_installer()
    {
        using var appHost = CreateGitAppHost();
        var builder = CreateBuilder(appHost.Path);
        builder.ConfigureModularAppHosts(options =>
            options.GitExecutablePath = "git-must-not-run-during-materialization");
        var module = builder.ExportModule("orders", definition =>
        {
            definition.WithRepository(appHost.Path);
            definition.AddContainer("orders-api", "orders-api")
                .WithImagePublishCommand(new ModuleContainerExportOptions(
                    "orders-api",
                    ModuleContainerExportOptions.ContainerRuntimePlaceholder,
                    ModuleContainerExportOptions.ImageReferencePlaceholder));
        });

        builder.AddModule(module);

        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var image = Assert.Single(container.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal(ModuleImageBuildRecipe.LocalRunTag, image.Tag);
        var publisher = Assert.Single(container.Annotations.OfType<ModuleImagePublisherAnnotation>());
        Assert.False(publisher.TryGetPreparedImage(out _));
        var installer = Assert.Single(builder.Resources.OfType<ModuleRepositoryInstallerResource>());
        Assert.Equal("orders-api:aspire-run", installer.ImageReference);
        Assert.Equal(ModuleContainerExportOptions.ContainerRuntimePlaceholder, installer.PublishCommand);
        Assert.NotNull(installer.Publisher);
    }

    [Fact]
    public void Project_image_publisher_uses_the_project_directory_for_preflight_and_builds()
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
                .ExportAsContainer(new ModuleContainerExportOptions(
                    "orders-api",
                    "dotnet",
                    "publish"));
        });

        builder.AddModule(module);

        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var publisher = Assert.Single(container.Annotations.OfType<ModuleImagePublisherAnnotation>());
        Assert.Equal(projectDirectory, publisher.WorkingDirectory);
        GetRegistry(builder).ValidateRepositoryPreflight();
    }

    [Fact]
    public void Containerized_project_preflight_requires_the_declared_project_file()
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
                .ExportAsContainer(new ModuleContainerExportOptions(
                    "orders-api",
                    "dotnet",
                    "publish"));
        });

        builder.AddModule(module);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            GetRegistry(builder).ValidateRepositoryPreflight());
        Assert.Contains(projectPath, exception.Message, StringComparison.Ordinal);
        Assert.Contains(ModuleRepositoryPreflight.InitializeCommand, exception.Message, StringComparison.Ordinal);
    }

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

    private static ModuleApplicationRegistry GetRegistry(IDistributedApplicationBuilder builder) =>
        Assert.IsType<ModuleApplicationRegistry>(builder.Services
            .Last(descriptor => descriptor.ServiceType == typeof(IDistributedApplicationModuleCatalog))
            .ImplementationInstance);
}
