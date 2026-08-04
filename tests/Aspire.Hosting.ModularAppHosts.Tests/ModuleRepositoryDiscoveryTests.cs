using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ModularAppHosts;
using CliWrap;
using CliWrap.Buffered;
using Xunit;
using CliCommand = global::CliWrap.Cli;

namespace Aspire.Hosting.ModularAppHosts.Tests;

public sealed class ModuleRepositoryDiscoveryTests
{
    [Theory]
    [InlineData("https://github.com/acme/orders.git", "orders")]
    [InlineData("git@github.com:acme/orders.git", "orders")]
    [InlineData("acme/orders", "orders")]
    [InlineData("ssh://git@github.com/acme/orders", "orders")]
    public void Repository_directory_name_is_inferred_from_GitHub_specifiers(
        string repository,
        string expected)
    {
        Assert.Equal(expected, GitHubRepositoryCloner.GetRepositoryDirectoryName(repository));
    }

    [Theory]
    [InlineData("https://github.com/acme/orders.git", "git@github.com:acme/orders.git")]
    [InlineData("ssh://git@github.com/acme/orders", "acme/orders")]
    public void Equivalent_GitHub_repository_specifiers_are_detected(string first, string second)
    {
        Assert.True(GitHubRepositoryCloner.RefersToSameRepository(first, second, Path.GetTempPath()));
    }

    [Fact]
    public void GitHub_clone_command_forwards_submodule_checkout_to_gh()
    {
        var target = Path.Combine(Path.GetTempPath(), "modules", "orders");

        var command = GitHubRepositoryCloner.CreateCommand(
            "custom-gh",
            "acme/orders",
            target);

        Assert.Equal("custom-gh", command.Executable);
        Assert.Equal(
            ["repo", "clone", "acme/orders", target, "--", "--recurse-submodules"],
            command.Arguments);
        Assert.Equal(Path.GetDirectoryName(target), command.WorkingDirectory);
    }

    [Fact]
    public void Auto_clone_defaults_off_and_binds_global_and_module_configuration()
    {
        using var workspace = TemporaryDirectory.Create();
        var builder = CreateBuilder(workspace.Path);

        var defaults = ModularAppHostsOptions.FromConfiguration(builder.Configuration);

        Assert.False(defaults.AutoCloneRepositories);
        Assert.Equal("gh", defaults.GitHubCliPath);

        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:AutoCloneRepositories"] = "true";
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:GitHubCliPath"] = "configured-gh";
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:Modules:orders:AutoCloneRepository"] = "false";
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:Modules:orders:RepositoryRevision"] = "v2.1.0";

        var configured = ModularAppHostsOptions.FromConfiguration(builder.Configuration);

        Assert.True(configured.AutoCloneRepositories);
        Assert.Equal("configured-gh", configured.GitHubCliPath);
        Assert.False(configured.Modules["orders"].AutoCloneRepository);
        Assert.Equal("v2.1.0", configured.Modules["orders"].RepositoryRevision);
    }

    [Fact]
    public void Same_git_repository_preserves_module_root_and_never_invokes_gh()
    {
        using var workspace = TemporaryDirectory.Create();
        var appHostDirectory = Path.Combine(workspace.Path, "apphost");
        var moduleRoot = Path.Combine(workspace.Path, "modules", "orders");
        var projectPath = Path.Combine(moduleRoot, "Orders.Api.csproj");
        Directory.CreateDirectory(appHostDirectory);
        Directory.CreateDirectory(moduleRoot);
        File.WriteAllText(projectPath, ProjectContents);
        InitializeGit(workspace.Path, "feature/same-repository");

        var builder = CreateBuilder(appHostDirectory);
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:AutoCloneRepositories"] = "true";
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:GitHubCliPath"] =
            Path.Combine(workspace.Path, "missing-gh");
        var module = builder.ExportModule("orders", definition =>
        {
            definition.WithRepository(moduleRoot);
            definition.AddProject("orders-api", projectPath)
                .ExportAsContainer("orders-api", "dotnet", ["publish"]);
        });

        builder.Add(module);

        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var annotation = Assert.Single(
            container.Annotations.OfType<DistributedApplicationModuleResourceAnnotation>());
        var image = Assert.Single(container.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal(moduleRoot, annotation.RepositoryPath);
        Assert.Equal(ExpectedTag(workspace.Path, "feature/same-repository"), image.Tag);
    }

    [Fact]
    public void Same_git_repository_dirty_state_is_applied_to_branch_tag()
    {
        using var workspace = TemporaryDirectory.Create();
        var appHostDirectory = Path.Combine(workspace.Path, "apphost");
        var projectPath = Path.Combine(workspace.Path, "Orders.Api.csproj");
        Directory.CreateDirectory(appHostDirectory);
        File.WriteAllText(projectPath, ProjectContents);
        InitializeGit(workspace.Path, "feature/dirty-image");
        File.AppendAllText(projectPath, Environment.NewLine + "<!-- dirty -->");

        var builder = CreateBuilder(appHostDirectory, publishMode: false);
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:AutoCloneRepositories"] = "true";
        var module = builder.ExportModule("orders", definition =>
        {
            definition.WithRepository(workspace.Path);
            definition.AddProject("orders-api", projectPath)
                .ExportAsContainer("orders-api", "dotnet", ["publish"]);
        });

        builder.Add(module);

        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var image = Assert.Single(container.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal($"{ExpectedTag(workspace.Path, "feature/dirty-image")}-dirty", image.Tag);
        var installer = Assert.Single(builder.Resources.OfType<ModuleRepositoryInstallerResource>());
        Assert.True(installer.RepositoryDirty);
    }

    [Fact]
    public void Same_repository_remote_is_discovered_without_cloning()
    {
        using var repository = TemporaryDirectory.Create();
        var appHostDirectory = Path.Combine(repository.Path, "AppHost");
        Directory.CreateDirectory(appHostDirectory);
        File.WriteAllText(Path.Combine(repository.Path, "README.md"), "consumer");
        InitializeGit(repository.Path, "main");
        RunGit(
            repository.Path,
            "remote",
            "add",
            "origin",
            "git@github.com:acme/consumer.git");

        var builder = CreateBuilder(appHostDirectory);
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:AutoCloneRepositories"] = "true";
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:GitHubCliPath"] =
            Path.Combine(repository.Path, "missing-gh");
        var module = builder.ExportModule("consumer", definition =>
        {
            definition.WithRepository("https://github.com/acme/consumer.git");
            definition.AddContainer("consumer-cache", "redis", "alpine");
        });

        builder.Add(module);

        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var annotation = Assert.Single(
            container.Annotations.OfType<DistributedApplicationModuleResourceAnnotation>());
        Assert.Equal(repository.Path, annotation.RepositoryPath);
    }

    [Fact]
    public void Repository_independent_module_ignores_global_auto_clone()
    {
        using var repository = TemporaryDirectory.Create();
        var appHostDirectory = Path.Combine(repository.Path, "AppHost");
        Directory.CreateDirectory(appHostDirectory);
        File.WriteAllText(Path.Combine(repository.Path, "README.md"), "consumer");
        InitializeGit(repository.Path, "main");

        var builder = CreateBuilder(appHostDirectory);
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:AutoCloneRepositories"] = "true";
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:GitHubCliPath"] =
            Path.Combine(repository.Path, "missing-gh");
        var module = builder.ExportModule("portable", definition =>
            definition.AddContainer("portable-cache", "redis", "alpine"));

        builder.ImportModule("portable");

        Assert.Empty(builder.Resources.OfType<ParameterResource>());
        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var annotation = Assert.Single(
            container.Annotations.OfType<DistributedApplicationModuleResourceAnnotation>());
        Assert.Equal(appHostDirectory, annotation.RepositoryPath);
    }

    [Fact]
    public void Imported_same_worktree_without_remote_does_not_request_repository_parameter()
    {
        using var repository = TemporaryDirectory.Create();
        var appHostDirectory = Path.Combine(repository.Path, "AppHost");
        var projectPath = Path.Combine(repository.Path, "Orders.Api.csproj");
        Directory.CreateDirectory(appHostDirectory);
        File.WriteAllText(projectPath, ProjectContents);
        InitializeGit(repository.Path, "feature/local-import");

        var builder = CreateBuilder(appHostDirectory);
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:AutoCloneRepositories"] = "true";
        builder.ExportModule("orders", definition =>
            definition.AddProject("orders-api", projectPath)
                .ExportAsContainer("orders-api", "dotnet", ["publish"]));

        builder.ImportModule("orders");

        Assert.Empty(builder.Resources.OfType<ParameterResource>());
        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var annotation = Assert.Single(
            container.Annotations.OfType<DistributedApplicationModuleResourceAnnotation>());
        Assert.True(annotation.Imported);
        Assert.Equal(repository.Path, annotation.RepositoryPath);
    }

    [Fact]
    public void Existing_sibling_repository_is_discovered_without_invoking_gh()
    {
        using var parent = TemporaryDirectory.Create();
        var appHostRoot = Path.Combine(parent.Path, "consumer");
        var appHostDirectory = Path.Combine(appHostRoot, "src", "AppHost");
        var moduleRoot = Path.Combine(parent.Path, "orders");
        var projectPath = Path.Combine(moduleRoot, "src", "Orders.Api", "Orders.Api.csproj");
        Directory.CreateDirectory(appHostDirectory);
        File.WriteAllText(Path.Combine(appHostRoot, "README.md"), "consumer");
        InitializeGit(appHostRoot, "consumer-branch");
        Directory.CreateDirectory(Path.GetDirectoryName(projectPath)!);
        File.WriteAllText(projectPath, ProjectContents);
        InitializeGit(moduleRoot, "feature/orders-service");
        RunGit(moduleRoot, "remote", "add", "origin", "https://github.com/acme/orders.git");

        var builder = CreateBuilder(appHostDirectory);
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:AutoCloneRepositories"] = "true";
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:GitHubCliPath"] =
            Path.Combine(parent.Path, "missing-gh");
        var module = ExportRemoteProject(builder, projectPath);

        builder.Add(module);

        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var annotation = Assert.Single(
            container.Annotations.OfType<DistributedApplicationModuleResourceAnnotation>());
        var image = Assert.Single(container.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal(moduleRoot, annotation.RepositoryPath);
        Assert.Equal(ExpectedTag(moduleRoot, "feature/orders-service"), image.Tag);
    }

    [Fact]
    public void Missing_sibling_repository_is_cloned_through_configured_gh_process()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var parent = TemporaryDirectory.Create();
        using var source = TemporaryDirectory.Create();
        var sourceProject = Path.Combine(source.Path, "src", "Orders.Api", "Orders.Api.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceProject)!);
        File.WriteAllText(sourceProject, ProjectContents);
        InitializeGit(source.Path, "feature/cloned-service");
        RunGit(source.Path, "remote", "add", "origin", "https://github.com/acme/orders.git");

        var appHostRoot = Path.Combine(parent.Path, "consumer");
        var appHostDirectory = Path.Combine(appHostRoot, "src", "AppHost");
        Directory.CreateDirectory(appHostDirectory);
        File.WriteAllText(Path.Combine(appHostRoot, "README.md"), "consumer");
        InitializeGit(appHostRoot, "consumer-branch");

        var siblingProject = Path.Combine(
            parent.Path,
            "orders",
            "src",
            "Orders.Api",
            "Orders.Api.csproj");
        var fakeGh = CreateFakeGh(parent.Path, source.Path);
        var builder = CreateBuilder(appHostDirectory);
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:AutoCloneRepositories"] = "true";
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:GitHubCliPath"] = fakeGh;
        var module = ExportRemoteProject(builder, siblingProject);

        builder.Add(module);

        var clonePath = Path.Combine(parent.Path, "orders");
        Assert.True(RepositoryInspector.IsGitRepository(clonePath));
        Assert.True(File.Exists(siblingProject));
        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var annotation = Assert.Single(
            container.Annotations.OfType<DistributedApplicationModuleResourceAnnotation>());
        var image = Assert.Single(container.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal(clonePath, annotation.RepositoryPath);
        Assert.Equal(ExpectedTag(clonePath, "feature/cloned-service"), image.Tag);
    }

    [Fact]
    public void Missing_gh_has_an_actionable_error_only_when_auto_clone_is_enabled()
    {
        using var parent = TemporaryDirectory.Create();
        var appHostRoot = Path.Combine(parent.Path, "consumer");
        var appHostDirectory = Path.Combine(appHostRoot, "AppHost");
        Directory.CreateDirectory(appHostDirectory);
        File.WriteAllText(Path.Combine(appHostRoot, "README.md"), "consumer");
        InitializeGit(appHostRoot, "main");
        var siblingProject = Path.Combine(parent.Path, "orders", "Orders.Api.csproj");

        var builder = CreateBuilder(appHostDirectory);
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:AutoCloneRepositories"] = "true";
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:GitHubCliPath"] =
            Path.Combine(parent.Path, "missing-gh");
        var module = ExportRemoteProject(builder, siblingProject);

        var exception = Assert.Throws<InvalidOperationException>(() => builder.Add(module));

        Assert.Contains("requires the GitHub CLI", exception.Message, StringComparison.Ordinal);
        Assert.Contains("AutoCloneRepositories", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GitHub_cli_clone_failure_preserves_exit_code_and_diagnostic()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = TemporaryDirectory.Create();
        var fakeGh = Path.Combine(workspace.Path, "failing-gh");
        File.WriteAllText(
            fakeGh,
            $"#!/bin/sh{Environment.NewLine}echo 'authentication failed' >&2{Environment.NewLine}exit 23{Environment.NewLine}");
        File.SetUnixFileMode(
            fakeGh,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            GitHubRepositoryCloner.Clone(
                fakeGh,
                "acme/orders",
                Path.Combine(workspace.Path, "orders")));

        Assert.Contains("exit code 23", exception.Message, StringComparison.Ordinal);
        Assert.Contains("authentication failed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Disabled_auto_clone_reports_missing_service_without_invoking_gh()
    {
        using var parent = TemporaryDirectory.Create();
        var appHostRoot = Path.Combine(parent.Path, "consumer");
        var appHostDirectory = Path.Combine(appHostRoot, "AppHost");
        Directory.CreateDirectory(appHostDirectory);
        File.WriteAllText(Path.Combine(appHostRoot, "README.md"), "consumer");
        InitializeGit(appHostRoot, "consumer-branch");
        var siblingProject = Path.Combine(parent.Path, "orders", "Orders.Api.csproj");

        var builder = CreateBuilder(appHostDirectory);
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:GitHubCliPath"] =
            Path.Combine(parent.Path, "missing-gh");
        var module = ExportRemoteProject(builder, siblingProject);

        var exception = Assert.Throws<InvalidOperationException>(() => builder.Add(module));

        Assert.False(Directory.Exists(Path.Combine(parent.Path, "orders")));
        Assert.Contains("project service 'orders-api'", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("GitHub CLI", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Published_container_without_explicit_tag_uses_sanitized_branch()
    {
        using var repository = TemporaryDirectory.Create();
        var appHostDirectory = Path.Combine(repository.Path, "AppHost");
        Directory.CreateDirectory(appHostDirectory);
        File.WriteAllText(Path.Combine(repository.Path, "README.md"), "module");
        InitializeGit(repository.Path, "feature/container-publisher");

        var builder = CreateBuilder(appHostDirectory);
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:AutoCloneRepositories"] = "true";
        var module = builder.ExportModule("static", definition =>
        {
            definition.WithRepository(repository.Path);
            definition.AddContainer("static-site", "static-site", "dev")
                .WithImagePublishCommand(new ModuleContainerExportOptions(
                    "static-site",
                    "podman",
                    "build",
                    ModuleContainerExportOptions.ImageReferencePlaceholder));
        });

        builder.Add(module);

        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var image = Assert.Single(container.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal(ExpectedTag(repository.Path, "feature/container-publisher"), image.Tag);
    }

    [Fact]
    public void Existing_non_git_sibling_breaks_with_discovery_diagnostic()
    {
        using var parent = TemporaryDirectory.Create();
        var appHostRoot = Path.Combine(parent.Path, "consumer");
        var appHostDirectory = Path.Combine(appHostRoot, "AppHost");
        var siblingProject = Path.Combine(parent.Path, "orders", "Orders.Api.csproj");
        Directory.CreateDirectory(appHostDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(siblingProject)!);
        File.WriteAllText(Path.Combine(appHostRoot, "README.md"), "consumer");
        File.WriteAllText(siblingProject, ProjectContents);
        InitializeGit(appHostRoot, "main");

        var builder = CreateBuilder(appHostDirectory);
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:AutoCloneRepositories"] = "true";
        var module = ExportRemoteProject(builder, siblingProject);

        var exception = Assert.Throws<InvalidOperationException>(() => builder.Add(module));

        Assert.Contains("orders", exception.Message, StringComparison.Ordinal);
        Assert.Contains("not a Git repository", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_service_project_after_clone_breaks_with_module_and_service_names()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var parent = TemporaryDirectory.Create();
        using var source = TemporaryDirectory.Create();
        File.WriteAllText(Path.Combine(source.Path, "README.md"), "no project");
        InitializeGit(source.Path, "main");
        RunGit(source.Path, "remote", "add", "origin", "https://github.com/acme/orders.git");
        var appHostRoot = Path.Combine(parent.Path, "consumer");
        var appHostDirectory = Path.Combine(appHostRoot, "AppHost");
        Directory.CreateDirectory(appHostDirectory);
        File.WriteAllText(Path.Combine(appHostRoot, "README.md"), "consumer");
        InitializeGit(appHostRoot, "main");

        var siblingProject = Path.Combine(parent.Path, "orders", "Orders.Api.csproj");
        var builder = CreateBuilder(appHostDirectory);
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:AutoCloneRepositories"] = "true";
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:GitHubCliPath"] =
            CreateFakeGh(parent.Path, source.Path);
        var module = ExportRemoteProject(builder, siblingProject);

        var exception = Assert.Throws<InvalidOperationException>(() => builder.Add(module));

        Assert.Contains("Module 'orders'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("project service 'orders-api'", exception.Message, StringComparison.Ordinal);
        Assert.Contains(siblingProject, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Projects", "missing-project", "project service")]
    [InlineData("Containers", "missing-container", "container service")]
    public void Unknown_configured_service_breaks_with_available_service_diagnostic(
        string resourceKind,
        string missingName,
        string expectedKind)
    {
        using var repository = TemporaryDirectory.Create();
        var projectPath = Path.Combine(repository.Path, "Orders.Api.csproj");
        File.WriteAllText(projectPath, ProjectContents);
        var builder = CreateBuilder(repository.Path);
        builder.Configuration[
            $"{ModularAppHostsOptions.ConfigurationSectionName}:Modules:orders:{resourceKind}:{missingName}:ImageTag"] =
            "debug";
        var exception = Assert.Throws<InvalidOperationException>(() =>
            builder.ExportModule("orders", definition =>
            {
                definition.WithRepository(repository.Path);
                definition.AddProject("orders-api", projectPath)
                    .ExportAsContainer("orders-api", "dotnet", ["publish"]);
                definition.AddContainer("orders-cache", "redis", "alpine");
            }));

        Assert.Contains(expectedKind, exception.Message, StringComparison.Ordinal);
        Assert.Contains(missingName, exception.Message, StringComparison.Ordinal);
        Assert.Contains("Available", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_configured_module_breaks_with_available_module_diagnostic()
    {
        var options = new ModularAppHostsOptions();
        options.Modules.Add("typo", new DistributedApplicationModuleOptions());
        var registry = new ModuleApplicationRegistry(options);

        var exception = Assert.Throws<InvalidOperationException>(registry.ValidateConfiguredModules);

        Assert.Contains("module 'typo'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Available modules: (none)", exception.Message, StringComparison.Ordinal);
    }

    private static IDistributedApplicationModule ExportRemoteProject(
        IDistributedApplicationBuilder builder,
        string projectPath)
    {
        return builder.ExportModule("orders", definition =>
        {
            definition.WithRepository("https://github.com/acme/orders.git");
            definition.AddProject("orders-api", projectPath)
                .ExportAsContainer("orders-api", "dotnet", ["publish"]);
        });
    }

    private static IDistributedApplicationBuilder CreateBuilder(
        string projectDirectory,
        bool publishMode = true)
    {
        return DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = publishMode ? ["--publisher", "manifest"] : [],
            DisableDashboard = true,
            ProjectDirectory = projectDirectory
        });
    }

    private static string CreateFakeGh(string directory, string sourceRepository)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The process-level fake GitHub CLI uses a POSIX shell.");
        }

        var path = Path.Combine(directory, "fake-gh");
        var escapedSource = sourceRepository.Replace("'", "'\"'\"'", StringComparison.Ordinal);
        File.WriteAllText(
            path,
            $"#!/bin/sh{Environment.NewLine}" +
            $"git clone --recurse-submodules -- '{escapedSource}' \"$4\" || exit $?{Environment.NewLine}" +
            $"exec git -C \"$4\" remote set-url origin https://github.com/acme/orders.git{Environment.NewLine}");
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute);
        return path;
    }

    private static void InitializeGit(string repositoryPath, string branch)
    {
        RunGit(repositoryPath, "init", "--initial-branch", branch);
        RunGit(repositoryPath, "config", "user.name", "Test User");
        RunGit(repositoryPath, "config", "user.email", "test@example.test");
        RunGit(repositoryPath, "add", ".");
        RunGit(repositoryPath, "commit", "-m", "initial");
    }

    private static void RunGit(string workingDirectory, params string[] arguments)
    {
        var result = CliCommand.Wrap("git")
            .WithArguments(arguments)
            .WithWorkingDirectory(workingDirectory)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync()
            .GetAwaiter()
            .GetResult();
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(result.StandardError);
        }
    }

    private static string ExpectedTag(string repositoryPath, string branch)
    {
        return ModuleImageTag.FromRepository(branch, RepositoryInspector.TryGetCommit(repositoryPath));
    }

    private const string ProjectContents =
        "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>";

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"aspire-module-discovery-{Guid.NewGuid():N}");
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
