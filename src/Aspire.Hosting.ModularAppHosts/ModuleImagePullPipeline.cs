#pragma warning disable ASPIRECOMPUTE003
#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES003

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using CliWrap;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using CliCommand = global::CliWrap.Cli;

namespace Aspire.Hosting.ModularAppHosts;

internal static class ModuleImagePullPipeline
{
    internal const string PullStepName = "pull";
    internal const string PullPrerequisiteStepName = "pull-prereq";
    internal const string PullContainerImageTag = "pull-container-image";

    private static readonly Action<ILogger, string, string, Exception?> LogContainerRuntimeOutput =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            new EventId(3, nameof(LogContainerRuntimeOutput)),
            "{ContainerRuntime}: {Output}");

    private static readonly Action<ILogger, string, string, Exception?> LogContainerRuntimeError =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(4, nameof(LogContainerRuntimeError)),
            "{ContainerRuntime}: {Output}");

    private static readonly Action<ILogger, string, string, Exception?> LogImagePullStarted =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            new EventId(5, nameof(LogImagePullStarted)),
            "Pulling remote image {RemoteImage} for resource {ResourceName}.");

    private static readonly Action<ILogger, string, string, string, Exception?> LogImageRetagStarted =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Information,
            new EventId(6, nameof(LogImageRetagStarted)),
            "Re-tagging remote image {RemoteImage} as local image {LocalImage} for resource {ResourceName}.");

    private static readonly Action<ILogger, string, string, string, Exception?> LogImageRetagCompleted =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Information,
            new EventId(7, nameof(LogImageRetagCompleted)),
            "Re-tagged remote image {RemoteImage} as local image {LocalImage} for resource {ResourceName}.");

    public static void Configure(IDistributedApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Pipeline.AddStep(new PipelineStep
        {
            Name = PullStepName,
            Description = "Aggregation step for all modular container image pull operations.",
            Action = _ => Task.CompletedTask,
            DependsOnSteps = [PullPrerequisiteStepName]
        });
        builder.Pipeline.AddStep(new PipelineStep
        {
            Name = PullPrerequisiteStepName,
            Description = "Prerequisite step that runs before modular container image pulls.",
            Action = _ => Task.CompletedTask
        });

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

    public static void AddPullStep(IResourceBuilder<ContainerResource> container)
    {
        ArgumentNullException.ThrowIfNull(container);

        container.WithPipelineStepFactory(factoryContext =>
        {
            var resource = factoryContext.Resource;
            if (resource.IsExcludedFromPublish() || !ModuleEffectiveImageResolver.HasPullSource(resource))
            {
                return [];
            }

            return
            [
                new PipelineStep
                {
                    Name = $"pull-{resource.Name}",
                    Description = $"Pulls the container image for the {resource.Name} resource.",
                    Action = context => PullAsync(resource, context),
                    DependsOnSteps =
                    [
                        PullPrerequisiteStepName,
                        WellKnownPipelineSteps.CheckContainerRuntime
                    ],
                    RequiredBySteps = [PullStepName],
                    Tags = [PullContainerImageTag],
                    Resource = resource
                }
            ];
        });
    }

    internal static ModuleImageSelection GetSelection(IReadOnlyList<string> arguments) =>
        ModuleImagePipelineSelectionParser.GetSelection(arguments, PullStepName);

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

        var pullSteps = steps
            .Where(step =>
                step.Resource is not null &&
                step.Tags.Contains(PullContainerImageTag) &&
                step.RequiredBySteps.Contains(PullStepName))
            .ToArray();
        var availableResources = pullSteps
            .SelectMany(step => ModuleImageSelection.GetNames(step.Resource!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknownResources = selection.Resources
            .Where(resource => !pullSteps.Any(step => ModuleImageSelection.NameMatches(step.Resource!, resource)))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (unknownResources.Length > 0)
        {
            var available = availableResources.Count == 0
                ? "none"
                : string.Join(", ", availableResources.Order(StringComparer.OrdinalIgnoreCase));
            throw new InvalidOperationException(
                $"The following resources do not contribute image pull steps: {string.Join(", ", unknownResources)}. " +
                $"Available image resources: {available}.");
        }

        foreach (var step in pullSteps.Where(step => !selection.Includes(step.Resource!)))
        {
            step.RequiredBySteps.RemoveAll(requiredBy =>
                string.Equals(requiredBy, PullStepName, StringComparison.Ordinal));
        }
    }

    internal static async Task<(string RemoteImage, string LocalImage)> ResolveImageReferencesAsync(
        IResource resource,
        CancellationToken cancellationToken)
    {
        var resolved = await ModuleEffectiveImageResolver.ResolveAsync(resource, cancellationToken)
            .ConfigureAwait(false);
        return (resolved.PullReference, resolved.Reference);
    }

    private static Task PullAsync(IResource resource, PipelineStepContext context) =>
        PullAsync(
            resource,
            context,
            ContainerRuntimeResolver.ResolveAsync,
            ExecuteRuntimeAsync);

    internal static async Task PullAsync(
        IResource resource,
        PipelineStepContext context,
        Func<CancellationToken, Task<string>> resolveRuntimeAsync,
        Func<string, IReadOnlyList<string>, PipelineStepContext, Task> executeRuntimeAsync)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(resolveRuntimeAsync);
        ArgumentNullException.ThrowIfNull(executeRuntimeAsync);

        var (remoteImage, localImage) = await ResolveImageReferencesAsync(
            resource,
            context.CancellationToken).ConfigureAwait(false);
        var runtime = await resolveRuntimeAsync(context.CancellationToken)
            .ConfigureAwait(false);
        var resourceLogger = context.Services
            .GetRequiredService<ResourceLoggerService>()
            .GetLogger(resource);

        LogImagePullStarted(context.Logger, remoteImage, resource.Name, null);
        LogImagePullStarted(resourceLogger, remoteImage, resource.Name, null);

        await executeRuntimeAsync(
            runtime,
            ["pull", remoteImage],
            context).ConfigureAwait(false);
        if (!string.Equals(remoteImage, localImage, StringComparison.Ordinal))
        {
            LogImageRetagStarted(context.Logger, remoteImage, localImage, resource.Name, null);
            LogImageRetagStarted(resourceLogger, remoteImage, localImage, resource.Name, null);
            await executeRuntimeAsync(
                runtime,
                ["tag", remoteImage, localImage],
                context).ConfigureAwait(false);
            LogImageRetagCompleted(context.Logger, remoteImage, localImage, resource.Name, null);
            LogImageRetagCompleted(resourceLogger, remoteImage, localImage, resource.Name, null);
        }
    }

    private static async Task ExecuteRuntimeAsync(
        string runtime,
        IReadOnlyList<string> arguments,
        PipelineStepContext context)
    {
        await CliCommand.Wrap(runtime)
            .WithArguments(arguments)
            .WithStandardOutputPipe(PipeTarget.ToDelegate(line =>
                LogContainerRuntimeOutput(context.Logger, runtime, line, null)))
            .WithStandardErrorPipe(PipeTarget.ToDelegate(line =>
                LogContainerRuntimeError(context.Logger, runtime, line, null)))
            .ExecuteAsync(context.CancellationToken)
            .ConfigureAwait(false);
    }
}
