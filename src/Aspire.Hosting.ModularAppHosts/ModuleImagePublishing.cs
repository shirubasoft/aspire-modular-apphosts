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
    public static async Task<ModuleImagePublishPlan> CreateAsync(
        ModuleContainerExportOptions options,
        bool repositoryDirty,
        Func<string, CancellationToken, Task<bool>> imageExists,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(imageExists);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ImageTag);

        var cleanImageReference = $"{options.ImageName}:{options.ImageTag}";
        var effectiveTag = repositoryDirty &&
            !options.ImageTag.EndsWith("-dirty", StringComparison.OrdinalIgnoreCase)
                ? ModuleImageTag.AppendDirtySuffix(options.ImageTag)
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

        var shouldPublish = repositoryDirty ||
            !await imageExists(cleanImageReference, cancellationToken).ConfigureAwait(false);
        return new ModuleImagePublishPlan(
            options.ImageName,
            effectiveTag,
            effectiveImageReference,
            publishArguments,
            repositoryDirty,
            shouldPublish);
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

internal static class ModuleImageTag
{
    private const int MaximumLength = 128;
    private const string FallbackTag = "latest";
    private const string DirtySuffix = "-dirty";

    public static string FromRepository(string? branchName, string? commit)
    {
        var branchTag = FromBranch(branchName);
        var commitTag = NormalizeCommit(commit);
        if (commitTag is null)
        {
            return branchTag;
        }

        var prefix = branchTag == FallbackTag && string.IsNullOrWhiteSpace(branchName)
            ? "sha"
            : branchTag;
        var suffix = $"-{commitTag}";
        var availableLength = MaximumLength - suffix.Length;
        prefix = prefix[..Math.Min(prefix.Length, availableLength)].TrimEnd('.', '-');
        return $"{prefix}{suffix}";
    }

    public static string FromBranch(string? branchName)
    {
        if (string.IsNullOrWhiteSpace(branchName))
        {
            return FallbackTag;
        }

        var characters = branchName.Trim()
            .Select(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '-'
                ? char.ToLowerInvariant(character)
                : '-')
            .ToArray();
        var tag = new string(characters);
        while (tag.Contains("--", StringComparison.Ordinal))
        {
            tag = tag.Replace("--", "-", StringComparison.Ordinal);
        }

        tag = tag.Trim('.', '-');
        if (tag.Length == 0)
        {
            return FallbackTag;
        }

        if (!(char.IsAsciiLetterOrDigit(tag[0]) || tag[0] == '_'))
        {
            tag = $"branch-{tag.TrimStart('_', '.', '-')}".TrimEnd('-');
        }

        if (tag.Length == 0)
        {
            return FallbackTag;
        }

        return tag[..Math.Min(tag.Length, MaximumLength)].TrimEnd('.', '-');
    }

    public static string AppendDirtySuffix(string imageTag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageTag);
        if (imageTag.EndsWith(DirtySuffix, StringComparison.OrdinalIgnoreCase))
        {
            return imageTag;
        }

        var availableLength = MaximumLength - DirtySuffix.Length;
        var cleanTag = imageTag[..Math.Min(imageTag.Length, availableLength)].TrimEnd('.', '-');
        return $"{cleanTag}{DirtySuffix}";
    }

    private static string? NormalizeCommit(string? commit)
    {
        if (string.IsNullOrWhiteSpace(commit))
        {
            return null;
        }

        var value = new string(commit.Trim()
            .Where(char.IsAsciiHexDigit)
            .Select(char.ToLowerInvariant)
            .Take(12)
            .ToArray());
        return value.Length >= 7 ? value : null;
    }
}

internal static class ContainerImageInspector
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(5);

    public static async Task<bool> ExistsAsync(
        string imageReference,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageReference);

        var configuredRuntime = Environment.GetEnvironmentVariable("ASPIRE_CONTAINER_RUNTIME");
        if (!string.IsNullOrWhiteSpace(configuredRuntime))
        {
            return await RunAsync(
                configuredRuntime,
                ["image", "inspect", imageReference],
                cancellationToken).ConfigureAwait(false) == 0;
        }

        foreach (var runtime in new[] { "docker", "podman" })
        {
            if (await RunAsync(
                    runtime,
                    ["container", "ls", "-n", "1"],
                    cancellationToken).ConfigureAwait(false) == 0)
            {
                return await RunAsync(
                    runtime,
                    ["image", "inspect", imageReference],
                    cancellationToken).ConfigureAwait(false) == 0;
            }
        }

        return false;
    }

    private static async Task<int?> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = new CancellationTokenSource(CommandTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeout.Token);
            var result = await CliCommand.Wrap(executable)
                .WithArguments(arguments)
                .WithValidation(CommandResultValidation.None)
                .WithStandardOutputPipe(PipeTarget.ToStream(Stream.Null))
                .WithStandardErrorPipe(PipeTarget.ToStream(Stream.Null))
                .ExecuteAsync(linked.Token)
                .ConfigureAwait(false);
            return result.ExitCode;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or System.ComponentModel.Win32Exception
                or IOException
                || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return null;
        }
    }
}
