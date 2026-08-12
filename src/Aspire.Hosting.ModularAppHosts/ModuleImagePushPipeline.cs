#pragma warning disable ASPIRECOMPUTE003
#pragma warning disable ASPIRECONTAINERRUNTIME001
#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES003

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Publishing;
using CliWrap;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net;
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

    private static async Task PushAsync(IResource resource, PipelineStepContext context)
    {
        var resourceLogger = context.Services
            .GetRequiredService<ResourceLoggerService>()
            .GetLogger(resource);
        var publisher = resource.Annotations.OfType<ModuleImagePublisherAnnotation>().LastOrDefault()
            ?? throw new InvalidOperationException(
                $"Resource '{resource.Name}' does not have a module image publisher.");
        var task = await context.ReportingStep.CreateTaskAsync(
            $"Push image for {resource.Name}",
            context.CancellationToken).ConfigureAwait(false);
        await using var configuredTask = task.ConfigureAwait(false);
        try
        {
            await PushCoreAsync(resource, context, resourceLogger, publisher).ConfigureAwait(false);
            await task.SucceedAsync(
                $"Pushed image for {resource.Name}",
                context.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await task.FailAsync(
                exception.Message,
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task PushCoreAsync(
        IResource resource,
        PipelineStepContext context,
        ILogger resourceLogger,
        ModuleImagePublisherAnnotation publisher)
    {
        var preparedImage = await publisher.PrepareAsync(
            context.Services,
            NullLogger.Instance,
            resourceLogger,
            context.CancellationToken).ConfigureAwait(false);
        if (preparedImage.SourceState.IsDirty)
        {
            throw new InvalidOperationException(
                $"Resource '{resource.Name}' cannot push an image built from a dirty repository. " +
                "Commit or stash the source changes before publishing the image.");
        }

        var resolved = await ModuleEffectiveImageResolver.ResolveAsync(
            resource,
            context.CancellationToken,
            usePreparedPublisherImage: true).ConfigureAwait(false);
        if (resolved.PushTargetKind == ModuleImagePushTargetKind.None)
        {
            throw new InvalidOperationException(
                $"Resource '{resource.Name}' does not have a remote image push target.");
        }

        var imageManager = context.Services.GetRequiredService<IResourceContainerImageManager>();
        var transferTimeout = context.Services
            .GetRequiredService<IOptions<ModularAppHostsOptions>>()
            .Value.ImageTransferTimeout;
        IContainerRuntime? runtime = null;
        if (resolved.PushTargetKind == ModuleImagePushTargetKind.ContainerRuntime)
        {
            runtime = await context.Services
                .GetRequiredService<IContainerRuntimeResolver>()
                .ResolveAsync(context.CancellationToken).ConfigureAwait(false);
        }

        await PushImageAsync(
            resolved.PushTargetKind,
            resource,
            resolved.PushReference!,
            (reference, token) => RunContainerRuntimeAsync(
                ModuleImageRecipeOperations.GetContainerRuntimeExecutableName(
                    (runtime ?? throw new InvalidOperationException(
                        "The container runtime was not resolved for an explicit registry push.")).Name),
                CreatePushArguments(runtime.Name, reference),
                context,
                resourceLogger,
                token),
            imageManager.PushImageAsync,
            transferTimeout,
            context.CancellationToken).ConfigureAwait(false);

        var branchAlias = GetBranchAliasReference(resource, resolved);
        if (branchAlias is null)
        {
            return;
        }

        runtime ??= await context.Services
            .GetRequiredService<IContainerRuntimeResolver>()
            .ResolveAsync(context.CancellationToken).ConfigureAwait(false);
        LogBranchAlias(context.Logger, branchAlias, resource.Name, null);
        await ModuleOperationTimeout.RunAsync(
            token => runtime.TagImageAsync(resolved.Reference, branchAlias, token),
            transferTimeout,
            $"Branch image tag for resource '{resource.Name}'",
            context.CancellationToken).ConfigureAwait(false);
        await ModuleOperationTimeout.RunAsync(
            token => RunContainerRuntimeAsync(
                ModuleImageRecipeOperations.GetContainerRuntimeExecutableName(runtime.Name),
                CreatePushArguments(runtime.Name, branchAlias),
                context,
                resourceLogger,
                token),
            transferTimeout,
            $"Branch image push for resource '{resource.Name}'",
            context.CancellationToken).ConfigureAwait(false);
    }

    internal static Task PushImageAsync(
        ModuleImagePushTargetKind targetKind,
        IResource resource,
        string pushReference,
        Func<string, CancellationToken, Task> pushWithRuntimeAsync,
        Func<IResource, CancellationToken, Task> pushWithImageManagerAsync,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentException.ThrowIfNullOrWhiteSpace(pushReference);
        ArgumentNullException.ThrowIfNull(pushWithRuntimeAsync);
        ArgumentNullException.ThrowIfNull(pushWithImageManagerAsync);
        Func<CancellationToken, Task> pushAsync = targetKind switch
        {
            ModuleImagePushTargetKind.ContainerRuntime =>
                token => pushWithRuntimeAsync(pushReference, token),
            ModuleImagePushTargetKind.AspireRegistry =>
                token => pushWithImageManagerAsync(resource, token),
            _ => throw new InvalidOperationException(
                $"Resource '{resource.Name}' does not have a remote image push target.")
        };
        return ModuleOperationTimeout.RunAsync(
            pushAsync,
            timeout,
            $"Image push for resource '{resource.Name}'",
            cancellationToken);
    }

    internal static string? GetBranchAliasReference(IResource resource, ModuleEffectiveImage resolved)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(resolved);
        var publisher = resource.Annotations.OfType<ModuleImagePublisherAnnotation>().LastOrDefault();
        if (publisher is null ||
            !publisher.TryGetPreparedImage(out var preparedImage) ||
            preparedImage.SourceState.IsDirty ||
            resolved.PushImage is null)
        {
            return null;
        }

        var branch = preparedImage.SourceState.Branch ?? publisher.Recipe.DetachedBranchAlias;
        if (string.IsNullOrWhiteSpace(branch))
        {
            return null;
        }

        var branchImageTag = ModuleImageTag.FromBranch(branch);
        var alias = $"{resolved.PushImage.Registry}/{resolved.PushImage.Repository}:{branchImageTag}";
        return string.Equals(alias, resolved.PushReference, StringComparison.OrdinalIgnoreCase)
            ? null
            : alias;
    }

    internal static IReadOnlyList<string> CreatePushArguments(
        string runtimeName,
        string imageReference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(imageReference);
        var (registry, _) = ModuleImageReference.ParseRepository(imageReference);
        if (string.Equals(runtimeName, "Podman", StringComparison.OrdinalIgnoreCase) &&
            registry is not null &&
            IsLoopbackRegistry(registry))
        {
            return ["push", "--tls-verify=false", imageReference];
        }

        return ["push", imageReference];
    }

    private static bool IsLoopbackRegistry(string registry)
    {
        if (!Uri.TryCreate($"http://{registry}", UriKind.Absolute, out var uri))
        {
            return false;
        }

        return string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            IPAddress.TryParse(uri.Host, out var address) && IPAddress.IsLoopback(address);
    }

    private static async Task RunContainerRuntimeAsync(
        string runtime,
        IReadOnlyList<string> arguments,
        PipelineStepContext context,
        ILogger resourceLogger,
        CancellationToken cancellationToken)
    {
        await CliCommand.Wrap(runtime)
            .WithArguments(arguments)
            .WithStandardOutputPipe(PipeTarget.ToDelegate(line =>
                LogContainerRuntimeOutput(
                    resourceLogger,
                    runtime,
                    line,
                    null)))
            .WithStandardErrorPipe(PipeTarget.ToDelegate(line =>
                LogContainerRuntimeOutput(
                    resourceLogger,
                    runtime,
                    line,
                    null)))
            .ExecuteAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
