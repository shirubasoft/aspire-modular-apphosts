#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES003

using System.Diagnostics;
using Aspire.Hosting.Pipelines;
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

    public static bool IsInitializeCommand(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return string.Equals(
            ModuleImagePipelineSelectionParser.GetRequestedStep(arguments),
            StepName,
            StringComparison.OrdinalIgnoreCase);
    }

    public static void Configure(IDistributedApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
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
        Description = "Initializes all Git repositories required by modular AppHosts.",
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
            Action = context => InitializeAsync(
                requirement,
                settingsFactory(),
                context.Logger,
                context.CancellationToken),
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
    {
        ArgumentNullException.ThrowIfNull(requirement);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(synchronizeAsync);
        cancellationToken.ThrowIfCancellationRequested();

        var operationId = Guid.NewGuid().ToString("N");
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["OperationId"] = operationId,
            ["Repository"] = requirement.NormalizedRepository,
            ["RepositoryPath"] = requirement.RepositoryPath,
            ["RepositoryKind"] = requirement.Revision is null ? "branch" : "revision",
            ["Modules"] = requirement.ModuleNames.Order(StringComparer.OrdinalIgnoreCase).ToArray()
        });
        var modules = string.Join(", ", requirement.ModuleNames.Order(StringComparer.OrdinalIgnoreCase));
        LogInitializationStarted(
            logger,
            requirement.NormalizedRepository,
            requirement.RepositoryPath,
            modules,
            null);
        var stopwatch = Stopwatch.StartNew();
        await synchronizeAsync(
            requirement,
            settings,
            progress => LogInitializationProgress(
                logger,
                requirement.NormalizedRepository,
                ModuleCliOutputRedactor.Redact(progress),
                null),
            lifecycle =>
            {
                if (string.IsNullOrWhiteSpace(lifecycle.Reason))
                {
                    LogRepositoryOperation(
                        logger,
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
                        logger,
                        lifecycle.Operation,
                        lifecycle.State,
                        requirement.NormalizedRepository,
                        requirement.RepositoryPath,
                        lifecycle.Reason,
                        null);
                }
            },
            cancellationToken).ConfigureAwait(false);
        await ModuleInitializationReceiptStore.WriteAsync(
            requirement,
            DateTimeOffset.UtcNow,
            cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();
        LogInitializationCompleted(
            logger,
            requirement.NormalizedRepository,
            requirement.RepositoryPath,
            stopwatch.Elapsed.TotalMilliseconds,
            null);
    }
}
