using CliWrap;
using CliCommand = global::CliWrap.Cli;

namespace Aspire.Hosting;

internal static class ModuleImageReference
{
    public static (string? Registry, string Name) ParseRepository(string repository)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        var separator = repository.IndexOf('/', StringComparison.Ordinal);
        if (separator <= 0)
        {
            return (null, repository);
        }

        var firstSegment = repository[..separator];
        var hasExplicitRegistry = firstSegment.Contains('.', StringComparison.Ordinal) ||
            firstSegment.Contains(':', StringComparison.Ordinal) ||
            string.Equals(firstSegment, "localhost", StringComparison.OrdinalIgnoreCase);
        return hasExplicitRegistry
            ? (firstSegment, repository[(separator + 1)..])
            : (null, repository);
    }

    public static string GetRepository(ModuleContainerExportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return string.IsNullOrWhiteSpace(options.ImageRegistry)
            ? options.ImageName
            : $"{options.ImageRegistry}/{options.ImageName}";
    }

    public static string GetTag(string imageReference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageReference);
        var withoutDigest = imageReference.Split('@', 2)[0];
        var lastSlash = withoutDigest.LastIndexOf('/');
        var lastColon = withoutDigest.LastIndexOf(':');
        return lastColon > lastSlash
            ? withoutDigest[(lastColon + 1)..]
            : "latest";
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
        string containerRuntime,
        string imageReference,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerRuntime);
        ArgumentException.ThrowIfNullOrWhiteSpace(imageReference);

        return await RunAsync(
            containerRuntime,
            ["image", "inspect", imageReference],
            cancellationToken).ConfigureAwait(false) == 0;
    }

    public static async Task<bool> PullAsync(
        string containerRuntime,
        string imageReference,
        Action<string> progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerRuntime);
        ArgumentException.ThrowIfNullOrWhiteSpace(imageReference);
        ArgumentNullException.ThrowIfNull(progress);

        try
        {
            var progressLock = new object();
            void ReportProgress(string line)
            {
                var redacted = ModuleCliOutputRedactor.Redact(line);
                if (string.IsNullOrWhiteSpace(redacted))
                {
                    return;
                }

                lock (progressLock)
                {
                    progress(redacted);
                }
            }

            var result = await CliCommand.Wrap(containerRuntime)
                .WithArguments(["pull", imageReference])
                .WithValidation(CommandResultValidation.None)
                .WithStandardOutputPipe(PipeTarget.ToDelegate(ReportProgress))
                .WithStandardErrorPipe(PipeTarget.ToDelegate(ReportProgress))
                .ExecuteAsync(cancellationToken)
                .ConfigureAwait(false);
            return result.ExitCode == 0;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or System.ComponentModel.Win32Exception
                or IOException)
        {
            return false;
        }
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
