#pragma warning disable ASPIRECOMPUTE003
#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES003

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ModularAppHosts;
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
                    ImageTag = "preview"
                });
        });

        await builder.AddAsync(module);

        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var step = Assert.Single(await CreatePushStepsAsync(container));
        Assert.Equal("push-orders-api", step.Name);
        Assert.Same(container, step.Resource);
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
                    container => container
                        .WithContainerRegistry(registry)
                        .WithRemoteImageName("services/orders")
                        .WithRemoteImageTag("preview"));
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
        Assert.Equal("preview", pushOptions.RemoteImageTag);
        Assert.Single(await CreatePushStepsAsync(container));
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
    public void Scoped_push_rejects_resources_without_a_push_step()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ModuleImagePushPipeline.ApplySelection(
                [CreatePushStep("orders-api")],
                new ModuleImageSelection(["missing-api"])));

        Assert.Contains("missing-api", exception.Message, StringComparison.Ordinal);
        Assert.Contains("orders-api", exception.Message, StringComparison.Ordinal);
    }

    private static PipelineStep CreatePushStep(string resourceName)
    {
        return new PipelineStep
        {
            Name = $"push-{resourceName}",
            Action = _ => Task.CompletedTask,
            RequiredBySteps = [WellKnownPipelineSteps.Push],
            Tags = [WellKnownPipelineTags.PushContainerImage],
            Resource = new ContainerResource(resourceName)
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
