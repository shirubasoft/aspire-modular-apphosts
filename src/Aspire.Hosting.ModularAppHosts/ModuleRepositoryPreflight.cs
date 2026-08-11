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
    internal const string InitializeCommand = "aspire do initialize --non-interactive";

    private static readonly Action<ILogger, int, string, Exception?> LogPreflightFailed =
        LoggerMessage.Define<int, string>(
            LogLevel.Error,
            new EventId(10, nameof(LogPreflightFailed)),
            "Repository preflight failed with {FailureCount} problem(s). Run '{InitializeCommand}'.");

    public static void Validate(
        IEnumerable<ModuleRepositoryRequirement> repositories,
        IEnumerable<ModuleRequiredPath> requiredPaths,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(repositories);
        ArgumentNullException.ThrowIfNull(requiredPaths);

        var failures = new List<string>();
        foreach (var repository in repositories.OrderBy(
            requirement => requirement.RepositoryPath,
            PathSafety.Comparer))
        {
            if (!Directory.Exists(repository.RepositoryPath))
            {
                failures.Add(
                    $"modules {FormatModules(repository.ModuleNames)} require repository " +
                    $"'{repository.NormalizedRepository}' at '{repository.RepositoryPath}', but the directory is missing");
                continue;
            }

            if (!ModuleRepositoryPathPlanner.HasGitMetadata(repository.RepositoryPath))
            {
                failures.Add(
                    $"modules {FormatModules(repository.ModuleNames)} require '{repository.RepositoryPath}' to be a Git checkout");
                continue;
            }

            if (!ModuleInitializationReceiptStore.HasMatchingReceipt(repository))
            {
                failures.Add(
                    $"modules {FormatModules(repository.ModuleNames)} have no current initialization receipt for " +
                    $"'{repository.RepositoryPath}'");
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

        var exception = new InvalidOperationException(
            "Modular AppHost initialization is incomplete:" + Environment.NewLine +
            string.Join(Environment.NewLine, failures.Select(failure => $"  - {failure}.")) +
            Environment.NewLine +
            $"Run '{InitializeCommand}'.");
        if (logger is not null)
        {
            LogPreflightFailed(logger, failures.Count, InitializeCommand, exception);
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
