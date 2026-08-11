#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES003

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

internal static class ModuleImageBuildPipeline
{
    internal const string BuildContainerImageTag = "build-module-container-image";

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

    internal static async Task BuildAsync(
        IResource resource,
        PipelineStepContext context)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(context);
        var publisher = resource.Annotations.OfType<ModuleImagePublisherAnnotation>().LastOrDefault()
            ?? throw new InvalidOperationException(
                $"Resource '{resource.Name}' does not have a module image publisher.");
        var resourceLogger = context.Services
            .GetRequiredService<ResourceLoggerService>()
            .GetLogger(resource);
        await publisher.PrepareAsync(
            context.Logger,
            resourceLogger,
            context.CancellationToken).ConfigureAwait(false);
    }
}
