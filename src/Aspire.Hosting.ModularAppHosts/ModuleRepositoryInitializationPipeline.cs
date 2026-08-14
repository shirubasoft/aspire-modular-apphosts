#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES002
#pragma warning disable ASPIREPIPELINES003

using System.Diagnostics;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

internal sealed class ModuleRepositoryInitializationSettings
{
    public ModuleRepositoryInitializationSettings(
        string gitExecutablePath,
        string githubCliPath,
        TimeSpan commandTimeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gitExecutablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(githubCliPath);
        if (commandTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(commandTimeout),
                commandTimeout,
                "The repository command timeout must be positive.");
        }

        GitExecutablePath = gitExecutablePath;
        GitHubCliPath = githubCliPath;
        CommandTimeout = commandTimeout;
    }

    public string GitExecutablePath { get; }

    public string GitHubCliPath { get; }

    public TimeSpan CommandTimeout { get; }
}

internal static class ModuleRepositoryInitializationPipeline
{
    internal const string StepName = "initialize";
    internal const string RepositoryStepTag = "initialize-module-repository";

    private sealed class InitializationPipelineRegistration;

    private static readonly Action<ILogger, string, string, string, Exception?> LogInitializationStarted =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Information,
            new EventId(1, nameof(LogInitializationStarted)),
            "Initializing repository {Repository} at {RepositoryPath} for modules {Modules}.");

    private static readonly Action<ILogger, string, string, Exception?> LogInitializationProgress =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            new EventId(2, nameof(LogInitializationProgress)),
            "Repository {Repository}: {Output}");

    private static readonly Action<ILogger, string, string, double, Exception?> LogInitializationCompleted =
        LoggerMessage.Define<string, string, double>(
            LogLevel.Information,
            new EventId(3, nameof(LogInitializationCompleted)),
            "Initialized repository {Repository} at {RepositoryPath} in {ElapsedMilliseconds} ms.");

    private static readonly Action<ILogger, string, string, string, string, double, Exception?> LogRepositoryOperation =
        LoggerMessage.Define<string, string, string, string, double>(
            LogLevel.Information,
            new EventId(4, nameof(LogRepositoryOperation)),
            "Repository operation {Operation} {State} for {Repository} at {RepositoryPath}. " +
            "Elapsed: {ElapsedMilliseconds} ms.");

    private static readonly Action<ILogger, string, string, string, string, string, Exception?> LogRepositoryOperationSkipped =
        LoggerMessage.Define<string, string, string, string, string>(
            LogLevel.Information,
            new EventId(5, nameof(LogRepositoryOperationSkipped)),
            "Repository operation {Operation} {State} for {Repository} at {RepositoryPath}: {Reason}.");

    public static void Configure(IDistributedApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (builder.Services.Any(descriptor =>
                descriptor.ServiceType == typeof(InitializationPipelineRegistration)))
        {
            return;
        }

        builder.Services.AddSingleton<InitializationPipelineRegistration>();
        builder.Pipeline.AddStep(CreateAggregateStep());
    }

    public static void AddRepositoryStep(
        IDistributedApplicationBuilder builder,
        ModuleRepositoryRequirement requirement,
        Func<ModuleRepositoryInitializationSettings> settingsFactory)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Pipeline.AddStep(CreateRepositoryStep(requirement, settingsFactory));
    }

    internal static PipelineStep CreateAggregateStep() => new()
    {
        Name = StepName,
        Description = "Initializes all prerequisites required by the AppHost.",
        Action = _ => Task.CompletedTask
    };

    internal static PipelineStep CreateRepositoryStep(
        ModuleRepositoryRequirement requirement,
        Func<ModuleRepositoryInitializationSettings> settingsFactory)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        ArgumentNullException.ThrowIfNull(settingsFactory);
        return new PipelineStep
        {
            Name = GetRepositoryStepName(requirement),
            Description =
                $"Initializes repository {requirement.NormalizedRepository} for " +
                $"{string.Join(", ", requirement.ModuleNames.Order(StringComparer.OrdinalIgnoreCase))}.",
            Action = async context =>
            {
                var task = await context.ReportingStep.CreateTaskAsync(
                    $"Initialize {requirement.NormalizedRepository}",
                    context.CancellationToken).ConfigureAwait(false);
                await using var configuredTask = task.ConfigureAwait(false);
                try
                {
                    await InitializeAndRecordAsync(
                        requirement,
                        settingsFactory(),
                        context.Logger,
                        context.Logger,
                        context.Services.GetRequiredService<IModuleRepositoryStateStore>(),
                        context.ReportingStep,
                        context.CancellationToken).ConfigureAwait(false);
                    await task.SucceedAsync(
                        $"Initialized at {requirement.RepositoryPath}",
                        context.CancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    await task.FailAsync(
                        exception.Message,
                        CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
            },
            RequiredBySteps = [StepName],
            Tags = [RepositoryStepTag]
        };
    }

    internal static string GetRepositoryStepName(ModuleRepositoryRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        return $"initialize-{requirement.StepKey}";
    }

    internal static Task InitializeAsync(
        ModuleRepositoryRequirement requirement,
        ModuleRepositoryInitializationSettings settings,
        ILogger logger,
        CancellationToken cancellationToken) =>
        InitializeAsync(
            requirement,
            settings,
            logger,
            logger,
            static (plannedRepository, initializationSettings, progress, lifecycle, token) =>
                RepositorySynchronizer.SynchronizeAsync(
                    plannedRepository.RepositoryPath,
                    plannedRepository.Repository,
                    plannedRepository.UpdateOnInitialize,
                    token,
                    plannedRepository.Revision,
                    initializationSettings.GitExecutablePath,
                    initializationSettings.GitHubCliPath,
                    initializationSettings.CommandTimeout,
                    progress,
                    lifecycle),
            cancellationToken);

    internal static async Task InitializeAndRecordAsync(
        ModuleRepositoryRequirement requirement,
        ModuleRepositoryInitializationSettings settings,
        ILogger lifecycleLogger,
        ILogger resourceLogger,
        IModuleRepositoryStateStore stateStore,
        IReportingStep? reportingStep,
        CancellationToken cancellationToken,
        Func<
            ModuleRepositoryRequirement,
            ModuleRepositoryInitializationSettings,
            Action<string>,
            Action<RepositorySyncLifecycleEvent>,
            CancellationToken,
            Task>? synchronizeAsync = null)
    {
        var previousState = await stateStore.ReadAsync(
            requirement,
            cancellationToken).ConfigureAwait(false);
        var ownership = ClassifyOwnership(requirement, previousState);
        if (requirement.UsesCanonicalCheckout && PathExists(requirement.RepositoryPath))
        {
            await ValidateCanonicalCheckoutAsync(
                requirement,
                settings,
                cancellationToken).ConfigureAwait(false);
        }

        if (ownership == ModuleRepositoryCheckoutOwnership.Adopted)
        {
            if (!PathExists(requirement.RepositoryPath))
            {
                throw new InvalidOperationException(
                    $"Repository state records canonical checkout '{requirement.RepositoryPath}' for " +
                    $"'{requirement.NormalizedRepository}' as Adopted, but the developer-owned checkout is missing. " +
                    "Restore the checkout; initialization will not replace an adopted checkout with an initializer-owned clone.");
            }

            LogRepositoryOperationSkipped(
                lifecycleLogger,
                "synchronize",
                "skipped",
                requirement.NormalizedRepository,
                requirement.RepositoryPath,
                "adopted-developer-checkout",
                null);
        }
        else
        {
            if (synchronizeAsync is null)
            {
                await InitializeAsync(
                    requirement,
                    settings,
                    lifecycleLogger,
                    resourceLogger,
                    reportingStep,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await InitializeAsync(
                    requirement,
                    settings,
                    lifecycleLogger,
                    resourceLogger,
                    synchronizeAsync,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        await RecordStateAsync(
            requirement,
            settings,
            stateStore,
            ownership,
            cancellationToken).ConfigureAwait(false);
    }

    internal static Task InitializeAsync(
        ModuleRepositoryRequirement requirement,
        ModuleRepositoryInitializationSettings settings,
        ILogger lifecycleLogger,
        ILogger resourceLogger,
        IReportingStep? reportingStep,
        CancellationToken cancellationToken) =>
        InitializeAsync(
            requirement,
            settings,
            lifecycleLogger,
            resourceLogger,
            (plannedRepository, initializationSettings, progress, lifecycle, token) =>
                RepositorySynchronizer.SynchronizeAsync(
                    plannedRepository.RepositoryPath,
                    plannedRepository.Repository,
                    plannedRepository.UpdateOnInitialize,
                    token,
                    plannedRepository.Revision,
                    initializationSettings.GitExecutablePath,
                    initializationSettings.GitHubCliPath,
                    initializationSettings.CommandTimeout,
                    progress,
                    lifecycle,
                    reportingStep),
            cancellationToken);

    internal static async Task InitializeAsync(
        ModuleRepositoryRequirement requirement,
        ModuleRepositoryInitializationSettings settings,
        ILogger logger,
        Func<
            ModuleRepositoryRequirement,
            ModuleRepositoryInitializationSettings,
            Action<string>,
            Action<RepositorySyncLifecycleEvent>,
            CancellationToken,
            Task> synchronizeAsync,
        CancellationToken cancellationToken)
        => await InitializeAsync(
            requirement,
            settings,
            logger,
            logger,
            synchronizeAsync,
            cancellationToken).ConfigureAwait(false);

    internal static async Task InitializeAsync(
        ModuleRepositoryRequirement requirement,
        ModuleRepositoryInitializationSettings settings,
        ILogger lifecycleLogger,
        ILogger resourceLogger,
        Func<
            ModuleRepositoryRequirement,
            ModuleRepositoryInitializationSettings,
            Action<string>,
            Action<RepositorySyncLifecycleEvent>,
            CancellationToken,
            Task> synchronizeAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(lifecycleLogger);
        ArgumentNullException.ThrowIfNull(resourceLogger);
        ArgumentNullException.ThrowIfNull(synchronizeAsync);
        cancellationToken.ThrowIfCancellationRequested();

        var operationId = Guid.NewGuid().ToString("N");
        var scopeState = new Dictionary<string, object?>
        {
            ["OperationId"] = operationId,
            ["Repository"] = requirement.NormalizedRepository,
            ["RepositoryPath"] = requirement.RepositoryPath,
            ["RepositoryKind"] = requirement.Revision is null ? "branch" : "revision",
            ["Modules"] = requirement.ModuleNames.Order(StringComparer.OrdinalIgnoreCase).ToArray()
        };
        using var lifecycleScope = lifecycleLogger.BeginScope(scopeState);
        using var resourceScope = ReferenceEquals(lifecycleLogger, resourceLogger)
            ? null
            : resourceLogger.BeginScope(scopeState);
        var modules = string.Join(", ", requirement.ModuleNames.Order(StringComparer.OrdinalIgnoreCase));
        LogInitializationStarted(
            lifecycleLogger,
            requirement.NormalizedRepository,
            requirement.RepositoryPath,
            modules,
            null);
        var stopwatch = Stopwatch.StartNew();
        await synchronizeAsync(
            requirement,
            settings,
            progress => LogInitializationProgress(
                resourceLogger,
                requirement.NormalizedRepository,
                progress,
                null),
            lifecycle =>
            {
                if (string.IsNullOrWhiteSpace(lifecycle.Reason))
                {
                    LogRepositoryOperation(
                        lifecycleLogger,
                        lifecycle.Operation,
                        lifecycle.State,
                        requirement.NormalizedRepository,
                        requirement.RepositoryPath,
                        lifecycle.ElapsedMilliseconds,
                        null);
                }
                else
                {
                    LogRepositoryOperationSkipped(
                        lifecycleLogger,
                        lifecycle.Operation,
                        lifecycle.State,
                        requirement.NormalizedRepository,
                        requirement.RepositoryPath,
                        lifecycle.Reason,
                        null);
                }
            },
            cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();
        LogInitializationCompleted(
            lifecycleLogger,
            requirement.NormalizedRepository,
            requirement.RepositoryPath,
            stopwatch.Elapsed.TotalMilliseconds,
            null);
    }

    internal static async Task RecordStateAsync(
        ModuleRepositoryRequirement requirement,
        ModuleRepositoryInitializationSettings settings,
        IModuleRepositoryStateStore stateStore,
        ModuleRepositoryCheckoutOwnership ownership,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(stateStore);
        var origin = await RepositoryInspector.TryGetRemoteAsync(
            requirement.RepositoryPath,
            settings.GitExecutablePath,
            settings.CommandTimeout,
            cancellationToken).ConfigureAwait(false);
        var resolvedCommit = await RepositoryInspector.TryResolveCommitAsync(
            requirement.RepositoryPath,
            "HEAD",
            settings.GitExecutablePath,
            settings.CommandTimeout,
            cancellationToken).ConfigureAwait(false);
        if (origin is null || resolvedCommit is null)
        {
            throw new InvalidOperationException(
                $"Repository '{requirement.RepositoryPath}' could not be inspected after initialization.");
        }

        var normalizedOrigin = RepositoryIdentity.NormalizeRepositoryIdentity(
            origin,
            requirement.RepositoryPath);
        if (!string.Equals(
                normalizedOrigin,
                requirement.NormalizedRepository,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Repository '{requirement.RepositoryPath}' has origin '{normalizedOrigin}' after initialization; " +
                $"expected '{requirement.NormalizedRepository}'.");
        }

        var expectedState = new ModuleRepositoryInitializationState(
            ModuleRepositoryInitializationState.CurrentSchemaVersion,
            requirement.NormalizedRepository,
            requirement.RepositoryPath,
            requirement.Revision,
            requirement.ConfigurationFingerprint,
            normalizedOrigin,
            resolvedCommit,
            ownership,
            DateTimeOffset.UtcNow);
        await stateStore.WriteAsync(
            requirement,
            expectedState,
            cancellationToken).ConfigureAwait(false);
        var persistedState = await stateStore.ReadAsync(
            requirement,
            cancellationToken).ConfigureAwait(false);
        if (persistedState != expectedState || !persistedState.Matches(requirement))
        {
            var stateLocation = string.IsNullOrWhiteSpace(stateStore.StateFilePath)
                ? string.Empty
                : $" at '{stateStore.StateFilePath}'";
            throw new InvalidOperationException(
                $"Repository initialization state for '{requirement.RepositoryPath}' could not be verified" +
                $"{stateLocation} after it was written.");
        }
    }

    private static ModuleRepositoryCheckoutOwnership ClassifyOwnership(
        ModuleRepositoryRequirement requirement,
        ModuleRepositoryInitializationState? previousState)
    {
        if (!requirement.UsesCanonicalCheckout)
        {
            return ModuleRepositoryCheckoutOwnership.Created;
        }

        if (previousState?.RefersTo(requirement) == true)
        {
            return previousState.Ownership;
        }

        return PathExists(requirement.RepositoryPath)
            ? ModuleRepositoryCheckoutOwnership.Adopted
            : ModuleRepositoryCheckoutOwnership.Created;
    }

    private static async Task ValidateCanonicalCheckoutAsync(
        ModuleRepositoryRequirement requirement,
        ModuleRepositoryInitializationSettings settings,
        CancellationToken cancellationToken)
    {
        bool isGitRepository;
        try
        {
            isGitRepository = await RepositoryInspector.IsGitRepositoryAsync(
                requirement.RepositoryPath,
                settings.GitExecutablePath,
                settings.CommandTimeout,
                requireSuccessfulInspection: true,
                cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidOperationException(
                CreateCanonicalCheckoutConflictException(
                    requirement,
                    actualOrigin: null,
                    $"the canonical checkout could not be inspected with Git executable " +
                    $"'{settings.GitExecutablePath}'").Message,
                exception);
        }
        if (!isGitRepository)
        {
            throw CreateCanonicalCheckoutConflictException(
                requirement,
                actualOrigin: null,
                "the canonical directory exists but is not a Git checkout");
        }

        var origin = await RepositoryInspector.TryGetRemoteAsync(
            requirement.RepositoryPath,
            settings.GitExecutablePath,
            settings.CommandTimeout,
            cancellationToken).ConfigureAwait(false);
        var normalizedOrigin = TryNormalizeOrigin(origin, requirement.RepositoryPath);
        if (!string.Equals(
                normalizedOrigin,
                requirement.NormalizedRepository,
                StringComparison.Ordinal))
        {
            throw CreateCanonicalCheckoutConflictException(
                requirement,
                normalizedOrigin,
                origin is null
                    ? "the checkout has no origin"
                    : "the checkout origin does not match the planned repository");
        }
    }

    private static InvalidOperationException CreateCanonicalCheckoutConflictException(
        ModuleRepositoryRequirement requirement,
        string? actualOrigin,
        string reason)
    {
        var configurationKeys = string.Join(
            ", ",
            requirement.CheckoutDirectoryNameConfigurationKeys
                .Order(StringComparer.Ordinal)
                .Select(key => $"'{key}'"));
        var actual = actualOrigin is null
            ? string.Empty
            : $" Actual normalized origin: '{actualOrigin}'.";
        return new InvalidOperationException(
            $"Canonical checkout conflict at '{requirement.RepositoryPath}': {reason}. " +
            $"Expected normalized repository identity: '{requirement.NormalizedRepository}'.{actual} " +
            $"Choose a distinct sibling name with CheckoutDirectoryName at configuration key(s) " +
            $"{configurationKeys}; initialization will not clone to a hashed fallback.");
    }

    private static string? TryNormalizeOrigin(string? origin, string repositoryPath)
    {
        if (string.IsNullOrWhiteSpace(origin))
        {
            return null;
        }

        try
        {
            return RepositoryIdentity.NormalizeRepositoryIdentity(origin, repositoryPath);
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                          or ArgumentException
                                          or NotSupportedException)
        {
            return null;
        }
    }

    private static bool PathExists(string path) => Directory.Exists(path) || File.Exists(path);

}
