#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES003
#pragma warning disable ASPIREPIPELINES004

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Publishing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.ModularAppHosts;

internal static class ModuleImageManifestPipeline
{
    internal const string StepName = "workflow-images";

    private static readonly Action<ILogger, string, string, Exception?> LogImage =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            new EventId(1, nameof(LogImage)),
            "Workflow image {Resource}: {Reference}.");

    private static readonly Action<ILogger, string, Exception?> LogOutput =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(2, nameof(LogOutput)),
            "Wrote the workflow image manifest to {Path}.");

    public static void Configure(IDistributedApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var selection = ModuleImagePipelineSelectionParser.GetSelection(
            Environment.GetCommandLineArgs(),
            StepName);
        builder.Pipeline.AddStep(new PipelineStep
        {
            Name = StepName,
            Description = "Pushes selected module images and writes their resolved remote identities.",
            Action = context => WriteAsync(context, selection),
            DependsOnSteps = [WellKnownPipelineSteps.Push]
        });
        builder.Pipeline.AddPipelineConfiguration(context =>
        {
            ModuleImagePushPipeline.ApplySelection(context.Steps, selection);
            return Task.CompletedTask;
        });
    }

    internal static async Task<ModuleImageManifestDocument> CreateDocumentAsync(
        IEnumerable<IResource> resources,
        ModuleImageSelection selection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(selection);
        var images = resources
            .Select(resource => (
                Resource: resource,
                Module: resource.Annotations
                    .OfType<DistributedApplicationModuleResourceAnnotation>()
                    .LastOrDefault(),
                Publisher: resource.Annotations.OfType<ModuleImagePublisherAnnotation>().LastOrDefault()))
            .Where(item =>
                item.Module is not null &&
                item.Publisher is not null &&
                ModuleEffectiveImageResolver.HasPushTarget(item.Resource))
            .OrderBy(item => item.Module!.ModuleName, StringComparer.Ordinal)
            .ThenBy(item => item.Module!.ResourceName, StringComparer.Ordinal)
            .ToArray();
        var selectedResources = selection.ResolveResources(
            images.Select(item => item.Resource),
            "workflow image publishers");

        var document = new ModuleImageManifestDocument();
        foreach (var item in images.Where(item => selectedResources.Contains(item.Resource)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var effective = await ModuleEffectiveImageResolver.ResolveAsync(
                item.Resource,
                cancellationToken).ConfigureAwait(false);
            var remote = effective.PushImage ?? throw new InvalidOperationException(
                $"Resource '{item.Resource.Name}' does not resolve to a complete remote image identity.");
            document.Images.Add(new ModuleImageManifestEntry
            {
                Module = item.Module!.ModuleName,
                Resource = item.Module.ResourceName,
                ResourceKind = item.Publisher!.ResourceKind,
                Registry = remote.Registry,
                Repository = remote.Repository,
                Tag = remote.Tag
            });
        }

        document.Validate();
        return document;
    }

    private static async Task WriteAsync(
        PipelineStepContext context,
        ModuleImageSelection selection)
    {
        var document = await CreateDocumentAsync(
            context.Model.Resources,
            selection,
            context.CancellationToken).ConfigureAwait(false);
        var output = context.Services.GetRequiredService<IPipelineOutputService>().GetOutputDirectory();
        var path = Path.Combine(output, ModuleImageManifestDocument.DefaultFileName);
        await document.SaveAsync(path, context.CancellationToken).ConfigureAwait(false);
        foreach (var image in document.Images)
        {
            LogImage(context.Logger, $"{image.Module}/{image.Resource}", image.Reference, null);
        }

        LogOutput(context.Logger, path, null);
    }
}
