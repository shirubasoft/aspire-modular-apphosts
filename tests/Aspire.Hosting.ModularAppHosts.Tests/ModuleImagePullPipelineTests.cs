#pragma warning disable ASPIRECOMPUTE003
#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES003

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ModularAppHosts;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aspire.Hosting.ModularAppHosts.Tests;

public sealed class ModuleImagePullPipelineTests
{
    [Fact]
    public async Task Exported_project_with_an_explicit_image_registry_contributes_a_pull_step()
    {
        using var repository = CreateProject();
        var builder = CreatePublishBuilder(repository.Path);
        var module = await builder.ExportModuleAsync("orders", definition =>
        {
            definition.WithRepository(repository.Path);
            definition.AddProject("orders-api", GetProjectPath(repository.Path))
                .ExportAsContainer(new ModuleContainerExportOptions("orders-api", "dotnet", "publish")
                {
                    ImageRegistry = "registry.example.test",
                    ImageTag = "preview"
                });
        });

        await builder.AddAsync(module);

        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var step = Assert.Single(await CreatePullStepsAsync(container));
        Assert.Equal("pull-orders-api", step.Name);
        Assert.Same(container, step.Resource);
        Assert.Contains(ModuleImagePullPipeline.PullPrerequisiteStepName, step.DependsOnSteps);
        Assert.Contains(WellKnownPipelineSteps.CheckContainerRuntime, step.DependsOnSteps);
        Assert.Contains(ModuleImagePullPipeline.PullStepName, step.RequiredBySteps);
        Assert.Contains(ModuleImagePullPipeline.PullContainerImageTag, step.Tags);
    }

    [Fact]
    public async Task Declared_and_factory_created_image_publishers_contribute_pull_steps()
    {
        using var repository = CreateProject();
        var builder = CreatePublishBuilder(repository.Path);
        var module = await builder.ExportModuleAsync("assets", definition =>
        {
            definition.WithRepository(repository.Path);
            definition.AddContainer(
                    "declared-static",
                    "registry.example.test/assets/declared-static",
                    "preview")
                .WithImagePublishCommand(new ModuleContainerExportOptions(
                    "assets/declared-static",
                    "docker",
                    "build")
                {
                    ImageRegistry = "registry.example.test",
                    ImageTag = "preview"
                });
            definition.AddResource<ContainerResource>(
                "factory-static",
                context => context.ApplicationBuilder.AddContainer(context.ResourceName, "placeholder"),
                new ModuleContainerExportOptions("assets/factory-static", "docker", "build")
                {
                    ImageRegistry = "registry.example.test",
                    ImageTag = "preview"
                });
        });

        await builder.AddAsync(module);

        var containers = builder.Resources.OfType<ContainerResource>().ToArray();
        Assert.Equal(2, containers.Length);
        foreach (var container in containers)
        {
            var step = Assert.Single(await CreatePullStepsAsync(container));
            Assert.Equal($"pull-{container.Name}", step.Name);
        }
    }

    [Fact]
    public async Task Exported_project_resolves_its_registry_image_back_to_its_local_image()
    {
        using var repository = CreateProject();
        var builder = CreatePublishBuilder(repository.Path);
        var registry = builder.AddContainerRegistry(
            "registry",
            "registry.example.test",
            "acme");
        var module = await builder.ExportModuleAsync("orders", definition =>
        {
            definition.WithRepository(repository.Path);
            definition.AddProject("orders-api", GetProjectPath(repository.Path))
                .ExportAsContainer(
                    new ModuleContainerExportOptions("orders-api", "dotnet", "publish")
                    {
                        ImageTag = "local"
                    },
                    (_, container) => container
                        .WithContainerRegistry(registry)
                        .WithRemoteImageName("services/orders")
                        .WithRemoteImageTag("preview"));
        });

        await builder.AddAsync(module);

        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var references = await ModuleImagePullPipeline.ResolveImageReferencesAsync(
            container,
            TestContext.Current.CancellationToken);

        Assert.Equal("registry.example.test/acme/services/orders:preview", references.RemoteImage);
        Assert.Equal("orders-api:local", references.LocalImage);
    }

    [Fact]
    public async Task Explicit_pull_mapping_resolves_a_remote_image_from_a_different_registry()
    {
        var builder = DistributedApplication.CreateBuilder();
        var container = builder
            .AddContainer("orders-api", "ghcr.io/api", "1-0")
            .WithImagePullMapping("mycustomregistry.io/images:api-1-0")
            .Resource;

        var references = await ModuleImagePullPipeline.ResolveImageReferencesAsync(
            container,
            TestContext.Current.CancellationToken);

        Assert.Equal("mycustomregistry.io/images:api-1-0", references.RemoteImage);
        Assert.Equal("ghcr.io/api:1-0", references.LocalImage);
    }

    [Fact]
    public async Task Pull_mapping_records_the_remote_to_local_lifecycle_in_the_resource_log()
    {
        var builder = DistributedApplication.CreateBuilder();
        var container = builder
            .AddContainer("orders-api", "ghcr.io/api", "1-0")
            .WithImagePullMapping("mycustomregistry.io/images:api-1-0")
            .Resource;
        await using var application = builder.Build();
        var resourceLoggerService = application.Services.GetRequiredService<ResourceLoggerService>();
        var pipelineContext = new PipelineContext(
            application.Services.GetRequiredService<DistributedApplicationModel>(),
            application.Services.GetRequiredService<DistributedApplicationExecutionContext>(),
            application.Services,
            NullLogger.Instance,
            TestContext.Current.CancellationToken);
        await using var reportingStep = await new NullPublishingActivityReporter().CreateStepAsync(
            "pull-orders-api",
            TestContext.Current.CancellationToken);
        var stepContext = new PipelineStepContext
        {
            PipelineContext = pipelineContext,
            ReportingStep = reportingStep
        };
        var commands = new List<(string Runtime, string[] Arguments)>();

        await ModuleImagePullPipeline.PullAsync(
            container,
            stepContext,
            _ => Task.FromResult("test-runtime"),
            (runtime, arguments, _) =>
            {
                commands.Add((runtime, arguments.ToArray()));
                return Task.CompletedTask;
            });

        Assert.Collection(
            commands,
            command =>
            {
                Assert.Equal("test-runtime", command.Runtime);
                Assert.Equal(["pull", "mycustomregistry.io/images:api-1-0"], command.Arguments);
            },
            command =>
            {
                Assert.Equal("test-runtime", command.Runtime);
                Assert.Equal(
                    ["tag", "mycustomregistry.io/images:api-1-0", "ghcr.io/api:1-0"],
                    command.Arguments);
            });

        resourceLoggerService.Complete(container);
        var logs = new List<LogLine>();
        await foreach (var lines in resourceLoggerService.WatchAsync(container))
        {
            logs.AddRange(lines);
        }

        Assert.Contains(logs, line => line.Content.Contains(
            "Pulling remote image mycustomregistry.io/images:api-1-0 for resource orders-api.",
            StringComparison.Ordinal));
        Assert.Contains(logs, line => line.Content.Contains(
            "Re-tagging remote image mycustomregistry.io/images:api-1-0 as local image ghcr.io/api:1-0 for resource orders-api.",
            StringComparison.Ordinal));
        Assert.Contains(logs, line => line.Content.Contains(
            "Re-tagged remote image mycustomregistry.io/images:api-1-0 as local image ghcr.io/api:1-0 for resource orders-api.",
            StringComparison.Ordinal));
    }

    [Fact]
    public async Task Explicit_pull_mapping_takes_precedence_over_an_Aspire_registry_mapping()
    {
        var builder = DistributedApplication.CreateBuilder();
        var registry = builder.AddContainerRegistry("registry", "ignored.example.test", "ignored");
        var container = builder
            .AddContainer("orders-api", "ghcr.io/api", "1-0")
            .WithContainerRegistry(registry)
            .WithRemoteImageName("ignored-api")
            .WithRemoteImageTag("ignored-tag")
            .WithImagePullMapping("source.example.test/images:api-1-0")
            .Resource;

        var references = await ModuleImagePullPipeline.ResolveImageReferencesAsync(
            container,
            TestContext.Current.CancellationToken);

        Assert.Equal("source.example.test/images:api-1-0", references.RemoteImage);
        Assert.Equal("ghcr.io/api:1-0", references.LocalImage);
    }

    [Fact]
    public async Task Mapping_only_declared_container_contributes_a_pull_step_but_no_push_step()
    {
        using var repository = CreateProject();
        var builder = CreatePublishBuilder(repository.Path);
        var module = await builder.ExportModuleAsync("orders", definition =>
        {
            definition.WithRepository(repository.Path);
            definition.AddContainer("orders-api", "orders-api", "1-0")
                .WithImagePullMapping("source.example.test/images:api-1-0");
        });

        await builder.AddAsync(module);

        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        Assert.Single(await CreatePullStepsAsync(container));
        Assert.DoesNotContain(
            await CreatePipelineStepsAsync(container),
            step => step.Tags.Contains(WellKnownPipelineTags.PushContainerImage));
    }

    [Fact]
    public void A_later_explicit_pull_mapping_replaces_the_previous_mapping()
    {
        var builder = DistributedApplication.CreateBuilder();
        var container = builder
            .AddContainer("orders-api", "orders-api", "1-0")
            .WithImagePullMapping("first.example.test/images:api-1-0")
            .WithImagePullMapping("second.example.test/images:api-1-0")
            .Resource;

        var mapping = Assert.Single(container.Annotations.OfType<ModuleImagePullMappingAnnotation>());
        Assert.Equal("second.example.test/images:api-1-0", mapping.RemoteImageReference);
    }

    [Fact]
    public async Task Explicit_pull_mapping_rejects_a_digest_pinned_local_image()
    {
        var builder = DistributedApplication.CreateBuilder();
        var container = builder
            .AddContainer("orders-api", "orders-api", "1-0")
            .WithImageRegistry("ghcr.io")
            .WithImageSHA256(new string('a', 64))
            .WithImagePullMapping("source.example.test/images:api-1-0");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ModuleImagePullPipeline.ResolveImageReferencesAsync(
                container.Resource,
                TestContext.Current.CancellationToken));

        Assert.Contains("digest-pinned", exception.Message, StringComparison.Ordinal);
        Assert.Contains("WithImagePullMapping", exception.Message, StringComparison.Ordinal);

        ModuleImagePullPipeline.AddPullStep(container);
        var pipelineException = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreatePullStepsAsync(container.Resource));
        Assert.Contains("digest-pinned", pipelineException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Explicit_image_registry_uses_the_resource_image_as_remote_and_local_reference()
    {
        var builder = DistributedApplication.CreateBuilder();
        var container = builder
            .AddContainer("orders-api", "orders-api", "preview")
            .WithImageRegistry("registry.example.test")
            .Resource;

        var references = await ModuleImagePullPipeline.ResolveImageReferencesAsync(
            container,
            TestContext.Current.CancellationToken);

        Assert.Equal("registry.example.test/orders-api:preview", references.RemoteImage);
        Assert.Equal(references.RemoteImage, references.LocalImage);
    }

    [Fact]
    public async Task Explicit_registry_digest_uses_the_pinned_reference_directly()
    {
        var digest = new string('a', 64);
        var builder = DistributedApplication.CreateBuilder();
        var container = builder
            .AddContainer("orders-api", "orders-api", "preview")
            .WithImageRegistry("registry.example.test")
            .WithImageSHA256(digest)
            .Resource;

        var references = await ModuleImagePullPipeline.ResolveImageReferencesAsync(
            container,
            TestContext.Current.CancellationToken);

        Assert.Equal($"registry.example.test/orders-api@sha256:{digest}", references.RemoteImage);
        Assert.Equal(references.RemoteImage, references.LocalImage);
    }

    [Fact]
    public async Task Registry_associated_digest_does_not_contribute_an_untaggable_pull_step()
    {
        var builder = DistributedApplication.CreateBuilder();
        var registry = builder.AddContainerRegistry("registry", "registry.example.test");
        var container = builder
            .AddContainer("orders-api", "orders-api", "preview")
            .WithImageSHA256(new string('a', 64))
            .WithContainerRegistry(registry);

        ModuleImagePullPipeline.AddPullStep(container);

        Assert.Empty(await CreatePullStepsAsync(container.Resource));
    }

    [Fact]
    public async Task Default_registry_resolves_the_default_remote_name_and_tag()
    {
        var builder = DistributedApplication.CreateBuilder();
        var registry = builder.AddContainerRegistry(
            "registry",
            "registry.example.test",
            "acme");
        var container = builder.AddContainer("orders-api", "orders-api", "preview").Resource;
        container.Annotations.Add(new RegistryTargetAnnotation(registry.Resource));

        var references = await ModuleImagePullPipeline.ResolveImageReferencesAsync(
            container,
            TestContext.Current.CancellationToken);

        Assert.Equal("registry.example.test/acme/orders-api:latest", references.RemoteImage);
        Assert.Equal("orders-api:preview", references.LocalImage);
    }

    [Fact]
    public async Task Module_publisher_identity_wins_over_a_colliding_resource_name_default()
    {
        using var repository = CreateProject();
        var builder = CreatePublishBuilder(repository.Path);
        var registry = builder.AddContainerRegistry(
            "registry",
            "registry.example.test",
            "mirror");
        var module = await builder.ExportModuleAsync("database", definition =>
        {
            definition.WithRepository(repository.Path);
            definition.AddContainer("postgres", "owner/database", "ci")
                .WithImagePublishCommand(new ModuleContainerExportOptions(
                    "owner/database",
                    "docker",
                    "build")
                {
                    ImageTag = "ci"
                });
        });
        await builder.AddAsync(module);
        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        container.Annotations.Add(new RegistryTargetAnnotation(registry.Resource));

        var references = await ModuleImagePullPipeline.ResolveImageReferencesAsync(
            container,
            TestContext.Current.CancellationToken);

        Assert.Equal("registry.example.test/mirror/owner/database:ci", references.RemoteImage);
        Assert.Equal("owner/database:ci", references.LocalImage);
        Assert.DoesNotContain("postgres:latest", references.RemoteImage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pull_reference_resolution_rejects_a_resource_without_an_image()
    {
        var resource = new ContainerResource("orders-api");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ModuleImagePullPipeline.ResolveImageReferencesAsync(
                resource,
                TestContext.Current.CancellationToken));

        Assert.Contains("container image reference", exception.Message, StringComparison.Ordinal);

        var builder = DistributedApplication.CreateBuilder();
        var container = builder.AddResource(resource);
        ModuleImagePullPipeline.AddPullStep(container);
        Assert.Empty(await CreatePullStepsAsync(resource));
    }

    [Fact]
    public async Task Pull_reference_resolution_rejects_an_image_without_a_registry()
    {
        var builder = DistributedApplication.CreateBuilder();
        var resource = builder.AddContainer("orders-api", "orders-api", "preview").Resource;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ModuleImagePullPipeline.ResolveImageReferencesAsync(
                resource,
                TestContext.Current.CancellationToken));

        Assert.Contains("container registry", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Multiple_default_registries_require_an_explicit_resource_registry()
    {
        var builder = DistributedApplication.CreateBuilder();
        var firstRegistry = builder.AddContainerRegistry("first", "first.example.test");
        var secondRegistry = builder.AddContainerRegistry("second", "second.example.test");
        var container = builder.AddContainer("orders-api", "orders-api", "preview");
        container.Resource.Annotations.Add(new RegistryTargetAnnotation(firstRegistry.Resource));
        container.Resource.Annotations.Add(new RegistryTargetAnnotation(secondRegistry.Resource));
        ModuleImagePullPipeline.AddPullStep(container);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreatePullStepsAsync(container.Resource));

        Assert.Contains("multiple container registries", exception.Message, StringComparison.Ordinal);
        Assert.Contains("WithContainerRegistry", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deployment_target_registry_is_used_when_no_resource_registry_is_configured()
    {
        var builder = DistributedApplication.CreateBuilder();
        var registry = builder.AddContainerRegistry(
            "registry",
            "registry.example.test",
            "acme");
        var container = builder.AddContainer("orders-api", "orders-api", "preview").Resource;
        container.Annotations.Add(new DeploymentTargetAnnotation(new ContainerResource("deployment"))
        {
            ContainerRegistry = registry.Resource
        });

        var references = await ModuleImagePullPipeline.ResolveImageReferencesAsync(
            container,
            TestContext.Current.CancellationToken);

        Assert.Equal("registry.example.test/acme/orders-api:latest", references.RemoteImage);
        Assert.Equal("orders-api:preview", references.LocalImage);
    }

    [Fact]
    public void Pull_arguments_scope_the_pipeline_to_named_resources()
    {
        var selection = ModuleImagePullPipeline.GetSelection(
        [
            "--operation",
            "publish",
            "--step",
            "pull",
            "--log-level",
            "debug",
            "orders-api",
            "orders-worker"
        ]);

        Assert.True(selection.IsScoped);
        Assert.True(selection.Includes("orders-api"));
        Assert.True(selection.Includes("ORDERS-WORKER"));
        Assert.False(selection.Includes("catalog-api"));
    }

    [Fact]
    public void Pull_without_resource_arguments_keeps_all_pull_steps()
    {
        var selection = ModuleImagePullPipeline.GetSelection(
        [
            "--operation",
            "publish",
            "--step=pull",
            "--output-path",
            "artifacts",
            "--include-exception-details",
            "true"
        ]);

        Assert.False(selection.IsScoped);
        Assert.True(selection.Includes("orders-api"));
    }

    [Fact]
    public void Resource_arguments_for_another_step_do_not_scope_pull_steps()
    {
        var selection = ModuleImagePullPipeline.GetSelection(
            ["--operation", "publish", "--step", "push", "orders-api"]);

        Assert.False(selection.IsScoped);
    }

    [Fact]
    public void Positional_separator_allows_resource_names_that_start_with_a_dash()
    {
        var selection = ModuleImagePullPipeline.GetSelection(
            ["--operation", "publish", "--step", "pull", "--", "-orders-api"]);

        Assert.True(selection.Includes("-orders-api"));
    }

    [Fact]
    public void Scoped_pull_detaches_unselected_resource_steps_from_the_pull_aggregate()
    {
        var apiStep = CreatePullStep("orders-api");
        var workerStep = CreatePullStep("orders-worker");

        ModuleImagePullPipeline.ApplySelection(
            [apiStep, workerStep],
            new ModuleImageSelection(["orders-api"]));

        Assert.Contains(ModuleImagePullPipeline.PullStepName, apiStep.RequiredBySteps);
        Assert.DoesNotContain(ModuleImagePullPipeline.PullStepName, workerStep.RequiredBySteps);
        Assert.False(workerStep.Resource!.IsExcludedFromPublish());
    }

    [Fact]
    public void Scoped_pull_rejects_resources_without_a_pull_step()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ModuleImagePullPipeline.ApplySelection(
                [CreatePullStep("orders-api")],
                new ModuleImageSelection(["missing-api"])));

        Assert.Contains("missing-api", exception.Message, StringComparison.Ordinal);
        Assert.Contains("orders-api", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unscoped_selection_does_not_modify_pull_steps()
    {
        var step = CreatePullStep("orders-api");

        ModuleImagePullPipeline.ApplySelection([step], ModuleImageSelection.All);

        Assert.Contains(ModuleImagePullPipeline.PullStepName, step.RequiredBySteps);
    }

    private static PipelineStep CreatePullStep(string resourceName)
    {
        return new PipelineStep
        {
            Name = $"pull-{resourceName}",
            Action = _ => Task.CompletedTask,
            RequiredBySteps = [ModuleImagePullPipeline.PullStepName],
            Tags = [ModuleImagePullPipeline.PullContainerImageTag],
            Resource = new ContainerResource(resourceName)
        };
    }

    private static async Task<IReadOnlyList<PipelineStep>> CreatePullStepsAsync(IResource resource)
    {
        return (await CreatePipelineStepsAsync(resource))
            .Where(step => step.Tags.Contains(ModuleImagePullPipeline.PullContainerImageTag))
            .ToArray();
    }

    private static async Task<IReadOnlyList<PipelineStep>> CreatePipelineStepsAsync(IResource resource)
    {
        var steps = new List<PipelineStep>();
        foreach (var annotation in resource.Annotations.OfType<PipelineStepAnnotation>())
        {
            steps.AddRange(await annotation.CreateStepsAsync(new PipelineStepFactoryContext
            {
                PipelineContext = null!,
                Resource = resource
            }));
        }

        return steps;
    }

    private static IDistributedApplicationBuilder CreatePublishBuilder(string projectDirectory)
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = ["--publisher", "manifest"],
            DisableDashboard = true,
            ProjectDirectory = projectDirectory
        });
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:ProjectMode"] =
            nameof(ModuleProjectMode.Container);
        return builder;
    }

    private static TemporaryDirectory CreateProject()
    {
        var directory = TemporaryDirectory.Create();
        File.WriteAllText(
            GetProjectPath(directory.Path),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        return directory;
    }

    private static string GetProjectPath(string directory) =>
        Path.Combine(directory, "Orders.Api.csproj");
}
