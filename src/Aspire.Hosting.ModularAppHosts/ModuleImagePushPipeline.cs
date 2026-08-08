#pragma warning disable ASPIRECOMPUTE003
#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES003

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Publishing;
using CliWrap;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using CliCommand = global::CliWrap.Cli;

namespace Aspire.Hosting.ModularAppHosts;

internal static class ModuleImagePushPipeline
{
    private static readonly Action<ILogger, string, string, Exception?> LogContainerRuntimeOutput =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            new EventId(1, nameof(LogContainerRuntimeOutput)),
            "{ContainerRuntime}: {Output}");

    private static readonly Action<ILogger, string, string, Exception?> LogContainerRuntimeError =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(2, nameof(LogContainerRuntimeError)),
            "{ContainerRuntime}: {Output}");

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

    public static void AddPushStep(IResourceBuilder<ContainerResource> container)
    {
        ArgumentNullException.ThrowIfNull(container);

        container.WithPipelineStepFactory(factoryContext =>
        {
            var resource = factoryContext.Resource;
            if (resource.IsExcludedFromPublish() ||
                resource.RequiresImageBuildAndPush() ||
                !ModuleEffectiveImageResolver.HasPushTarget(resource))
            {
                return [];
            }

            return
            [
                new PipelineStep
                {
                    Name = $"push-{resource.Name}",
                    Description = $"Pushes the existing container image for the {resource.Name} resource.",
                    Action = context => PushAsync(resource, context),
                    DependsOnSteps =
                    [
                        ModuleImageBuildPipeline.GetStepName(resource),
                        WellKnownPipelineSteps.PushPrereq,
                        WellKnownPipelineSteps.CheckContainerRuntime
                    ],
                    RequiredBySteps = [WellKnownPipelineSteps.Push],
                    Tags = [WellKnownPipelineTags.PushContainerImage],
                    Resource = resource
                }
            ];
        });
    }

    internal static ModuleImageSelection GetSelection(IReadOnlyList<string> arguments) =>
        ModuleImagePipelineSelectionParser.GetSelection(arguments, WellKnownPipelineSteps.Push);

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

        var pushSteps = steps
            .Where(step =>
                step.Resource is not null &&
                step.Tags.Contains(WellKnownPipelineTags.PushContainerImage) &&
                step.RequiredBySteps.Contains(WellKnownPipelineSteps.Push))
            .ToArray();
        var selectedResources = selection.ResolveResources(
            pushSteps.Select(step => step.Resource!),
            "image push steps");

        foreach (var step in pushSteps.Where(step => !selectedResources.Contains(step.Resource!)))
        {
            step.RequiredBySteps.RemoveAll(requiredBy =>
                string.Equals(requiredBy, WellKnownPipelineSteps.Push, StringComparison.Ordinal));

            var resource = step.Resource!;
            if (!resource.IsExcludedFromPublish())
            {
                resource.Annotations.Add(ManifestPublishingCallbackAnnotation.Ignore);
            }
        }
    }

    private static async Task PushAsync(IResource resource, PipelineStepContext context)
    {
        var resolved = await ModuleEffectiveImageResolver.ResolveAsync(
            resource,
            context.CancellationToken).ConfigureAwait(false);
        if (resolved.PushTargetKind == ModuleImagePushTargetKind.ContainerRuntime)
        {
            var runtime = await ContainerRuntimeResolver.ResolveAsync(context.CancellationToken).ConfigureAwait(false);
            await CliCommand.Wrap(runtime)
                .WithArguments(["push", resolved.PushReference!])
                .WithStandardOutputPipe(PipeTarget.ToDelegate(line =>
                    LogContainerRuntimeOutput(context.Logger, runtime, line, null)))
                .WithStandardErrorPipe(PipeTarget.ToDelegate(line =>
                    LogContainerRuntimeError(context.Logger, runtime, line, null)))
                .ExecuteAsync(context.CancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (resolved.PushTargetKind != ModuleImagePushTargetKind.AspireRegistry)
        {
            throw new InvalidOperationException(
                $"Resource '{resource.Name}' does not have a remote image push target.");
        }

        var imageManager = context.Services.GetRequiredService<IResourceContainerImageManager>();
        await imageManager.PushImageAsync(resource, context.CancellationToken).ConfigureAwait(false);
    }
}
