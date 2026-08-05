using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ModularAppHosts;
using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;
using CliCommand = global::CliWrap.Cli;

namespace Aspire.Hosting.ModularAppHosts.Tests;

public sealed class DistributedApplicationModuleExtensionsTests
{
    [Fact]
    public async Task ExportModule_registers_definition_without_materializing_resources()
    {
        using var repository = await TestRepository.CreateAsync();
        var builder = CreateBuilder(repository.Path);

        var module = await ExportModuleAsync(builder, repository.ProjectPath);

        Assert.Equal("orders", module.Name);
        Assert.Single(module.Projects);
        Assert.Empty(builder.Resources);
        Assert.Single(builder.Services, descriptor =>
            descriptor.ServiceType == typeof(IDistributedApplicationModuleCatalog));
    }

    [Fact]
    public async Task ExportModule_is_idempotent_by_name()
    {
        using var repository = await TestRepository.CreateAsync();
        var builder = CreateBuilder(repository.Path);
        var callbackCount = 0;

        var first = await builder.ExportModuleAsync("orders", module =>
        {
            callbackCount++;
            module.AddProject("orders-api", repository.ProjectPath)
                .ExportAsContainer("orders-api", "dotnet", ["publish", "--os", "linux"]);
        });
        var second = await builder.ExportModuleAsync("orders", _ => callbackCount++);

        Assert.Same(first, second);
        Assert.Equal(1, callbackCount);
    }

    [Fact]
    public async Task Concurrent_module_operations_are_serialized_per_builder()
    {
        var registry = new ModuleApplicationRegistry();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = false;

        var first = registry.RunModuleOperationAsync(async () =>
        {
            firstEntered.SetResult();
            await releaseFirst.Task;
            return 1;
        }, TestContext.Current.CancellationToken);
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        var second = registry.RunModuleOperationAsync(() =>
        {
            secondEntered = true;
            return Task.FromResult(2);
        }, TestContext.Current.CancellationToken);

        Assert.False(secondEntered);
        releaseFirst.SetResult();

        var results = await Task.WhenAll(first, second);
        Assert.Equal([1, 2], results);
        Assert.True(secondEntered);
    }

    [Fact]
    public async Task DefineModule_tracks_contract_version_and_rejects_a_conflicting_definition()
    {
        using var repository = await TestRepository.CreateAsync();
        var builder = CreateBuilder(repository.Path);

        var module = await builder.DefineModuleAsync("orders", "2", definition =>
            definition.AddContainer("cache", "redis"));
        var duplicate = await builder.DefineModuleAsync("orders", "2", _ =>
            throw new InvalidOperationException("The idempotent callback must not run."));

        Assert.Equal("2", module.Version);
        Assert.Same(module, duplicate);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            builder.DefineModuleAsync("orders", "3", _ => { }));
        Assert.Contains("version '2'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("version '3'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportModule_applies_prefixes_and_per_resource_aliases_without_changing_typed_lookup_names()
    {
        using var repository = await TestRepository.CreateAsync();
        var builder = CreateBuilder(repository.Path);
        await builder.DefineModuleAsync("portable", "1", definition =>
        {
            definition.AddContainer("api", "nginx");
            definition.AddContainer("cache", "redis");
        });
        var options = new ModuleImportOptions { ResourcePrefix = "shop-" };
        options.ResourceAliases["cache"] = "shared-cache";

        var module = await builder.ImportModuleAsync("portable", options);

        Assert.Equal(["shop-api", "shared-cache"], builder.Resources.Select(resource => resource.Name));
        Assert.Equal("shop-api", module.GetResource<ContainerResource>("api").Resource.Name);
        Assert.Equal("shared-cache", module.GetResource<ContainerResource>("cache").Resource.Name);
        var annotation = Assert.Single(
            module.GetResource<ContainerResource>("cache").Resource.Annotations
                .OfType<DistributedApplicationModuleResourceAnnotation>());
        Assert.Equal("cache", annotation.ResourceName);

        var conflictingOptions = new ModuleImportOptions { ResourcePrefix = "other-" };
        var conflicting = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            builder.ImportModuleAsync("portable", conflictingOptions));
        Assert.Contains("already materialized", conflicting.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportModule_reports_unknown_duplicate_and_existing_alias_collisions()
    {
        using var repository = await TestRepository.CreateAsync();

        var unknownBuilder = CreateBuilder(repository.Path);
        await unknownBuilder.DefineModuleAsync("unknown", "1", definition =>
            definition.AddContainer("cache", "redis"));
        var unknownOptions = new ModuleImportOptions();
        unknownOptions.ResourceAliases["typo"] = "cache";
        var unknown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            unknownBuilder.ImportModuleAsync("unknown", unknownOptions));
        Assert.Contains("unknown resource 'typo'", unknown.Message, StringComparison.Ordinal);

        var duplicateBuilder = CreateBuilder(repository.Path);
        await duplicateBuilder.DefineModuleAsync("duplicate", "1", definition =>
        {
            definition.AddContainer("api", "nginx");
            definition.AddContainer("cache", "redis");
        });
        var duplicateOptions = new ModuleImportOptions();
        duplicateOptions.ResourceAliases["api"] = "shared";
        duplicateOptions.ResourceAliases["cache"] = "SHARED";
        var duplicate = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            duplicateBuilder.ImportModuleAsync("duplicate", duplicateOptions));
        Assert.Contains("both 'api' and 'cache'", duplicate.Message, StringComparison.Ordinal);

        var existingBuilder = CreateBuilder(repository.Path);
        existingBuilder.AddContainer("shop-cache", "redis");
        await existingBuilder.DefineModuleAsync("existing", "1", definition =>
            definition.AddContainer("cache", "redis"));
        var existing = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            existingBuilder.ImportModuleAsync("existing", new ModuleImportOptions { ResourcePrefix = "shop-" }));
        Assert.Contains("resource 'shop-cache' already exists", existing.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportModule_rejects_an_empty_module()
    {
        using var repository = await TestRepository.CreateAsync();
        var builder = CreateBuilder(repository.Path);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            builder.ExportModuleAsync("empty", _ => { }));

        Assert.Contains("does not contain any resources", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportModule_requires_every_project_to_be_exported_as_a_container()
    {
        using var repository = await TestRepository.CreateAsync();
        var builder = CreateBuilder(repository.Path);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            builder.ExportModuleAsync("orders", module =>
                module.AddProject("orders-api", repository.ProjectPath)));

        Assert.Contains("orders-api", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportModule_rejects_case_insensitive_duplicate_resource_names()
    {
        using var repository = await TestRepository.CreateAsync();
        var builder = CreateBuilder(repository.Path);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            builder.ExportModuleAsync("orders", module =>
            {
                module.AddProject("orders-api", repository.ProjectPath)
                    .ExportAsContainer("orders-api", "dotnet", ["publish"]);
                module.AddContainer("ORDERS-API", "nginx");
            }));

        Assert.Contains("already contains a resource", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportModule_rejects_projects_from_multiple_source_trees()
    {
        using var appHost = TemporaryDirectory.Create();
        using var firstSource = await TestRepository.CreateAsync();
        using var secondSource = await TestRepository.CreateAsync();
        var builder = CreateBuilder(appHost.Path);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            builder.ExportModuleAsync("orders", module =>
            {
                module.AddProject("orders-api", firstSource.ProjectPath)
                    .ExportAsContainer("orders-api", "dotnet", ["publish"]);
                module.AddProject("orders-worker", secondSource.ProjectPath)
                    .ExportAsContainer("orders-worker", "dotnet", ["publish"]);
            }));

        Assert.Contains("same Git repository or source tree", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Add_materializes_container_and_exact_publish_installer_once()
    {
        using var repository = await TestRepository.CreateAsync();
        var builder = CreateBuilder(repository.Path);
        var module = await ExportModuleAsync(builder, repository.ProjectPath);

        await builder.AddAsync(module);
        await builder.AddAsync(module);

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
    public async Task Concurrent_adds_materialize_the_module_once()
    {
        using var repository = await TestRepository.CreateAsync();
        var builder = CreateBuilder(repository.Path);
        var module = await ExportModuleAsync(builder, repository.ProjectPath);

        await Task.WhenAll(
            builder.AddAsync(module),
            builder.AddAsync(module));

        Assert.Single(builder.Resources.OfType<ContainerResource>());
        Assert.Single(builder.Resources.OfType<ModuleRepositoryInstallerResource>());
    }

    [Fact]
    public async Task ImportModule_uses_configured_repository_base_path_and_marks_installer_for_updates()
    {
        using var repository = await TestRepository.CreateAsync();
        using var imports = TemporaryDirectory.Create();
        var builder = CreateBuilder(repository.Path);
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:RepositoryBasePath"] = imports.Path;

        await ExportModuleAsync(builder, repository.ProjectPath);
        var imported = await builder.ImportModuleAsync("orders");
        await builder.ImportModuleAsync("orders");

        Assert.Equal("orders", imported.Name);
        Assert.Empty(builder.Resources.OfType<ParameterResource>());

        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var annotation = Assert.Single(container.Annotations.OfType<DistributedApplicationModuleResourceAnnotation>());
        var installer = Assert.Single(builder.Resources.OfType<ModuleRepositoryInstallerResource>());

        Assert.True(annotation.Imported);
        Assert.Equal(
            Path.Combine(imports.Path, "acme-orders-orders"),
            annotation.RepositoryPath);
        Assert.True(installer.UpdatesRepository);
        Assert.Equal("https://example.test/acme/orders.git", installer.Repository);
    }

    [Fact]
    public async Task ImportModule_requires_an_exported_definition()
    {
        using var repository = await TestRepository.CreateAsync();
        var builder = CreateBuilder(repository.Path);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => builder.ImportModuleAsync("missing"));

        Assert.Contains("has not been exported", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportModule_with_projects_adds_an_interaction_backed_repository_parameter()
    {
        using var repository = await TestRepository.CreateAsync();
        var builder = CreateBuilder(repository.Path);
        await builder.ExportModuleAsync("orders", module =>
            module.AddProject("orders-api", repository.ProjectPath)
                .ExportAsContainer("orders-api", "dotnet", ["publish"]));

        await builder.ImportModuleAsync("orders");

        var parameter = Assert.Single(builder.Resources.OfType<ParameterResource>());
        Assert.Equal(DistributedApplicationModuleExtensions.GetRepositoryParameterName("orders"), parameter.Name);
        Assert.Null(parameter.Default);
#pragma warning disable ASPIREINTERACTION001
        var inputGenerator = Assert.Single(parameter.Annotations.OfType<InputGeneratorAnnotation>());
        var input = inputGenerator.InputGenerator(parameter);
        Assert.Equal(InputType.Text, input.InputType);
#pragma warning restore ASPIREINTERACTION001
        Assert.True(input.Required);
        Assert.Contains("repository", input.Placeholder, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ImportModule_with_generic_repository_content_requires_a_model_time_repository()
    {
        using var repository = await TestRepository.CreateAsync();
        var builder = CreateBuilder(repository.Path);
        await builder.ExportModuleAsync("content", module =>
        {
            module.RequiresRepository();
            module.AddResource<TestResource>("clock", context =>
                context.ApplicationBuilder.AddResource(new TestResource(context.ResourceName)));
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => builder.ImportModuleAsync("content"));

        Assert.Contains("application model is constructed", exception.Message, StringComparison.Ordinal);
        Assert.Contains(
            DistributedApplicationModuleExtensions.GetRepositoryConfigurationKey("content"),
            exception.Message,
            StringComparison.Ordinal);
        Assert.Empty(builder.Resources.OfType<ParameterResource>());
    }

    [Fact]
    public async Task ImportModule_prepares_configured_repository_before_running_generic_factories()
    {
        using var repository = await TestRepository.CreateAsync(initializeGit: true);
        using var imports = TemporaryDirectory.Create();
        using var appHost = TemporaryDirectory.Create();
        var builder = CreateBuilder(appHost.Path);
        var repositoryKey = DistributedApplicationModuleExtensions.GetRepositoryConfigurationKey("content");
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:RepositoryBasePath"] = imports.Path;
        builder.Configuration[repositoryKey] = repository.Path;
        string? materializedRepositoryPath = null;
        await builder.ExportModuleAsync("content", module =>
        {
            module.RequiresRepository();
            module.AddResource<TestResource>("clock", context =>
            {
                materializedRepositoryPath = context.RepositoryPath;
                Assert.True(File.Exists(Path.Combine(context.RepositoryPath, "Orders.Api.csproj")));
                return context.ApplicationBuilder.AddResource(new TestResource(context.ResourceName));
            });
        });

        await builder.ImportModuleAsync("content");

        var expectedPath = Path.Combine(
            imports.Path,
            ModuleRepositoryIdentity.GetCanonicalName(repository.Path, "content", repository.Path));
        Assert.Equal(expectedPath, materializedRepositoryPath);
        Assert.True(await RepositoryInspector.IsGitRepositoryAsync(
            expectedPath,
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Imported_local_module_root_in_the_apphost_worktree_is_reused_without_origin_validation()
    {
        using var workspace = TemporaryDirectory.Create();
        var appHostDirectory = Path.Combine(workspace.Path, "AppHostB");
        var moduleRoot = Path.Combine(workspace.Path, "modules", "AppHostA");
        var projectPath = Path.Combine(moduleRoot, "Api", "Api.csproj");
        Directory.CreateDirectory(appHostDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(projectPath)!);
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        await TestRepository.RunGitAsync(workspace.Path, "init");
        await TestRepository.RunGitAsync(workspace.Path, "config", "user.name", "Test User");
        await TestRepository.RunGitAsync(workspace.Path, "config", "user.email", "test@example.test");
        await TestRepository.RunGitAsync(workspace.Path, "remote", "add", "origin", "https://github.com/acme/monorepo.git");
        await TestRepository.RunGitAsync(workspace.Path, "add", ".");
        await TestRepository.RunGitAsync(workspace.Path, "commit", "-m", "initial");
        var builder = CreateBuilder(appHostDirectory);
        await builder.ExportModuleAsync("apphost-a", module =>
        {
            module.WithRepository(moduleRoot);
            module.AddProject("api", projectPath)
                .ExportAsContainer($"module-test-apphost-a-{Guid.NewGuid():N}", "dotnet", ["publish"]);
        });

        await builder.ImportModuleAsync("apphost-a");

        var annotation = Assert.Single(
            Assert.Single(builder.Resources.OfType<ContainerResource>())
                .Annotations.OfType<DistributedApplicationModuleResourceAnnotation>());
        Assert.Equal(moduleRoot, annotation.RepositoryPath);
    }

    [Fact]
    public async Task Repository_and_module_slugs_keep_managed_parameters_and_checkouts_distinct()
    {
        using var repository = await TestRepository.CreateAsync();
        using var imports = TemporaryDirectory.Create();
        var builder = CreateBuilder(repository.Path);
        var remote = "https://github.com/acme/catalog.git";
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:RepositoryBasePath"] = imports.Path;
        foreach (var (moduleName, resourceName) in new[]
        {
            ("sales.orders", "sales-dot-api"),
            ("sales-orders", "sales-dash-api")
        })
        {
            builder.Configuration[DistributedApplicationModuleExtensions.GetRepositoryConfigurationKey(moduleName)] = remote;
            await builder.ExportModuleAsync(moduleName, module =>
                module.AddProject(resourceName, repository.ProjectPath)
                    .ExportAsContainer(resourceName, "dotnet", ["publish"]));
            await builder.ImportModuleAsync(moduleName);
        }

        var parameters = builder.Resources.OfType<ParameterResource>().Select(resource => resource.Name).ToArray();
        Assert.Equal(2, parameters.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(
            DistributedApplicationModuleExtensions.GetRepositoryParameterName(remote, "sales.orders"),
            parameters);
        Assert.Contains(
            DistributedApplicationModuleExtensions.GetRepositoryParameterName(remote, "sales-orders"),
            parameters);

        var repositoryPaths = builder.Resources
            .OfType<ContainerResource>()
            .SelectMany(resource => resource.Annotations.OfType<DistributedApplicationModuleResourceAnnotation>())
            .Select(annotation => annotation.RepositoryPath)
            .ToArray();
        Assert.Equal(2, repositoryPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task Add_rejects_a_module_defined_on_another_application_builder()
    {
        using var repository = await TestRepository.CreateAsync();
        var definitionBuilder = CreateBuilder(repository.Path);
        var materializationBuilder = CreateBuilder(repository.Path);
        var module = await definitionBuilder.ExportModuleAsync("portable", definition =>
            definition.AddContainer("cache", "redis"));

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            materializationBuilder.AddAsync(module));

        Assert.Contains("different distributed application builder", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Configuration_can_run_an_exported_project_directly_for_debugging()
    {
        using var repository = await TestRepository.CreateAsync();
        var builder = CreateBuilder(repository.Path);
        var projectConfigured = false;
        builder.Configuration[
            $"{ModularAppHostsOptions.ConfigurationSectionName}:Modules:orders:Projects:orders-api:ProjectMode"] =
            nameof(ModuleProjectMode.Project);

        var module = await builder.ExportModuleAsync("orders", definition =>
            definition.AddProject("orders-api", repository.ProjectPath)
                .ConfigureProject(project =>
                {
                    projectConfigured = true;
                    project.WithExplicitStart();
                })
                .ExportAsContainer("orders-api", "dotnet", ["publish"]));

        await builder.AddAsync(module);

        var project = Assert.Single(builder.Resources.OfType<ProjectResource>());
        Assert.Equal("orders-api", project.Name);
        Assert.True(projectConfigured);
        Assert.Empty(builder.Resources.OfType<ContainerResource>());
        Assert.Empty(builder.Resources.OfType<ModuleRepositoryInstallerResource>());
        Assert.Same(project, module.GetResource<IResourceWithEndpoints>("orders-api").Resource);

        var descriptor = Assert.Single(
            builder.Services,
            service => service.ServiceType == typeof(IOptions<ModularAppHostsOptions>));
        var boundOptions = Assert.IsAssignableFrom<IOptions<ModularAppHostsOptions>>(
            descriptor.ImplementationInstance);
        Assert.Equal(
            ModuleProjectMode.Project,
            boundOptions.Value.Modules["orders"].Projects["orders-api"].ProjectMode);
    }

    [Fact]
    public async Task Configuration_can_run_an_imported_project_from_an_existing_managed_checkout()
    {
        using var repository = await TestRepository.CreateAsync();
        using var imports = TemporaryDirectory.Create();
        var checkout = Path.Combine(imports.Path, "acme-orders-orders");
        Directory.CreateDirectory(checkout);
        File.Copy(repository.ProjectPath, Path.Combine(checkout, Path.GetFileName(repository.ProjectPath)));
        var builder = CreateBuilder(repository.Path);
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:RepositoryBasePath"] = imports.Path;
        builder.Configuration[
            $"{ModularAppHostsOptions.ConfigurationSectionName}:Modules:orders:Projects:orders-api:ProjectMode"] =
            nameof(ModuleProjectMode.Project);
        await ExportModuleAsync(builder, repository.ProjectPath);

        var module = await builder.ImportModuleAsync("orders");

        var project = Assert.Single(builder.Resources.OfType<ProjectResource>());
        var annotation = Assert.Single(project.Annotations.OfType<DistributedApplicationModuleResourceAnnotation>());
        Assert.Equal(checkout, annotation.RepositoryPath);
        Assert.True(annotation.Imported);
        Assert.Empty(builder.Resources.OfType<ContainerResource>());
        Assert.Empty(builder.Resources.OfType<ModuleRepositoryInstallerResource>());
        Assert.Same(project, module.GetResource<IResourceWithEndpoints>("orders-api").Resource);
    }

    [Fact]
    public async Task Configuration_overrides_project_image_publishing_and_repository_policy()
    {
        using var repository = await TestRepository.CreateAsync();
        using var imports = TemporaryDirectory.Create();
        var builder = CreateBuilder(repository.Path);
        var section = $"{ModularAppHostsOptions.ConfigurationSectionName}:Modules:orders";
        var repositoryConfigurationKey =
            DistributedApplicationModuleExtensions.GetRepositoryConfigurationKey("orders");
        Assert.Equal($"{section}:Repository", repositoryConfigurationKey);
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:RepositoryBasePath"] = imports.Path;
        builder.Configuration[repositoryConfigurationKey] = "https://example.test/configured/orders.git";
        builder.Configuration[$"{section}:UpdateRepository"] = "false";
        builder.Configuration[$"{section}:Projects:orders-api:ImageName"] = "configured/orders-api";
        builder.Configuration[$"{section}:Projects:orders-api:ImageTag"] = "debug";
        builder.Configuration[$"{section}:Projects:orders-api:PublishCommand"] = "configured-publisher";
        builder.Configuration[$"{section}:Projects:orders-api:PublishArguments:0"] = "publish";
        builder.Configuration[$"{section}:Projects:orders-api:PublishArguments:1"] =
            ModuleContainerExportOptions.ImageReferencePlaceholder;
        builder.Configuration[$"{section}:Projects:orders-api:PublishWorkingDirectory"] = ".";
        builder.Configuration[$"{section}:Projects:orders-api:ImagePullPolicy"] = nameof(ImagePullPolicy.Missing);

        await builder.ExportModuleAsync("orders", definition =>
            definition.AddProject("orders-api", repository.ProjectPath)
                .ExportAsContainer("declared/orders-api", "dotnet", ["publish"]));
        var imported = await builder.ImportModuleAsync("orders");

        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var image = Assert.Single(container.Annotations.OfType<ContainerImageAnnotation>());
        var pullPolicy = Assert.Single(container.Annotations.OfType<ContainerImagePullPolicyAnnotation>());
        var installer = Assert.Single(builder.Resources.OfType<ModuleRepositoryInstallerResource>());
        var repositoryParameter = Assert.Single(builder.Resources.OfType<ParameterResource>());
        Assert.Equal(
            DistributedApplicationModuleExtensions.GetRepositoryParameterName(
                "https://example.test/configured/orders.git",
                "orders"),
            repositoryParameter.Name);
        Assert.Equal(
            "https://example.test/configured/orders.git",
            await repositoryParameter.GetValueAsync(TestContext.Current.CancellationToken));
        Assert.Equal("configured/orders-api", image.Image);
        Assert.Equal("debug", image.Tag);
        Assert.Equal(ImagePullPolicy.Missing, pullPolicy.ImagePullPolicy);
        Assert.Equal("configured-publisher", installer.PublishCommand);
        Assert.Equal(["publish", "configured/orders-api:debug"], installer.PublishArguments);
        Assert.Equal(Path.Combine(imports.Path, "configured-orders-orders"), installer.WorkingDirectory);
        Assert.Equal("https://example.test/configured/orders.git", installer.Repository);
        Assert.False(installer.UpdatesRepository);
        Assert.Same(container, imported.GetResource<IResourceWithEndpoints>("orders-api").Resource);
    }

    [Fact]
    public async Task Programmatic_repository_option_is_used_directly_without_a_parameter()
    {
        using var repository = await TestRepository.CreateAsync();
        var builder = CreateBuilder(repository.Path);
        var imageName = $"module-test-orders-api-{Guid.NewGuid():N}";
        builder.ConfigureModularAppHosts(options =>
            options.Modules["orders"] = new DistributedApplicationModuleOptions
            {
                Repository = "https://example.test/programmatic/orders.git"
            });
        await builder.ExportModuleAsync("orders", definition =>
            definition.AddProject("orders-api", repository.ProjectPath)
                .ExportAsContainer(imageName, "dotnet", ["publish"]));

        await builder.ImportModuleAsync("orders");

        Assert.Empty(builder.Resources.OfType<ParameterResource>());
        var installer = Assert.Single(builder.Resources.OfType<ModuleRepositoryInstallerResource>());
        Assert.Equal("https://example.test/programmatic/orders.git", installer.Repository);
    }

    [Fact]
    public async Task Configuration_can_disable_publishing_and_override_a_container_image()
    {
        using var repository = await TestRepository.CreateAsync();
        var builder = CreateBuilder(repository.Path);
        var section = $"{ModularAppHostsOptions.ConfigurationSectionName}:Modules:static:Containers:static-site";
        builder.Configuration[$"{section}:ImageName"] = "registry.example.test/static-site";
        builder.Configuration[$"{section}:ImageTag"] = "preview";
        builder.Configuration[$"{section}:PublishImage"] = "false";
        builder.Configuration[$"{section}:ImagePullPolicy"] = nameof(ImagePullPolicy.Always);

        var module = await builder.ExportModuleAsync("static", definition =>
            definition.AddContainer("static-site", "declared/static-site", "dev")
                .WithImagePublishCommand(new ModuleContainerExportOptions(
                    "declared/static-site",
                    "dotnet",
                    "publish")
                {
                    ImageTag = "dev"
                })
                .Configure(container => container.WithImagePullPolicy(ImagePullPolicy.Missing)));

        await builder.AddAsync(module);

        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var image = Assert.Single(container.Annotations.OfType<ContainerImageAnnotation>());
        var pullPolicy = Assert.Single(container.Annotations.OfType<ContainerImagePullPolicyAnnotation>());
        Assert.Equal("registry.example.test/static-site", image.Image);
        Assert.Equal("preview", image.Tag);
        Assert.Equal(ImagePullPolicy.Always, pullPolicy.ImagePullPolicy);
        Assert.Empty(builder.Resources.OfType<ModuleRepositoryInstallerResource>());
    }

    [Fact]
    public async Task Configuration_cannot_introduce_an_undeclared_container_publisher()
    {
        using var repository = await TestRepository.CreateAsync();
        var builder = CreateBuilder(repository.Path);
        var section = $"{ModularAppHostsOptions.ConfigurationSectionName}:Modules:static:Containers:static-site";
        builder.Configuration[$"{section}:PublishImage"] = "true";
        builder.Configuration[$"{section}:PublishCommand"] = "dotnet";

        var module = await builder.ExportModuleAsync("static", definition =>
            definition.AddContainer("static-site", "nginx", "alpine"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => builder.AddAsync(module));

        Assert.Contains("does not call WithImagePublishCommand", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfigureModularAppHosts_applies_programmatic_options_before_materialization()
    {
        using var repository = await TestRepository.CreateAsync();
        var builder = CreateBuilder(repository.Path);
        builder.ConfigureModularAppHosts(options => options.PublishImages = false);
        var module = await ExportModuleAsync(builder, repository.ProjectPath);

        await builder.AddAsync(module);

        Assert.Single(builder.Resources.OfType<ContainerResource>());
        Assert.Empty(builder.Resources.OfType<ModuleRepositoryInstallerResource>());
        var configured = Assert.Single(
            builder.Services,
            service => service.ServiceType == typeof(IOptions<ModularAppHostsOptions>));
        Assert.False(
            Assert.IsAssignableFrom<IOptions<ModularAppHostsOptions>>(configured.ImplementationInstance)
                .Value
                .PublishImages);
    }

    [Fact]
    public async Task Configuration_changes_after_definition_are_applied_before_materialization()
    {
        using var repository = await TestRepository.CreateAsync();
        var builder = CreateBuilder(repository.Path);
        var module = await ExportModuleAsync(builder, repository.ProjectPath);
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:PublishImages"] = "false";

        await builder.AddAsync(module);

        Assert.Single(builder.Resources.OfType<ContainerResource>());
        Assert.Empty(builder.Resources.OfType<ModuleRepositoryInstallerResource>());
        var configured = Assert.Single(
            builder.Services,
            service => service.ServiceType == typeof(IOptions<ModularAppHostsOptions>));
        Assert.False(
            Assert.IsAssignableFrom<IOptions<ModularAppHostsOptions>>(configured.ImplementationInstance)
                .Value
                .PublishImages);
    }

    [Fact]
    public async Task Fluent_options_keep_configuration_added_after_the_first_call_visible()
    {
        using var repository = await TestRepository.CreateAsync();
        using var imports = TemporaryDirectory.Create();
        var builder = CreateBuilder(repository.Path);
        builder.BuildModuleImages();
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:RepositoryBasePath"] = imports.Path;
        var module = await ExportModuleAsync(builder, repository.ProjectPath);

        await builder.ImportModuleAsync(module.Name);

        var annotation = Assert.Single(
            Assert.Single(builder.Resources.OfType<ContainerResource>())
                .Annotations.OfType<DistributedApplicationModuleResourceAnnotation>());
        Assert.Equal(Path.Combine(imports.Path, "acme-orders-orders"), annotation.RepositoryPath);
    }

    [Theory]
    [InlineData("ProjectMode", "42")]
    [InlineData("Modules:orders:ProjectMode", "42")]
    [InlineData("Modules:orders:Projects:orders-api:ImagePullPolicy", "42")]
    public async Task Configuration_rejects_unsupported_enum_values(string relativeKey, string value)
    {
        using var repository = await TestRepository.CreateAsync();
        var builder = CreateBuilder(repository.Path);
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:{relativeKey}"] = value;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ExportModuleAsync(builder, repository.ProjectPath));

        Assert.Contains("unsupported value", exception.Message, StringComparison.Ordinal);
        Assert.Contains(relativeKey.Split(':')[^1], exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Repository_inspection_uses_the_configured_git_executable_and_fails_closed()
    {
        using var repository = await TestRepository.CreateAsync(initializeGit: true);
        var builder = CreateBuilder(repository.Path);
        var missingGit = Path.Combine(repository.Path, "missing-git");
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:GitExecutablePath"] = missingGit;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ExportModuleAsync(builder, repository.ProjectPath));

        Assert.Contains(missingGit, exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(ModularAppHostsOptions.GitExecutablePath), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetResource_reports_unmaterialized_missing_and_incompatible_resources()
    {
        using var repository = await TestRepository.CreateAsync();
        var builder = CreateBuilder(repository.Path);
        var module = await ExportModuleAsync(builder, repository.ProjectPath);

        var unmaterialized = Assert.Throws<InvalidOperationException>(() =>
            module.GetResource<ContainerResource>("orders-api"));
        Assert.Contains("has not been materialized", unmaterialized.Message, StringComparison.Ordinal);

        await builder.AddAsync(module);

        Assert.Throws<KeyNotFoundException>(() =>
            module.GetResource<ContainerResource>("missing"));
        var incompatible = Assert.Throws<InvalidOperationException>(() =>
            module.GetResource<ParameterResource>("orders-api"));
        Assert.Contains(nameof(ParameterResource), incompatible.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Materialize_rejects_existing_resource_and_installer_name_collisions()
    {
        using var repository = await TestRepository.CreateAsync();

        var resourceBuilder = CreateBuilder(repository.Path);
        var resourceModule = await ExportModuleAsync(resourceBuilder, repository.ProjectPath);
        resourceBuilder.AddContainer("orders-api", "busybox");
        var resourceCollision = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resourceBuilder.AddAsync(resourceModule));
        Assert.Contains("orders-api", resourceCollision.Message, StringComparison.Ordinal);

        var installerBuilder = CreateBuilder(repository.Path);
        var installerModule = await ExportModuleAsync(installerBuilder, repository.ProjectPath);
        installerBuilder.AddContainer("orders-api-installer", "busybox");
        var installerCollision = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            installerBuilder.AddAsync(installerModule));
        Assert.Contains("orders-api-installer", installerCollision.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Module_exports_existing_container_and_exposes_materialized_resource_builders()
    {
        using var repository = await TestRepository.CreateAsync();
        var builder = CreateBuilder(repository.Path);

        var module = await builder.ExportModuleAsync("mixed", definition =>
        {
            definition.AddProject("mixed-api", repository.ProjectPath)
                .ExportAsContainer("mixed-api", "dotnet", ["publish"]);
            definition.AddContainer("mixed-static", "nginx", "alpine")
                .Configure(container => container.WithHttpEndpoint(targetPort: 80, name: "http"));
        });

        await builder.AddAsync(module);

        Assert.Single(module.Containers);
        Assert.Equal(2, builder.Resources.OfType<ContainerResource>().Count());
        Assert.Equal("mixed-api", module.GetResource<ContainerResource>("mixed-api").Resource.Name);

        var staticContainer = module.GetResource<ContainerResource>("mixed-static");
        Assert.Equal("mixed-static", staticContainer.Resource.Name);
        Assert.Single(staticContainer.Resource.Annotations.OfType<EndpointAnnotation>());
    }

    [Fact]
    public async Task Project_working_directory_must_remain_inside_the_repository()
    {
        using var repository = await TestRepository.CreateAsync();
        var builder = CreateBuilder(repository.Path);
        var module = await builder.ExportModuleAsync("orders", definition =>
            definition.AddProject("orders-api", repository.ProjectPath)
                .ExportAsContainer(new ModuleContainerExportOptions(
                    "orders-api",
                    "dotnet",
                    "publish")
                {
                    WorkingDirectory = ".."
                }));

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => builder.AddAsync(module));

        Assert.Equal(nameof(ModuleContainerExportOptions.WorkingDirectory), exception.ParamName);
    }

    [Fact]
    public async Task Publish_mode_does_not_add_run_only_installer_resources()
    {
        using var repository = await TestRepository.CreateAsync();
        var builder = CreateBuilder(repository.Path, "--publisher", "manifest");
        builder.Configuration[
            $"{ModularAppHostsOptions.ConfigurationSectionName}:Modules:orders:Projects:orders-api:ProjectMode"] =
            nameof(ModuleProjectMode.Project);
        var module = await ExportModuleAsync(builder, repository.ProjectPath);

        await builder.AddAsync(module);

        Assert.True(builder.ExecutionContext.IsPublishMode);
        Assert.Single(builder.Resources.OfType<ContainerResource>());
        Assert.Empty(builder.Resources.OfType<ProjectResource>());
        Assert.Empty(builder.Resources.OfType<ModuleRepositoryInstallerResource>());
    }

    [Fact]
    public async Task ExportAsContainer_copies_mutable_options()
    {
        using var repository = await TestRepository.CreateAsync();
        var builder = CreateBuilder(repository.Path);
        var publishArguments = new[] { "publish", "Orders.Api.csproj" };
        var options = new ModuleContainerExportOptions("orders-api", "dotnet", publishArguments)
        {
            ImageTag = "dev"
        };
        var module = await builder.ExportModuleAsync("orders", definition =>
        {
            definition.WithRepository(repository.Path);
            definition.AddProject("orders-api", repository.ProjectPath)
                .ExportAsContainer(options);
        });

        publishArguments[0] = "mutated";
        options.ImageTag = "changed";
        options.WorkingDirectory = "missing";

        await builder.AddAsync(module);

        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var image = Assert.Single(container.Annotations.OfType<ContainerImageAnnotation>());
        var installer = Assert.Single(builder.Resources.OfType<ModuleRepositoryInstallerResource>());
        Assert.Equal("dev", image.Tag);
        Assert.Equal(["publish", "Orders.Api.csproj"], installer.PublishArguments);
        Assert.Equal(repository.Path, installer.WorkingDirectory);
    }

    [Fact]
    public async Task Container_image_publish_command_uses_a_dirty_tag_and_always_adds_an_installer()
    {
        using var repository = await TestRepository.CreateAsync(initializeGit: true);
        File.AppendAllText(repository.ProjectPath, Environment.NewLine + "<!-- dirty -->");
        var builder = CreateBuilder(repository.Path);

        var module = await builder.ExportModuleAsync("generated", definition =>
        {
            definition.WithRepository(repository.Path);
            definition.AddContainer("generated-static", "modular-static", "dev")
                .WithImagePublishCommand(new ModuleContainerExportOptions(
                    "modular-static",
                    "podman",
                    "build",
                    "--tag",
                    "modular-static:dev",
                    ".")
                {
                    ImageTag = "dev"
                });
        });

        await builder.AddAsync(module);

        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var image = Assert.Single(container.Annotations.OfType<ContainerImageAnnotation>());
        var installer = Assert.Single(builder.Resources.OfType<ModuleRepositoryInstallerResource>());
        var wait = Assert.Single(container.Annotations.OfType<WaitAnnotation>());

        Assert.Equal("modular-static", image.Image);
        Assert.Equal("dev-dirty", image.Tag);
        Assert.Equal("modular-static:dev-dirty", installer.ImageReference);
        Assert.True(installer.RepositoryDirty);
        Assert.Equal(
            ["build", "--tag", "modular-static:dev-dirty", "."],
            installer.PublishArguments);
        Assert.Equal(repository.Path, installer.WorkingDirectory);
        Assert.Same(installer, wait.Resource);
    }

    [Fact]
    public async Task Container_image_publish_command_must_match_the_declared_container_image()
    {
        using var repository = await TestRepository.CreateAsync();
        var builder = CreateBuilder(repository.Path);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            builder.ExportModuleAsync("invalid", definition =>
                definition.AddContainer("static", "declared", "dev")
                    .WithImagePublishCommand(new ModuleContainerExportOptions(
                        "published",
                        "podman",
                        "build")
                    {
                        ImageTag = "dev"
                    })));

        Assert.Contains("published:dev", exception.Message, StringComparison.Ordinal);
        Assert.Contains("declared:dev", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Container_image_publish_working_directory_must_remain_inside_the_repository()
    {
        using var repository = await TestRepository.CreateAsync(initializeGit: true);
        File.AppendAllText(repository.ProjectPath, Environment.NewLine + "<!-- dirty -->");
        var builder = CreateBuilder(repository.Path);
        var module = await builder.ExportModuleAsync("invalid", definition =>
        {
            definition.WithRepository(repository.Path);
            definition.AddContainer("static", "modular-static", "dev")
                .WithImagePublishCommand(new ModuleContainerExportOptions(
                    "modular-static",
                    "podman",
                    "build")
                {
                    ImageTag = "dev",
                    WorkingDirectory = ".."
                });
        });

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => builder.AddAsync(module));

        Assert.Equal(nameof(ModuleContainerExportOptions.WorkingDirectory), exception.ParamName);
    }

    [Fact]
    public async Task Container_image_publish_command_can_only_be_configured_once()
    {
        using var repository = await TestRepository.CreateAsync();
        var builder = CreateBuilder(repository.Path);
        var options = new ModuleContainerExportOptions("modular-static", "podman", "build")
        {
            ImageTag = "dev"
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            builder.ExportModuleAsync("invalid", definition =>
            {
                var container = definition.AddContainer("static", "modular-static", "dev");
                container.WithImagePublishCommand(options);
                container.WithImagePublishCommand(options);
            }));

        Assert.Contains("static", exception.Message, StringComparison.Ordinal);
        Assert.Contains("already has an image publish command", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithImagePublishCommand_copies_mutable_options()
    {
        using var repository = await TestRepository.CreateAsync(initializeGit: true);
        File.AppendAllText(repository.ProjectPath, Environment.NewLine + "<!-- dirty -->");
        var builder = CreateBuilder(repository.Path);
        var publishArguments = new[]
        {
            "build",
            "--tag",
            ModuleContainerExportOptions.ImageReferencePlaceholder,
            "."
        };
        var options = new ModuleContainerExportOptions("modular-static", "podman", publishArguments)
        {
            ImageTag = "dev"
        };
        var module = await builder.ExportModuleAsync("static", definition =>
        {
            definition.WithRepository(repository.Path);
            definition.AddContainer("static", "modular-static", "dev")
                .WithImagePublishCommand(options);
        });

        publishArguments[0] = "mutated";
        options.ImageTag = "changed";
        options.WorkingDirectory = "missing";

        await builder.AddAsync(module);

        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var image = Assert.Single(container.Annotations.OfType<ContainerImageAnnotation>());
        var installer = Assert.Single(builder.Resources.OfType<ModuleRepositoryInstallerResource>());
        Assert.Equal("dev-dirty", image.Tag);
        Assert.Equal(
            ["build", "--tag", "modular-static:dev-dirty", "."],
            installer.PublishArguments);
        Assert.Equal(repository.Path, installer.WorkingDirectory);
    }

    [Fact]
    public async Task Publish_mode_does_not_add_container_image_publish_installers()
    {
        using var repository = await TestRepository.CreateAsync();
        var builder = CreateBuilder(repository.Path, "--publisher", "manifest");
        var module = await builder.ExportModuleAsync("static", definition =>
        {
            definition.WithRepository(repository.Path);
            definition.AddContainer("static", "modular-static", "dev")
                .WithImagePublishCommand(new ModuleContainerExportOptions(
                    "modular-static",
                    "podman",
                    "build")
                {
                    ImageTag = "dev"
                });
        });

        await builder.AddAsync(module);

        Assert.True(builder.ExecutionContext.IsPublishMode);
        Assert.Single(builder.Resources.OfType<ContainerResource>());
        Assert.Empty(builder.Resources.OfType<ModuleRepositoryInstallerResource>());
    }

    [Fact]
    public async Task Clean_image_publish_plan_skips_a_tag_that_already_exists()
    {
        var options = new ModuleContainerExportOptions(
            "modular-static",
            "podman",
            "build",
            "--tag",
            ModuleContainerExportOptions.ImageReferencePlaceholder,
            ".")
        {
            ImageTag = "dev"
        };

        var plan = await ModuleImagePublishPlan.CreateAsync(
            options,
            repositoryDirty: false,
            (imageReference, _) =>
            {
                Assert.Equal("modular-static:dev", imageReference);
                return Task.FromResult(true);
            },
            TestContext.Current.CancellationToken);

        Assert.False(plan.ShouldPublish);
        Assert.False(plan.RepositoryDirty);
        Assert.Equal("dev", plan.ImageTag);
        Assert.Equal("modular-static:dev", plan.ImageReference);
        Assert.Equal(["build", "--tag", "modular-static:dev", "."], plan.PublishArguments);
    }

    [Fact]
    public async Task Clean_image_publish_plan_publishes_a_missing_tag()
    {
        var options = new ModuleContainerExportOptions(
            "modular-static",
            "podman",
            "build",
            "--tag",
            ModuleContainerExportOptions.ImageReferencePlaceholder)
        {
            ImageTag = "dev"
        };
        var inspectedReference = string.Empty;

        var plan = await ModuleImagePublishPlan.CreateAsync(
            options,
            repositoryDirty: false,
            (imageReference, _) =>
            {
                inspectedReference = imageReference;
                return Task.FromResult(false);
            },
            TestContext.Current.CancellationToken);

        Assert.True(plan.ShouldPublish);
        Assert.Equal("modular-static:dev", inspectedReference);
        Assert.Equal("modular-static:dev", plan.ImageReference);
        Assert.Equal(["build", "--tag", "modular-static:dev"], plan.PublishArguments);
    }

    [Fact]
    public async Task Dirty_image_publish_plan_always_publishes_and_resolves_image_placeholders()
    {
        var options = new ModuleContainerExportOptions(
            "modular-static",
            "podman",
            "build",
            "--tag",
            ModuleContainerExportOptions.ImageReferencePlaceholder,
            "--label",
            $"name={ModuleContainerExportOptions.ImageNamePlaceholder}",
            "--label",
            $"tag={ModuleContainerExportOptions.ImageTagPlaceholder}")
        {
            ImageTag = "dev"
        };

        var plan = await ModuleImagePublishPlan.CreateAsync(
            options,
            repositoryDirty: true,
            (_, _) => throw new InvalidOperationException("Dirty images must not be checked before publishing."),
            TestContext.Current.CancellationToken);

        Assert.True(plan.ShouldPublish);
        Assert.True(plan.RepositoryDirty);
        Assert.Equal("dev-dirty", plan.ImageTag);
        Assert.Equal("modular-static:dev-dirty", plan.ImageReference);
        Assert.Equal(
            [
                "build",
                "--tag",
                "modular-static:dev-dirty",
                "--label",
                "name=modular-static",
                "--label",
                "tag=dev-dirty"
            ],
            plan.PublishArguments);
    }

    [Fact]
    public async Task Dirty_image_publish_plan_does_not_duplicate_the_dirty_suffix()
    {
        var options = new ModuleContainerExportOptions(
            "modular-static",
            "podman",
            "build",
            ModuleContainerExportOptions.ImageReferencePlaceholder)
        {
            ImageTag = "dev-dirty"
        };

        var plan = await ModuleImagePublishPlan.CreateAsync(
            options,
            repositoryDirty: true,
            (_, _) => throw new InvalidOperationException("Dirty images must not be inspected."),
            TestContext.Current.CancellationToken);

        Assert.True(plan.ShouldPublish);
        Assert.Equal("dev-dirty", plan.ImageTag);
        Assert.Equal("modular-static:dev-dirty", plan.ImageReference);
        Assert.Equal(["build", "modular-static:dev-dirty"], plan.PublishArguments);
    }

    [Fact]
    public async Task Module_exports_any_custom_resource_type_through_a_lazy_factory()
    {
        using var repository = await TestRepository.CreateAsync();
        var builder = CreateBuilder(repository.Path);
        IDistributedApplicationModuleResourceContext? capturedContext = null;

        var module = await builder.ExportModuleAsync("custom", definition =>
            definition.AddResource<TestResource>("clock", context =>
            {
                capturedContext = context;
                return context.ApplicationBuilder.AddResource(new TestResource(context.ResourceName));
            }));

        var exportedResource = Assert.Single(module.Resources);
        Assert.Equal("clock", exportedResource.Name);
        Assert.Equal(typeof(TestResource), exportedResource.ResourceType);
        Assert.Empty(builder.Resources);

        await builder.AddAsync(module);

        var clock = module.GetResource<TestResource>("clock");
        Assert.Same(clock.Resource, Assert.Single(builder.Resources.OfType<TestResource>()));
        Assert.NotNull(capturedContext);
        Assert.False(capturedContext.Imported);
        Assert.Equal(repository.Path, capturedContext.RepositoryPath);
        var annotation = Assert.Single(
            clock.Resource.Annotations.OfType<DistributedApplicationModuleResourceAnnotation>());
        Assert.Equal("clock", annotation.ResourceName);
    }

    [Fact]
    public async Task Generic_resource_factories_can_resolve_earlier_exports_in_declaration_order()
    {
        using var repository = await TestRepository.CreateAsync();
        var builder = CreateBuilder(repository.Path);
        TestResource? resolvedDependency = null;

        var module = await builder.ExportModuleAsync("ordered", definition =>
        {
            definition.AddResource<TestResource>("first", context =>
                context.ApplicationBuilder.AddResource(new TestResource(context.ResourceName)));
            definition.AddResource<TestResource>("second", context =>
            {
                resolvedDependency = context.GetResource<TestResource>("first").Resource;
                return context.ApplicationBuilder.AddResource(new TestResource(context.ResourceName));
            });
        });

        await builder.AddAsync(module);

        Assert.Same(module.GetResource<TestResource>("first").Resource, resolvedDependency);
        Assert.Equal(
            ["first", "second"],
            builder.Resources.OfType<TestResource>().Select(resource => resource.Name));
    }

    [Fact]
    public async Task ImportModule_allows_repository_independent_generic_resources()
    {
        using var repository = await TestRepository.CreateAsync();
        var builder = CreateBuilder(repository.Path);
        IDistributedApplicationModuleResourceContext? capturedContext = null;

        await builder.ExportModuleAsync("portable", definition =>
            definition.AddResource<TestResource>("portable-resource", context =>
            {
                capturedContext = context;
                return context.ApplicationBuilder.AddResource(new TestResource(context.ResourceName));
            }));

        var imported = await builder.ImportModuleAsync("portable");

        Assert.NotNull(capturedContext);
        Assert.True(capturedContext.Imported);
        Assert.Equal(repository.Path, capturedContext.RepositoryPath);
        Assert.Empty(builder.Resources.OfType<ParameterResource>());
        Assert.Equal(
            "portable-resource",
            imported.GetResource<TestResource>("portable-resource").Resource.Name);
    }

    [Fact]
    public async Task Generic_resource_factory_must_return_the_declared_name()
    {
        using var repository = await TestRepository.CreateAsync();
        var builder = CreateBuilder(repository.Path);
        var module = await builder.ExportModuleAsync("invalid", definition =>
            definition.AddResource<TestResource>("expected", context =>
                context.ApplicationBuilder.AddResource(new TestResource("actual"))));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => builder.AddAsync(module));

        Assert.Contains("expected", exception.Message, StringComparison.Ordinal);
        Assert.Contains("actual", exception.Message, StringComparison.Ordinal);
        Assert.Contains("context.ResourceName", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Generic_resource_factory_must_not_return_null()
    {
        using var repository = await TestRepository.CreateAsync();
        var builder = CreateBuilder(repository.Path);
        var module = await builder.ExportModuleAsync("invalid", definition =>
            definition.AddResource<TestResource>("clock", _ => null!));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => builder.AddAsync(module));

        Assert.Contains("clock", exception.Message, StringComparison.Ordinal);
        Assert.Contains("returned null", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Generic_resource_names_collide_case_insensitively_with_other_exports()
    {
        using var repository = await TestRepository.CreateAsync();
        var builder = CreateBuilder(repository.Path);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            builder.ExportModuleAsync("invalid", definition =>
            {
                definition.AddContainer("shared", "nginx", "alpine");
                definition.AddResource<TestResource>("SHARED", context =>
                    context.ApplicationBuilder.AddResource(new TestResource(context.ResourceName)));
            }));

        Assert.Contains("SHARED", exception.Message, StringComparison.Ordinal);
        Assert.Contains("already contains a resource", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Imported_existing_non_git_source_does_not_attempt_clone_over_it()
    {
        using var repository = await TestRepository.CreateAsync();

        var command = await RepositorySynchronizer.CreateCommandAsync(
            repository.Path,
            repository.Path,
            updateRepository: true,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(command);
    }

    [Fact]
    public async Task Repository_synchronizer_rejects_an_existing_non_git_remote_target()
    {
        using var target = TemporaryDirectory.Create();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RepositorySynchronizer.CreateCommandsAsync(
                target.Path,
                "https://example.test/acme/orders.git",
                updateRepository: true,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("not a Git checkout", exception.Message, StringComparison.Ordinal);
        Assert.Contains(target.Path, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportModule_preserves_project_directory_relative_to_configured_local_repository()
    {
        using var directory = TemporaryDirectory.Create();
        var sourceRoot = Path.Combine(directory.Path, "AppHostA");
        var projectDirectory = Path.Combine(sourceRoot, "Api");
        var consumerDirectory = Path.Combine(directory.Path, "AppHostB");
        Directory.CreateDirectory(projectDirectory);
        Directory.CreateDirectory(consumerDirectory);
        var projectPath = Path.Combine(projectDirectory, "Api.csproj");
        var imageName = $"module-test-api-{Guid.NewGuid():N}";
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        var builder = CreateBuilder(consumerDirectory);
        builder.Configuration[$"Parameters:{DistributedApplicationModuleExtensions.RepositoryBaseLocationParameterName}"] = directory.Path;
        await builder.ExportModuleAsync("AppHostA", module =>
        {
            module.WithRepository(sourceRoot);
            module.AddProject("api", projectPath)
                .ExportAsContainer(imageName, "podman", ["build", "."]);
        });

        await builder.ImportModuleAsync("AppHostA");

        var installer = Assert.Single(builder.Resources.OfType<ModuleRepositoryInstallerResource>());
        Assert.Equal(
            Path.Combine(directory.Path, "apphosta-apphosta", "Api"),
            installer.WorkingDirectory);
    }

    [Fact]
    public async Task WithRepository_after_projects_rebases_repository_relative_paths()
    {
        using var source = TemporaryDirectory.Create();
        using var imports = TemporaryDirectory.Create();
        var moduleRoot = Path.Combine(source.Path, "catalog");
        var projectDirectory = Path.Combine(moduleRoot, "src", "Api");
        var projectPath = Path.Combine(projectDirectory, "Api.csproj");
        Directory.CreateDirectory(projectDirectory);
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var builder = CreateBuilder(source.Path);
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:RepositoryBasePath"] = imports.Path;
        await builder.ExportModuleAsync("catalog", module =>
        {
            module.AddProject("api", projectPath)
                .ExportAsContainer($"module-test-catalog-{Guid.NewGuid():N}", "dotnet", ["publish"]);
            module.WithRepository(moduleRoot);
        });

        await builder.ImportModuleAsync("catalog");

        var installer = Assert.Single(builder.Resources.OfType<ModuleRepositoryInstallerResource>());
        var canonicalName = ModuleRepositoryIdentity.GetCanonicalName(moduleRoot, "catalog", source.Path);
        Assert.Equal(Path.Combine(imports.Path, canonicalName, "src", "Api"), installer.WorkingDirectory);
    }

    [Fact]
    public async Task Repository_synchronizer_clones_missing_import_and_skips_dirty_worktree()
    {
        using var imports = TemporaryDirectory.Create();
        var clonePath = Path.Combine(imports.Path, "orders");

        var clone = await RepositorySynchronizer.CreateCommandAsync(
            clonePath,
            "https://example.test/acme/orders.git",
            updateRepository: true,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(clone);
        Assert.Equal("git", clone.Executable);
        Assert.Equal(
            ["clone", "--recurse-submodules", "--", "https://example.test/acme/orders.git", clonePath],
            clone.Arguments);

        using var repository = await TestRepository.CreateAsync(initializeGit: true);
        File.AppendAllText(repository.ProjectPath, Environment.NewLine + "<!-- dirty -->");

        var dirty = await RepositorySynchronizer.CreateCommandAsync(
            repository.Path,
            repository: null,
            updateRepository: true,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(dirty);
        Assert.True(await RepositoryInspector.IsDirtyAsync(
            repository.Path,
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Repository_synchronizer_pulls_a_clean_worktree_and_requires_a_remote_for_a_missing_clone()
    {
        using var repository = await TestRepository.CreateAsync(initializeGit: true);

        var pull = await RepositorySynchronizer.CreateCommandAsync(
            repository.Path,
            repository: null,
            updateRepository: true,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(pull);
        Assert.Equal("git", pull.Executable);
        Assert.Equal(
            ["-C", repository.Path, "pull", "--ff-only", "--recurse-submodules"],
            pull.Arguments);
        Assert.Null(await RepositorySynchronizer.CreateCommandAsync(
            repository.Path,
            repository: null,
            updateRepository: false,
            cancellationToken: TestContext.Current.CancellationToken));

        using var imports = TemporaryDirectory.Create();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RepositorySynchronizer.CreateCommandAsync(
                Path.Combine(imports.Path, "missing"),
                repository: null,
                updateRepository: true,
                cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("does not define a Git remote", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Repository_synchronizer_executes_success_failure_and_cancellation_paths()
    {
        using var source = await TestRepository.CreateAsync(initializeGit: true);
        using var imports = TemporaryDirectory.Create();
        var clonePath = Path.Combine(imports.Path, "clone");

        await RepositorySynchronizer.SynchronizeAsync(
            clonePath,
            source.Path,
            updateRepository: true,
            TestContext.Current.CancellationToken);

        Assert.True(await RepositoryInspector.IsGitRepositoryAsync(
            clonePath,
            cancellationToken: TestContext.Current.CancellationToken));

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RepositorySynchronizer.SynchronizeAsync(
                Path.Combine(imports.Path, "failed"),
                Path.Combine(imports.Path, "missing.git"),
                updateRepository: true,
                TestContext.Current.CancellationToken));
        Assert.Contains("synchronization failed", failure.Message, StringComparison.Ordinal);

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RepositorySynchronizer.SynchronizeAsync(
                Path.Combine(imports.Path, "cancelled"),
                source.Path,
                updateRepository: true,
                cancellation.Token));
    }

    [Fact]
    public async Task Repository_synchronizer_checks_out_the_configured_revision()
    {
        using var source = await TestRepository.CreateAsync(initializeGit: true);
        var firstCommit = Assert.IsType<string>(await RepositoryInspector.TryResolveCommitAsync(
            source.Path,
            cancellationToken: TestContext.Current.CancellationToken));
        File.AppendAllText(source.ProjectPath, Environment.NewLine + "<!-- second -->");
        await TestRepository.RunGitAsync(source.Path, "add", ".");
        await TestRepository.RunGitAsync(source.Path, "commit", "-m", "second");
        using var imports = TemporaryDirectory.Create();
        var clonePath = Path.Combine(imports.Path, "orders");

        await RepositorySynchronizer.SynchronizeAsync(
            clonePath,
            source.Path,
            updateRepository: true,
            TestContext.Current.CancellationToken,
            firstCommit);

        Assert.Equal(firstCommit, await RepositoryInspector.TryResolveCommitAsync(
            clonePath,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Null(await RepositoryInspector.TryGetBranchAsync(
            clonePath,
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Repository_synchronizer_rejects_a_checkout_with_the_wrong_origin()
    {
        using var repository = await TestRepository.CreateAsync(initializeGit: true);
        await TestRepository.RunGitAsync(
            repository.Path,
            "remote",
            "add",
            "origin",
            "https://github.com/acme/catalog.git");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RepositorySynchronizer.CreateCommandsAsync(
                repository.Path,
                "https://github.com/acme/orders.git",
                updateRepository: true,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("does not match", exception.Message, StringComparison.Ordinal);
        Assert.Contains("catalog", exception.Message, StringComparison.Ordinal);
        Assert.Contains("orders", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Existing_managed_checkout_is_updated_before_commit_tag_is_selected()
    {
        using var source = await TestRepository.CreateAsync(initializeGit: true);
        using var imports = TemporaryDirectory.Create();
        using var appHost = TemporaryDirectory.Create();
        var checkout = Path.Combine(imports.Path, ModuleRepositoryIdentity.GetCanonicalName(source.Path, "orders", source.Path));
        await TestRepository.RunGitAsync(imports.Path, "clone", "--", source.Path, checkout);
        File.AppendAllText(source.ProjectPath, Environment.NewLine + "<!-- current -->");
        await TestRepository.RunGitAsync(source.Path, "add", ".");
        await TestRepository.RunGitAsync(source.Path, "commit", "-m", "current");
        var currentCommit = Assert.IsType<string>(await RepositoryInspector.TryResolveCommitAsync(
            source.Path,
            cancellationToken: TestContext.Current.CancellationToken));
        var builder = CreateBuilder(appHost.Path);
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:RepositoryBasePath"] = imports.Path;
        var module = await builder.ExportModuleAsync("orders", definition =>
        {
            definition.WithRepository(source.Path);
            definition.AddProject("orders-api", source.ProjectPath)
                .ExportAsContainer(
                    $"module-test-orders-{Guid.NewGuid():N}",
                    "dotnet",
                    ["publish"]);
        });

        await builder.ImportModuleAsync(module.Name);

        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var image = Assert.Single(container.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal(currentCommit, await RepositoryInspector.TryResolveCommitAsync(
            checkout,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.EndsWith($"-{currentCommit[..12]}", image.Tag, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Module_registry_deduplicates_repository_work_and_replays_progress_to_resource_logs()
    {
        using var workspace = TemporaryDirectory.Create();
        var builder = CreateBuilder(workspace.Path);
        var resource = builder.AddContainer("orders-api", "example/orders-api").Resource;
        await using var application = builder.Build();
        var registry = new ModuleApplicationRegistry();
        var resourceLoggerService = application.Services.GetRequiredService<ResourceLoggerService>();
        var logger = resourceLoggerService.GetLogger(resource);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocationCount = 0;

        Task SynchronizeAsync(Action<string> progress)
        {
            Interlocked.Increment(ref invocationCount);
            progress("pulling");
            return release.Task;
        }

        var policy = RepositorySynchronizationPolicy.Create(updateRepository: true, revision: null);
        var first = registry.SynchronizeRepositoryAsync("repository", policy, SynchronizeAsync);
        var second = registry.SynchronizeRepositoryAsync(
            Path.Combine("repository", "."),
            policy,
            SynchronizeAsync,
            progress => logger.LogInformation("{Progress}", progress));

        Assert.Same(first, second);
        Assert.Equal(1, Volatile.Read(ref invocationCount));
        release.SetResult();
        await Task.WhenAll(first, second);

        resourceLoggerService.Complete(resource);
        var logs = new List<LogLine>();
        await foreach (var lines in resourceLoggerService.WatchAsync(resource))
        {
            logs.AddRange(lines);
        }

        Assert.Contains(logs, line => line.Content.Contains("pulling", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Module_registry_rejects_conflicting_policies_for_a_shared_repository()
    {
        var registry = new ModuleApplicationRegistry();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var synchronization = registry.SynchronizeRepositoryAsync(
            "repository",
            RepositorySynchronizationPolicy.Create(updateRepository: true, revision: null),
            _ => release.Task);

        var exception = Assert.Throws<InvalidOperationException>(() =>
        {
            _ = registry.SynchronizeRepositoryAsync(
                Path.Combine("repository", "."),
                RepositorySynchronizationPolicy.Create(updateRepository: false, revision: null),
                _ => Task.CompletedTask);
        });

        Assert.Contains("conflicting", exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(DistributedApplicationModuleOptions.UpdateRepository), exception.Message, StringComparison.Ordinal);
        release.SetResult();
        await synchronization;
    }

    private static async Task<IDistributedApplicationModule> ExportModuleAsync(
        IDistributedApplicationBuilder builder,
        string projectPath)
    {
        return await builder.ExportModuleAsync("orders", module =>
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

    private static IDistributedApplicationBuilder CreateBuilder(string projectDirectory, params string[] args)
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = args,
            DisableDashboard = true,
            ProjectDirectory = projectDirectory
        });
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:ProjectMode"] =
            nameof(ModuleProjectMode.Container);
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:PublishImages"] = "true";
        return builder;
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

        public static async Task<TestRepository> CreateAsync(bool initializeGit = false)
        {
            var directory = TemporaryDirectory.Create();
            var projectPath = System.IO.Path.Combine(directory.Path, "Orders.Api.csproj");
            File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");

            if (initializeGit)
            {
                await RunGitAsync(directory.Path, "init");
                await RunGitAsync(directory.Path, "config", "user.name", "Test User");
                await RunGitAsync(directory.Path, "config", "user.email", "test@example.test");
                await RunGitAsync(directory.Path, "add", ".");
                await RunGitAsync(directory.Path, "commit", "-m", "initial");
            }

            return new TestRepository(directory, projectPath);
        }

        public void Dispose() => _directory.Dispose();

        public static async Task RunGitAsync(string workingDirectory, params string[] arguments)
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
    }

    private sealed class TestResource(string name) : Resource(name);

}
