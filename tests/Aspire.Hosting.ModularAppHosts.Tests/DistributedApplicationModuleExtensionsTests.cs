using System.Diagnostics;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ModularAppHosts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Hosting.ModularAppHosts.Tests;

public sealed class DistributedApplicationModuleExtensionsTests
{
    [Fact]
    public void ExportModule_registers_definition_without_materializing_resources()
    {
        using var repository = TestRepository.Create();
        var builder = CreateBuilder(repository.Path);

        var module = ExportModule(builder, repository.ProjectPath);

        Assert.Equal("orders", module.Name);
        Assert.Single(module.Projects);
        Assert.Empty(builder.Resources);
        Assert.Single(builder.Services, descriptor =>
            descriptor.ServiceType == typeof(IDistributedApplicationModuleCatalog));
    }

    [Fact]
    public void ExportModule_is_idempotent_by_name()
    {
        using var repository = TestRepository.Create();
        var builder = CreateBuilder(repository.Path);
        var callbackCount = 0;

        var first = builder.ExportModule("orders", module =>
        {
            callbackCount++;
            module.AddProject("orders-api", repository.ProjectPath)
                .ExportAsContainer("orders-api", "dotnet", ["publish", "--os", "linux"]);
        });
        var second = builder.ExportModule("orders", _ => callbackCount++);

        Assert.Same(first, second);
        Assert.Equal(1, callbackCount);
    }

    [Fact]
    public void Add_materializes_container_and_exact_publish_installer_once()
    {
        using var repository = TestRepository.Create();
        var builder = CreateBuilder(repository.Path);
        var module = ExportModule(builder, repository.ProjectPath);

        builder.Add(module);
        builder.Add(module);

        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var installer = Assert.Single(builder.Resources.OfType<ModuleRepositoryInstallerResource>());
        var image = Assert.Single(container.Annotations.OfType<ContainerImageAnnotation>());
        var wait = Assert.Single(container.Annotations.OfType<WaitAnnotation>());
        var endpoint = Assert.Single(container.Annotations.OfType<EndpointAnnotation>());

        Assert.Equal("orders-api", image.Image);
        Assert.Equal("dev", image.Tag);
        Assert.Equal("dotnet", installer.PublishCommand);
        Assert.Equal(["publish", "Orders.Api.csproj", "-t:PublishContainer"], installer.PublishArguments);
        Assert.False(installer.UpdatesRepository);
        Assert.Same(installer, wait.Resource);
        Assert.Equal(WaitType.WaitForCompletion, wait.WaitType);
        Assert.Equal(8080, endpoint.TargetPort);
    }

    [Fact]
    public void ImportModule_uses_repository_base_parameter_and_marks_installer_for_updates()
    {
        using var repository = TestRepository.Create();
        using var imports = TemporaryDirectory.Create();
        var builder = CreateBuilder(repository.Path);
        builder.Configuration[$"Parameters:{DistributedApplicationModuleExtensions.RepositoryBaseLocationParameterName}"] = imports.Path;

        ExportModule(builder, repository.ProjectPath);
        var imported = builder.ImportModule("orders");
        builder.ImportModule("orders");

        Assert.Equal("orders", imported.Name);
        Assert.Single(builder.Resources.OfType<ParameterResource>());

        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var annotation = Assert.Single(container.Annotations.OfType<DistributedApplicationModuleResourceAnnotation>());
        var installer = Assert.Single(builder.Resources.OfType<ModuleRepositoryInstallerResource>());

        Assert.True(annotation.Imported);
        Assert.Equal(Path.Combine(imports.Path, "orders"), annotation.RepositoryPath);
        Assert.True(installer.UpdatesRepository);
        Assert.Equal("https://example.test/acme/orders.git", installer.Repository);
    }

    [Fact]
    public void ImportModule_requires_an_exported_definition()
    {
        using var repository = TestRepository.Create();
        var builder = CreateBuilder(repository.Path);

        var exception = Assert.Throws<InvalidOperationException>(() => builder.ImportModule("missing"));

        Assert.Contains("has not been exported", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Module_exports_existing_container_and_exposes_materialized_resource_builders()
    {
        using var repository = TestRepository.Create();
        var builder = CreateBuilder(repository.Path);

        var module = builder.ExportModule("mixed", definition =>
        {
            definition.AddProject("mixed-api", repository.ProjectPath)
                .ExportAsContainer("mixed-api", "dotnet", ["publish"]);
            definition.AddContainer("mixed-static", "nginx", "alpine")
                .Configure(container => container.WithHttpEndpoint(targetPort: 80, name: "http"));
        });

        builder.Add(module);

        Assert.Single(module.Containers);
        Assert.Equal(2, builder.Resources.OfType<ContainerResource>().Count());
        Assert.Equal("mixed-api", module.GetResource<ContainerResource>("mixed-api").Resource.Name);

        var staticContainer = module.GetResource<ContainerResource>("mixed-static");
        Assert.Equal("mixed-static", staticContainer.Resource.Name);
        Assert.Single(staticContainer.Resource.Annotations.OfType<EndpointAnnotation>());
    }

    [Fact]
    public void Imported_existing_non_git_source_does_not_attempt_clone_over_it()
    {
        using var repository = TestRepository.Create();

        var command = RepositorySynchronizer.CreateCommand(
            repository.Path,
            repository.Path,
            updateRepository: true);

        Assert.Null(command);
    }

    [Fact]
    public void ImportModule_preserves_project_directory_relative_to_configured_local_repository()
    {
        using var directory = TemporaryDirectory.Create();
        var sourceRoot = Path.Combine(directory.Path, "AppHostA");
        var projectDirectory = Path.Combine(sourceRoot, "Api");
        var consumerDirectory = Path.Combine(directory.Path, "AppHostB");
        Directory.CreateDirectory(projectDirectory);
        Directory.CreateDirectory(consumerDirectory);
        var projectPath = Path.Combine(projectDirectory, "Api.csproj");
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        var builder = CreateBuilder(consumerDirectory);
        builder.Configuration[$"Parameters:{DistributedApplicationModuleExtensions.RepositoryBaseLocationParameterName}"] = directory.Path;
        builder.ExportModule("AppHostA", module =>
        {
            module.WithRepository(sourceRoot);
            module.AddProject("api", projectPath)
                .ExportAsContainer("api", "podman", ["build", "."]);
        });

        builder.ImportModule("AppHostA");

        var installer = Assert.Single(builder.Resources.OfType<ModuleRepositoryInstallerResource>());
        Assert.Equal(projectDirectory, installer.WorkingDirectory);
    }

    [Fact]
    public void Repository_synchronizer_clones_missing_import_and_skips_dirty_worktree()
    {
        using var imports = TemporaryDirectory.Create();
        var clonePath = Path.Combine(imports.Path, "orders");

        var clone = RepositorySynchronizer.CreateCommand(
            clonePath,
            "https://example.test/acme/orders.git",
            updateRepository: true);

        Assert.NotNull(clone);
        Assert.Equal("git", clone.Executable);
        Assert.Equal(
            ["clone", "--recurse-submodules", "--", "https://example.test/acme/orders.git", clonePath],
            clone.Arguments);

        using var repository = TestRepository.Create(initializeGit: true);
        File.AppendAllText(repository.ProjectPath, Environment.NewLine + "<!-- dirty -->");

        var dirty = RepositorySynchronizer.CreateCommand(
            repository.Path,
            "https://example.test/acme/orders.git",
            updateRepository: true);

        Assert.Null(dirty);
        Assert.True(RepositoryInspector.IsDirty(repository.Path));
    }

    private static IDistributedApplicationModule ExportModule(
        IDistributedApplicationBuilder builder,
        string projectPath)
    {
        return builder.ExportModule("orders", module =>
        {
            module.WithRepository("https://example.test/acme/orders.git");
            module.AddProject("orders-api", projectPath)
                .ExportAsContainer(
                    new ModuleContainerExportOptions(
                        "orders-api",
                        "dotnet",
                        "publish",
                        "Orders.Api.csproj",
                        "-t:PublishContainer")
                    {
                        ImageTag = "dev"
                    },
                    container => container.WithHttpEndpoint(targetPort: 8080));
        });
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

    private sealed class TestRepository : IDisposable
    {
        private readonly TemporaryDirectory _directory;

        private TestRepository(TemporaryDirectory directory, string projectPath)
        {
            _directory = directory;
            ProjectPath = projectPath;
        }

        public string Path => _directory.Path;

        public string ProjectPath { get; }

        public static TestRepository Create(bool initializeGit = false)
        {
            var directory = TemporaryDirectory.Create();
            var projectPath = System.IO.Path.Combine(directory.Path, "Orders.Api.csproj");
            File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");

            if (initializeGit)
            {
                RunGit(directory.Path, "init");
                RunGit(directory.Path, "config", "user.name", "Test User");
                RunGit(directory.Path, "config", "user.email", "test@example.test");
                RunGit(directory.Path, "add", ".");
                RunGit(directory.Path, "commit", "-m", "initial");
            }

            return new TestRepository(directory, projectPath);
        }

        public void Dispose() => _directory.Dispose();

        private static void RunGit(string workingDirectory, params string[] arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Unable to start git.");
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(process.StandardError.ReadToEnd());
            }
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"aspire-modules-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
