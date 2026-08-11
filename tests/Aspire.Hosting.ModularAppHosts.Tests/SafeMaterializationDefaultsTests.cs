using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting;
using Xunit;

namespace Aspire.Hosting.ModularAppHosts.Tests;

public sealed class SafeMaterializationDefaultsTests
{
    [Fact]
    public void Defaults_update_imported_repositories_without_executing_image_builds()
    {
        var options = new ModularAppHostsOptions();

        Assert.Equal(ModuleProjectMode.Auto, options.ProjectMode);
        Assert.True(options.UpdateImportedRepositories);
        Assert.False(options.PublishImages);
        Assert.Equal("git", options.GitExecutablePath);
        Assert.Equal("gh", options.GitHubCliPath);
        Assert.Equal(TimeSpan.FromMinutes(2), options.RepositoryCommandTimeout);
    }

    [Fact]
    public async Task Auto_mode_runs_local_projects_directly_without_an_installer()
    {
        using var source = TemporaryDirectory.Create();
        var projectPath = CreateProject(source.Path);
        var builder = CreateBuilder(source.Path);
        var module = await ExportProjectAsync(builder, projectPath);

        await builder.AddAsync(module);

        Assert.Single(builder.Resources.OfType<ProjectResource>());
        Assert.Empty(builder.Resources.OfType<ContainerResource>());
        Assert.Empty(builder.Resources.OfType<ModuleRepositoryInstallerResource>());
    }

    [Fact]
    public async Task Auto_mode_runs_imported_projects_as_containers_without_building_images()
    {
        using var source = TemporaryDirectory.Create();
        using var appHost = TemporaryDirectory.Create();
        var projectPath = CreateProject(source.Path);
        var builder = CreateBuilder(appHost.Path);
        var module = await builder.ExportModuleAsync("orders", definition =>
        {
            definition.WithRepository(source.Path);
            definition.AddProject("orders-api", projectPath)
                .ExportAsContainer("example/orders-api", "dotnet", ["publish"]);
        });

        await builder.ImportModuleAsync(module.Name);

        Assert.Single(builder.Resources.OfType<ContainerResource>());
        Assert.Empty(builder.Resources.OfType<ProjectResource>());
        Assert.Empty(builder.Resources.OfType<ModuleRepositoryInstallerResource>());
    }

    [Fact]
    public async Task Fluent_opt_ins_select_containers_and_image_builds()
    {
        using var source = TemporaryDirectory.Create();
        var projectPath = CreateProject(source.Path);
        var builder = CreateBuilder(source.Path)
            .UseModuleContainers()
            .BuildModuleImages();
        var module = await ExportProjectAsync(builder, projectPath);

        await builder.AddAsync(module);

        Assert.Single(builder.Resources.OfType<ContainerResource>());
        Assert.Single(builder.Resources.OfType<ModuleRepositoryInstallerResource>());
    }

    [Fact]
    public void Cli_and_mode_options_bind_from_configuration()
    {
        using var source = TemporaryDirectory.Create();
        var builder = CreateBuilder(source.Path);
        var section = ModularAppHostsOptions.ConfigurationSectionName;
        builder.Configuration[$"{section}:GitExecutablePath"] = "custom-git";
        builder.Configuration[$"{section}:RepositoryCommandTimeout"] = "00:00:15";
        builder.Configuration[$"{section}:ProjectMode"] = nameof(ModuleProjectMode.Project);
        builder.Configuration[$"{section}:Modules:orders:ProjectMode"] = nameof(ModuleProjectMode.Container);
        builder.Configuration[$"{section}:Modules:orders:Projects:orders-api:ProjectMode"] =
            nameof(ModuleProjectMode.Project);

        var options = ModularAppHostsOptions.FromConfiguration(builder.Configuration);

        Assert.Equal("custom-git", options.GitExecutablePath);
        Assert.Equal(TimeSpan.FromSeconds(15), options.RepositoryCommandTimeout);
        Assert.Equal(ModuleProjectMode.Project, options.ProjectMode);
        Assert.Equal(ModuleProjectMode.Container, options.Modules["orders"].ProjectMode);
        Assert.Equal(
            ModuleProjectMode.Project,
            options.Modules["orders"].Projects["orders-api"].ProjectMode);
    }

    [Fact]
    public async Task Non_positive_repository_timeout_is_rejected_from_configuration_or_code()
    {
        using var source = TemporaryDirectory.Create();
        var section = ModularAppHostsOptions.ConfigurationSectionName;
        var configuredBuilder = CreateBuilder(source.Path);
        configuredBuilder.Configuration[$"{section}:RepositoryCommandTimeout"] = "00:00:00";

        var configuredException = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            configuredBuilder.ExportModuleAsync("cache", module =>
                module.AddContainer("redis", "redis")));
        Assert.Contains(nameof(ModularAppHostsOptions.RepositoryCommandTimeout), configuredException.Message);

        var programmaticBuilder = CreateBuilder(source.Path);
        var programmaticException = Assert.Throws<InvalidOperationException>(() =>
            programmaticBuilder.ConfigureModularAppHosts(options =>
                options.RepositoryCommandTimeout = TimeSpan.Zero));
        Assert.Contains(nameof(ModularAppHostsOptions.RepositoryCommandTimeout), programmaticException.Message);
    }

    private static IDistributedApplicationBuilder CreateBuilder(string projectDirectory)
    {
        return DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = [],
            DisableDashboard = true,
            ProjectDirectory = projectDirectory
        });
    }

    private static async Task<IDistributedApplicationModule> ExportProjectAsync(
        IDistributedApplicationBuilder builder,
        string projectPath)
    {
        return await builder.ExportModuleAsync("orders", definition =>
            definition.AddProject("orders-api", projectPath)
                .ExportAsContainer(
                    $"module-defaults-{Guid.NewGuid():N}",
                    "dotnet",
                    ["publish"]));
    }

    private static string CreateProject(string directory)
    {
        var projectPath = Path.Combine(directory, "Orders.Api.csproj");
        File.WriteAllText(
            projectPath,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        return projectPath;
    }

}
