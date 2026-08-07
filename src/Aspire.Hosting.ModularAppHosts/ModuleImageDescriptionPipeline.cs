#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES003
#pragma warning disable ASPIREPIPELINES004

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.ModularAppHosts;

internal static class ModuleImageDescriptionPipeline
{
    internal const string StepName = "describe-images";
    internal const string FileName = "module-images.json";

    private static readonly Action<ILogger, string, string, string, Exception?> LogImage =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Information,
            new EventId(1, nameof(LogImage)),
            "Module image {Resource}: {Reference} (push: {PushReference}).");

    private static readonly Action<ILogger, string, Exception?> LogOutput =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(2, nameof(LogOutput)),
            "Wrote module image descriptions to {Path}.");

    public static void Configure(IDistributedApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var selection = ModuleImagePipelineSelectionParser.GetSelection(
            Environment.GetCommandLineArgs(),
            StepName);
        builder.Pipeline.AddStep(new PipelineStep
        {
            Name = StepName,
            Description = "Writes effective module image identities and build origins.",
            Action = context => DescribeAsync(context, selection)
        });
    }

    internal static async Task<ModuleImageDescriptionDocument> CreateDocumentAsync(
        IEnumerable<IResource> resources,
        ModuleImageSelection selection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(selection);
        var images = resources
            .Select(resource => (
                Resource: resource,
                Module: resource.Annotations.OfType<DistributedApplicationModuleResourceAnnotation>().LastOrDefault(),
                Publisher: resource.Annotations.OfType<ModuleImagePublisherAnnotation>().LastOrDefault()))
            .Where(item =>
                item.Module is not null &&
                item.Resource.Annotations.OfType<ContainerImageAnnotation>().Any())
            .OrderBy(item => item.Resource.Name, StringComparer.Ordinal)
            .ToArray();

        var selectedResources = selection.ResolveResources(
            images.Select(item => item.Resource),
            "described module images");

        var document = new ModuleImageDescriptionDocument();
        foreach (var item in images.Where(item => selectedResources.Contains(item.Resource)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var module = item.Module!;
            var publisher = item.Publisher;
            var effective = await ModuleEffectiveImageResolver.ResolveAsync(
                item.Resource,
                cancellationToken,
                allowUnqualifiedPullReference: true).ConfigureAwait(false);
            var description = new ModuleImageDescription
            {
                Module = module.ModuleName,
                Resource = module.ResourceName,
                EffectiveResource = item.Resource.Name,
                ResourceKind = publisher?.ResourceKind ?? ModulePreviewResourceKind.Container,
                Registry = effective.Registry,
                Repository = effective.Repository,
                Tag = effective.Tag,
                Digest = effective.Digest,
                Reference = effective.Reference,
                PullReference = effective.PullReference,
                PushReference = publisher is null ? null : effective.PushReference,
                Build = publisher is null
                    ? null
                    : new ModuleImageBuildDescription
                    {
                        Command = publisher.Options.PublishCommand,
                        WorkingDirectory = publisher.WorkingDirectory,
                        Repository = publisher.Repository,
                        Revision = publisher.Revision,
                        Step = $"build-{item.Resource.Name}"
                    }
            };
            foreach (var argument in publisher?.Plan.PublishArguments ?? [])
            {
                description.Build!.Arguments.Add(argument);
            }

            document.Images.Add(description);
        }

        return document;
    }

    private static async Task DescribeAsync(
        PipelineStepContext context,
        ModuleImageSelection selection)
    {
        var document = await CreateDocumentAsync(
            context.Model.Resources,
            selection,
            context.CancellationToken).ConfigureAwait(false);
        var output = context.Services.GetRequiredService<IPipelineOutputService>().GetOutputDirectory();
        var path = Path.Combine(output, FileName);
        await document.SaveAsync(path, context.CancellationToken).ConfigureAwait(false);
        foreach (var image in document.Images)
        {
            LogImage(
                context.Logger,
                image.EffectiveResource,
                image.Reference,
                image.PushReference ?? "none",
                null);
        }

        LogOutput(context.Logger, path, null);
    }

}
