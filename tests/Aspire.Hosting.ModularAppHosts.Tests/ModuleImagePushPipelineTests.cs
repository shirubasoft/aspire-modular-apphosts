#pragma warning disable ASPIRECOMPUTE003
#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES003

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting;
using Aspire.Hosting.Pipelines;
using Xunit;

namespace Aspire.Hosting.ModularAppHosts.Tests;

public sealed class ModuleImagePushPipelineTests
{
    [Fact]
    public async Task Exported_project_with_an_explicit_image_registry_contributes_a_push_step()
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
                    ImageTag = "candidate"
                });
        });

        await builder.AddAsync(module);

        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var step = Assert.Single(await CreatePushStepsAsync(container));
        Assert.Equal("push-orders-api", step.Name);
        Assert.Same(container, step.Resource);
        Assert.Contains("build-orders-api", step.DependsOnSteps);
        Assert.Contains(WellKnownPipelineSteps.PushPrereq, step.DependsOnSteps);
        Assert.Contains(WellKnownPipelineSteps.CheckContainerRuntime, step.DependsOnSteps);
        Assert.Contains(WellKnownPipelineSteps.Push, step.RequiredBySteps);
        Assert.Contains(WellKnownPipelineTags.PushContainerImage, step.Tags);
    }

    [Fact]
    public async Task Declared_and_factory_created_image_publishers_contribute_push_steps()
    {
        using var repository = CreateProject();
        var builder = CreatePublishBuilder(repository.Path);
        var module = await builder.ExportModuleAsync("assets", definition =>
        {
            definition.WithRepository(repository.Path);
            definition.AddContainer(
                    "declared-static",
                    "registry.example.test/assets/declared-static",
                    "candidate")
                .WithImagePublishCommand(new ModuleContainerExportOptions(
                    "assets/declared-static",
                    "docker",
                    "build")
                {
                    ImageRegistry = "registry.example.test",
                    ImageTag = "candidate"
                });
            definition.AddResource<ContainerResource>(
                "factory-static",
                context => context.ApplicationBuilder.AddContainer(context.ResourceName, "placeholder"),
                new ModuleContainerExportOptions("assets/factory-static", "docker", "build")
                {
                    ImageRegistry = "registry.example.test",
                    ImageTag = "candidate"
                });
        });

        await builder.AddAsync(module);

        var containers = builder.Resources.OfType<ContainerResource>().ToArray();
        Assert.Equal(2, containers.Length);
        foreach (var container in containers)
        {
            var step = Assert.Single(await CreatePushStepsAsync(container));
            Assert.Equal($"push-{container.Name}", step.Name);
        }
    }

    [Fact]
    public async Task Exported_project_reuses_its_container_registry_and_remote_image_options()
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
                        .WithRemoteImageTag("candidate"));
        });

        await builder.AddAsync(module);

        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var registryAnnotation = Assert.Single(
            container.Annotations.OfType<ContainerRegistryReferenceAnnotation>());
        Assert.Same(registry.Resource, registryAnnotation.Registry);

        var pushOptions = new ContainerImagePushOptions();
        var pushOptionsContext = new ContainerImagePushOptionsCallbackContext
        {
            Resource = container,
            Options = pushOptions,
            CancellationToken = TestContext.Current.CancellationToken
        };
        foreach (var annotation in container.Annotations.OfType<ContainerImagePushOptionsCallbackAnnotation>())
        {
            await annotation.Callback(pushOptionsContext);
        }

        Assert.Equal("services/orders", pushOptions.RemoteImageName);
        Assert.Equal("candidate", pushOptions.RemoteImageTag);
        Assert.Single(await CreatePushStepsAsync(container));
    }

    [Fact]
    public async Task Clean_publisher_resolves_a_branch_alias_in_the_remote_repository()
    {
        var resource = await CreateBranchAliasResourceAsync("feature-orders", repositoryDirty: false);
        var resolved = new ModuleEffectiveImage(
            "orders-api:candidate",
            "registry.example.test/acme/orders-api:candidate",
            "registry.example.test/acme/orders-api:candidate",
            ModuleImagePushTargetKind.AspireRegistry,
            null,
            "orders-api",
            "candidate",
            null,
            new ModuleRemoteImage(
                "registry.example.test",
                "acme/orders-api",
                "candidate",
                "registry.example.test/acme/orders-api:candidate"));

        var alias = ModuleImagePushPipeline.GetBranchAliasReference(resource, resolved);

        Assert.Equal("registry.example.test/acme/orders-api:feature-orders", alias);
    }

    [Theory]
    [InlineData(true, "feature-orders", "candidate")]
    [InlineData(false, null, "candidate")]
    [InlineData(false, "candidate", "candidate")]
    public async Task Branch_alias_is_skipped_when_it_is_not_safe_or_distinct(
        bool repositoryDirty,
        string? branchImageTag,
        string pushedTag)
    {
        var resource = await CreateBranchAliasResourceAsync(branchImageTag, repositoryDirty);
        var reference = $"registry.example.test/acme/orders-api:{pushedTag}";
        var resolved = new ModuleEffectiveImage(
            "orders-api:candidate",
            reference,
            reference,
            ModuleImagePushTargetKind.ContainerRuntime,
            null,
            "orders-api",
            "candidate",
            null,
            new ModuleRemoteImage(
                "registry.example.test",
                "acme/orders-api",
                pushedTag,
                reference));

        Assert.Null(ModuleImagePushPipeline.GetBranchAliasReference(resource, resolved));
    }

    [Fact]
    public void Push_arguments_scope_the_pipeline_to_named_resources()
    {
        var selection = ModuleImagePushPipeline.GetSelection(
        [
            "--operation",
            "publish",
            "--step",
            "push",
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
    public void Push_arguments_accept_explicit_module_and_resource_selectors()
    {
        var selection = ModuleImagePushPipeline.GetSelection(
        [
            "--operation",
            "publish",
            "--step",
            "push",
            "module:orders",
            "resource:catalog-api"
        ]);

        Assert.Collection(
            selection.Selectors.OrderBy(selector => selector.Kind),
            selector =>
            {
                Assert.Equal(ModuleImageSelectorKind.Resource, selector.Kind);
                Assert.Equal("catalog-api", selector.Name);
            },
            selector =>
            {
                Assert.Equal(ModuleImageSelectorKind.Module, selector.Kind);
                Assert.Equal("orders", selector.Name);
            });
    }

    [Fact]
    public void Push_without_resource_arguments_keeps_all_push_steps()
    {
        var selection = ModuleImagePushPipeline.GetSelection(
        [
            "--operation",
            "publish",
            "--step=push",
            "--output-path",
            "artifacts",
            "--include-exception-details",
            "true"
        ]);

        Assert.False(selection.IsScoped);
        Assert.True(selection.Includes("orders-api"));
    }

    [Fact]
    public void Resource_arguments_for_another_step_do_not_scope_push_steps()
    {
        var selection = ModuleImagePushPipeline.GetSelection(
            ["--operation", "publish", "--step", "deploy", "orders-api"]);

        Assert.False(selection.IsScoped);
    }

    [Fact]
    public void Scoped_push_detaches_unselected_resource_steps_from_the_push_aggregate()
    {
        var apiStep = CreatePushStep("orders-api");
        var workerStep = CreatePushStep("orders-worker");

        ModuleImagePushPipeline.ApplySelection(
            [apiStep, workerStep],
            new ModuleImageSelection(["orders-api"]));

        Assert.Contains(WellKnownPipelineSteps.Push, apiStep.RequiredBySteps);
        Assert.DoesNotContain(WellKnownPipelineSteps.Push, workerStep.RequiredBySteps);
        Assert.True(workerStep.Resource!.IsExcludedFromPublish());
    }

    [Fact]
    public void Scoped_push_only_reaches_the_matching_module_build_dependency()
    {
        var apiStep = CreatePushStep("orders-api");
        var workerStep = CreatePushStep("orders-worker");

        ModuleImagePushPipeline.ApplySelection(
            [apiStep, workerStep],
            new ModuleImageSelection(["orders-api"]));

        var reachableBuildSteps = new[] { apiStep, workerStep }
            .Where(step => step.RequiredBySteps.Contains(WellKnownPipelineSteps.Push))
            .SelectMany(step => step.DependsOnSteps)
            .Where(step => step.StartsWith("build-", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(["build-orders-api"], reachableBuildSteps);
    }

    [Fact]
    public void Module_selector_includes_every_owned_publisher_and_deduplicates_mixed_selection()
    {
        var apiStep = CreatePushStep("orders-api", "orders", "api");
        var workerStep = CreatePushStep("orders-worker", "orders", "worker");
        var catalogStep = CreatePushStep("catalog-api", "catalog", "api");

        ModuleImagePushPipeline.ApplySelection(
            [apiStep, workerStep, catalogStep],
            new ModuleImageSelection(["module:orders", "orders-api"]));

        Assert.Contains(WellKnownPipelineSteps.Push, apiStep.RequiredBySteps);
        Assert.Contains(WellKnownPipelineSteps.Push, workerStep.RequiredBySteps);
        Assert.DoesNotContain(WellKnownPipelineSteps.Push, catalogStep.RequiredBySteps);
    }

    [Fact]
    public void Plain_selectors_resolve_unambiguous_names_and_require_prefixes_for_collisions()
    {
        var collidingResource = CreatePushStep("orders", "catalog", "orders");
        var moduleResource = CreatePushStep("orders-worker", "orders", "worker");

        var ambiguous = Assert.Throws<InvalidOperationException>(() =>
            ModuleImagePushPipeline.ApplySelection(
                [collidingResource, moduleResource],
                new ModuleImageSelection(["orders"])));
        Assert.Contains("ambiguous", ambiguous.Message, StringComparison.OrdinalIgnoreCase);

        ModuleImagePushPipeline.ApplySelection(
            [collidingResource, moduleResource],
            new ModuleImageSelection(["resource:orders"]));

        Assert.Contains(WellKnownPipelineSteps.Push, collidingResource.RequiredBySteps);
        Assert.DoesNotContain(WellKnownPipelineSteps.Push, moduleResource.RequiredBySteps);

        collidingResource = CreatePushStep("orders", "catalog", "orders");
        moduleResource = CreatePushStep("orders-worker", "orders", "worker");
        ModuleImagePushPipeline.ApplySelection(
            [collidingResource, moduleResource],
            new ModuleImageSelection(["module:orders"]));

        Assert.DoesNotContain(WellKnownPipelineSteps.Push, collidingResource.RequiredBySteps);
        Assert.Contains(WellKnownPipelineSteps.Push, moduleResource.RequiredBySteps);

        moduleResource = CreatePushStep("orders-worker", "orders", "worker");
        ModuleImagePushPipeline.ApplySelection(
            [moduleResource],
            new ModuleImageSelection(["orders"]));
        Assert.Contains(WellKnownPipelineSteps.Push, moduleResource.RequiredBySteps);
    }

    [Fact]
    public void Scoped_push_rejects_resources_without_a_push_step()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ModuleImagePushPipeline.ApplySelection(
                [CreatePushStep("orders-api")],
                new ModuleImageSelection(["missing-api"])));

        Assert.Contains("missing-api", exception.Message, StringComparison.Ordinal);
        Assert.Contains("orders-api", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Available modules", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_module_selector_lists_available_resources_and_modules()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ModuleImagePushPipeline.ApplySelection(
                [CreatePushStep("orders-api", "orders", "api")],
                new ModuleImageSelection(["module:catalog"])));

        Assert.Contains("module:catalog", exception.Message, StringComparison.Ordinal);
        Assert.Contains("orders-api", exception.Message, StringComparison.Ordinal);
        Assert.Contains("orders", exception.Message, StringComparison.Ordinal);
    }

    private static PipelineStep CreatePushStep(
        string resourceName,
        string? moduleName = null,
        string? declaredResourceName = null)
    {
        var resource = new ContainerResource(resourceName);
        if (moduleName is not null)
        {
            resource.Annotations.Add(new DistributedApplicationModuleResourceAnnotation(
                moduleName,
                declaredResourceName ?? resourceName,
                "/work",
                imported: true));
        }

        return new PipelineStep
        {
            Name = $"push-{resourceName}",
            Action = _ => Task.CompletedTask,
            DependsOnSteps = [$"build-{resourceName}"],
            RequiredBySteps = [WellKnownPipelineSteps.Push],
            Tags = [WellKnownPipelineTags.PushContainerImage],
            Resource = resource
        };
    }

    private static async Task<IReadOnlyList<PipelineStep>> CreatePushStepsAsync(IResource resource)
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

        return steps
            .Where(step => step.Tags.Contains(WellKnownPipelineTags.PushContainerImage))
            .ToArray();
    }

    private static async Task<ContainerResource> CreateBranchAliasResourceAsync(
        string? branchImageTag,
        bool repositoryDirty)
    {
        var resource = new ContainerResource("orders-api");
        var options = new ModuleContainerExportOptions("orders-api", "docker", "build")
        {
            ImageTag = "candidate"
        };
        var plan = await ModuleImagePublishPlan.CreateAsync(
            options,
            repositoryDirty,
            (_, _) => Task.FromResult(false),
            TestContext.Current.CancellationToken);
        resource.Annotations.Add(new ModuleImagePublisherAnnotation(
            "orders",
            "api",
            ModuleResourceKind.Container,
            options,
            plan,
            "/work",
            "https://example.test/orders.git",
            null,
            branchImageTag));
        return resource;
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
