#pragma warning disable ASPIRECOMPUTE003
#pragma warning disable ASPIREFILESYSTEM001
#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES003
#pragma warning disable ASPIREPIPELINES004
#pragma warning disable ASPIREUSERSECRETS001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Publishing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Reflection;
using Xunit;

namespace Aspire.Hosting.ModularAppHosts.Tests;

[Collection("Module image runtime adapter coverage")]
public sealed class ModuleImageDeferredCoverageTests
{
    private static readonly ModuleImageSourceState CleanSource = new(
        "feature/coverage",
        "0123456789ab",
        IsDirty: false,
        StatusFingerprint: "CLEAN");

    [Fact]
    public async Task Clean_explicit_run_tag_reuses_without_a_redundant_retag()
    {
        var options = CreateOptions();
        options.ImageTag = ModuleImageBuildRecipe.LocalRunTag;
        var recipe = CreateRecipe(options);
        var operations = new RecordingOperations(CleanSource)
        {
            ImageExists = true
        };

        var prepared = await ModuleImageRecipeEvaluator.PrepareAsync(
            recipe,
            NullLogger.Instance,
            NullLogger.Instance,
            operations,
            TestContext.Current.CancellationToken);

        Assert.Equal(recipe.LocalImageReference, prepared.CanonicalImageReference);
        Assert.Equal(ModuleImagePreparationDisposition.Reused, prepared.Disposition);
        Assert.Empty(operations.Tags);
    }

    [Fact]
    public async Task Clean_refresh_without_an_upstream_skips_refresh_and_reuses_the_image()
    {
        var recipe = CreateRecipe(refreshCleanCheckout: true);
        var operations = new RecordingOperations(CleanSource)
        {
            ImageExists = true,
            HasUpstream = false
        };

        await ModuleImageRecipeEvaluator.PrepareAsync(
            recipe,
            NullLogger.Instance,
            NullLogger.Instance,
            operations,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, operations.HasUpstreamCount);
        Assert.Equal(0, operations.RefreshCount);
        Assert.Equal(1, operations.ImageExistsCount);
    }

    [Fact]
    public async Task Cancellation_before_preparation_does_not_invoke_operations()
    {
        var operations = new RecordingOperations(CleanSource);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ModuleImageRecipeEvaluator.PrepareAsync(
                CreateRecipe(),
                NullLogger.Instance,
                NullLogger.Instance,
                operations,
                cancellation.Token));

        Assert.Equal(0, operations.ResolveRuntimeCount);
        Assert.Equal(0, operations.CaptureCount);
    }

    [Fact]
    public async Task Publisher_rejects_missing_loggers_before_starting_preparation()
    {
        var publisher = CreatePublisher(CreateRecipe());

        await Assert.ThrowsAsync<ArgumentNullException>(() => publisher.PrepareAsync(
            null!,
            NullLogger.Instance,
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(() => publisher.PrepareAsync(
            NullLogger.Instance,
            null!,
            TestContext.Current.CancellationToken));
        Assert.False(publisher.TryGetPreparedImage(out _));
    }

    [Fact]
    public async Task Canonical_resolution_requires_preparation_when_requested()
    {
        var (resource, publisher) = CreatePublishedContainer(CreateRecipe());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ModuleEffectiveImageResolver.ResolveAsync(
                resource,
                TestContext.Current.CancellationToken,
                usePreparedPublisherImage: true));

        Assert.Contains("has not prepared", exception.Message, StringComparison.Ordinal);
        Assert.False(publisher.TryGetPreparedImage(out _));
    }

    [Fact]
    public async Task Prepared_explicit_registry_resolution_uses_canonical_not_stable_alias_identity()
    {
        var recipe = CreateRecipe();
        var (resource, publisher) = CreatePublishedContainer(recipe);
        var prepared = await publisher.PrepareAsync(
            NullLogger.Instance,
            NullLogger.Instance,
            TestContext.Current.CancellationToken);

        var resolved = await ModuleEffectiveImageResolver.ResolveAsync(
            resource,
            TestContext.Current.CancellationToken,
            usePreparedPublisherImage: true);

        Assert.Equal(prepared.CanonicalImageReference, resolved.Reference);
        Assert.Equal(prepared.CanonicalImageReference, resolved.PullReference);
        Assert.Equal(prepared.CanonicalImageReference, resolved.PushReference);
        Assert.Equal(ModuleImagePushTargetKind.ContainerRuntime, resolved.PushTargetKind);
        Assert.Equal("feature-coverage-0123456789ab", resolved.Tag);
        Assert.Equal("registry.example.test/acme/api:aspire-run", recipe.LocalImageReference);
    }

    [Fact]
    public async Task Prepared_registry_target_preserves_canonical_tag_in_remote_identity()
    {
        var builder = DistributedApplication.CreateBuilder();
        var registry = builder.AddContainerRegistry(
            "registry",
            "registry.example.test",
            "team");
        var options = new ModuleContainerExportOptions("acme/api", "dotnet", "--version");
        var recipe = CreateRecipe(options);
        var resource = builder
            .AddContainer("api", options.ImageName, ModuleImageBuildRecipe.LocalRunTag)
            .WithContainerRegistry(registry)
            .Resource;
        var publisher = CreatePublisher(recipe);
        resource.Annotations.Add(publisher);
        var prepared = await publisher.PrepareAsync(
            NullLogger.Instance,
            NullLogger.Instance,
            TestContext.Current.CancellationToken);

        var resolved = await ModuleEffectiveImageResolver.ResolveAsync(
            resource,
            TestContext.Current.CancellationToken,
            usePreparedPublisherImage: true);

        Assert.Equal(ModuleImagePushTargetKind.AspireRegistry, resolved.PushTargetKind);
        Assert.Equal(prepared.CanonicalImageReference, resolved.Reference);
        Assert.Equal("registry.example.test", resolved.PushImage!.Registry);
        Assert.Equal("team/acme/api", resolved.PushImage.Repository);
        Assert.Equal("feature-coverage-0123456789ab", resolved.PushImage.Tag);
    }

    [Fact]
    public async Task Description_prepares_once_uses_resource_logger_and_emits_runtime_arguments()
    {
        var recipe = CreateRecipe();
        var calls = 0;
        var publisher = CreatePublisher(
            recipe,
            (_, _, _, _) =>
            {
                calls++;
                return Task.FromResult(CreatePreparedImage(recipe));
            });
        var builder = DistributedApplication.CreateBuilder();
        var resource = builder
            .AddContainer("effective-api", recipe.Options.ImageName, ModuleImageBuildRecipe.LocalRunTag)
            .WithImageRegistry(recipe.Options.ImageRegistry)
            .Resource;
        resource.Annotations.Add(publisher);
        resource.Annotations.Add(new DistributedApplicationModuleResourceAnnotation(
            "coverage",
            "api",
            recipe.RepositoryPath,
            imported: true));
        var resourceLoggerRequests = 0;

        var document = await ModuleImageDescriptionPipeline.CreateDocumentAsync(
            [resource],
            ModuleImageSelection.All,
            TestContext.Current.CancellationToken,
            lifecycleLogger: NullLogger.Instance,
            resourceLoggerFactory: _ =>
            {
                resourceLoggerRequests++;
                return NullLogger.Instance;
            });
        var second = await ModuleImageDescriptionPipeline.CreateDocumentAsync(
            [resource],
            ModuleImageSelection.All,
            TestContext.Current.CancellationToken);

        var image = Assert.Single(document.Images);
        Assert.Equal("feature-coverage-0123456789ab", image.Tag);
        Assert.Equal(
            "registry.example.test/acme/api:feature-coverage-0123456789ab",
            image.Reference);
        Assert.Contains(image.Reference, image.Build!.Arguments);
        Assert.Single(second.Images);
        Assert.Equal(1, calls);
        Assert.Equal(1, resourceLoggerRequests);
    }

    [Fact]
    public async Task Workflow_manifest_uses_prepared_canonical_tag()
    {
        var recipe = CreateRecipe();
        var (resource, publisher) = CreatePublishedContainer(recipe);
        resource.Annotations.Add(new DistributedApplicationModuleResourceAnnotation(
            "coverage",
            "api",
            recipe.RepositoryPath,
            imported: true));
        await publisher.PrepareAsync(
            NullLogger.Instance,
            NullLogger.Instance,
            TestContext.Current.CancellationToken);

        var document = await ModuleImageManifestPipeline.CreateDocumentAsync(
            [resource],
            ModuleImageSelection.All,
            TestContext.Current.CancellationToken);

        var image = Assert.Single(document.Images);
        Assert.Equal("coverage", image.Module);
        Assert.Equal("api", image.Resource);
        Assert.Equal("feature-coverage-0123456789ab", image.Tag);
        Assert.Equal(
            "registry.example.test/acme/api:feature-coverage-0123456789ab",
            image.Reference);
    }

    [Fact]
    public async Task Default_operations_capture_clean_and_dirty_git_source_states()
    {
        using var repository = TemporaryDirectory.Create();
        var trackedFile = Path.Combine(repository.Path, "tracked.txt");
        File.WriteAllText(trackedFile, "clean");
        await RunGitAsync(repository.Path, "init");
        await RunGitAsync(repository.Path, "config", "user.email", "coverage@example.test");
        await RunGitAsync(repository.Path, "config", "user.name", "Coverage Tests");
        await RunGitAsync(repository.Path, "add", "tracked.txt");
        await RunGitAsync(repository.Path, "-c", "commit.gpgsign=false", "commit", "-m", "initial");
        var recipe = CreateRecipe(
            repositoryPath: repository.Path,
            workingDirectory: repository.Path,
            repository: repository.Path);

        var clean = await ModuleImageRecipeOperations.Instance.CaptureSourceStateAsync(
            recipe,
            TestContext.Current.CancellationToken);
        File.WriteAllText(trackedFile, "dirty");
        var dirty = await ModuleImageRecipeOperations.Instance.CaptureSourceStateAsync(
            recipe,
            TestContext.Current.CancellationToken);

        Assert.False(clean.IsDirty);
        Assert.NotNull(clean.Branch);
        Assert.Equal(12, clean.Commit!.Length);
        Assert.True(dirty.IsDirty);
        Assert.NotEqual(clean.StatusFingerprint, dirty.StatusFingerprint);
        Assert.False(await ModuleImageRecipeOperations.Instance.HasUpstreamAsync(
            recipe,
            TestContext.Current.CancellationToken));

        var progress = new List<string>();
        await ModuleImageRecipeOperations.Instance.RefreshRepositoryAsync(
            recipe,
            progress.Add,
            TestContext.Current.CancellationToken);
        Assert.NotEmpty(progress);
    }

    [Fact]
    public async Task Default_source_capture_reports_non_repository_as_inspection_failure()
    {
        using var directory = TemporaryDirectory.Create();
        var recipe = CreateRecipe(
            repositoryPath: directory.Path,
            workingDirectory: directory.Path);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ModuleImageRecipeOperations.Instance.CaptureSourceStateAsync(
                recipe,
                TestContext.Current.CancellationToken));

        Assert.Contains("Unable to inspect", exception.Message, StringComparison.Ordinal);
        Assert.Contains("coverage", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Default_build_operation_runs_cross_platform_managed_command_and_reports_output()
    {
        using var workingDirectory = TemporaryDirectory.Create();
        var recipe = CreateRecipe(
            options: new ModuleContainerExportOptions("acme/api", "dotnet", "--version"),
            repositoryPath: workingDirectory.Path,
            workingDirectory: workingDirectory.Path);
        var plan = new ModuleImageExecutionPlan(
            "acme/api:test",
            ProducedImageReference: null,
            PublishArguments: ["--version"]);
        var output = new List<string>();

        await ModuleImageRecipeOperations.Instance.BuildImageAsync(
            recipe,
            plan,
            output.Add,
            TestContext.Current.CancellationToken);

        Assert.NotEmpty(output);
    }

    [Fact]
    public async Task Default_build_and_tag_operations_surface_managed_command_failures()
    {
        using var workingDirectory = TemporaryDirectory.Create();
        var recipe = CreateRecipe(
            options: new ModuleContainerExportOptions("acme/api", "dotnet", "missing-coverage-command"),
            repositoryPath: workingDirectory.Path,
            workingDirectory: workingDirectory.Path);
        var invalidBuild = new ModuleImageExecutionPlan(
            "acme/api:test",
            ProducedImageReference: null,
            PublishArguments: ["missing-coverage-command"]);

        var buildException = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ModuleImageRecipeOperations.Instance.BuildImageAsync(
                recipe,
                invalidBuild,
                _ => { },
                TestContext.Current.CancellationToken));
        var tagException = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ModuleImageRecipeOperations.Instance.TagImageAsync(
                recipe,
                "dotnet",
                "source:test",
                "target:test",
                _ => { },
                TestContext.Current.CancellationToken));

        Assert.Contains("failed", buildException.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("failed to tag", tagException.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Default_build_operation_converts_its_own_timeout_to_a_clear_error()
    {
        using var workingDirectory = TemporaryDirectory.Create();
        var options = new ModuleContainerExportOptions("acme/api", "dotnet", "--info");
        var recipe = new ModuleImageBuildRecipe(
            "coverage",
            "api",
            options,
            workingDirectory.Path,
            workingDirectory.Path,
            repository: null,
            revision: null,
            refreshCleanCheckout: false,
            "git",
            "gh",
            TimeSpan.FromTicks(1));
        var plan = new ModuleImageExecutionPlan(
            "acme/api:test",
            ProducedImageReference: null,
            PublishArguments: ["--info"]);

        var exception = await Assert.ThrowsAsync<TimeoutException>(() =>
            ModuleImageRecipeOperations.Instance.BuildImageAsync(
                recipe,
                plan,
                _ => { },
                TestContext.Current.CancellationToken));

        Assert.Contains("exceeded", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Push_step_uses_image_manager_for_prepared_registry_image_without_a_runtime()
    {
        var builder = DistributedApplication.CreateBuilder();
        var registry = builder.AddContainerRegistry(
            "registry",
            "registry.example.test",
            "team");
        var recipe = CreateRecipe(new ModuleContainerExportOptions("acme/api", "dotnet", "--version"));
        var dirtySource = CleanSource with
        {
            IsDirty = true,
            StatusFingerprint = "DIRTY"
        };
        var executionPlan = ModuleImageExecutionPlan.Create(recipe, dirtySource);
        var prepared = new ModulePreparedImage(
            executionPlan.CanonicalImageReference,
            recipe.LocalImageReference,
            dirtySource,
            ModuleImagePreparationDisposition.Built);
        var publisher = CreatePublisher(
            recipe,
            (_, _, _, _) => Task.FromResult(prepared));
        var container = builder
            .AddContainer("api", recipe.Options.ImageName, ModuleImageBuildRecipe.LocalRunTag)
            .WithContainerRegistry(registry)
            .WithAnnotation(publisher);
        ModuleImagePushPipeline.AddPushStep(container);
        await publisher.PrepareAsync(
            NullLogger.Instance,
            NullLogger.Instance,
            TestContext.Current.CancellationToken);
        var imageManager = new RecordingImageManager();
        builder.Services.AddSingleton<IResourceContainerImageManager>(imageManager);

        await using var application = builder.Build();
        var step = Assert.Single(await CreatePipelineStepsAsync(
            container.Resource,
            WellKnownPipelineTags.PushContainerImage));
        await using var reportingStep = await new NullPublishingActivityReporter().CreateStepAsync(
            step.Name,
            TestContext.Current.CancellationToken);
        var pipelineContext = new PipelineContext(
            application.Services.GetRequiredService<DistributedApplicationModel>(),
            application.Services.GetRequiredService<DistributedApplicationExecutionContext>(),
            application.Services,
            NullLogger.Instance,
            TestContext.Current.CancellationToken);

        await step.Action(new PipelineStepContext
        {
            PipelineContext = pipelineContext,
            ReportingStep = reportingStep
        });

        Assert.Same(container.Resource, Assert.Single(imageManager.PushedResources));
    }

    [Fact]
    public async Task Push_step_uses_fake_runtime_for_container_target_and_clean_branch_alias()
    {
        using var runtime = new FakeContainerRuntimeEnvironment(FakeRuntimeMode.Success, configured: true);
        var builder = DistributedApplication.CreateBuilder();
        var recipe = CreateRecipe();
        var (resource, publisher) = CreatePublishedContainer(recipe);
        var container = builder.CreateResourceBuilder(resource);
        ModuleImagePushPipeline.AddPushStep(container);
        await publisher.PrepareAsync(
            NullLogger.Instance,
            NullLogger.Instance,
            TestContext.Current.CancellationToken);

        await using var application = builder.Build();
        var step = Assert.Single(await CreatePipelineStepsAsync(
            resource,
            WellKnownPipelineTags.PushContainerImage));
        await ExecutePipelineStepAsync(application, step);

        Assert.True(runtime.InvocationCount >= 3);
        Assert.Contains(
            runtime.Invocations,
            invocation => invocation.Contains("push", StringComparison.Ordinal));
        Assert.Contains(
            runtime.Invocations,
            invocation => invocation.Contains("tag", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Push_step_reports_missing_remote_target_after_step_creation()
    {
        var builder = DistributedApplication.CreateBuilder();
        var options = new ModuleContainerExportOptions("acme/api", "dotnet", "--version");
        var recipe = CreateRecipe(options);
        var resource = builder
            .AddContainer("api", options.ImageName, ModuleImageBuildRecipe.LocalRunTag)
            .WithImageRegistry("registry.example.test")
            .Resource;
        var publisher = CreatePublisher(recipe);
        resource.Annotations.Add(publisher);
        var container = builder.CreateResourceBuilder(resource);
        ModuleImagePushPipeline.AddPushStep(container);
        var step = Assert.Single(await CreatePipelineStepsAsync(
            resource,
            WellKnownPipelineTags.PushContainerImage));
        var image = resource.Annotations.OfType<ContainerImageAnnotation>().Last();
        resource.Annotations.Remove(image);
        resource.Annotations.Add(new ContainerImageAnnotation
        {
            Image = options.ImageName,
            Tag = ModuleImageBuildRecipe.LocalRunTag
        });
        resource.Annotations.Add(new ModuleImagePullMappingAnnotation(
            "registry.example.test/acme/api:remote"));
        await publisher.PrepareAsync(
            NullLogger.Instance,
            NullLogger.Instance,
            TestContext.Current.CancellationToken);

        await using var application = builder.Build();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ExecutePipelineStepAsync(application, step));

        Assert.Contains("remote image push target", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Manifest_pipeline_step_writes_and_logs_the_prepared_manifest()
    {
        using var output = TemporaryDirectory.Create();
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = ["--publisher", "manifest"],
            DisableDashboard = true
        });
        var recipe = CreateRecipe();
        var resource = builder
            .AddContainer("api", recipe.Options.ImageName, ModuleImageBuildRecipe.LocalRunTag)
            .WithImageRegistry(recipe.Options.ImageRegistry)
            .Resource;
        var publisher = CreatePublisher(recipe);
        resource.Annotations.Add(publisher);
        resource.Annotations.Add(new DistributedApplicationModuleResourceAnnotation(
            "coverage",
            "api",
            recipe.RepositoryPath,
            imported: true));
        await publisher.PrepareAsync(
            NullLogger.Instance,
            NullLogger.Instance,
            TestContext.Current.CancellationToken);
        builder.Services.AddSingleton<IPipelineOutputService>(new FixedPipelineOutputService(output.Path));
        var pipeline = new CapturingPipeline();
        ModuleImageManifestPipeline.Configure(new PipelineCapturingBuilder(builder, pipeline));
        var step = Assert.Single(pipeline.Steps);

        await using var application = builder.Build();
        await ExecutePipelineStepAsync(application, step);

        var path = Path.Combine(output.Path, ModuleImageManifestDocument.DefaultFileName);
        var manifest = await ModuleImageManifestDocument.LoadAsync(
            path,
            TestContext.Current.CancellationToken);
        var image = Assert.Single(manifest.Images);
        Assert.Equal("coverage", image.Module);
        Assert.Equal("feature-coverage-0123456789ab", image.Tag);
    }

    [Theory]
    [InlineData(FakeRuntimeMode.Success, true, true)]
    [InlineData(FakeRuntimeMode.Failure, false, false)]
    [InlineData(FakeRuntimeMode.Missing, false, false)]
    public async Task Container_inspector_handles_runtime_success_failure_and_missing_executable(
        FakeRuntimeMode mode,
        bool expectedExists,
        bool expectedPull)
    {
        using var runtime = new FakeContainerRuntimeEnvironment(mode, configured: true);

        var exists = await ContainerImageInspector.ExistsAsync(
            "acme/api:test",
            TestContext.Current.CancellationToken);
        var pulled = await ContainerImageInspector.PullAsync(
            "acme/api:test",
            TestContext.Current.CancellationToken);

        Assert.Equal(expectedExists, exists);
        Assert.Equal(expectedPull, pulled);
    }

    [Fact]
    public async Task Container_inspector_rejects_empty_references_before_resolving_runtime()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            ContainerImageInspector.ExistsAsync(
                " ",
                TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            ContainerImageInspector.PullAsync(
                string.Empty,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Runtime_adapter_probes_fake_docker_and_podman_without_real_runtime()
    {
        using var runtime = new FakeContainerRuntimeEnvironment(FakeRuntimeMode.Success, configured: false);

        var resolved = await ContainerRuntimeResolver.ResolveAsync(
            TestContext.Current.CancellationToken);
        var viaRecipeAdapter = await ModuleImageRecipeOperations.Instance.ResolveContainerRuntimeAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal("docker", resolved);
        Assert.Equal("docker", viaRecipeAdapter);
        Assert.Contains(
            runtime.Invocations,
            invocation => invocation.Contains("container", StringComparison.Ordinal));
    }

    private static ModuleContainerExportOptions CreateOptions() =>
        new(
            "acme/api",
            "dotnet",
            "publish",
            ModuleContainerExportOptions.ImageReferencePlaceholder,
            ModuleContainerExportOptions.ImageRepositoryPlaceholder,
            ModuleContainerExportOptions.ImageTagPlaceholder)
        {
            ImageRegistry = "registry.example.test"
        };

    private static ModuleImageBuildRecipe CreateRecipe(
        ModuleContainerExportOptions? options = null,
        bool refreshCleanCheckout = false,
        string repositoryPath = "/work/coverage",
        string workingDirectory = "/work/coverage",
        string? repository = "https://example.test/acme/coverage.git") =>
        new(
            "coverage",
            "api",
            options ?? CreateOptions(),
            repositoryPath,
            workingDirectory,
            repository,
            revision: null,
            refreshCleanCheckout,
            "git",
            "gh",
            TimeSpan.FromMinutes(2));

    private static ModuleImagePublisherAnnotation CreatePublisher(
        ModuleImageBuildRecipe recipe,
        Func<
            ModuleImageBuildRecipe,
            ILogger,
            ILogger,
            CancellationToken,
            Task<ModulePreparedImage>>? prepareAsync = null) =>
        new(
            ModuleResourceKind.Container,
            recipe,
            prepareAsync ?? ((_, _, _, _) => Task.FromResult(CreatePreparedImage(recipe))));

    private static (ContainerResource Resource, ModuleImagePublisherAnnotation Publisher)
        CreatePublishedContainer(ModuleImageBuildRecipe recipe)
    {
        var builder = DistributedApplication.CreateBuilder();
        var resource = builder
            .AddContainer("api", recipe.Options.ImageName, ModuleImageBuildRecipe.LocalRunTag)
            .WithImageRegistry(recipe.Options.ImageRegistry)
            .Resource;
        var publisher = CreatePublisher(recipe);
        resource.Annotations.Add(publisher);
        return (resource, publisher);
    }

    private static ModulePreparedImage CreatePreparedImage(ModuleImageBuildRecipe recipe)
    {
        var plan = ModuleImageExecutionPlan.Create(recipe, CleanSource);
        return new ModulePreparedImage(
            plan.CanonicalImageReference,
            recipe.LocalImageReference,
            CleanSource,
            ModuleImagePreparationDisposition.Built);
    }

    private static async Task RunGitAsync(string workingDirectory, params string[] arguments)
    {
        var result = await ModuleCliRunner.RunAsync(
            "git",
            arguments,
            workingDirectory,
            TimeSpan.FromMinutes(1),
            "prepare coverage repository",
            TestContext.Current.CancellationToken,
            static _ => { });
        Assert.True(result.IsSuccess, result.StandardError);
    }

    private static async Task<IReadOnlyList<PipelineStep>> CreatePipelineStepsAsync(
        IResource resource,
        string tag)
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

        return steps.Where(step => step.Tags.Contains(tag)).ToArray();
    }

    private static async Task ExecutePipelineStepAsync(
        DistributedApplication application,
        PipelineStep step)
    {
        await using var reportingStep = await new NullPublishingActivityReporter().CreateStepAsync(
            step.Name,
            TestContext.Current.CancellationToken);
        var pipelineContext = new PipelineContext(
            application.Services.GetRequiredService<DistributedApplicationModel>(),
            application.Services.GetRequiredService<DistributedApplicationExecutionContext>(),
            application.Services,
            NullLogger.Instance,
            TestContext.Current.CancellationToken);
        await step.Action(new PipelineStepContext
        {
            PipelineContext = pipelineContext,
            ReportingStep = reportingStep
        });
    }

    private sealed class RecordingOperations(ModuleImageSourceState sourceState)
        : IModuleImageRecipeOperations
    {
        public bool ImageExists { get; init; }

        public bool HasUpstream { get; init; }

        public int ResolveRuntimeCount { get; private set; }

        public int CaptureCount { get; private set; }

        public int HasUpstreamCount { get; private set; }

        public int RefreshCount { get; private set; }

        public int ImageExistsCount { get; private set; }

        public List<(string Source, string Target)> Tags { get; } = [];

        public Task<string> ResolveContainerRuntimeAsync(CancellationToken cancellationToken)
        {
            ResolveRuntimeCount++;
            return Task.FromResult("container-runtime");
        }

        public Task<ModuleImageSourceState> CaptureSourceStateAsync(
            ModuleImageBuildRecipe recipe,
            CancellationToken cancellationToken)
        {
            CaptureCount++;
            return Task.FromResult(sourceState);
        }

        public Task<bool> HasUpstreamAsync(
            ModuleImageBuildRecipe recipe,
            CancellationToken cancellationToken)
        {
            HasUpstreamCount++;
            return Task.FromResult(HasUpstream);
        }

        public Task RefreshRepositoryAsync(
            ModuleImageBuildRecipe recipe,
            Action<string> progress,
            CancellationToken cancellationToken)
        {
            RefreshCount++;
            return Task.CompletedTask;
        }

        public Task<bool> ImageExistsAsync(
            string imageReference,
            CancellationToken cancellationToken)
        {
            ImageExistsCount++;
            return Task.FromResult(ImageExists);
        }

        public Task<bool> PullImageAsync(
            string imageReference,
            CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task BuildImageAsync(
            ModuleImageBuildRecipe recipe,
            ModuleImageExecutionPlan plan,
            Action<string> progress,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task TagImageAsync(
            ModuleImageBuildRecipe recipe,
            string containerRuntime,
            string sourceImageReference,
            string targetImageReference,
            Action<string> progress,
            CancellationToken cancellationToken)
        {
            Tags.Add((sourceImageReference, targetImageReference));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingImageManager : IResourceContainerImageManager
    {
        public List<IResource> PushedResources { get; } = [];

        public Task BuildImageAsync(IResource resource, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task BuildImagesAsync(
            IEnumerable<IResource> resources,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task PushImageAsync(IResource resource, CancellationToken cancellationToken)
        {
            PushedResources.Add(resource);
            return Task.CompletedTask;
        }
    }

    public enum FakeRuntimeMode
    {
        Success,
        Failure,
        Missing
    }

    private sealed class FakeContainerRuntimeEnvironment : IDisposable
    {
        private readonly TemporaryDirectory _directory = TemporaryDirectory.Create();
        private readonly string? _originalPath = Environment.GetEnvironmentVariable("PATH");
        private readonly string? _originalRuntime = Environment.GetEnvironmentVariable("ASPIRE_CONTAINER_RUNTIME");
        private readonly string? _originalLegacyRuntime = Environment.GetEnvironmentVariable(
            "DOTNET_ASPIRE_CONTAINER_RUNTIME");
        private readonly string _invocationPath;

        public FakeContainerRuntimeEnvironment(FakeRuntimeMode mode, bool configured)
        {
            _invocationPath = Path.Combine(_directory.Path, "invocations.txt");
            if (mode is not FakeRuntimeMode.Missing)
            {
                CreateExecutable("docker", mode);
                CreateExecutable("podman", mode);
            }

            Environment.SetEnvironmentVariable(
                "PATH",
                string.IsNullOrEmpty(_originalPath)
                    ? _directory.Path
                    : $"{_directory.Path}{Path.PathSeparator}{_originalPath}");
            Environment.SetEnvironmentVariable(
                "ASPIRE_CONTAINER_RUNTIME",
                configured ? "docker" : null);
            Environment.SetEnvironmentVariable("DOTNET_ASPIRE_CONTAINER_RUNTIME", null);
        }

        public IReadOnlyList<string> Invocations => File.Exists(_invocationPath)
            ? File.ReadAllLines(_invocationPath)
            : [];

        public int InvocationCount => Invocations.Count;

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("PATH", _originalPath);
            Environment.SetEnvironmentVariable("ASPIRE_CONTAINER_RUNTIME", _originalRuntime);
            Environment.SetEnvironmentVariable("DOTNET_ASPIRE_CONTAINER_RUNTIME", _originalLegacyRuntime);
            _directory.Dispose();
        }

        private void CreateExecutable(string name, FakeRuntimeMode mode)
        {
            var exitCode = mode is FakeRuntimeMode.Success ? 0 : 7;
            if (OperatingSystem.IsWindows())
            {
                var path = Path.Combine(_directory.Path, $"{name}.cmd");
                File.WriteAllText(
                    path,
                    $"@echo off{Environment.NewLine}" +
                    $"echo %*>>\"{_invocationPath}\"{Environment.NewLine}" +
                    $"echo runtime stdout token=secret{Environment.NewLine}" +
                    $"echo runtime stderr https://user:secret@example.test/path?token=secret 1>&2{Environment.NewLine}" +
                    $"exit /b {exitCode}{Environment.NewLine}");
                return;
            }

            var executablePath = Path.Combine(_directory.Path, name);
            File.WriteAllText(
                executablePath,
                "#!/bin/sh\n" +
                $"printf '%s\\n' \"$*\" >> \"{_invocationPath}\"\n" +
                "printf '%s\\n' 'runtime stdout token=secret'\n" +
                "printf '%s\\n' 'runtime stderr https://user:secret@example.test/path?token=secret' >&2\n" +
                $"exit {exitCode}\n");
            File.SetUnixFileMode(
                executablePath,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.UserExecute);
        }
    }

    private sealed class PipelineCapturingBuilder(
        IDistributedApplicationBuilder inner,
        IDistributedApplicationPipeline pipeline) : IDistributedApplicationBuilder
    {
        public ConfigurationManager Configuration => inner.Configuration;

        public string AppHostDirectory => inner.AppHostDirectory;

        public Assembly? AppHostAssembly => inner.AppHostAssembly;

        public IHostEnvironment Environment => inner.Environment;

        public IServiceCollection Services => inner.Services;

        public IDistributedApplicationEventing Eventing => inner.Eventing;

        public DistributedApplicationExecutionContext ExecutionContext => inner.ExecutionContext;

        public IResourceCollection Resources => inner.Resources;

        public IDistributedApplicationPipeline Pipeline => pipeline;

        public IFileSystemService FileSystemService => inner.FileSystemService;

        public IUserSecretsManager UserSecretsManager => inner.UserSecretsManager;

        public IResourceBuilder<T> AddResource<T>(T resource)
            where T : IResource => inner.AddResource(resource);

        public IResourceBuilder<T> CreateResourceBuilder<T>(T resource)
            where T : IResource => inner.CreateResourceBuilder(resource);

        public DistributedApplication Build() => inner.Build();
    }

    private sealed class CapturingPipeline : IDistributedApplicationPipeline
    {
        public IList<PipelineStep> Steps { get; } = [];

        public void AddStep(
            string name,
            Func<PipelineStepContext, Task> action,
            object? dependsOn = null,
            object? requiredBy = null) => throw new NotSupportedException();

        public void AddStep(PipelineStep step) => Steps.Add(step);

        public void AddPipelineConfiguration(Func<PipelineConfigurationContext, Task> callback)
        {
        }

        public Task ExecuteAsync(PipelineContext context) => throw new NotSupportedException();
    }

    private sealed class FixedPipelineOutputService(string path) : IPipelineOutputService
    {
        public string GetOutputDirectory() => path;

        public string GetOutputDirectory(IResource resource) => path;

        public string GetTempDirectory() => path;

        public string GetTempDirectory(IResource resource) => path;
    }
}

[CollectionDefinition("Module image runtime adapter coverage", DisableParallelization = true)]
public sealed class ModuleImageRuntimeAdapterCoverageCollection;
