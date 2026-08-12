using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

internal enum ModuleRequiredPathKind
{
    Directory,
    File
}

internal sealed record ModuleRequiredPath(
    string ModuleName,
    string Description,
    string Path,
    ModuleRequiredPathKind Kind);

internal static class ModuleRepositoryPreflight
{
    private static readonly Action<ILogger, int, string, Exception?> LogPreflightFailed =
        LoggerMessage.Define<int, string>(
            LogLevel.Error,
            new EventId(10, nameof(LogPreflightFailed)),
            "Repository preflight failed with {FailureCount} problem(s). Run '{InitializeCommand}'.");

    public static string CreateInitializeCommand(string appHostPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appHostPath);
        return $"aspire do initialize --apphost \"{Path.GetFullPath(appHostPath)}\" --non-interactive";
    }

    public static async Task ValidateAsync(
        IEnumerable<ModuleRepositoryRequirement> repositories,
        IEnumerable<ModuleRequiredPath> requiredPaths,
        IModuleRepositoryStateStore stateStore,
        ModuleRepositoryInitializationSettings settings,
        string appHostPath,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repositories);
        ArgumentNullException.ThrowIfNull(requiredPaths);
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(settings);

        var failures = new List<string>();
        foreach (var repository in repositories.OrderBy(
            requirement => requirement.RepositoryPath,
            PathSafety.Comparer))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(repository.RepositoryPath))
            {
                failures.Add(
                    $"modules {FormatModules(repository.ModuleNames)} require repository " +
                    $"'{repository.NormalizedRepository}' at '{repository.RepositoryPath}', but the directory is missing");
                continue;
            }

            if (!await RepositoryInspector.IsGitRepositoryAsync(
                    repository.RepositoryPath,
                    settings.GitExecutablePath,
                    settings.CommandTimeout,
                    requireSuccessfulInspection: true,
                    cancellationToken).ConfigureAwait(false))
            {
                failures.Add(
                    $"modules {FormatModules(repository.ModuleNames)} require '{repository.RepositoryPath}' to be a Git checkout");
                continue;
            }

            var state = await stateStore.ReadAsync(repository, cancellationToken).ConfigureAwait(false);
            if (state is null || !state.Matches(repository))
            {
                failures.Add(
                    $"modules {FormatModules(repository.ModuleNames)} have no current initialization state for " +
                    $"'{repository.RepositoryPath}'");
                continue;
            }

            var origin = await RepositoryInspector.TryGetRemoteAsync(
                repository.RepositoryPath,
                settings.GitExecutablePath,
                settings.CommandTimeout,
                cancellationToken).ConfigureAwait(false);
            var normalizedOrigin = origin is null
                ? null
                : RepositoryIdentity.NormalizeRepositoryIdentity(
                    origin,
                    repository.RepositoryPath);
            if (!string.Equals(normalizedOrigin, repository.NormalizedRepository, StringComparison.Ordinal) ||
                !string.Equals(state.Origin, repository.NormalizedRepository, StringComparison.Ordinal))
            {
                failures.Add(
                    $"modules {FormatModules(repository.ModuleNames)} require origin " +
                    $"'{repository.NormalizedRepository}' at '{repository.RepositoryPath}', but the checkout origin differs");
                continue;
            }

            if (repository.Revision is not null)
            {
                var head = await RepositoryInspector.TryResolveCommitAsync(
                    repository.RepositoryPath,
                    "HEAD",
                    settings.GitExecutablePath,
                    settings.CommandTimeout,
                    cancellationToken).ConfigureAwait(false);
                var expected = await RepositoryInspector.TryResolveCommitAsync(
                    repository.RepositoryPath,
                    repository.Revision,
                    settings.GitExecutablePath,
                    settings.CommandTimeout,
                    cancellationToken).ConfigureAwait(false);
                if (head is null || expected is null ||
                    !string.Equals(head, expected, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(head, state.ResolvedCommit, StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add(
                        $"modules {FormatModules(repository.ModuleNames)} require revision '{repository.Revision}' " +
                        $"at '{repository.RepositoryPath}', but HEAD does not match the initialized commit");
                }
            }
        }

        foreach (var requiredPath in requiredPaths
            .OrderBy(path => path.ModuleName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => path.Path, PathSafety.Comparer))
        {
            var exists = requiredPath.Kind == ModuleRequiredPathKind.File
                ? File.Exists(requiredPath.Path)
                : Directory.Exists(requiredPath.Path);
            if (!exists)
            {
                failures.Add(
                    $"module '{requiredPath.ModuleName}' requires {requiredPath.Description} at " +
                    $"'{requiredPath.Path}', but it is missing");
            }
        }

        if (failures.Count == 0)
        {
            return;
        }

        var initializeCommand = CreateInitializeCommand(appHostPath);
        var exception = new InvalidOperationException(
            "Modular AppHost initialization is incomplete:" + Environment.NewLine +
            string.Join(Environment.NewLine, failures.Select(failure => $"  - {failure}.")) +
            Environment.NewLine +
            $"Run '{initializeCommand}'.");
        if (logger is not null)
        {
            LogPreflightFailed(logger, failures.Count, initializeCommand, exception);
        }

        throw exception;
    }

    private static string FormatModules(IEnumerable<string> moduleNames) =>
        string.Join(
            ", ",
            moduleNames
                .Order(StringComparer.OrdinalIgnoreCase)
                .Select(name => $"'{name}'"));
}
