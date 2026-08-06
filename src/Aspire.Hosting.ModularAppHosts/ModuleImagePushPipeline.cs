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

    private static readonly HashSet<string> OptionsWithValues = new(StringComparer.Ordinal)
    {
        "--operation",
        "--step",
        "--output-path",
        "--log-level",
        "--include-exception-details",
        "--environment",
        "--clear-cache",
        "--yes",
        "--dcp-cli-path",
        "--dcp-container-runtime",
        "--dcp-dependency-check-timeout",
        "--dcp-dashboard-path"
    };

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
                !HasPushTarget(resource))
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

    internal static ModuleImagePushSelection GetSelection(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var resourceArgumentsStart = -1;
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (string.Equals(argument, "--step", StringComparison.Ordinal) && index + 1 < arguments.Count)
            {
                if (string.Equals(arguments[index + 1], WellKnownPipelineSteps.Push, StringComparison.OrdinalIgnoreCase))
                {
                    resourceArgumentsStart = index + 2;
                }

                break;
            }

            const string stepPrefix = "--step=";
            if (argument.StartsWith(stepPrefix, StringComparison.Ordinal))
            {
                if (string.Equals(argument[stepPrefix.Length..], WellKnownPipelineSteps.Push, StringComparison.OrdinalIgnoreCase))
                {
                    resourceArgumentsStart = index + 1;
                }

                break;
            }
        }

        if (resourceArgumentsStart < 0)
        {
            return ModuleImagePushSelection.All;
        }

        var resources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var positionalOnly = false;
        for (var index = resourceArgumentsStart; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (positionalOnly)
            {
                resources.Add(argument);
                continue;
            }

            if (string.Equals(argument, "--", StringComparison.Ordinal))
            {
                positionalOnly = true;
                continue;
            }

            if (OptionsWithValues.Contains(argument))
            {
                index++;
                continue;
            }

            if (OptionsWithValues.Any(option => argument.StartsWith($"{option}=", StringComparison.Ordinal)))
            {
                continue;
            }

            if (argument.StartsWith('-'))
            {
                continue;
            }

            resources.Add(argument);
        }

        return resources.Count == 0
            ? ModuleImagePushSelection.All
            : new ModuleImagePushSelection(resources);
    }

    internal static void ApplySelection(
        IReadOnlyList<PipelineStep> steps,
        ModuleImagePushSelection selection)
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
        var availableResources = pushSteps
            .Select(step => step.Resource!.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknownResources = selection.Resources
            .Where(resource => !availableResources.Contains(resource))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (unknownResources.Length > 0)
        {
            var available = availableResources.Count == 0
                ? "none"
                : string.Join(", ", availableResources.Order(StringComparer.OrdinalIgnoreCase));
            throw new InvalidOperationException(
                $"The following resources do not contribute image push steps: {string.Join(", ", unknownResources)}. " +
                $"Available image resources: {available}.");
        }

        foreach (var step in pushSteps.Where(step => !selection.Includes(step.Resource!.Name)))
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

    private static bool HasPushTarget(IResource resource)
    {
        var image = resource.Annotations.OfType<ContainerImageAnnotation>().LastOrDefault();
        if (image is { SHA256.Length: > 0 })
        {
            return false;
        }

        return image is { Registry.Length: > 0 } ||
            resource.Annotations.OfType<ContainerRegistryReferenceAnnotation>().Any() ||
            resource.Annotations.OfType<DeploymentTargetAnnotation>().Any(annotation =>
                annotation.ContainerRegistry is not null) ||
            resource.Annotations.OfType<RegistryTargetAnnotation>().Any();
    }

    private static async Task PushAsync(IResource resource, PipelineStepContext context)
    {
        var image = resource.Annotations.OfType<ContainerImageAnnotation>().LastOrDefault();
        var hasExplicitAspireRegistry =
            resource.Annotations.OfType<ContainerRegistryReferenceAnnotation>().Any() ||
            resource.Annotations.OfType<DeploymentTargetAnnotation>().Any(annotation =>
                annotation.ContainerRegistry is not null);
        if (!hasExplicitAspireRegistry && image is { Registry.Length: > 0 })
        {
            if (!resource.TryGetContainerImageName(out var imageReference))
            {
                throw new InvalidOperationException(
                    $"Resource '{resource.Name}' does not have a container image reference to push.");
            }

            var runtime = await ContainerRuntimeResolver.ResolveAsync(context.CancellationToken).ConfigureAwait(false);
            await CliCommand.Wrap(runtime)
                .WithArguments(["push", imageReference])
                .WithStandardOutputPipe(PipeTarget.ToDelegate(line =>
                    LogContainerRuntimeOutput(context.Logger, runtime, line, null)))
                .WithStandardErrorPipe(PipeTarget.ToDelegate(line =>
                    LogContainerRuntimeError(context.Logger, runtime, line, null)))
                .ExecuteAsync(context.CancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var imageManager = context.Services.GetRequiredService<IResourceContainerImageManager>();
        await imageManager.PushImageAsync(resource, context.CancellationToken).ConfigureAwait(false);
    }
}

internal sealed class ModuleImagePushSelection
{
    public static ModuleImagePushSelection All { get; } = new([]);

    public ModuleImagePushSelection(IEnumerable<string> resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        Resources = resources.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlySet<string> Resources { get; }

    public bool IsScoped => Resources.Count > 0;

    public bool Includes(string resourceName) =>
        !IsScoped || Resources.Contains(resourceName);
}
