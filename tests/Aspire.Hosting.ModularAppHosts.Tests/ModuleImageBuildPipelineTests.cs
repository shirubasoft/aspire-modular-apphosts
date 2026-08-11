#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES003

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ModularAppHosts;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aspire.Hosting.ModularAppHosts.Tests;

public sealed class ModuleImageBuildPipelineTests
{
    [Fact]
    public async Task Every_module_publisher_kind_contributes_a_build_step()
    {
        using var repository = TemporaryDirectory.Create();
        var projectPath = Path.Combine(repository.Path, "ImageProject.csproj");
        File.WriteAllText(
            projectPath,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        var builder = CreatePublishBuilder(repository.Path);
        var module = await builder.ExportModuleAsync("images", definition =>
        {
            definition.WithRepository(repository.Path);
            definition.AddProject("project", projectPath)
                .ExportAsContainer(Publisher("project"));
            definition.AddContainer("declared", "registry.example.test/acme/declared", "ci")
                .WithImagePublishCommand(Publisher("declared"));
            definition.AddResource<ContainerResource>(
                "factory",
                context => context.ApplicationBuilder.AddContainer(context.ResourceName, "placeholder"),
                Publisher("factory"));
        });

        await builder.AddAsync(module);

        foreach (var resource in builder.Resources.OfType<ContainerResource>())
        {
            var step = Assert.Single(await CreateBuildStepsAsync(resource));
            Assert.Equal($"build-{resource.Name}", step.Name);
            Assert.Contains(WellKnownPipelineSteps.BuildPrereq, step.DependsOnSteps);
            Assert.Contains(WellKnownPipelineSteps.Build, step.RequiredBySteps);
            Assert.Contains(ModuleImageBuildPipeline.BuildContainerImageTag, step.Tags);
        }
    }

    [Fact]
    public void Build_selection_accepts_declared_aliases_and_rejects_unknown_resources()
    {
        var selected = CreateBuildStep("imported-api", "api");
        var unselected = CreateBuildStep("imported-worker", "worker");

        ModuleImageBuildPipeline.ApplySelection(
            [selected, unselected],
            new ModuleImageSelection(["api"]));

        Assert.Contains(WellKnownPipelineSteps.Build, selected.RequiredBySteps);
        Assert.DoesNotContain(WellKnownPipelineSteps.Build, unselected.RequiredBySteps);
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ModuleImageBuildPipeline.ApplySelection(
                [selected, unselected],
                new ModuleImageSelection(["missing"])));
        Assert.Contains("missing", exception.Message, StringComparison.Ordinal);
        Assert.Contains("api", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("pull", false)]
    [InlineData("describe-images", false)]
    [InlineData("push", true)]
    [InlineData("build", true)]
    public void Pull_only_operations_do_not_prepare_build_repositories(
        string step,
        bool expected)
    {
        Assert.Equal(expected, ModuleImageBuildPipeline.ShouldPrepareBuildRepository(
            ["--operation", "publish", "--step", step],
            "images",
            "api",
            "imported-api"));
    }

    [Theory]
    [InlineData("push", "api", true)]
    [InlineData("push", "imported-api", true)]
    [InlineData("push", "worker", false)]
    [InlineData("build", "api", true)]
    [InlineData("build", "worker", false)]
    [InlineData("push-api", null, true)]
    [InlineData("push-worker", null, false)]
    public void Scoped_operations_only_prepare_the_selected_build_repository(
        string step,
        string? positionalResource,
        bool expected)
    {
        var arguments = positionalResource is null
            ? new[] { "--operation", "publish", "--step", step }
            : ["--operation", "publish", "--step", step, positionalResource];
        Assert.Equal(expected, ModuleImageBuildPipeline.ShouldPrepareBuildRepository(
            arguments,
            "images",
            "api",
            "imported-api"));
    }

    [Theory]
    [InlineData("module:images", true)]
    [InlineData("images", true)]
    [InlineData("api", true)]
    [InlineData("module:catalog", false)]
    public void Module_selectors_control_build_repository_preparation(
        string selector,
        bool expected)
    {
        Assert.Equal(expected, ModuleImageBuildPipeline.ShouldPrepareBuildRepository(
            ["--operation", "publish", "--step", "push", selector],
            "images",
            "api",
            "imported-api"));
    }

    [Fact]
    public async Task Clean_available_or_pulled_images_skip_the_build_command()
    {
        var options = Publisher("api");
        options.PullBeforeBuild = true;
        var (resource, context, application) = await CreateContextAsync(options);
        await using (application)
        {
            var builds = 0;
            var pulls = 0;
            await ModuleImageBuildPipeline.BuildAsync(
                resource,
                context,
                (_, _) => Task.FromResult(false),
                (_, _) =>
                {
                    pulls++;
                    return Task.FromResult(true);
                },
                (_, _) =>
                {
                    builds++;
                    return Task.CompletedTask;
                },
                (_, _, _) => Task.CompletedTask);

            Assert.Equal(1, pulls);
            Assert.Equal(0, builds);
        }
    }

    [Fact]
    public async Task Dirty_images_build_and_retag_the_declared_output()
    {
        var options = Publisher("api");
        options.ProducedImageReference = "legacy/api:output";
        var (resource, context, application) = await CreateContextAsync(options, repositoryDirty: true);
        await using (application)
        {
            ModuleImagePublisherAnnotation? executed = null;
            (string Source, string Target)? retag = null;
            await ModuleImageBuildPipeline.BuildAsync(
                resource,
                context,
                (_, _) => throw new InvalidOperationException("Dirty images must not be inspected."),
                (_, _) => throw new InvalidOperationException("Dirty images must not be pulled."),
                (publisher, _) =>
                {
                    executed = publisher;
                    return Task.CompletedTask;
                },
                (source, target, _) =>
                {
                    retag = (source, target);
                    return Task.CompletedTask;
                });

            Assert.NotNull(executed);
            Assert.Equal("registry.example.test/acme/api:ci-dirty", executed.Plan.ImageReference);
            Assert.Equal(
                ("legacy/api:output", "registry.example.test/acme/api:ci-dirty"),
                retag);
        }
    }

    [Fact]
    public async Task Build_command_failures_propagate_without_retagging()
    {
        var options = Publisher("api");
        options.ProducedImageReference = "legacy/api:output";
        var (resource, context, application) = await CreateContextAsync(options, repositoryDirty: true);
        await using (application)
        {
            var retagged = false;
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ModuleImageBuildPipeline.BuildAsync(
                    resource,
                    context,
                    (_, _) => Task.FromResult(false),
                    (_, _) => Task.FromResult(false),
                    (_, _) => throw new InvalidOperationException("build failed"),
                    (_, _, _) =>
                    {
                        retagged = true;
                        return Task.CompletedTask;
                    }));

            Assert.Equal("build failed", exception.Message);
            Assert.False(retagged);
        }
    }

    private static ModuleContainerExportOptions Publisher(string resource) =>
        new($"acme/{resource}", $"build-{resource}", "publish", "{image}")
        {
            ImageRegistry = "registry.example.test",
            ImageTag = "ci"
        };

    private static async Task<(
        ContainerResource Resource,
        PipelineStepContext Context,
        DistributedApplication Application)> CreateContextAsync(
        ModuleContainerExportOptions options,
        bool repositoryDirty = false)
    {
        var builder = DistributedApplication.CreateBuilder();
        var resource = builder
            .AddContainer("api", options.ImageName, options.ImageTag!)
            .WithImageRegistry(options.ImageRegistry)
            .Resource;
        var plan = await ModuleImagePublishPlan.CreateAsync(
            options,
            repositoryDirty,
            (_, _) => Task.FromResult(false),
            TestContext.Current.CancellationToken);
        resource.Annotations.Add(new ModuleImagePublisherAnnotation(
            "images",
            "api",
            ModuleResourceKind.Container,
            options,
            plan,
            "/work",
            "https://example.test/images.git",
            "main"));
        var application = builder.Build();
        var pipelineContext = new PipelineContext(
            application.Services.GetRequiredService<DistributedApplicationModel>(),
            application.Services.GetRequiredService<DistributedApplicationExecutionContext>(),
            application.Services,
            NullLogger.Instance,
            TestContext.Current.CancellationToken);
        var reportingStep = await new NullPublishingActivityReporter().CreateStepAsync(
            "build-api",
            TestContext.Current.CancellationToken);
        var context = new PipelineStepContext
        {
            PipelineContext = pipelineContext,
            ReportingStep = reportingStep
        };
        return (resource, context, application);
    }

    private static PipelineStep CreateBuildStep(string effectiveName, string declaredName)
    {
        var resource = new ContainerResource(effectiveName);
        var options = Publisher(declaredName);
        var plan = new ModuleImagePublishPlan(
            options.ImageRegistry,
            options.ImageName,
            options.ImageTag!,
            $"{options.ImageRegistry}/{options.ImageName}:{options.ImageTag}",
            null,
            options.PublishArguments,
            false,
            true);
        resource.Annotations.Add(new ModuleImagePublisherAnnotation(
            "images",
            declaredName,
            ModuleResourceKind.Container,
            options,
            plan,
            "/work",
            null,
            null));
        return new PipelineStep
        {
            Name = $"build-{effectiveName}",
            Action = _ => Task.CompletedTask,
            RequiredBySteps = [WellKnownPipelineSteps.Build],
            Tags = [ModuleImageBuildPipeline.BuildContainerImageTag],
            Resource = resource
        };
    }

    private static async Task<IReadOnlyList<PipelineStep>> CreateBuildStepsAsync(IResource resource)
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

        return steps.Where(step => step.Tags.Contains(ModuleImageBuildPipeline.BuildContainerImageTag)).ToArray();
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
}
