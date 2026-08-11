using CliWrap;
using CliCommand = global::CliWrap.Cli;

namespace Aspire.Hosting;

internal sealed record ModuleImagePublishPlan(
    string? ImageRegistry,
    string ImageName,
    string ImageTag,
    string ImageReference,
    string? ProducedImageReference,
    IReadOnlyList<string> PublishArguments,
    bool RepositoryDirty,
    bool ShouldPublish)
{
    public static async Task<ModuleImagePublishPlan> CreateAsync(
        ModuleContainerExportOptions options,
        bool repositoryDirty,
        Func<string, CancellationToken, Task<bool>> imageExists,
        Func<string, CancellationToken, Task<bool>> pullImage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(imageExists);
        ArgumentNullException.ThrowIfNull(pullImage);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ImageTag);

        var imageRepository = ModuleImageReference.GetRepository(options);
        var cleanImageReference = $"{imageRepository}:{options.ImageTag}";
        var effectiveTag = repositoryDirty &&
            !options.ImageTag.EndsWith("-dirty", StringComparison.OrdinalIgnoreCase)
                ? ModuleImageTag.AppendDirtySuffix(options.ImageTag)
                : options.ImageTag;
        var effectiveImageReference = $"{imageRepository}:{effectiveTag}";
        var publishArguments = options.PublishArguments
            .Select(argument => ResolveArgument(
                argument,
                options.ImageRegistry,
                options.ImageName,
                imageRepository,
                effectiveTag,
                cleanImageReference,
                effectiveImageReference,
                repositoryDirty))
            .ToArray();
        var producedImageReference = ResolveProducedImageReference(
            options,
            imageRepository,
            effectiveTag,
            cleanImageReference,
            effectiveImageReference);

        var shouldPublish = repositoryDirty;
        if (!shouldPublish)
        {
            var exists = await imageExists(cleanImageReference, cancellationToken).ConfigureAwait(false);
            var pulled = !exists && options.PullBeforeBuild &&
                await pullImage(cleanImageReference, cancellationToken).ConfigureAwait(false);
            shouldPublish = !exists && !pulled;
        }

        return new ModuleImagePublishPlan(
            options.ImageRegistry,
            options.ImageName,
            effectiveTag,
            effectiveImageReference,
            producedImageReference,
            publishArguments,
            repositoryDirty,
            shouldPublish);
    }

    public static Task<ModuleImagePublishPlan> CreateAsync(
        ModuleContainerExportOptions options,
        bool repositoryDirty,
        Func<string, CancellationToken, Task<bool>> imageExists,
        CancellationToken cancellationToken = default)
    {
        return CreateAsync(
            options,
            repositoryDirty,
            imageExists,
            (_, _) => Task.FromResult(false),
            cancellationToken);
    }

    public bool RequiresRetag =>
        ProducedImageReference is not null &&
        !string.Equals(ProducedImageReference, ImageReference, StringComparison.Ordinal);

    public static bool WouldRequireRetag(ModuleContainerExportOptions options, bool repositoryDirty)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ImageTag);

        var imageRepository = ModuleImageReference.GetRepository(options);
        var cleanImageReference = $"{imageRepository}:{options.ImageTag}";
        var effectiveTag = repositoryDirty
            ? ModuleImageTag.AppendDirtySuffix(options.ImageTag)
            : options.ImageTag;
        var effectiveImageReference = $"{imageRepository}:{effectiveTag}";
        var producedImageReference = ResolveProducedImageReference(
            options,
            imageRepository,
            effectiveTag,
            cleanImageReference,
            effectiveImageReference);
        return producedImageReference is not null &&
            !string.Equals(producedImageReference, effectiveImageReference, StringComparison.Ordinal);
    }

    private static string? ResolveProducedImageReference(
        ModuleContainerExportOptions options,
        string imageRepository,
        string effectiveTag,
        string cleanImageReference,
        string effectiveImageReference)
    {
        return string.IsNullOrWhiteSpace(options.ProducedImageReference)
            ? null
            : ResolveArgument(
                options.ProducedImageReference,
                options.ImageRegistry,
                options.ImageName,
                imageRepository,
                effectiveTag,
                cleanImageReference,
                effectiveImageReference,
                repositoryDirty: false);
    }

    private static string ResolveArgument(
        string argument,
        string? imageRegistry,
        string imageName,
        string imageRepository,
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
            .Replace(ModuleContainerExportOptions.ImageRepositoryPlaceholder, imageRepository, StringComparison.Ordinal)
            .Replace(ModuleContainerExportOptions.ImageRegistryPlaceholder, imageRegistry ?? string.Empty, StringComparison.Ordinal)
            .Replace(ModuleContainerExportOptions.ImageNamePlaceholder, imageName, StringComparison.Ordinal)
            .Replace(ModuleContainerExportOptions.ImageTagPlaceholder, imageTag, StringComparison.Ordinal);
    }
}

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

        var runtime = await ContainerRuntimeResolver.ResolveAsync(cancellationToken).ConfigureAwait(false);
        return await RunAsync(
            runtime,
            ["image", "inspect", imageReference],
            cancellationToken).ConfigureAwait(false) == 0;
    }

    public static async Task<bool> PullAsync(
        string imageReference,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageReference);

        try
        {
            var runtime = await ContainerRuntimeResolver.ResolveAsync(cancellationToken).ConfigureAwait(false);
            var result = await CliCommand.Wrap(runtime)
                .WithArguments(["pull", imageReference])
                .WithValidation(CommandResultValidation.None)
                .WithStandardOutputPipe(PipeTarget.ToStream(Stream.Null))
                .WithStandardErrorPipe(PipeTarget.ToStream(Stream.Null))
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
