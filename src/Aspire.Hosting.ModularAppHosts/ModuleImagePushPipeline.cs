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

namespace Aspire.Hosting;

internal static class ModuleImagePushPipeline
{
    private static readonly Action<ILogger, string, string, Exception?> LogContainerRuntimeOutput =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            new EventId(1, nameof(LogContainerRuntimeOutput)),
            "{ContainerRuntime}: {Output}");

    private static readonly Action<ILogger, string, string, Exception?> LogBranchAlias =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            new EventId(3, nameof(LogBranchAlias)),
            "Publishing branch image alias {Alias} for resource {Resource}.");

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
                    Description = $"Pushes the existing container image and branch alias for the {resource.Name} resource.",
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
        var resourceLogger = context.Services
            .GetRequiredService<ResourceLoggerService>()
            .GetLogger(resource);
        var resolved = await ModuleEffectiveImageResolver.ResolveAsync(
            resource,
            context.CancellationToken,
            usePreparedPublisherImage: true).ConfigureAwait(false);
        string? runtime = null;
        if (resolved.PushTargetKind == ModuleImagePushTargetKind.ContainerRuntime)
        {
            runtime = await ContainerRuntimeResolver.ResolveAsync(context.CancellationToken).ConfigureAwait(false);
            await RunContainerRuntimeAsync(
                runtime,
                ["push", resolved.PushReference!],
                context,
                resourceLogger).ConfigureAwait(false);
        }
        else if (resolved.PushTargetKind == ModuleImagePushTargetKind.AspireRegistry)
        {
            var imageManager = context.Services.GetRequiredService<IResourceContainerImageManager>();
            await imageManager.PushImageAsync(resource, context.CancellationToken).ConfigureAwait(false);
        }
        else
        {
            throw new InvalidOperationException(
                $"Resource '{resource.Name}' does not have a remote image push target.");
        }

        var branchAlias = GetBranchAliasReference(resource, resolved);
        if (branchAlias is null)
        {
            return;
        }

        runtime ??= await ContainerRuntimeResolver.ResolveAsync(context.CancellationToken).ConfigureAwait(false);
        LogBranchAlias(context.Logger, branchAlias, resource.Name, null);
        LogBranchAlias(resourceLogger, branchAlias, resource.Name, null);
        await RunContainerRuntimeAsync(
            runtime,
            ["tag", resolved.Reference, branchAlias],
            context,
            resourceLogger).ConfigureAwait(false);
        await RunContainerRuntimeAsync(
            runtime,
            ["push", branchAlias],
            context,
            resourceLogger).ConfigureAwait(false);
    }

    internal static string? GetBranchAliasReference(IResource resource, ModuleEffectiveImage resolved)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(resolved);
        var publisher = resource.Annotations.OfType<ModuleImagePublisherAnnotation>().LastOrDefault();
        if (publisher is null ||
            !publisher.TryGetPreparedImage(out var preparedImage) ||
            preparedImage.SourceState.IsDirty ||
            string.IsNullOrWhiteSpace(preparedImage.SourceState.Branch) ||
            resolved.PushImage is null)
        {
            return null;
        }

        var branchImageTag = ModuleImageTag.FromBranch(preparedImage.SourceState.Branch);
        var alias = $"{resolved.PushImage.Registry}/{resolved.PushImage.Repository}:{branchImageTag}";
        return string.Equals(alias, resolved.PushReference, StringComparison.OrdinalIgnoreCase)
            ? null
            : alias;
    }

    private static async Task RunContainerRuntimeAsync(
        string runtime,
        IReadOnlyList<string> arguments,
        PipelineStepContext context,
        ILogger resourceLogger)
    {
        await CliCommand.Wrap(runtime)
            .WithArguments(arguments)
            .WithStandardOutputPipe(PipeTarget.ToDelegate(line =>
                LogContainerRuntimeOutput(
                    resourceLogger,
                    ModuleCliOutputRedactor.Redact(runtime),
                    ModuleCliOutputRedactor.Redact(line),
                    null)))
            .WithStandardErrorPipe(PipeTarget.ToDelegate(line =>
                LogContainerRuntimeOutput(
                    resourceLogger,
                    ModuleCliOutputRedactor.Redact(runtime),
                    ModuleCliOutputRedactor.Redact(line),
                    null)))
            .ExecuteAsync(context.CancellationToken)
            .ConfigureAwait(false);
    }
}
