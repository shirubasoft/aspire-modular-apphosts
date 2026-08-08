#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES003

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using CliWrap;
using Microsoft.Extensions.Logging;
using CliCommand = global::CliWrap.Cli;

namespace Aspire.Hosting.ModularAppHosts;

internal static class ModuleImageBuildPipeline
{
    internal const string BuildContainerImageTag = "build-module-container-image";

    private static readonly Action<ILogger, string, string, Exception?> LogBuildStarted =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            new EventId(1, nameof(LogBuildStarted)),
            "Building image {ImageReference} for resource {ResourceName}.");

    private static readonly Action<ILogger, string, string, Exception?> LogImageAvailable =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            new EventId(2, nameof(LogImageAvailable)),
            "Using available image {ImageReference} for resource {ResourceName}.");

    private static readonly Action<ILogger, string, string, Exception?> LogCommandOutput =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            new EventId(3, nameof(LogCommandOutput)),
            "{Command}: {Output}");

    private static readonly Action<ILogger, string, string, Exception?> LogCommandError =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(4, nameof(LogCommandError)),
            "{Command}: {Output}");

    public static void ConfigureResourceSelection(IDistributedApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var selection = GetSelection(Environment.GetCommandLineArgs());
        if (!selection.IsScoped)
        {
            return;
        }

        builder.Pipeline.AddPipelineConfiguration(context =>
        {
            ApplySelection(context.Steps, selection);
            return Task.CompletedTask;
        });
    }

    public static void AddBuildStep(IResourceBuilder<ContainerResource> container)
    {
        ArgumentNullException.ThrowIfNull(container);
        container.WithPipelineStepFactory(factoryContext =>
        {
            var resource = factoryContext.Resource;
            if (resource.IsExcludedFromPublish() ||
                resource.Annotations.OfType<ModuleImagePublisherAnnotation>().LastOrDefault() is null)
            {
                return [];
            }

            return
            [
                new PipelineStep
                {
                    Name = GetStepName(resource),
                    Description = $"Builds the effective module container image for {resource.Name}.",
                    Action = context => BuildAsync(resource, context),
                    DependsOnSteps =
                    [
                        WellKnownPipelineSteps.BuildPrereq,
                        WellKnownPipelineSteps.CheckContainerRuntime
                    ],
                    RequiredBySteps = [WellKnownPipelineSteps.Build],
                    Tags = [BuildContainerImageTag, WellKnownPipelineTags.BuildCompute],
                    Resource = resource
                }
            ];
        });
    }

    internal static string GetStepName(IResource resource) => $"build-{resource.Name}";

    internal static bool ShouldPrepareBuildRepository(
        IReadOnlyList<string> arguments,
        string moduleName,
        string declaredResourceName,
        string effectiveResourceName)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);
        ArgumentException.ThrowIfNullOrWhiteSpace(declaredResourceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(effectiveResourceName);
        var requestedStep = ModuleImagePipelineSelectionParser.GetRequestedStep(arguments);
        if (requestedStep is null)
        {
            return true;
        }

        if (string.Equals(requestedStep, ModuleImagePullPipeline.PullStepName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(requestedStep, ModuleImageDescriptionPipeline.StepName, StringComparison.OrdinalIgnoreCase) ||
            requestedStep.StartsWith("pull-", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(requestedStep, WellKnownPipelineSteps.Build, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(requestedStep, WellKnownPipelineSteps.Push, StringComparison.OrdinalIgnoreCase))
        {
            var selection = ModuleImagePipelineSelectionParser.GetSelection(arguments, requestedStep);
            return !selection.IsScoped ||
                selection.Includes(moduleName, declaredResourceName, effectiveResourceName);
        }

        if (requestedStep.StartsWith("build-", StringComparison.OrdinalIgnoreCase) ||
            requestedStep.StartsWith("push-", StringComparison.OrdinalIgnoreCase))
        {
            var separator = requestedStep.IndexOf('-', StringComparison.Ordinal);
            var selectedResource = requestedStep[(separator + 1)..];
            return string.Equals(selectedResource, declaredResourceName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(selectedResource, effectiveResourceName, StringComparison.OrdinalIgnoreCase);
        }

        return true;
    }

    internal static ModuleImageSelection GetSelection(IReadOnlyList<string> arguments) =>
        ModuleImagePipelineSelectionParser.GetSelection(arguments, WellKnownPipelineSteps.Build);

    internal static void ApplySelection(
        IReadOnlyList<PipelineStep> steps,
        ModuleImageSelection selection)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(selection);
        if (!selection.IsScoped)
        {
            return;
        }

        var buildSteps = steps
            .Where(step =>
                step.Resource is not null &&
                step.Tags.Contains(BuildContainerImageTag) &&
                step.RequiredBySteps.Contains(WellKnownPipelineSteps.Build))
            .ToArray();
        var selectedResources = selection.ResolveResources(
            buildSteps.Select(step => step.Resource!),
            "image build steps");

        foreach (var step in buildSteps.Where(step => !selectedResources.Contains(step.Resource!)))
        {
            step.RequiredBySteps.RemoveAll(requiredBy =>
                string.Equals(requiredBy, WellKnownPipelineSteps.Build, StringComparison.Ordinal));
        }
    }

    private static Task BuildAsync(IResource resource, PipelineStepContext context) =>
        BuildAsync(
            resource,
            context,
            ContainerImageInspector.ExistsAsync,
            ContainerImageInspector.PullAsync,
            ExecuteBuildCommandAsync,
            ExecuteRetagAsync);

    internal static async Task BuildAsync(
        IResource resource,
        PipelineStepContext context,
        Func<string, CancellationToken, Task<bool>> imageExistsAsync,
        Func<string, CancellationToken, Task<bool>> pullImageAsync,
        Func<ModuleImagePublisherAnnotation, PipelineStepContext, Task> executeBuildAsync,
        Func<string, string, PipelineStepContext, Task> retagAsync)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(context);
        var publisher = resource.Annotations.OfType<ModuleImagePublisherAnnotation>().LastOrDefault()
            ?? throw new InvalidOperationException(
                $"Resource '{resource.Name}' does not have a module image publisher.");
        var plan = publisher.Plan;
        if (!plan.RepositoryDirty)
        {
            if (await imageExistsAsync(plan.ImageReference, context.CancellationToken).ConfigureAwait(false) ||
                (publisher.Options.PullBeforeBuild &&
                 await pullImageAsync(plan.ImageReference, context.CancellationToken).ConfigureAwait(false)))
            {
                LogImageAvailable(context.Logger, plan.ImageReference, resource.Name, null);
                return;
            }
        }

        LogBuildStarted(context.Logger, plan.ImageReference, resource.Name, null);
        await executeBuildAsync(publisher, context).ConfigureAwait(false);
        if (plan.RequiresRetag)
        {
            await retagAsync(
                plan.ProducedImageReference!,
                plan.ImageReference,
                context).ConfigureAwait(false);
        }
    }

    private static async Task ExecuteBuildCommandAsync(
        ModuleImagePublisherAnnotation publisher,
        PipelineStepContext context)
    {
        await CliCommand.Wrap(publisher.Options.PublishCommand)
            .WithArguments(publisher.Plan.PublishArguments)
            .WithWorkingDirectory(publisher.WorkingDirectory)
            .WithEnvironmentVariables(new Dictionary<string, string?>
            {
                ["ASPIRE_MODULE_IMAGE"] = publisher.Plan.ImageReference
            })
            .WithStandardOutputPipe(PipeTarget.ToDelegate(line =>
                LogCommandOutput(context.Logger, publisher.Options.PublishCommand, line, null)))
            .WithStandardErrorPipe(PipeTarget.ToDelegate(line =>
                LogCommandError(context.Logger, publisher.Options.PublishCommand, line, null)))
            .ExecuteAsync(context.CancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task ExecuteRetagAsync(
        string source,
        string target,
        PipelineStepContext context)
    {
        var runtime = await ContainerRuntimeResolver.ResolveAsync(context.CancellationToken).ConfigureAwait(false);
        await CliCommand.Wrap(runtime)
            .WithArguments(["tag", source, target])
            .WithStandardOutputPipe(PipeTarget.ToDelegate(line =>
                LogCommandOutput(context.Logger, runtime, line, null)))
            .WithStandardErrorPipe(PipeTarget.ToDelegate(line =>
                LogCommandError(context.Logger, runtime, line, null)))
            .ExecuteAsync(context.CancellationToken)
            .ConfigureAwait(false);
    }
}
