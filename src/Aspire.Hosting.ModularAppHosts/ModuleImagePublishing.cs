using CliWrap;
using CliCommand = global::CliWrap.Cli;

namespace Aspire.Hosting.ModularAppHosts;

internal sealed record ModuleImagePublishPlan(
    string ImageName,
    string ImageTag,
    string ImageReference,
    IReadOnlyList<string> PublishArguments,
    bool RepositoryDirty,
    bool ShouldPublish)
{
    public static ModuleImagePublishPlan Create(
        ModuleContainerExportOptions options,
        bool repositoryDirty,
        Func<string, bool> imageExists)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(imageExists);

        var cleanImageReference = $"{options.ImageName}:{options.ImageTag}";
        var effectiveTag = repositoryDirty &&
            !options.ImageTag.EndsWith("-dirty", StringComparison.Ordinal)
                ? $"{options.ImageTag}-dirty"
                : options.ImageTag;
        var effectiveImageReference = $"{options.ImageName}:{effectiveTag}";
        var publishArguments = options.PublishArguments
            .Select(argument => ResolveArgument(
                argument,
                options.ImageName,
                effectiveTag,
                cleanImageReference,
                effectiveImageReference,
                repositoryDirty))
            .ToArray();

        return new ModuleImagePublishPlan(
            options.ImageName,
            effectiveTag,
            effectiveImageReference,
            publishArguments,
            repositoryDirty,
            repositoryDirty || !imageExists(cleanImageReference));
    }

    private static string ResolveArgument(
        string argument,
        string imageName,
        string imageTag,
        string cleanImageReference,
        string effectiveImageReference,
        bool repositoryDirty)
    {
        ArgumentNullException.ThrowIfNull(argument);

        if (repositoryDirty && string.Equals(argument, cleanImageReference, StringComparison.Ordinal))
        {
            argument = effectiveImageReference;
        }

        return argument
            .Replace(ModuleContainerExportOptions.ImageReferencePlaceholder, effectiveImageReference, StringComparison.Ordinal)
            .Replace(ModuleContainerExportOptions.ImageNamePlaceholder, imageName, StringComparison.Ordinal)
            .Replace(ModuleContainerExportOptions.ImageTagPlaceholder, imageTag, StringComparison.Ordinal);
    }
}

internal static class ContainerImageInspector
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(5);

    public static bool Exists(string imageReference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageReference);

        var configuredRuntime = Environment.GetEnvironmentVariable("ASPIRE_CONTAINER_RUNTIME");
        if (!string.IsNullOrWhiteSpace(configuredRuntime))
        {
            return Run(configuredRuntime, ["image", "inspect", imageReference]) == 0;
        }

        foreach (var runtime in new[] { "docker", "podman" })
        {
            if (Run(runtime, ["container", "ls", "-n", "1"]) == 0)
            {
                return Run(runtime, ["image", "inspect", imageReference]) == 0;
            }
        }

        return false;
    }

    private static int? Run(string executable, IReadOnlyList<string> arguments)
    {
        try
        {
            using var timeout = new CancellationTokenSource(CommandTimeout);
            var result = CliCommand.Wrap(executable)
                .WithArguments(arguments)
                .WithValidation(CommandResultValidation.None)
                .WithStandardOutputPipe(PipeTarget.ToStream(Stream.Null))
                .WithStandardErrorPipe(PipeTarget.ToStream(Stream.Null))
                .ExecuteAsync(timeout.Token)
                .GetAwaiter()
                .GetResult();
            return result.ExitCode;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or System.ComponentModel.Win32Exception
                or IOException
                or OperationCanceledException)
        {
            return null;
        }
    }
}
