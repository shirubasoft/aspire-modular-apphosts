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
    public void ExportModule_rejects_an_empty_module()
    {
        using var repository = TestRepository.Create();
        var builder = CreateBuilder(repository.Path);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            builder.ExportModule("empty", _ => { }));

        Assert.Contains("does not contain any resources", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportModule_requires_every_project_to_be_exported_as_a_container()
    {
        using var repository = TestRepository.Create();
        var builder = CreateBuilder(repository.Path);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            builder.ExportModule("orders", module =>
                module.AddProject("orders-api", repository.ProjectPath)));

        Assert.Contains("orders-api", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportModule_rejects_case_insensitive_duplicate_resource_names()
    {
        using var repository = TestRepository.Create();
        var builder = CreateBuilder(repository.Path);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            builder.ExportModule("orders", module =>
            {
                module.AddProject("orders-api", repository.ProjectPath)
                    .ExportAsContainer("orders-api", "dotnet", ["publish"]);
                module.AddContainer("ORDERS-API", "nginx");
            }));

        Assert.Contains("already contains a resource", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportModule_rejects_projects_from_multiple_source_trees()
    {
        using var appHost = TemporaryDirectory.Create();
        using var firstSource = TestRepository.Create();
        using var secondSource = TestRepository.Create();
        var builder = CreateBuilder(appHost.Path);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            builder.ExportModule("orders", module =>
            {
                module.AddProject("orders-api", firstSource.ProjectPath)
                    .ExportAsContainer("orders-api", "dotnet", ["publish"]);
                module.AddProject("orders-worker", secondSource.ProjectPath)
                    .ExportAsContainer("orders-worker", "dotnet", ["publish"]);
            }));

        Assert.Contains("same Git repository or source tree", exception.Message, StringComparison.Ordinal);
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
    public void ImportModule_with_projects_requires_a_repository_location()
    {
        using var repository = TestRepository.Create();
        var builder = CreateBuilder(repository.Path);
        builder.ExportModule("orders", module =>
            module.AddProject("orders-api", repository.ProjectPath)
                .ExportAsContainer("orders-api", "dotnet", ["publish"]));

        var exception = Assert.Throws<InvalidOperationException>(() => builder.ImportModule("orders"));

        Assert.Contains("does not have a repository location", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetResource_reports_unmaterialized_missing_and_incompatible_resources()
    {
        using var repository = TestRepository.Create();
        var builder = CreateBuilder(repository.Path);
        var module = ExportModule(builder, repository.ProjectPath);

        var unmaterialized = Assert.Throws<InvalidOperationException>(() =>
            module.GetResource<ContainerResource>("orders-api"));
        Assert.Contains("has not been materialized", unmaterialized.Message, StringComparison.Ordinal);

        builder.Add(module);

        Assert.Throws<KeyNotFoundException>(() =>
            module.GetResource<ContainerResource>("missing"));
        var incompatible = Assert.Throws<InvalidOperationException>(() =>
            module.GetResource<ParameterResource>("orders-api"));
        Assert.Contains(nameof(ParameterResource), incompatible.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Materialize_rejects_existing_resource_and_installer_name_collisions()
    {
        using var repository = TestRepository.Create();

        var resourceBuilder = CreateBuilder(repository.Path);
        var resourceModule = ExportModule(resourceBuilder, repository.ProjectPath);
        resourceBuilder.AddContainer("orders-api", "busybox");
        var resourceCollision = Assert.Throws<InvalidOperationException>(() =>
            resourceBuilder.Add(resourceModule));
        Assert.Contains("orders-api", resourceCollision.Message, StringComparison.Ordinal);

        var installerBuilder = CreateBuilder(repository.Path);
        var installerModule = ExportModule(installerBuilder, repository.ProjectPath);
        installerBuilder.AddContainer("orders-api-installer", "busybox");
        var installerCollision = Assert.Throws<InvalidOperationException>(() =>
            installerBuilder.Add(installerModule));
        Assert.Contains("orders-api-installer", installerCollision.Message, StringComparison.Ordinal);
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
    public void Project_working_directory_must_remain_inside_the_repository()
    {
        using var repository = TestRepository.Create();
        var builder = CreateBuilder(repository.Path);
        var module = builder.ExportModule("orders", definition =>
            definition.AddProject("orders-api", repository.ProjectPath)
                .ExportAsContainer(new ModuleContainerExportOptions(
                    "orders-api",
                    "dotnet",
                    "publish")
                {
                    WorkingDirectory = ".."
                }));

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => builder.Add(module));

        Assert.Equal(nameof(ModuleContainerExportOptions.WorkingDirectory), exception.ParamName);
    }

    [Fact]
    public void Publish_mode_does_not_add_run_only_installer_resources()
    {
        using var repository = TestRepository.Create();
        var builder = CreateBuilder(repository.Path, "--publisher", "manifest");
        var module = ExportModule(builder, repository.ProjectPath);

        builder.Add(module);

        Assert.True(builder.ExecutionContext.IsPublishMode);
        Assert.Single(builder.Resources.OfType<ContainerResource>());
        Assert.Empty(builder.Resources.OfType<ModuleRepositoryInstallerResource>());
    }

    [Fact]
    public void ExportAsContainer_copies_mutable_options()
    {
        using var repository = TestRepository.Create();
        var builder = CreateBuilder(repository.Path);
        var publishArguments = new[] { "publish", "Orders.Api.csproj" };
        var options = new ModuleContainerExportOptions("orders-api", "dotnet", publishArguments)
        {
            ImageTag = "dev"
        };
        var module = builder.ExportModule("orders", definition =>
        {
            definition.WithRepository(repository.Path);
            definition.AddProject("orders-api", repository.ProjectPath)
                .ExportAsContainer(options);
        });

        publishArguments[0] = "mutated";
        options.ImageTag = "changed";
        options.WorkingDirectory = "missing";

        builder.Add(module);

        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var image = Assert.Single(container.Annotations.OfType<ContainerImageAnnotation>());
        var installer = Assert.Single(builder.Resources.OfType<ModuleRepositoryInstallerResource>());
        Assert.Equal("dev", image.Tag);
        Assert.Equal(["publish", "Orders.Api.csproj"], installer.PublishArguments);
        Assert.Equal(repository.Path, installer.WorkingDirectory);
    }

    [Fact]
    public void Container_image_publish_command_uses_a_dirty_tag_and_always_adds_an_installer()
    {
        using var repository = TestRepository.Create(initializeGit: true);
        File.AppendAllText(repository.ProjectPath, Environment.NewLine + "<!-- dirty -->");
        var builder = CreateBuilder(repository.Path);

        var module = builder.ExportModule("generated", definition =>
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

        builder.Add(module);

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
    public void Container_image_publish_command_must_match_the_declared_container_image()
    {
        using var repository = TestRepository.Create();
        var builder = CreateBuilder(repository.Path);

        var exception = Assert.Throws<ArgumentException>(() =>
            builder.ExportModule("invalid", definition =>
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
    public void Clean_image_publish_plan_skips_a_tag_that_already_exists()
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

        var plan = ModuleImagePublishPlan.Create(options, repositoryDirty: false, imageReference =>
        {
            Assert.Equal("modular-static:dev", imageReference);
            return true;
        });

        Assert.False(plan.ShouldPublish);
        Assert.False(plan.RepositoryDirty);
        Assert.Equal("dev", plan.ImageTag);
        Assert.Equal("modular-static:dev", plan.ImageReference);
        Assert.Equal(["build", "--tag", "modular-static:dev", "."], plan.PublishArguments);
    }

    [Fact]
    public void Dirty_image_publish_plan_always_publishes_and_resolves_image_placeholders()
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

        var plan = ModuleImagePublishPlan.Create(
            options,
            repositoryDirty: true,
            _ => throw new InvalidOperationException("Dirty images must not be checked before publishing."));

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
    public void Module_exports_any_custom_resource_type_through_a_lazy_factory()
    {
        using var repository = TestRepository.Create();
        var builder = CreateBuilder(repository.Path);
        IDistributedApplicationModuleResourceContext? capturedContext = null;

        var module = builder.ExportModule("custom", definition =>
            definition.AddResource<TestResource>("clock", context =>
            {
                capturedContext = context;
                return context.ApplicationBuilder.AddResource(new TestResource(context.ResourceName));
            }));

        var exportedResource = Assert.Single(module.Resources);
        Assert.Equal("clock", exportedResource.Name);
        Assert.Equal(typeof(TestResource), exportedResource.ResourceType);
        Assert.Empty(builder.Resources);

        builder.Add(module);

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
    public void Generic_resource_factories_can_resolve_earlier_exports_in_declaration_order()
    {
        using var repository = TestRepository.Create();
        var builder = CreateBuilder(repository.Path);
        TestResource? resolvedDependency = null;

        var module = builder.ExportModule("ordered", definition =>
        {
            definition.AddResource<TestResource>("first", context =>
                context.ApplicationBuilder.AddResource(new TestResource(context.ResourceName)));
            definition.AddResource<TestResource>("second", context =>
            {
                resolvedDependency = context.GetResource<TestResource>("first").Resource;
                return context.ApplicationBuilder.AddResource(new TestResource(context.ResourceName));
            });
        });

        builder.Add(module);

        Assert.Same(module.GetResource<TestResource>("first").Resource, resolvedDependency);
        Assert.Equal(
            ["first", "second"],
            builder.Resources.OfType<TestResource>().Select(resource => resource.Name));
    }

    [Fact]
    public void ImportModule_allows_repository_independent_generic_resources()
    {
        using var repository = TestRepository.Create();
        var builder = CreateBuilder(repository.Path);
        IDistributedApplicationModuleResourceContext? capturedContext = null;

        builder.ExportModule("portable", definition =>
            definition.AddResource<TestResource>("portable-resource", context =>
            {
                capturedContext = context;
                return context.ApplicationBuilder.AddResource(new TestResource(context.ResourceName));
            }));

        var imported = builder.ImportModule("portable");

        Assert.NotNull(capturedContext);
        Assert.True(capturedContext.Imported);
        Assert.Equal(repository.Path, capturedContext.RepositoryPath);
        Assert.Empty(builder.Resources.OfType<ParameterResource>());
        Assert.Equal(
            "portable-resource",
            imported.GetResource<TestResource>("portable-resource").Resource.Name);
    }

    [Fact]
    public void Generic_resource_factory_must_return_the_declared_name()
    {
        using var repository = TestRepository.Create();
        var builder = CreateBuilder(repository.Path);
        var module = builder.ExportModule("invalid", definition =>
            definition.AddResource<TestResource>("expected", context =>
                context.ApplicationBuilder.AddResource(new TestResource("actual"))));

        var exception = Assert.Throws<InvalidOperationException>(() => builder.Add(module));

        Assert.Contains("expected", exception.Message, StringComparison.Ordinal);
        Assert.Contains("actual", exception.Message, StringComparison.Ordinal);
        Assert.Contains("context.ResourceName", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Generic_resource_factory_must_not_return_null()
    {
        using var repository = TestRepository.Create();
        var builder = CreateBuilder(repository.Path);
        var module = builder.ExportModule("invalid", definition =>
            definition.AddResource<TestResource>("clock", _ => null!));

        var exception = Assert.Throws<InvalidOperationException>(() => builder.Add(module));

        Assert.Contains("clock", exception.Message, StringComparison.Ordinal);
        Assert.Contains("returned null", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Generic_resource_names_collide_case_insensitively_with_other_exports()
    {
        using var repository = TestRepository.Create();
        var builder = CreateBuilder(repository.Path);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            builder.ExportModule("invalid", definition =>
            {
                definition.AddContainer("shared", "nginx", "alpine");
                definition.AddResource<TestResource>("SHARED", context =>
                    context.ApplicationBuilder.AddResource(new TestResource(context.ResourceName)));
            }));

        Assert.Contains("SHARED", exception.Message, StringComparison.Ordinal);
        Assert.Contains("already contains a resource", exception.Message, StringComparison.Ordinal);
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
        var imageName = $"module-test-api-{Guid.NewGuid():N}";
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        var builder = CreateBuilder(consumerDirectory);
        builder.Configuration[$"Parameters:{DistributedApplicationModuleExtensions.RepositoryBaseLocationParameterName}"] = directory.Path;
        builder.ExportModule("AppHostA", module =>
        {
            module.WithRepository(sourceRoot);
            module.AddProject("api", projectPath)
                .ExportAsContainer(imageName, "podman", ["build", "."]);
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

    [Fact]
    public void Repository_synchronizer_pulls_a_clean_worktree_and_requires_a_remote_for_a_missing_clone()
    {
        using var repository = TestRepository.Create(initializeGit: true);

        var pull = RepositorySynchronizer.CreateCommand(
            repository.Path,
            "https://example.test/acme/orders.git",
            updateRepository: true);

        Assert.NotNull(pull);
        Assert.Equal("git", pull.Executable);
        Assert.Equal(
            ["-C", repository.Path, "pull", "--ff-only", "--recurse-submodules"],
            pull.Arguments);
        Assert.Null(RepositorySynchronizer.CreateCommand(
            repository.Path,
            "https://example.test/acme/orders.git",
            updateRepository: false));

        using var imports = TemporaryDirectory.Create();
        var exception = Assert.Throws<InvalidOperationException>(() =>
            RepositorySynchronizer.CreateCommand(
                Path.Combine(imports.Path, "missing"),
                repository: null,
                updateRepository: true));
        Assert.Contains("does not define a Git remote", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Repository_synchronizer_executes_success_failure_and_cancellation_paths()
    {
        using var source = TestRepository.Create(initializeGit: true);
        using var imports = TemporaryDirectory.Create();
        var clonePath = Path.Combine(imports.Path, "clone");

        await RepositorySynchronizer.SynchronizeAsync(
            clonePath,
            source.Path,
            updateRepository: true,
            TestContext.Current.CancellationToken);

        Assert.True(RepositoryInspector.IsGitRepository(clonePath));

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
    public async Task Module_registry_deduplicates_concurrent_repository_synchronization()
    {
        var registry = new ModuleApplicationRegistry();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocationCount = 0;

        Task SynchronizeAsync()
        {
            Interlocked.Increment(ref invocationCount);
            return release.Task;
        }

        var first = registry.SynchronizeRepositoryAsync("repository", SynchronizeAsync);
        var second = registry.SynchronizeRepositoryAsync("repository", SynchronizeAsync);

        Assert.Same(first, second);
        Assert.Equal(1, Volatile.Read(ref invocationCount));
        release.SetResult();
        await Task.WhenAll(first, second);
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

    private static IDistributedApplicationBuilder CreateBuilder(string projectDirectory, params string[] args)
    {
        return DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = args,
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

    private sealed class TestResource(string name) : Resource(name);

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
