#pragma warning disable ASPIRECONTAINERRUNTIME001

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using CliWrap;
using Aspire.Hosting.Publishing;
using Microsoft.Extensions.Logging;
using CliCommand = global::CliWrap.Cli;

namespace Aspire.Hosting;

internal sealed record ModuleImageRecipeIdentity(
    string ModuleName,
    string ResourceName);

internal sealed record ModuleImageRepositorySettings(
    string RepositoryPath,
    string WorkingDirectory,
    string? Repository,
    string? Revision,
    bool RefreshCleanCheckout,
    string GitExecutablePath,
    string GitHubCliPath,
    TimeSpan CommandTimeout,
    string? DetachedBranchAlias = null);

internal sealed record ModuleImageCommandSettings(
    ModuleImageCommandOptions Options,
    TimeSpan BuildTimeout,
    TimeSpan TransferTimeout);

internal sealed class ModuleImageBuildRecipe
{
    internal const string LocalRunTag = "aspire-run";

    public ModuleImageBuildRecipe(
        ModuleImageRecipeIdentity identity,
        ModuleImageRepositorySettings repository,
        ModuleImageCommandSettings command)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.ModuleName);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.ResourceName);
        ArgumentNullException.ThrowIfNull(command.Options);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Options.ImageName);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Options.PublishCommand);
        ArgumentException.ThrowIfNullOrWhiteSpace(repository.RepositoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(repository.WorkingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(repository.GitExecutablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(repository.GitHubCliPath);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(repository.CommandTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(command.BuildTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(command.TransferTimeout, TimeSpan.Zero);

        ModuleName = identity.ModuleName;
        ResourceName = identity.ResourceName;
        Options = DistributedApplicationModuleProjectBuilder.CopyOptions(command.Options);
        RepositoryPath = Path.GetFullPath(repository.RepositoryPath);
        WorkingDirectory = Path.GetFullPath(repository.WorkingDirectory);
        Repository = string.IsNullOrWhiteSpace(repository.Repository) ? null : repository.Repository.Trim();
        Revision = string.IsNullOrWhiteSpace(repository.Revision) ? null : repository.Revision.Trim();
        RefreshCleanCheckout = repository.RefreshCleanCheckout;
        GitExecutablePath = repository.GitExecutablePath;
        GitHubCliPath = repository.GitHubCliPath;
        RepositoryCommandTimeout = repository.CommandTimeout;
        ImageBuildTimeout = command.BuildTimeout;
        ImageTransferTimeout = command.TransferTimeout;
        DetachedBranchAlias = string.IsNullOrWhiteSpace(repository.DetachedBranchAlias)
            ? null
            : repository.DetachedBranchAlias.Trim();

        var imageRepository = ModuleImageReference.GetRepository(Options);
        LocalImageReference = $"{imageRepository}:{LocalRunTag}";
    }

    public string ModuleName { get; }

    public string ResourceName { get; }

    public ModuleImageCommandOptions Options { get; }

    public string RepositoryPath { get; }

    public string WorkingDirectory { get; }

    public string? Repository { get; }

    public string? Revision { get; }

    public bool RefreshCleanCheckout { get; }

    public string GitExecutablePath { get; }

    public string GitHubCliPath { get; }

    public TimeSpan RepositoryCommandTimeout { get; }

    public TimeSpan ImageBuildTimeout { get; }

    public TimeSpan ImageTransferTimeout { get; }

    public string? DetachedBranchAlias { get; }

    public string LocalImageReference { get; }
}

internal sealed record ModuleImageSourceState(
    string? Branch,
    string? Commit,
    bool IsDirty,
    string StatusFingerprint);

internal enum ModuleImagePreparationDisposition
{
    Reused,
    Pulled,
    Built
}

internal sealed record ModulePreparedImage(
    string CanonicalImageReference,
    string LocalImageReference,
    ModuleImageSourceState SourceState,
    ModuleImagePreparationDisposition Disposition);

internal sealed record ModuleImageExecutionPlan(
    string CanonicalImageReference,
    string? ProducedImageReference,
    IReadOnlyList<string> PublishArguments)
{
    public bool RequiresProducedImageRetag =>
        ProducedImageReference is not null &&
        !string.Equals(ProducedImageReference, CanonicalImageReference, StringComparison.Ordinal);

    public static ModuleImageExecutionPlan Create(
        ModuleImageBuildRecipe recipe,
        ModuleImageSourceState sourceState)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(sourceState);

        var options = recipe.Options;
        var imageRepository = ModuleImageReference.GetRepository(options);
        var cleanTag = string.IsNullOrWhiteSpace(options.ImageTag)
            ? ModuleImageTag.FromRepository(sourceState.Branch, sourceState.Commit)
            : options.ImageTag;
        var effectiveTag = sourceState.IsDirty
            ? ModuleImageTag.AppendDirtySuffix(cleanTag)
            : cleanTag;
        var canonicalImageReference = $"{imageRepository}:{effectiveTag}";
        var publishArguments = options.PublishArguments
            .Select(argument => ResolveValue(
                argument,
                options,
                imageRepository,
                effectiveTag,
                canonicalImageReference))
            .ToArray();
        var producedImageReference = string.IsNullOrWhiteSpace(options.ProducedImageReference)
            ? null
            : ResolveValue(
                options.ProducedImageReference,
                options,
                imageRepository,
                effectiveTag,
                canonicalImageReference);

        return new ModuleImageExecutionPlan(
            canonicalImageReference,
            producedImageReference,
            publishArguments);
    }

    private static string ResolveValue(
        string value,
        ModuleImageCommandOptions options,
        string imageRepository,
        string imageTag,
        string canonicalImageReference)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value
            .Replace(ModuleImageCommandOptions.ImageReferencePlaceholder, canonicalImageReference, StringComparison.Ordinal)
            .Replace(ModuleImageCommandOptions.ImageRepositoryPlaceholder, imageRepository, StringComparison.Ordinal)
            .Replace(ModuleImageCommandOptions.ImageRegistryPlaceholder, options.ImageRegistry ?? string.Empty, StringComparison.Ordinal)
            .Replace(ModuleImageCommandOptions.ImageNamePlaceholder, options.ImageName, StringComparison.Ordinal)
            .Replace(ModuleImageCommandOptions.ImageTagPlaceholder, imageTag, StringComparison.Ordinal);
    }
}

internal interface IModuleImageRecipeOperations
{
    Task<string> ResolveContainerRuntimeAsync(CancellationToken cancellationToken);

    Task<ModuleImageSourceState> CaptureSourceStateAsync(
        ModuleImageBuildRecipe recipe,
        CancellationToken cancellationToken);

    Task<bool> HasUpstreamAsync(
        ModuleImageBuildRecipe recipe,
        CancellationToken cancellationToken);

    Task RefreshRepositoryAsync(
        ModuleImageBuildRecipe recipe,
        Action<string> progress,
        CancellationToken cancellationToken);

    Task<bool> ImageExistsAsync(
        string containerRuntime,
        string imageReference,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    Task<bool> PullImageAsync(
        string containerRuntime,
        string imageReference,
        TimeSpan timeout,
        Action<string> progress,
        CancellationToken cancellationToken);

    Task BuildImageAsync(
        ModuleImageBuildRecipe recipe,
        ModuleImageExecutionPlan plan,
        string containerRuntime,
        Action<string> progress,
        CancellationToken cancellationToken);

    Task TagImageAsync(
        ModuleImageBuildRecipe recipe,
        string containerRuntime,
        string sourceImageReference,
        string targetImageReference,
        Action<string> progress,
        CancellationToken cancellationToken);
}

internal static class ModuleImageRecipeEvaluator
{
    private static readonly Action<ILogger, string, string, string, Exception?> LogPreparationStarted =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Information,
            new EventId(1, nameof(LogPreparationStarted)),
            "Preparing image {LocalImageReference} for module {ModuleName} resource {ResourceName}.");

    private static readonly Action<ILogger, string, string, Exception?> LogRefreshSkipped =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            new EventId(2, nameof(LogRefreshSkipped)),
            "Skipped runtime repository refresh for {RepositoryPath}: {Reason}.");

    private static readonly Action<ILogger, string, Exception?> LogRefreshStarted =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(3, nameof(LogRefreshStarted)),
            "Refreshing clean build repository {RepositoryPath}.");

    private static readonly Action<ILogger, string, double, Exception?> LogRefreshCompleted =
        LoggerMessage.Define<string, double>(
            LogLevel.Information,
            new EventId(4, nameof(LogRefreshCompleted)),
            "Refreshed clean build repository {RepositoryPath} in {ElapsedMilliseconds} ms.");

    private static readonly Action<ILogger, string, Exception?> LogLocalImageFound =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(5, nameof(LogLocalImageFound)),
            "Found canonical image {ImageReference} in the local container runtime.");

    private static readonly Action<ILogger, string, Exception?> LogPullStarted =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(6, nameof(LogPullStarted)),
            "Pulling canonical image {ImageReference}.");

    private static readonly Action<ILogger, string, Exception?> LogPullCompleted =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(7, nameof(LogPullCompleted)),
            "Pulled canonical image {ImageReference}.");

    private static readonly Action<ILogger, string, Exception?> LogBuildFallback =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(8, nameof(LogBuildFallback)),
            "Building the image because {Reason}.");

    private static readonly Action<ILogger, string, Exception?> LogDirtySourceBuild =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(9, nameof(LogDirtySourceBuild)),
            "Build repository {RepositoryPath} is dirty; rebuilding without pulling or reusing an image.");

    private static readonly Action<ILogger, string, double, Exception?> LogBuildCompleted =
        LoggerMessage.Define<string, double>(
            LogLevel.Information,
            new EventId(10, nameof(LogBuildCompleted)),
            "Built canonical image {ImageReference} in {ElapsedMilliseconds} ms.");

    private static readonly Action<ILogger, string, string, Exception?> LogRetagStarted =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            new EventId(11, nameof(LogRetagStarted)),
            "Tagging image {SourceImageReference} as {TargetImageReference}.");

    private static readonly Action<ILogger, string, string, double, Exception?> LogRetagCompleted =
        LoggerMessage.Define<string, string, double>(
            LogLevel.Information,
            new EventId(12, nameof(LogRetagCompleted)),
            "Tagged image {SourceImageReference} as {TargetImageReference} in {ElapsedMilliseconds} ms.");

    private static readonly Action<ILogger, string, string, double, Exception?> LogPreparationCompleted =
        LoggerMessage.Define<string, string, double>(
            LogLevel.Information,
            new EventId(13, nameof(LogPreparationCompleted)),
            "Prepared local image {LocalImageReference} by {Disposition} in {ElapsedMilliseconds} ms.");

    private static readonly Action<ILogger, string, Exception?> LogCommandOutput =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(14, nameof(LogCommandOutput)),
            "{Output}");

    internal static async Task<ModulePreparedImage> PrepareAsync(
        ModuleImageBuildRecipe recipe,
        ILogger lifecycleLogger,
        ILogger resourceLogger,
        IModuleImageRecipeOperations operations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(lifecycleLogger);
        ArgumentNullException.ThrowIfNull(resourceLogger);
        ArgumentNullException.ThrowIfNull(operations);
        cancellationToken.ThrowIfCancellationRequested();

        var operationId = Guid.NewGuid().ToString("N");
        using var lifecycleScope = BeginScope(lifecycleLogger, recipe, operationId);
        using var resourceScope = ReferenceEquals(lifecycleLogger, resourceLogger)
            ? null
            : BeginScope(resourceLogger, recipe, operationId);
        LogBoth(
            lifecycleLogger,
            resourceLogger,
            logger => LogPreparationStarted(
                logger,
                recipe.LocalImageReference,
                recipe.ModuleName,
                recipe.ResourceName,
                null));
        var preparationStopwatch = Stopwatch.StartNew();

        var containerRuntime = await operations.ResolveContainerRuntimeAsync(cancellationToken)
            .ConfigureAwait(false);
        var sourceState = await operations.CaptureSourceStateAsync(recipe, cancellationToken)
            .ConfigureAwait(false);
        sourceState = await RefreshAsync(
            recipe,
            sourceState,
            lifecycleLogger,
            resourceLogger,
            operations,
            cancellationToken).ConfigureAwait(false);
        var plan = ModuleImageExecutionPlan.Create(recipe, sourceState);

        ModuleImagePreparationDisposition disposition;
        if (sourceState.IsDirty)
        {
            LogBoth(
                lifecycleLogger,
                resourceLogger,
                logger => LogDirtySourceBuild(logger, recipe.RepositoryPath, null));
            disposition = await BuildAsync(
                recipe,
                plan,
                sourceState,
                containerRuntime,
                lifecycleLogger,
                resourceLogger,
                operations,
                cancellationToken).ConfigureAwait(false);
        }
        else if (await operations.ImageExistsAsync(
                containerRuntime,
                plan.CanonicalImageReference,
                recipe.ImageTransferTimeout,
                cancellationToken).ConfigureAwait(false))
        {
            LogBoth(
                lifecycleLogger,
                resourceLogger,
                logger => LogLocalImageFound(logger, plan.CanonicalImageReference, null));
            disposition = ModuleImagePreparationDisposition.Reused;
        }
        else if (recipe.Options.PullBeforeBuild)
        {
            LogBoth(
                lifecycleLogger,
                resourceLogger,
                logger => LogPullStarted(logger, plan.CanonicalImageReference, null));
            if (await operations.PullImageAsync(
                    containerRuntime,
                    plan.CanonicalImageReference,
                    recipe.ImageTransferTimeout,
                    line => LogRawOutput(resourceLogger, line),
                    cancellationToken).ConfigureAwait(false))
            {
                LogBoth(
                    lifecycleLogger,
                    resourceLogger,
                    logger => LogPullCompleted(logger, plan.CanonicalImageReference, null));
                disposition = ModuleImagePreparationDisposition.Pulled;
            }
            else
            {
                LogBoth(
                    lifecycleLogger,
                    resourceLogger,
                    logger => LogBuildFallback(logger, "the canonical image pull failed", null));
                disposition = await BuildAsync(
                    recipe,
                    plan,
                    sourceState,
                    containerRuntime,
                    lifecycleLogger,
                    resourceLogger,
                    operations,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            LogBoth(
                lifecycleLogger,
                resourceLogger,
                logger => LogBuildFallback(logger, "the canonical image is not available locally and pulling is disabled", null));
            disposition = await BuildAsync(
                recipe,
                plan,
                sourceState,
                containerRuntime,
                lifecycleLogger,
                resourceLogger,
                operations,
                cancellationToken).ConfigureAwait(false);
        }

        await RetagAsync(
            recipe,
            containerRuntime,
            plan.CanonicalImageReference,
            recipe.LocalImageReference,
            lifecycleLogger,
            resourceLogger,
            operations,
            cancellationToken).ConfigureAwait(false);
        preparationStopwatch.Stop();
        LogBoth(
            lifecycleLogger,
            resourceLogger,
            logger => LogPreparationCompleted(
                logger,
                recipe.LocalImageReference,
                GetDispositionName(disposition),
                preparationStopwatch.Elapsed.TotalMilliseconds,
                null));
        return new ModulePreparedImage(
            plan.CanonicalImageReference,
            recipe.LocalImageReference,
            sourceState,
            disposition);
    }

    private static async Task<ModuleImageSourceState> RefreshAsync(
        ModuleImageBuildRecipe recipe,
        ModuleImageSourceState sourceState,
        ILogger lifecycleLogger,
        ILogger resourceLogger,
        IModuleImageRecipeOperations operations,
        CancellationToken cancellationToken)
    {
        string? skipReason = null;
        if (!recipe.RefreshCleanCheckout)
        {
            skipReason = "runtime refresh is disabled";
        }
        else if (recipe.Revision is not null)
        {
            skipReason = "the build repository is pinned to a revision";
        }
        else if (sourceState.IsDirty)
        {
            skipReason = "the build repository is dirty";
        }
        else if (!await operations.HasUpstreamAsync(recipe, cancellationToken).ConfigureAwait(false))
        {
            skipReason = "the current branch has no upstream";
        }

        if (skipReason is not null)
        {
            LogBoth(
                lifecycleLogger,
                resourceLogger,
                logger => LogRefreshSkipped(logger, recipe.RepositoryPath, skipReason, null));
            return sourceState;
        }

        LogBoth(
            lifecycleLogger,
            resourceLogger,
            logger => LogRefreshStarted(logger, recipe.RepositoryPath, null));
        var stopwatch = Stopwatch.StartNew();
        await operations.RefreshRepositoryAsync(
            recipe,
            line => LogRawOutput(resourceLogger, line),
            cancellationToken).ConfigureAwait(false);
        sourceState = await operations.CaptureSourceStateAsync(recipe, cancellationToken)
            .ConfigureAwait(false);
        stopwatch.Stop();
        LogBoth(
            lifecycleLogger,
            resourceLogger,
            logger => LogRefreshCompleted(
                logger,
                recipe.RepositoryPath,
                stopwatch.Elapsed.TotalMilliseconds,
                null));
        return sourceState;
    }

    private static async Task<ModuleImagePreparationDisposition> BuildAsync(
        ModuleImageBuildRecipe recipe,
        ModuleImageExecutionPlan plan,
        ModuleImageSourceState sourceState,
        string containerRuntime,
        ILogger lifecycleLogger,
        ILogger resourceLogger,
        IModuleImageRecipeOperations operations,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        await operations.BuildImageAsync(
            recipe,
            plan,
            containerRuntime,
            line => LogRawOutput(resourceLogger, line),
            cancellationToken).ConfigureAwait(false);
        var sourceStateAfterBuild = await operations.CaptureSourceStateAsync(recipe, cancellationToken)
            .ConfigureAwait(false);
        if (sourceStateAfterBuild != sourceState)
        {
            throw new InvalidOperationException(
                $"Build inputs changed while preparing image '{recipe.LocalImageReference}' for module " +
                $"'{recipe.ModuleName}' resource '{recipe.ResourceName}'. Retry after the repository is stable.");
        }

        if (plan.RequiresProducedImageRetag)
        {
            await RetagAsync(
                recipe,
                containerRuntime,
                plan.ProducedImageReference!,
                plan.CanonicalImageReference,
                lifecycleLogger,
                resourceLogger,
                operations,
                cancellationToken).ConfigureAwait(false);
        }

        stopwatch.Stop();
        LogBoth(
            lifecycleLogger,
            resourceLogger,
            logger => LogBuildCompleted(
                logger,
                plan.CanonicalImageReference,
                stopwatch.Elapsed.TotalMilliseconds,
                null));
        return ModuleImagePreparationDisposition.Built;
    }

    private static async Task RetagAsync(
        ModuleImageBuildRecipe recipe,
        string containerRuntime,
        string sourceImageReference,
        string targetImageReference,
        ILogger lifecycleLogger,
        ILogger resourceLogger,
        IModuleImageRecipeOperations operations,
        CancellationToken cancellationToken)
    {
        if (string.Equals(sourceImageReference, targetImageReference, StringComparison.Ordinal))
        {
            return;
        }

        LogBoth(
            lifecycleLogger,
            resourceLogger,
            logger => LogRetagStarted(logger, sourceImageReference, targetImageReference, null));
        var stopwatch = Stopwatch.StartNew();
        await operations.TagImageAsync(
            recipe,
            containerRuntime,
            sourceImageReference,
            targetImageReference,
            line => LogRawOutput(resourceLogger, line),
            cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();
        LogBoth(
            lifecycleLogger,
            resourceLogger,
            logger => LogRetagCompleted(
                logger,
                sourceImageReference,
                targetImageReference,
                stopwatch.Elapsed.TotalMilliseconds,
                null));
    }

    private static IDisposable? BeginScope(
        ILogger logger,
        ModuleImageBuildRecipe recipe,
        string operationId) =>
        logger.BeginScope(new Dictionary<string, object?>
        {
            ["OperationId"] = operationId,
            ["ModuleName"] = recipe.ModuleName,
            ["ResourceName"] = recipe.ResourceName,
            ["RepositoryPath"] = recipe.RepositoryPath,
            ["RepositoryKind"] = recipe.Revision is null ? "branch" : "revision",
            ["LocalImageReference"] = recipe.LocalImageReference
        });

    private static void LogRawOutput(ILogger resourceLogger, string output)
    {
        var redacted = ModuleCliOutputRedactor.Redact(output);
        if (!string.IsNullOrWhiteSpace(redacted))
        {
            LogCommandOutput(resourceLogger, redacted, null);
        }
    }

    private static void LogBoth(
        ILogger lifecycleLogger,
        ILogger resourceLogger,
        Action<ILogger> log)
    {
        log(lifecycleLogger);
    }

    private static string GetDispositionName(ModuleImagePreparationDisposition disposition) =>
        disposition switch
        {
            ModuleImagePreparationDisposition.Reused => "reusing the canonical image",
            ModuleImagePreparationDisposition.Pulled => "pulling the canonical image",
            ModuleImagePreparationDisposition.Built => "building the canonical image",
            _ => throw new ArgumentOutOfRangeException(nameof(disposition), disposition, null)
        };
}

internal sealed class ModuleImageRecipeOperations(IContainerRuntimeResolver? runtimeResolver = null)
    : IModuleImageRecipeOperations
{
    internal static ModuleImageRecipeOperations Instance { get; } = new();

    public async Task<string> ResolveContainerRuntimeAsync(CancellationToken cancellationToken) =>
        GetContainerRuntimeExecutableName(
            (await GetRuntimeResolver().ResolveAsync(cancellationToken).ConfigureAwait(false)).Name);

    internal static string GetContainerRuntimeExecutableName(string runtimeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeName);
        if (string.Equals(runtimeName, "Docker", StringComparison.OrdinalIgnoreCase))
        {
            return "docker";
        }

        if (string.Equals(runtimeName, "Podman", StringComparison.OrdinalIgnoreCase))
        {
            return "podman";
        }

        return runtimeName;
    }

    public async Task<ModuleImageSourceState> CaptureSourceStateAsync(
        ModuleImageBuildRecipe recipe,
        CancellationToken cancellationToken) =>
        await InspectSourceStateAsync(recipe, cancellationToken).ConfigureAwait(false);

    internal static async Task<ModuleImageSourceState> InspectSourceStateAsync(
        ModuleImageBuildRecipe recipe,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        var branch = await RunGitAsync(
            recipe,
            ["branch", "--show-current"],
            cancellationToken).ConfigureAwait(false);
        var commit = await RunGitAsync(
            recipe,
            ["rev-parse", "--short=12", "HEAD"],
            cancellationToken).ConfigureAwait(false);
        var status = await RunGitAsync(
            recipe,
            ["status", "--porcelain=v1", "--untracked-files=all"],
            cancellationToken).ConfigureAwait(false);
        var trackedPaths = await RunGitPathsAsync(
            recipe,
            ["diff", "--name-only", "-z", "--no-ext-diff", "HEAD", "--"],
            cancellationToken).ConfigureAwait(false);
        var stagedPaths = await RunGitPathsAsync(
            recipe,
            ["diff", "--cached", "--name-only", "-z", "--no-ext-diff", "HEAD", "--"],
            cancellationToken).ConfigureAwait(false);
        var untrackedPaths = await RunGitPathsAsync(
            recipe,
            ["ls-files", "--others", "--exclude-standard", "-z", "--"],
            cancellationToken).ConfigureAwait(false);
        var fingerprint = await CreateStatusFingerprintAsync(
            recipe.RepositoryPath,
            status,
            [trackedPaths, stagedPaths, untrackedPaths],
            cancellationToken).ConfigureAwait(false);
        return new ModuleImageSourceState(
            string.IsNullOrWhiteSpace(branch) ? null : branch.Trim(),
            string.IsNullOrWhiteSpace(commit) ? null : commit.Trim(),
            !string.IsNullOrWhiteSpace(status),
            fingerprint);
    }

    public Task<bool> HasUpstreamAsync(
        ModuleImageBuildRecipe recipe,
        CancellationToken cancellationToken) =>
        RepositoryInspector.HasUpstreamAsync(
            recipe.RepositoryPath,
            recipe.GitExecutablePath,
            recipe.RepositoryCommandTimeout,
            cancellationToken);

    public Task RefreshRepositoryAsync(
        ModuleImageBuildRecipe recipe,
        Action<string> progress,
        CancellationToken cancellationToken) =>
        RepositorySynchronizer.SynchronizeAsync(
            recipe.RepositoryPath,
            recipe.Repository,
            updateRepository: true,
            cancellationToken,
            revision: null,
            recipe.GitExecutablePath,
            recipe.GitHubCliPath,
            recipe.RepositoryCommandTimeout,
            progress);

    public Task<bool> ImageExistsAsync(
        string containerRuntime,
        string imageReference,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        ContainerImageInspector.ExistsAsync(containerRuntime, imageReference, timeout, cancellationToken);

    public Task<bool> PullImageAsync(
        string containerRuntime,
        string imageReference,
        TimeSpan timeout,
        Action<string> progress,
        CancellationToken cancellationToken) =>
        ContainerImageInspector.PullAsync(
            containerRuntime,
            imageReference,
            timeout,
            progress,
            cancellationToken);

    public async Task BuildImageAsync(
        ModuleImageBuildRecipe recipe,
        ModuleImageExecutionPlan plan,
        string containerRuntime,
        Action<string> progress,
        CancellationToken cancellationToken)
    {
        await ModuleOperationTimeout.RunAsync(
            async operationToken =>
            {
                var publishCommand = ResolveContainerRuntimePlaceholder(
                    recipe.Options.PublishCommand,
                    containerRuntime);
                var publishArguments = plan.PublishArguments
                    .Select(argument => ResolveContainerRuntimePlaceholder(argument, containerRuntime))
                    .ToArray();
                var result = await CliCommand.Wrap(publishCommand)
                    .WithArguments(publishArguments)
                    .WithWorkingDirectory(recipe.WorkingDirectory)
                    .WithEnvironmentVariables(new Dictionary<string, string?>
                    {
                        ["ASPIRE_MODULE_IMAGE"] = plan.CanonicalImageReference
                    })
                    .WithValidation(CommandResultValidation.None)
                    .WithStandardOutputPipe(PipeTarget.ToDelegate(line =>
                        progress(ModuleCliOutputRedactor.Redact(line))))
                    .WithStandardErrorPipe(PipeTarget.ToDelegate(line =>
                        progress(ModuleCliOutputRedactor.Redact(line))))
                    .ExecuteAsync(operationToken)
                    .ConfigureAwait(false);
                if (result.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"Image build command '{ModuleCliOutputRedactor.Redact(recipe.Options.PublishCommand)}' failed " +
                        $"for module '{recipe.ModuleName}' resource '{recipe.ResourceName}' with exit code {result.ExitCode}.");
                }
            },
            recipe.ImageBuildTimeout,
            $"Image build for module '{recipe.ModuleName}' resource '{recipe.ResourceName}'",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task TagImageAsync(
        ModuleImageBuildRecipe recipe,
        string containerRuntime,
        string sourceImageReference,
        string targetImageReference,
        Action<string> progress,
        CancellationToken cancellationToken)
    {
        var runtime = await GetRuntimeResolver().ResolveAsync(cancellationToken).ConfigureAwait(false);
        await ModuleOperationTimeout.RunAsync(
            token => runtime.TagImageAsync(sourceImageReference, targetImageReference, token),
            recipe.ImageTransferTimeout,
            $"Image tag for module '{recipe.ModuleName}' resource '{recipe.ResourceName}'",
            cancellationToken).ConfigureAwait(false);
    }

    private IContainerRuntimeResolver GetRuntimeResolver() =>
        runtimeResolver ?? throw new InvalidOperationException(
            "Aspire's IContainerRuntimeResolver is required for container runtime operations.");

    private static async Task<string> RunGitAsync(
        ModuleImageBuildRecipe recipe,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await ModuleCliRunner.RunAsync(
            recipe.GitExecutablePath,
            ["-C", recipe.RepositoryPath, .. arguments],
            recipe.RepositoryPath,
            recipe.RepositoryCommandTimeout,
            $"inspect image build repository for {recipe.ModuleName}/{recipe.ResourceName}",
            cancellationToken,
            static _ => { }).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(
                $"Unable to inspect image build repository '{recipe.RepositoryPath}' for module " +
                $"'{recipe.ModuleName}' resource '{recipe.ResourceName}' with Git executable " +
                $"'{ModuleCliOutputRedactor.Redact(recipe.GitExecutablePath)}'.");
        }

        return result.StandardOutput;
    }

    private static async Task<string> CreateStatusFingerprintAsync(
        string repositoryPath,
        string status,
        IEnumerable<IReadOnlyList<string>> pathLists,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendFingerprintValue(hash, "status");
        AppendFingerprintValue(hash, status);

        var paths = pathLists
            .SelectMany(static pathList => pathList)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        var buffer = new byte[81920];
        foreach (var relativePath in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AppendFingerprintValue(hash, "path");
            AppendFingerprintValue(hash, relativePath);
            var fullPath = Path.GetFullPath(relativePath, repositoryPath);
            var pathFromRoot = Path.GetRelativePath(repositoryPath, fullPath);
            if (Path.IsPathRooted(pathFromRoot) ||
                pathFromRoot.Equals("..", StringComparison.Ordinal) ||
                pathFromRoot.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                pathFromRoot.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Git reported source path '{relativePath}' outside repository '{repositoryPath}'.");
            }

            var file = new FileInfo(fullPath);
            if (file.LinkTarget is { } linkTarget)
            {
                AppendFingerprintValue(hash, "link");
                AppendFingerprintValue(hash, linkTarget);
                continue;
            }

            if (!file.Exists)
            {
                AppendFingerprintValue(
                    hash,
                    Directory.Exists(fullPath) ? "directory" : "missing");
                continue;
            }

            AppendFingerprintValue(hash, "file");
            var attributes = file.Attributes;
            AppendFingerprintValue(
                hash,
                ((int)attributes).ToString(System.Globalization.CultureInfo.InvariantCulture));
            if ((attributes & FileAttributes.Device) != 0)
            {
                AppendFingerprintValue(hash, "device");
                continue;
            }

            if (!OperatingSystem.IsWindows())
            {
                AppendFingerprintValue(
                    hash,
                    ((int)File.GetUnixFileMode(fullPath))
                        .ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            var length = file.Length;
            AppendFingerprintValue(hash, length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (length == 0)
            {
                continue;
            }

            var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                buffer.Length,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (stream.ConfigureAwait(false))
            {
                int bytesRead;
                while ((bytesRead = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buffer, 0, bytesRead);
                }
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static async Task<IReadOnlyList<string>> RunGitPathsAsync(
        ModuleImageBuildRecipe recipe,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        using var timeout = new CancellationTokenSource(recipe.RepositoryCommandTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);
        try
        {
            var result = await CliCommand.Wrap(recipe.GitExecutablePath)
                .WithArguments(["-C", recipe.RepositoryPath, .. arguments])
                .WithWorkingDirectory(recipe.RepositoryPath)
                .WithValidation(CommandResultValidation.None)
                .WithStandardOutputPipe(PipeTarget.ToStream(output))
                .WithStandardErrorPipe(PipeTarget.ToStream(Stream.Null))
                .ExecuteAsync(linked.Token)
                .ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                throw CreateGitInspectionException(recipe);
            }
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Image source inspection for module '{recipe.ModuleName}' resource '{recipe.ResourceName}' " +
                $"exceeded the configured timeout of {recipe.RepositoryCommandTimeout}.",
                exception);
        }

        return Encoding.UTF8.GetString(output.ToArray())
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    private static InvalidOperationException CreateGitInspectionException(
        ModuleImageBuildRecipe recipe) =>
        new(
            $"Unable to inspect image build repository '{recipe.RepositoryPath}' for module " +
            $"'{recipe.ModuleName}' resource '{recipe.ResourceName}' with Git executable " +
            $"'{ModuleCliOutputRedactor.Redact(recipe.GitExecutablePath)}'.");

    private static void AppendFingerprintValue(IncrementalHash hash, string value)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(value));
        hash.AppendData([0]);
    }

    private static string ResolveContainerRuntimePlaceholder(string value, string containerRuntime) =>
        value.Replace(
            ModuleImageCommandOptions.ContainerRuntimePlaceholder,
            containerRuntime,
            StringComparison.Ordinal);
}
