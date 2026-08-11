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
    public void Repository_identity_includes_non_default_ports()
    {
        Assert.False(GitHubRepositoryCloner.RefersToSameRepository(
            "ssh://git@example.test:2222/acme/orders.git",
            "ssh://git@example.test:3333/acme/orders.git",
            Path.GetTempPath()));
        Assert.True(GitHubRepositoryCloner.RefersToSameRepository(
            "ssh://git@example.test:22/acme/orders.git",
            "git@example.test:acme/orders.git",
            Path.GetTempPath()));
    }

    [Fact]
    public void Repository_path_casing_is_host_specific()
    {
        Assert.True(GitHubRepositoryCloner.RefersToSameRepository(
            "https://github.com/Acme/Orders.git",
            "git@github.com:acme/orders.git",
            Path.GetTempPath()));
        Assert.False(GitHubRepositoryCloner.RefersToSameRepository(
            "https://git.example.test/Acme/Orders.git",
            "git@git.example.test:acme/orders.git",
            Path.GetTempPath()));
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
    public async Task Same_git_repository_preserves_module_root_and_never_invokes_gh()
    {
        using var workspace = TemporaryDirectory.Create();
        var appHostDirectory = Path.Combine(workspace.Path, "apphost");
        var moduleRoot = Path.Combine(workspace.Path, "modules", "orders");
        var projectPath = Path.Combine(moduleRoot, "Orders.Api.csproj");
        Directory.CreateDirectory(appHostDirectory);
        Directory.CreateDirectory(moduleRoot);
        File.WriteAllText(projectPath, ProjectContents);
        await InitializeGitAsync(workspace.Path, "feature/same-repository");

        var builder = CreateBuilder(appHostDirectory);
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:AutoCloneRepositories"] = "true";
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:GitHubCliPath"] =
            Path.Combine(workspace.Path, "missing-gh");
        var module = await builder.ExportModuleAsync("orders", definition =>
        {
            definition.WithRepository(moduleRoot);
            definition.AddProject("orders-api", projectPath)
                .ExportAsContainer("orders-api", "dotnet", ["publish"]);
        });

        await builder.AddAsync(module);

        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var annotation = Assert.Single(
            container.Annotations.OfType<DistributedApplicationModuleResourceAnnotation>());
        var image = Assert.Single(container.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal(moduleRoot, annotation.RepositoryPath);
        Assert.Equal(await ExpectedTagAsync(workspace.Path, "feature/same-repository"), image.Tag);
    }

    [Fact]
    public async Task Same_git_repository_dirty_state_is_applied_to_branch_tag()
    {
        using var workspace = TemporaryDirectory.Create();
        var appHostDirectory = Path.Combine(workspace.Path, "apphost");
        var projectPath = Path.Combine(workspace.Path, "Orders.Api.csproj");
        Directory.CreateDirectory(appHostDirectory);
        File.WriteAllText(projectPath, ProjectContents);
        await InitializeGitAsync(workspace.Path, "feature/dirty-image");
        File.AppendAllText(projectPath, Environment.NewLine + "<!-- dirty -->");

        var builder = CreateBuilder(appHostDirectory, publishMode: false);
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:AutoCloneRepositories"] = "true";
        var module = await builder.ExportModuleAsync("orders", definition =>
        {
            definition.WithRepository(workspace.Path);
            definition.AddProject("orders-api", projectPath)
                .ExportAsContainer("orders-api", "dotnet", ["publish"]);
        });

        await builder.AddAsync(module);

        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var image = Assert.Single(container.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal($"{await ExpectedTagAsync(workspace.Path, "feature/dirty-image")}-dirty", image.Tag);
        var installer = Assert.Single(builder.Resources.OfType<ModuleRepositoryInstallerResource>());
        Assert.True(installer.RepositoryDirty);
    }

    [Fact]
    public async Task Same_repository_remote_is_discovered_without_cloning()
    {
        using var repository = TemporaryDirectory.Create();
        var appHostDirectory = Path.Combine(repository.Path, "AppHost");
        Directory.CreateDirectory(appHostDirectory);
        File.WriteAllText(Path.Combine(repository.Path, "README.md"), "consumer");
        await InitializeGitAsync(repository.Path, "main");
        await RunGitAsync(
            repository.Path,
            "remote",
            "add",
            "origin",
            "git@github.com:acme/consumer.git");

        var builder = CreateBuilder(appHostDirectory);
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:AutoCloneRepositories"] = "true";
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:GitHubCliPath"] =
            Path.Combine(repository.Path, "missing-gh");
        var module = await builder.ExportModuleAsync("consumer", definition =>
        {
            definition.WithRepository("https://github.com/acme/consumer.git");
            definition.AddContainer("consumer-cache", "redis", "alpine");
        });

        await builder.AddAsync(module);

        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var annotation = Assert.Single(
            container.Annotations.OfType<DistributedApplicationModuleResourceAnnotation>());
        Assert.Equal(repository.Path, annotation.RepositoryPath);
    }

    [Fact]
    public async Task Repository_independent_module_ignores_global_auto_clone()
    {
        using var repository = TemporaryDirectory.Create();
        var appHostDirectory = Path.Combine(repository.Path, "AppHost");
        Directory.CreateDirectory(appHostDirectory);
        File.WriteAllText(Path.Combine(repository.Path, "README.md"), "consumer");
        await InitializeGitAsync(repository.Path, "main");

        var builder = CreateBuilder(appHostDirectory);
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:AutoCloneRepositories"] = "true";
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:GitHubCliPath"] =
            Path.Combine(repository.Path, "missing-gh");
        var module = await builder.ExportModuleAsync("portable", definition =>
            definition.AddContainer("portable-cache", "redis", "alpine"));

        await builder.ImportModuleAsync("portable");

        Assert.Empty(builder.Resources.OfType<ParameterResource>());
        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var annotation = Assert.Single(
            container.Annotations.OfType<DistributedApplicationModuleResourceAnnotation>());
        Assert.Equal(appHostDirectory, annotation.RepositoryPath);
    }

    [Fact]
    public async Task Imported_same_worktree_without_remote_does_not_request_repository_parameter()
    {
        using var repository = TemporaryDirectory.Create();
        var appHostDirectory = Path.Combine(repository.Path, "AppHost");
        var projectPath = Path.Combine(repository.Path, "Orders.Api.csproj");
        Directory.CreateDirectory(appHostDirectory);
        File.WriteAllText(projectPath, ProjectContents);
        await InitializeGitAsync(repository.Path, "feature/local-import");

        var builder = CreateBuilder(appHostDirectory);
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:AutoCloneRepositories"] = "true";
        await builder.ExportModuleAsync("orders", definition =>
            definition.AddProject("orders-api", projectPath)
                .ExportAsContainer("orders-api", "dotnet", ["publish"]));

        await builder.ImportModuleAsync("orders");

        Assert.Empty(builder.Resources.OfType<ParameterResource>());
        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var annotation = Assert.Single(
            container.Annotations.OfType<DistributedApplicationModuleResourceAnnotation>());
        Assert.True(annotation.Imported);
        Assert.Equal(repository.Path, annotation.RepositoryPath);
    }

    [Fact]
    public async Task Existing_sibling_repository_is_discovered_without_invoking_gh()
    {
        using var parent = TemporaryDirectory.Create();
        var appHostRoot = Path.Combine(parent.Path, "consumer");
        var appHostDirectory = Path.Combine(appHostRoot, "src", "AppHost");
        var moduleRoot = Path.Combine(parent.Path, "orders");
        var projectPath = Path.Combine(moduleRoot, "src", "Orders.Api", "Orders.Api.csproj");
        Directory.CreateDirectory(appHostDirectory);
        File.WriteAllText(Path.Combine(appHostRoot, "README.md"), "consumer");
        await InitializeGitAsync(appHostRoot, "consumer-branch");
        Directory.CreateDirectory(Path.GetDirectoryName(projectPath)!);
        File.WriteAllText(projectPath, ProjectContents);
        await InitializeGitAsync(moduleRoot, "feature/orders-service");
        await RunGitAsync(moduleRoot, "remote", "add", "origin", "https://github.com/acme/orders.git");

        var builder = CreateBuilder(appHostDirectory);
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:AutoCloneRepositories"] = "true";
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:GitHubCliPath"] =
            Path.Combine(parent.Path, "missing-gh");
        var module = await ExportRemoteProjectAsync(builder, projectPath);

        await builder.AddAsync(module);

        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var annotation = Assert.Single(
            container.Annotations.OfType<DistributedApplicationModuleResourceAnnotation>());
        var image = Assert.Single(container.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal(moduleRoot, annotation.RepositoryPath);
        Assert.Equal(await ExpectedTagAsync(moduleRoot, "feature/orders-service"), image.Tag);
    }

    [Fact]
    public async Task Pinned_import_uses_managed_checkout_without_detaching_sibling_worktree()
    {
        using var parent = TemporaryDirectory.Create();
        using var imports = TemporaryDirectory.Create();
        var appHostRoot = Path.Combine(parent.Path, "consumer");
        var appHostDirectory = Path.Combine(appHostRoot, "src", "AppHost");
        var moduleRoot = Path.Combine(parent.Path, "orders");
        var relativeProjectPath = Path.Combine("src", "Orders.Api", "Orders.Api.csproj");
        var projectPath = Path.Combine(moduleRoot, relativeProjectPath);
        Directory.CreateDirectory(appHostDirectory);
        File.WriteAllText(Path.Combine(appHostRoot, "README.md"), "consumer");
        await InitializeGitAsync(appHostRoot, "consumer-branch");
        Directory.CreateDirectory(Path.GetDirectoryName(projectPath)!);
        File.WriteAllText(projectPath, ProjectContents);
        await InitializeGitAsync(moduleRoot, "feature/orders-service");
        var pinnedCommit = Assert.IsType<string>(await RepositoryInspector.TryResolveCommitAsync(
            moduleRoot,
            cancellationToken: TestContext.Current.CancellationToken));
        File.AppendAllText(projectPath, Environment.NewLine + "<!-- current branch -->");
        await RunGitAsync(moduleRoot, "add", ".");
        await RunGitAsync(moduleRoot, "commit", "-m", "current branch");
        var developerCommit = Assert.IsType<string>(await RepositoryInspector.TryResolveCommitAsync(
            moduleRoot,
            cancellationToken: TestContext.Current.CancellationToken));

        var builder = CreateBuilder(appHostDirectory, publishMode: false);
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:AutoCloneRepositories"] = "true";
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:RepositoryBasePath"] = imports.Path;
        var module = await builder.ExportModuleAsync("orders", definition =>
        {
            definition.WithRepository(moduleRoot, pinnedCommit);
            definition.AddProject("orders-api", relativeProjectPath, ModuleProjectPathBase.Repository)
                .ExportAsContainer("orders-api", "dotnet", ["publish"]);
        });

        await builder.ImportModuleAsync(module.Name);

        Assert.Equal("feature/orders-service", await RepositoryInspector.TryGetBranchAsync(
            moduleRoot,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(developerCommit, await RepositoryInspector.TryResolveCommitAsync(
            moduleRoot,
            cancellationToken: TestContext.Current.CancellationToken));
        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var annotation = Assert.Single(
            container.Annotations.OfType<DistributedApplicationModuleResourceAnnotation>());
        Assert.StartsWith(Path.GetFullPath(imports.Path), annotation.RepositoryPath, StringComparison.Ordinal);
        Assert.NotEqual(moduleRoot, annotation.RepositoryPath);
        Assert.Equal(pinnedCommit, await RepositoryInspector.TryResolveCommitAsync(
            annotation.RepositoryPath,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Null(await RepositoryInspector.TryGetBranchAsync(
            annotation.RepositoryPath,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(
            ManagedRepositoryIsolation.BoundaryContents,
            File.ReadAllText(Path.Combine(imports.Path, "Directory.Build.props")));
        Assert.Equal(
            ManagedRepositoryIsolation.BoundaryContents,
            File.ReadAllText(Path.Combine(imports.Path, "Directory.Build.targets")));
        Assert.Equal(
            ManagedRepositoryIsolation.BoundaryContents,
            File.ReadAllText(Path.Combine(imports.Path, "Directory.Packages.props")));
    }

    [Fact]
    public void Managed_repository_boundary_preserves_explicit_MSBuild_configuration()
    {
        using var imports = TemporaryDirectory.Create();
        var existingProps = Path.Combine(imports.Path, "Directory.Build.props");
        const string existingContents = "<Project><PropertyGroup><Custom>true</Custom></PropertyGroup></Project>";
        File.WriteAllText(existingProps, existingContents);

        ManagedRepositoryIsolation.EnsureBoundary(imports.Path);

        Assert.Equal(existingContents, File.ReadAllText(existingProps));
        Assert.Equal(
            ManagedRepositoryIsolation.BoundaryContents,
            File.ReadAllText(Path.Combine(imports.Path, "Directory.Build.targets")));
        Assert.Equal(
            ManagedRepositoryIsolation.BoundaryContents,
            File.ReadAllText(Path.Combine(imports.Path, "Directory.Packages.props")));
    }

    [Fact]
    public async Task Pinned_import_uses_managed_checkout_without_detaching_AppHost_worktree()
    {
        using var repository = TemporaryDirectory.Create();
        using var imports = TemporaryDirectory.Create();
        var appHostDirectory = Path.Combine(repository.Path, "src", "AppHost");
        var relativeProjectPath = Path.Combine("src", "Orders.Api", "Orders.Api.csproj");
        var projectPath = Path.Combine(repository.Path, relativeProjectPath);
        Directory.CreateDirectory(appHostDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(projectPath)!);
        File.WriteAllText(projectPath, ProjectContents);
        await InitializeGitAsync(repository.Path, "feature/consumer");
        var pinnedCommit = Assert.IsType<string>(await RepositoryInspector.TryResolveCommitAsync(
            repository.Path,
            cancellationToken: TestContext.Current.CancellationToken));
        File.AppendAllText(projectPath, Environment.NewLine + "<!-- active AppHost -->");
        await RunGitAsync(repository.Path, "add", ".");
        await RunGitAsync(repository.Path, "commit", "-m", "active AppHost");
        var developerCommit = Assert.IsType<string>(await RepositoryInspector.TryResolveCommitAsync(
            repository.Path,
            cancellationToken: TestContext.Current.CancellationToken));

        var builder = CreateBuilder(appHostDirectory, publishMode: false);
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:AutoCloneRepositories"] = "true";
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:RepositoryBasePath"] = imports.Path;
        var module = await builder.ExportModuleAsync("orders", definition =>
        {
            definition.WithRepository(repository.Path, pinnedCommit);
            definition.AddProject("orders-api", relativeProjectPath, ModuleProjectPathBase.Repository)
                .ExportAsContainer("orders-api", "dotnet", ["publish"]);
        });

        await builder.ImportModuleAsync(module.Name);

        Assert.Equal("feature/consumer", await RepositoryInspector.TryGetBranchAsync(
            repository.Path,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(developerCommit, await RepositoryInspector.TryResolveCommitAsync(
            repository.Path,
            cancellationToken: TestContext.Current.CancellationToken));
        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var annotation = Assert.Single(
            container.Annotations.OfType<DistributedApplicationModuleResourceAnnotation>());
        Assert.StartsWith(Path.GetFullPath(imports.Path), annotation.RepositoryPath, StringComparison.Ordinal);
        Assert.NotEqual(repository.Path, annotation.RepositoryPath);
        Assert.Equal(pinnedCommit, await RepositoryInspector.TryResolveCommitAsync(
            annotation.RepositoryPath,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Null(await RepositoryInspector.TryGetBranchAsync(
            annotation.RepositoryPath,
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Missing_pinned_imports_nested_under_the_AppHost_repository_use_distinct_synchronization_keys()
    {
        using var appHostRepository = TemporaryDirectory.Create();
        using var firstModuleRepository = TemporaryDirectory.Create();
        using var secondModuleRepository = TemporaryDirectory.Create();
        var appHostDirectory = Path.Combine(appHostRepository.Path, "src", "AppHost");
        Directory.CreateDirectory(appHostDirectory);
        File.WriteAllText(Path.Combine(appHostRepository.Path, "README.md"), "consumer");
        File.WriteAllText(Path.Combine(firstModuleRepository.Path, "README.md"), "first");
        File.WriteAllText(Path.Combine(secondModuleRepository.Path, "README.md"), "second");
        File.WriteAllText(Path.Combine(firstModuleRepository.Path, "First.Api.csproj"), ProjectContents);
        File.WriteAllText(Path.Combine(secondModuleRepository.Path, "Second.Api.csproj"), ProjectContents);
        await InitializeGitAsync(appHostRepository.Path, "consumer");
        await InitializeGitAsync(firstModuleRepository.Path, "first");
        await InitializeGitAsync(secondModuleRepository.Path, "second");
        var firstRevision = Assert.IsType<string>(await RepositoryInspector.TryResolveCommitAsync(
            firstModuleRepository.Path,
            cancellationToken: TestContext.Current.CancellationToken));
        var secondRevision = Assert.IsType<string>(await RepositoryInspector.TryResolveCommitAsync(
            secondModuleRepository.Path,
            cancellationToken: TestContext.Current.CancellationToken));
        var builder = CreateBuilder(appHostDirectory, publishMode: false);
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:AutoCloneRepositories"] = "true";
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:RepositoryBasePath"] =
            Path.Combine(appHostRepository.Path, ".aspire", "module-repositories");
        await builder.ExportModuleAsync("first", definition =>
        {
            definition.WithRepository(firstModuleRepository.Path, firstRevision);
            definition.AddProject("first-api", "First.Api.csproj", ModuleProjectPathBase.Repository)
                .ExportAsContainer("first-api", "dotnet", ["publish"]);
        });
        await builder.ExportModuleAsync("second", definition =>
        {
            definition.WithRepository(secondModuleRepository.Path, secondRevision);
            definition.AddProject("second-api", "Second.Api.csproj", ModuleProjectPathBase.Repository)
                .ExportAsContainer("second-api", "dotnet", ["publish"]);
        });

        await builder.ImportModuleAsync("first");
        await builder.ImportModuleAsync("second");

        var annotations = builder.Resources
            .OfType<ContainerResource>()
            .SelectMany(resource => resource.Annotations.OfType<DistributedApplicationModuleResourceAnnotation>())
            .OrderBy(annotation => annotation.ModuleName, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(2, annotations.Length);
        Assert.NotEqual(annotations[0].RepositoryPath, annotations[1].RepositoryPath);
        Assert.Equal(firstRevision, await RepositoryInspector.TryResolveCommitAsync(
            annotations.Single(annotation => annotation.ModuleName == "first").RepositoryPath,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(secondRevision, await RepositoryInspector.TryResolveCommitAsync(
            annotations.Single(annotation => annotation.ModuleName == "second").RepositoryPath,
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Repository_relative_project_does_not_inherit_the_consumer_repository_identity()
    {
        using var parent = TemporaryDirectory.Create();
        var appHostRoot = Path.Combine(parent.Path, "consumer");
        var appHostDirectory = Path.Combine(appHostRoot, "src", "AppHost");
        var moduleRoot = Path.Combine(parent.Path, "orders");
        var relativeProjectPath = Path.Combine("src", "Orders.Api", "Orders.Api.csproj");
        Directory.CreateDirectory(appHostDirectory);
        File.WriteAllText(Path.Combine(appHostRoot, "README.md"), "consumer");
        await InitializeGitAsync(appHostRoot, "consumer-branch");
        await RunGitAsync(
            appHostRoot,
            "remote",
            "add",
            "origin",
            "https://github.com/acme/consumer.git");
        Directory.CreateDirectory(Path.Combine(moduleRoot, "src", "Orders.Api"));
        File.WriteAllText(Path.Combine(moduleRoot, relativeProjectPath), ProjectContents);
        await InitializeGitAsync(moduleRoot, "feature/orders-service");
        await RunGitAsync(
            moduleRoot,
            "remote",
            "add",
            "origin",
            "https://github.com/acme/orders.git");

        var builder = CreateBuilder(appHostDirectory);
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:AutoCloneRepositories"] = "true";
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:GitHubCliPath"] =
            Path.Combine(parent.Path, "missing-gh");
        var module = await builder.ExportModuleAsync("orders", definition =>
        {
            definition.WithRepository("https://github.com/acme/orders.git");
            definition.AddProject(
                    "orders-api",
                    relativeProjectPath,
                    ModuleProjectPathBase.Repository)
                .ExportAsContainer("orders-api", "dotnet", ["publish"]);
        });

        await builder.AddAsync(module);

        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var annotation = Assert.Single(
            container.Annotations.OfType<DistributedApplicationModuleResourceAnnotation>());
        Assert.Equal(moduleRoot, annotation.RepositoryPath);
    }

    [Fact]
    public async Task Repository_relative_project_does_not_infer_the_consumer_remote()
    {
        using var repository = TemporaryDirectory.Create();
        var appHostDirectory = Path.Combine(repository.Path, "AppHost");
        Directory.CreateDirectory(appHostDirectory);
        File.WriteAllText(Path.Combine(repository.Path, "README.md"), "consumer");
        await InitializeGitAsync(repository.Path, "main");
        await RunGitAsync(
            repository.Path,
            "remote",
            "add",
            "origin",
            "https://github.com/acme/consumer.git");
        var builder = CreateBuilder(appHostDirectory);

        var module = await builder.ExportModuleAsync("orders", definition =>
            definition.AddProject(
                    "orders-api",
                    Path.Combine("src", "Orders.Api", "Orders.Api.csproj"),
                    ModuleProjectPathBase.Repository)
                .ExportAsContainer("orders-api", "dotnet", ["publish"]));

        var definition = Assert.IsType<DistributedApplicationModule>(module);
        Assert.Null(definition.Repository);
        Assert.Null(Assert.Single(definition.ProjectDefinitions).SourceRepositoryRoot);
    }

    [Fact]
    public async Task Missing_sibling_repository_is_cloned_through_configured_gh_process()
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
        await InitializeGitAsync(source.Path, "feature/cloned-service");
        await RunGitAsync(source.Path, "remote", "add", "origin", "https://github.com/acme/orders.git");

        var appHostRoot = Path.Combine(parent.Path, "consumer");
        var appHostDirectory = Path.Combine(appHostRoot, "src", "AppHost");
        Directory.CreateDirectory(appHostDirectory);
        File.WriteAllText(Path.Combine(appHostRoot, "README.md"), "consumer");
        await InitializeGitAsync(appHostRoot, "consumer-branch");

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
        var module = await ExportRemoteProjectAsync(builder, siblingProject);

        await builder.AddAsync(module);

        var clonePath = Path.Combine(parent.Path, "orders");
        Assert.True(await RepositoryInspector.IsGitRepositoryAsync(
            clonePath,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(File.Exists(siblingProject));
        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var annotation = Assert.Single(
            container.Annotations.OfType<DistributedApplicationModuleResourceAnnotation>());
        var image = Assert.Single(container.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal(clonePath, annotation.RepositoryPath);
        Assert.Equal(await ExpectedTagAsync(clonePath, "feature/cloned-service"), image.Tag);
    }

    [Fact]
    public async Task Resource_auto_clone_policy_clones_build_repository_without_cloning_container_definition_repository()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var parent = TemporaryDirectory.Create();
        using var source = TemporaryDirectory.Create();
        File.WriteAllText(Path.Combine(source.Path, "Containerfile"), "FROM scratch");
        await InitializeGitAsync(source.Path, "feature/build-inputs");
        const string BuildRepository = "https://github.com/acme/build-inputs.git";
        await RunGitAsync(source.Path, "remote", "add", "origin", BuildRepository);

        var appHostRoot = Path.Combine(parent.Path, "consumer");
        var appHostDirectory = Path.Combine(appHostRoot, "src", "AppHost");
        Directory.CreateDirectory(appHostDirectory);
        File.WriteAllText(Path.Combine(appHostRoot, "README.md"), "consumer");
        await InitializeGitAsync(appHostRoot, "consumer-branch");

        var fakeGh = CreateFakeGh(parent.Path, source.Path, BuildRepository);
        var builder = CreateBuilder(appHostDirectory, publishMode: false);
        var section =
            $"{ModularAppHostsOptions.ConfigurationSectionName}:Modules:static:Containers:static-site";
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:GitHubCliPath"] = fakeGh;
        builder.Configuration[$"{section}:BuildRepository"] = BuildRepository;
        builder.Configuration[$"{section}:AutoCloneBuildRepository"] = "true";
        builder.Configuration[$"{section}:UpdateBuildRepository"] = "false";
        var imageName = $"module-test-auto-cloned-build-{Guid.NewGuid():N}";
        await builder.ExportModuleAsync("static", definition =>
        {
            definition.WithRepository("https://github.com/acme/module-definition.git");
            definition.AddContainer("static-site", imageName, "dev")
                .WithImagePublishCommand(new ModuleContainerExportOptions(
                    imageName,
                    "podman",
                    "build",
                    ModuleContainerExportOptions.ImageReferencePlaceholder,
                    "."));
        });

        await builder.ImportModuleAsync("static");

        var clonePath = Path.Combine(parent.Path, "build-inputs");
        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var definitionAnnotation = Assert.Single(
            container.Annotations.OfType<DistributedApplicationModuleResourceAnnotation>());
        var image = Assert.Single(container.Annotations.OfType<ContainerImageAnnotation>());
        var installer = Assert.Single(builder.Resources.OfType<ModuleRepositoryInstallerResource>());
        Assert.True(definitionAnnotation.Imported);
        Assert.False(Directory.Exists(definitionAnnotation.RepositoryPath));
        Assert.Empty(builder.Resources.OfType<ParameterResource>());
        Assert.Equal(await ExpectedTagAsync(clonePath, "feature/build-inputs"), image.Tag);
        Assert.Equal(clonePath, installer.RepositoryPath);
        Assert.Equal(clonePath, installer.WorkingDirectory);
        Assert.Equal(BuildRepository, installer.Repository);
        Assert.False(installer.UpdatesRepository);
        Assert.True(await RepositoryInspector.IsGitRepositoryAsync(
            clonePath,
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Resource_auto_clone_reuses_but_does_not_update_the_active_apphost_worktree()
    {
        using var appHost = TemporaryDirectory.Create();
        var appHostDirectory = Path.Combine(appHost.Path, "src", "AppHost");
        Directory.CreateDirectory(appHostDirectory);
        File.WriteAllText(Path.Combine(appHost.Path, "README.md"), "consumer build inputs");
        await InitializeGitAsync(appHost.Path, "feature/consumer-build");
        const string BuildRepository = "https://github.com/acme/consumer.git";
        await RunGitAsync(appHost.Path, "remote", "add", "origin", BuildRepository);
        var originalHead = Assert.IsType<string>(await RepositoryInspector.TryResolveCommitAsync(
            appHost.Path,
            cancellationToken: TestContext.Current.CancellationToken));

        var builder = CreateBuilder(appHostDirectory, publishMode: false);
        var section =
            $"{ModularAppHostsOptions.ConfigurationSectionName}:Modules:static:Containers:static-site";
        builder.Configuration[$"{section}:BuildRepository"] = BuildRepository;
        builder.Configuration[$"{section}:AutoCloneBuildRepository"] = "true";
        builder.Configuration[$"{section}:UpdateBuildRepository"] = "true";
        var imageName = $"module-test-active-apphost-build-{Guid.NewGuid():N}";
        await builder.ExportModuleAsync("static", definition =>
            definition.AddContainer("static-site", imageName, "dev")
                .WithImagePublishCommand(new ModuleContainerExportOptions(
                    imageName,
                    "podman",
                    "build",
                    ModuleContainerExportOptions.ImageReferencePlaceholder,
                    ".")
                {
                    ImageTag = "dev"
                }));

        await builder.ImportModuleAsync("static");

        var installer = Assert.Single(builder.Resources.OfType<ModuleRepositoryInstallerResource>());
        Assert.Equal(appHost.Path, installer.RepositoryPath);
        Assert.False(installer.UpdatesRepository);
        Assert.Equal("feature/consumer-build", await RepositoryInspector.TryGetBranchAsync(
            appHost.Path,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(originalHead, await RepositoryInspector.TryResolveCommitAsync(
            appHost.Path,
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Missing_gh_has_an_actionable_error_only_when_auto_clone_is_enabled()
    {
        using var parent = TemporaryDirectory.Create();
        var appHostRoot = Path.Combine(parent.Path, "consumer");
        var appHostDirectory = Path.Combine(appHostRoot, "AppHost");
        Directory.CreateDirectory(appHostDirectory);
        File.WriteAllText(Path.Combine(appHostRoot, "README.md"), "consumer");
        await InitializeGitAsync(appHostRoot, "main");
        var siblingProject = Path.Combine(parent.Path, "orders", "Orders.Api.csproj");

        var builder = CreateBuilder(appHostDirectory);
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:AutoCloneRepositories"] = "true";
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:GitHubCliPath"] =
            Path.Combine(parent.Path, "missing-gh");
        var module = await ExportRemoteProjectAsync(builder, siblingProject);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => builder.AddAsync(module));

        Assert.Contains("requires the GitHub CLI", exception.Message, StringComparison.Ordinal);
        Assert.Contains("AutoCloneRepositories", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GitHub_cli_clone_failure_preserves_exit_code_and_diagnostic()
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

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            GitHubRepositoryCloner.CloneAsync(
                fakeGh,
                "acme/orders",
                Path.Combine(workspace.Path, "orders"),
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("exit code 23", exception.Message, StringComparison.Ordinal);
        Assert.Contains("authentication failed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Disabled_auto_clone_reports_missing_service_without_invoking_gh()
    {
        using var parent = TemporaryDirectory.Create();
        var appHostRoot = Path.Combine(parent.Path, "consumer");
        var appHostDirectory = Path.Combine(appHostRoot, "AppHost");
        Directory.CreateDirectory(appHostDirectory);
        File.WriteAllText(Path.Combine(appHostRoot, "README.md"), "consumer");
        await InitializeGitAsync(appHostRoot, "consumer-branch");
        var siblingProject = Path.Combine(parent.Path, "orders", "Orders.Api.csproj");

        var builder = CreateBuilder(appHostDirectory);
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:GitHubCliPath"] =
            Path.Combine(parent.Path, "missing-gh");
        var module = await ExportRemoteProjectAsync(builder, siblingProject);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => builder.AddAsync(module));

        Assert.False(Directory.Exists(Path.Combine(parent.Path, "orders")));
        Assert.Contains("project service 'orders-api'", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("GitHub CLI", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Published_container_without_explicit_tag_uses_sanitized_branch()
    {
        using var repository = TemporaryDirectory.Create();
        var appHostDirectory = Path.Combine(repository.Path, "AppHost");
        Directory.CreateDirectory(appHostDirectory);
        File.WriteAllText(Path.Combine(repository.Path, "README.md"), "module");
        await InitializeGitAsync(repository.Path, "feature/container-publisher");

        var builder = CreateBuilder(appHostDirectory);
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:AutoCloneRepositories"] = "true";
        var module = await builder.ExportModuleAsync("static", definition =>
        {
            definition.WithRepository(repository.Path);
            definition.AddContainer("static-site", "static-site", "dev")
                .WithImagePublishCommand(new ModuleContainerExportOptions(
                    "static-site",
                    "podman",
                    "build",
                    ModuleContainerExportOptions.ImageReferencePlaceholder));
        });

        await builder.AddAsync(module);

        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var image = Assert.Single(container.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal(await ExpectedTagAsync(repository.Path, "feature/container-publisher"), image.Tag);
    }

    [Fact]
    public async Task Existing_non_git_sibling_breaks_with_discovery_diagnostic()
    {
        using var parent = TemporaryDirectory.Create();
        var appHostRoot = Path.Combine(parent.Path, "consumer");
        var appHostDirectory = Path.Combine(appHostRoot, "AppHost");
        var siblingProject = Path.Combine(parent.Path, "orders", "Orders.Api.csproj");
        Directory.CreateDirectory(appHostDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(siblingProject)!);
        File.WriteAllText(Path.Combine(appHostRoot, "README.md"), "consumer");
        File.WriteAllText(siblingProject, ProjectContents);
        await InitializeGitAsync(appHostRoot, "main");

        var builder = CreateBuilder(appHostDirectory);
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:AutoCloneRepositories"] = "true";
        var module = await ExportRemoteProjectAsync(builder, siblingProject);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => builder.AddAsync(module));

        Assert.Contains("orders", exception.Message, StringComparison.Ordinal);
        Assert.Contains("not a Git repository", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_service_project_after_clone_breaks_with_module_and_service_names()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var parent = TemporaryDirectory.Create();
        using var source = TemporaryDirectory.Create();
        File.WriteAllText(Path.Combine(source.Path, "README.md"), "no project");
        await InitializeGitAsync(source.Path, "main");
        await RunGitAsync(source.Path, "remote", "add", "origin", "https://github.com/acme/orders.git");
        var appHostRoot = Path.Combine(parent.Path, "consumer");
        var appHostDirectory = Path.Combine(appHostRoot, "AppHost");
        Directory.CreateDirectory(appHostDirectory);
        File.WriteAllText(Path.Combine(appHostRoot, "README.md"), "consumer");
        await InitializeGitAsync(appHostRoot, "main");

        var siblingProject = Path.Combine(parent.Path, "orders", "Orders.Api.csproj");
        var builder = CreateBuilder(appHostDirectory);
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:AutoCloneRepositories"] = "true";
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:GitHubCliPath"] =
            CreateFakeGh(parent.Path, source.Path);
        var module = await ExportRemoteProjectAsync(builder, siblingProject);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => builder.AddAsync(module));

        Assert.Contains("Module 'orders'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("project service 'orders-api'", exception.Message, StringComparison.Ordinal);
        Assert.Contains(siblingProject, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Projects", "missing-project", "project service")]
    [InlineData("Containers", "missing-container", "container service")]
    public async Task Unknown_configured_service_breaks_with_available_service_diagnostic(
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
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            builder.ExportModuleAsync("orders", definition =>
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

    private static async Task<IDistributedApplicationModule> ExportRemoteProjectAsync(
        IDistributedApplicationBuilder builder,
        string projectPath)
    {
        return await builder.ExportModuleAsync("orders", definition =>
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
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = publishMode ? ["--publisher", "manifest"] : [],
            DisableDashboard = true,
            ProjectDirectory = projectDirectory
        });
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:ProjectMode"] =
            nameof(ModuleProjectMode.Container);
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:PublishImages"] = "true";
        return builder;
    }

    private static string CreateFakeGh(
        string directory,
        string sourceRepository,
        string remoteRepository = "https://github.com/acme/orders.git")
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The process-level fake GitHub CLI uses a POSIX shell.");
        }

        var path = Path.Combine(directory, "fake-gh");
        var escapedSource = sourceRepository.Replace("'", "'\"'\"'", StringComparison.Ordinal);
        var escapedRemote = remoteRepository.Replace("'", "'\"'\"'", StringComparison.Ordinal);
        File.WriteAllText(
            path,
            $"#!/bin/sh{Environment.NewLine}" +
            $"git clone --recurse-submodules -- '{escapedSource}' \"$4\" || exit $?{Environment.NewLine}" +
            $"exec git -C \"$4\" remote set-url origin '{escapedRemote}'{Environment.NewLine}");
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute);
        return path;
    }

    private static async Task InitializeGitAsync(string repositoryPath, string branch)
    {
        await RunGitAsync(repositoryPath, "init", "--initial-branch", branch);
        await RunGitAsync(repositoryPath, "config", "user.name", "Test User");
        await RunGitAsync(repositoryPath, "config", "user.email", "test@example.test");
        await RunGitAsync(repositoryPath, "add", ".");
        await RunGitAsync(repositoryPath, "commit", "-m", "initial");
    }

    private static async Task RunGitAsync(string workingDirectory, params string[] arguments)
    {
        var result = await CliCommand.Wrap("git")
            .WithArguments(arguments)
            .WithWorkingDirectory(workingDirectory)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync()
            .ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(result.StandardError);
        }
    }

    private static async Task<string> ExpectedTagAsync(string repositoryPath, string branch)
    {
        return ModuleImageTag.FromRepository(branch, await RepositoryInspector.TryGetCommitAsync(repositoryPath));
    }

    private const string ProjectContents =
        "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>";

}
