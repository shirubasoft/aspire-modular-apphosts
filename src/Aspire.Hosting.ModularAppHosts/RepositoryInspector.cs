#pragma warning disable ASPIREPIPELINES001

using CliWrap;
using CliWrap.Buffered;
using Aspire.Hosting.Pipelines;
using CliCommand = global::CliWrap.Cli;

namespace Aspire.Hosting;

internal static class RepositoryInspector
{
    public static async Task<string> FindRepositoryRootAsync(
        string projectPath,
        string gitExecutablePath = "git",
        TimeSpan? commandTimeout = null,
        CancellationToken cancellationToken = default)
    {
        var root = await TryFindRepositoryRootAsync(
            projectPath,
            gitExecutablePath,
            commandTimeout,
            cancellationToken).ConfigureAwait(false);
        if (root is not null)
        {
            return root;
        }

        var startDirectory = Directory.Exists(projectPath)
            ? projectPath
            : Path.GetDirectoryName(projectPath)
                ?? throw new InvalidOperationException($"Unable to determine the directory for '{projectPath}'.");
        return Path.GetFullPath(startDirectory);
    }

    public static async Task<string?> TryFindRepositoryRootAsync(
        string path,
        string gitExecutablePath = "git",
        TimeSpan? commandTimeout = null,
        CancellationToken cancellationToken = default)
    {
        var startDirectory = GetWorkingDirectory(path);
        var result = await TryRunGitAsync(
            startDirectory,
            ["rev-parse", "--show-toplevel"],
            gitExecutablePath,
            commandTimeout,
            cancellationToken).ConfigureAwait(false);
        if (result.Success && !string.IsNullOrWhiteSpace(result.Output))
        {
            return Path.GetFullPath(result.Output.Trim());
        }

        return null;
    }

    public static async Task<string?> TryGetRemoteAsync(
        string repositoryPath,
        string gitExecutablePath = "git",
        TimeSpan? commandTimeout = null,
        CancellationToken cancellationToken = default)
    {
        var result = await TryRunGitAsync(
            repositoryPath,
            ["config", "--get", "remote.origin.url"],
            gitExecutablePath,
            commandTimeout,
            cancellationToken).ConfigureAwait(false);
        return result.Success
            ? result.Output.Trim() is { Length: > 0 } value ? value : null
            : null;
    }

    public static async Task<bool> IsGitRepositoryAsync(
        string repositoryPath,
        string gitExecutablePath = "git",
        TimeSpan? commandTimeout = null,
        bool requireSuccessfulInspection = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(repositoryPath))
        {
            return false;
        }

        var result = await TryRunGitAsync(
            repositoryPath,
            ["rev-parse", "--is-inside-work-tree"],
            gitExecutablePath,
            commandTimeout,
            cancellationToken).ConfigureAwait(false);
        if (result.Success)
        {
            return string.Equals(result.Output.Trim(), "true", StringComparison.OrdinalIgnoreCase);
        }

        if (requireSuccessfulInspection && ContainsGitMetadata(repositoryPath))
        {
            throw CreateInspectionException(repositoryPath, gitExecutablePath);
        }

        return false;
    }

    public static async Task<bool> IsDirtyAsync(
        string repositoryPath,
        string gitExecutablePath = "git",
        TimeSpan? commandTimeout = null,
        bool requireSuccessfulInspection = false,
        CancellationToken cancellationToken = default)
    {
        if (!await IsGitRepositoryAsync(
                repositoryPath,
                gitExecutablePath,
                commandTimeout,
                requireSuccessfulInspection,
                cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var result = await TryRunGitAsync(
            repositoryPath,
            ["status", "--porcelain", "--untracked-files=normal"],
            gitExecutablePath,
            commandTimeout,
            cancellationToken).ConfigureAwait(false);
        if (result.Success)
        {
            return !string.IsNullOrWhiteSpace(result.Output);
        }

        if (requireSuccessfulInspection)
        {
            throw CreateInspectionException(repositoryPath, gitExecutablePath);
        }

        return false;
    }

    public static async Task<string?> TryGetBranchAsync(
        string repositoryPath,
        string gitExecutablePath = "git",
        TimeSpan? commandTimeout = null,
        CancellationToken cancellationToken = default)
    {
        var result = await TryRunGitAsync(
            repositoryPath,
            ["branch", "--show-current"],
            gitExecutablePath,
            commandTimeout,
            cancellationToken).ConfigureAwait(false);
        return result.Success
            ? result.Output.Trim() is { Length: > 0 } value ? value : null
            : null;
    }

    public static async Task<bool> HasUpstreamAsync(
        string repositoryPath,
        string gitExecutablePath = "git",
        TimeSpan? commandTimeout = null,
        CancellationToken cancellationToken = default)
    {
        var result = await TryRunGitAsync(
            repositoryPath,
            ["rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{upstream}"],
            gitExecutablePath,
            commandTimeout,
            cancellationToken).ConfigureAwait(false);
        return result.Success && !string.IsNullOrWhiteSpace(result.Output);
    }

    public static async Task<string?> TryGetCommitAsync(
        string repositoryPath,
        string gitExecutablePath = "git",
        TimeSpan? commandTimeout = null,
        CancellationToken cancellationToken = default)
    {
        var result = await TryRunGitAsync(
            repositoryPath,
            ["rev-parse", "--short=12", "HEAD"],
            gitExecutablePath,
            commandTimeout,
            cancellationToken).ConfigureAwait(false);
        return result.Success
            ? result.Output.Trim() is { Length: > 0 } value ? value : null
            : null;
    }

    public static async Task<string?> TryResolveCommitAsync(
        string repositoryPath,
        string revision = "HEAD",
        string gitExecutablePath = "git",
        TimeSpan? commandTimeout = null,
        CancellationToken cancellationToken = default)
    {
        var result = await TryRunGitAsync(
            repositoryPath,
            ["rev-parse", $"{revision}^{{commit}}"],
            gitExecutablePath,
            commandTimeout,
            cancellationToken).ConfigureAwait(false);
        return result.Success
            ? result.Output.Trim() is { Length: > 0 } value ? value : null
            : null;
    }

    private static string GetWorkingDirectory(string path)
    {
        var candidate = Path.GetFullPath(path);
        while (!Directory.Exists(candidate))
        {
            var parent = Path.GetDirectoryName(candidate);
            if (parent is null || string.Equals(parent, candidate, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Unable to determine an existing directory for '{path}'.");
            }

            candidate = parent;
        }

        return candidate;
    }

    private static async Task<(bool Success, string Output)> TryRunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        string gitExecutablePath,
        TimeSpan? commandTimeout,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = new CancellationTokenSource(commandTimeout ?? TimeSpan.FromSeconds(5));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeout.Token);
            var result = await CliCommand.Wrap(gitExecutablePath)
                .WithArguments(arguments)
                .WithWorkingDirectory(Directory.Exists(workingDirectory)
                    ? workingDirectory
                    : Path.GetDirectoryName(workingDirectory) ?? workingDirectory)
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(linked.Token)
                .ConfigureAwait(false);

            return (result.IsSuccess, result.StandardOutput);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or System.ComponentModel.Win32Exception
                or IOException
                || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return (false, string.Empty);
        }
    }

    private static bool ContainsGitMetadata(string repositoryPath)
    {
        var current = new DirectoryInfo(Path.GetFullPath(repositoryPath));
        while (current is not null)
        {
            var metadataPath = Path.Combine(current.FullName, ".git");
            if (Directory.Exists(metadataPath) || File.Exists(metadataPath))
            {
                return true;
            }

            current = current.Parent;
        }

        return false;
    }

    private static InvalidOperationException CreateInspectionException(
        string repositoryPath,
        string gitExecutablePath)
    {
        return new InvalidOperationException(
            $"Unable to inspect Git repository '{repositoryPath}' with executable '{gitExecutablePath}'. " +
            $"Verify {nameof(ModularAppHostsOptions.GitExecutablePath)} and " +
            $"{nameof(ModularAppHostsOptions.RepositoryCommandTimeout)} before materializing modules.");
    }
}

internal sealed record RepositorySyncCommand(
    string Executable,
    IReadOnlyList<string> Arguments,
    string Operation);

internal sealed record RepositorySyncLifecycleEvent(
    string Operation,
    string State,
    string? Reason = null,
    double ElapsedMilliseconds = 0);

internal static class RepositorySynchronizer
{
    public static async Task<RepositorySyncCommand?> CreateCommandAsync(
        string repositoryPath,
        string? repository,
        bool updateRepository,
        string? revision = null,
        string gitExecutablePath = "git",
        string githubCliPath = "gh",
        TimeSpan? commandTimeout = null,
        CancellationToken cancellationToken = default)
    {
        var commands = await CreateCommandsAsync(
            repositoryPath,
            repository,
            updateRepository,
            revision,
            gitExecutablePath,
            githubCliPath,
            commandTimeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return commands.Count == 0 ? null : commands[0];
    }

    public static async Task<IReadOnlyList<RepositorySyncCommand>> CreateCommandsAsync(
        string repositoryPath,
        string? repository,
        bool updateRepository,
        string? revision = null,
        string gitExecutablePath = "git",
        string githubCliPath = "gh",
        TimeSpan? commandTimeout = null,
        Action<RepositorySyncLifecycleEvent>? lifecycle = null,
        CancellationToken cancellationToken = default)
    {
        if (!await RepositoryInspector.IsGitRepositoryAsync(
                repositoryPath,
                gitExecutablePath,
                commandTimeout,
                requireSuccessfulInspection: true,
                cancellationToken).ConfigureAwait(false))
        {
            if (Directory.Exists(repositoryPath))
            {
                var baseDirectory = Path.GetDirectoryName(repositoryPath) ?? repositoryPath;
                if (!string.IsNullOrWhiteSpace(repository) &&
                    RepositoryIdentity.IsRemoteRepository(repository, baseDirectory))
                {
                    var normalizedRepository = TryNormalizeDiagnosticIdentity(repository, baseDirectory) ??
                        "(unavailable)";
                    throw new InvalidOperationException(
                        $"Repository path '{repositoryPath}' already exists, but it is not a Git checkout of " +
                        $"configured normalized repository identity '{normalizedRepository}'. " +
                        "Move that directory or correct the module configuration.");
                }

                lifecycle?.Invoke(new RepositorySyncLifecycleEvent("update", "skipped", "not-git"));
                return [];
            }

            if (string.IsNullOrWhiteSpace(repository))
            {
                throw new InvalidOperationException(
                    $"Repository '{repositoryPath}' does not exist and the module does not define a Git remote.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(repositoryPath)
                ?? throw new InvalidOperationException($"Unable to determine the parent of '{repositoryPath}'."));

            var commands = new List<RepositorySyncCommand>
            {
                new(
                    gitExecutablePath,
                    GitHubGitAuthentication.ConfigureCredentialHelper(
                        ["clone", "--recurse-submodules", "--", repository, repositoryPath],
                        repository,
                        githubCliPath),
                    "clone")
            };
            AddRevisionCommands(
                commands,
                repositoryPath,
                revision,
                gitExecutablePath,
                githubCliPath,
                repository);
            return commands;
        }

        var actualRepository = await EnsureExpectedOriginAsync(
            repositoryPath,
            repository,
            gitExecutablePath,
            commandTimeout,
            cancellationToken).ConfigureAwait(false);

        if (await RepositoryInspector.IsDirtyAsync(
                repositoryPath,
                gitExecutablePath,
                commandTimeout,
                requireSuccessfulInspection: true,
                cancellationToken).ConfigureAwait(false))
        {
            await EnsureDirtyCheckoutMatchesRevisionAsync(
                repositoryPath,
                revision,
                gitExecutablePath,
                commandTimeout,
                cancellationToken).ConfigureAwait(false);
            lifecycle?.Invoke(new RepositorySyncLifecycleEvent("update", "skipped", "dirty"));
            return [];
        }

        if (!string.IsNullOrWhiteSpace(revision))
        {
            var commands = new List<RepositorySyncCommand>();
            AddRevisionCommands(
                commands,
                repositoryPath,
                revision,
                gitExecutablePath,
                githubCliPath,
                actualRepository ?? repository);
            return commands;
        }

        if (!updateRepository)
        {
            lifecycle?.Invoke(new RepositorySyncLifecycleEvent("update", "skipped", "disabled"));
            return [CreateSubmoduleUpdateCommand(
                repositoryPath,
                gitExecutablePath,
                githubCliPath,
                actualRepository ?? repository)];
        }

        if (!await RepositoryInspector.HasUpstreamAsync(
                repositoryPath,
                gitExecutablePath,
                commandTimeout,
                cancellationToken).ConfigureAwait(false))
        {
            lifecycle?.Invoke(new RepositorySyncLifecycleEvent("update", "skipped", "no-upstream"));
            return [CreateSubmoduleUpdateCommand(
                repositoryPath,
                gitExecutablePath,
                githubCliPath,
                actualRepository ?? repository)];
        }

        return
        [
            new RepositorySyncCommand(
                gitExecutablePath,
                GitHubGitAuthentication.ConfigureCredentialHelper(
                    ["-C", repositoryPath, "pull", "--ff-only", "--recurse-submodules"],
                    actualRepository ?? repository,
                    githubCliPath),
                "fast-forward"),
            CreateSubmoduleUpdateCommand(
                repositoryPath,
                gitExecutablePath,
                githubCliPath,
                actualRepository ?? repository)
        ];
    }

    public static async Task SynchronizeAsync(
        string repositoryPath,
        string? repository,
        bool updateRepository,
        CancellationToken cancellationToken,
        string? revision = null,
        string gitExecutablePath = "git",
        string githubCliPath = "gh",
        TimeSpan? commandTimeout = null,
        Action<string>? progress = null,
        Action<RepositorySyncLifecycleEvent>? lifecycle = null,
        IReportingStep? reportingStep = null)
    {
        var commands = await CreateCommandsAsync(
            repositoryPath,
            repository,
            updateRepository,
            revision,
            gitExecutablePath,
            githubCliPath,
            commandTimeout,
            lifecycle,
            cancellationToken).ConfigureAwait(false);
        progress?.Invoke($"Synchronizing repository '{repositoryPath}'.");
        foreach (var command in commands)
        {
            lifecycle?.Invoke(new RepositorySyncLifecycleEvent(command.Operation, "started"));
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            IReportingTask? reportingTask = null;
            try
            {
                if (reportingStep is not null)
                {
                    reportingTask = await reportingStep.CreateTaskAsync(
                        $"{GetOperationTitle(command.Operation)} {Path.GetFileName(repositoryPath)}",
                        cancellationToken).ConfigureAwait(false);
                }

                var result = await ModuleCliRunner.RunAsync(
                    command.Executable,
                    command.Arguments,
                    Path.GetDirectoryName(repositoryPath)
                        ?? throw new InvalidOperationException($"Unable to determine the parent of '{repositoryPath}'."),
                    commandTimeout ?? TimeSpan.FromMinutes(2),
                    $"prepare {Path.GetFileName(repositoryPath)}",
                    cancellationToken,
                    progress).ConfigureAwait(false);

                if (!result.IsSuccess)
                {
                    var error = string.IsNullOrWhiteSpace(result.StandardError)
                        ? result.StandardOutput
                        : result.StandardError;
                    var diagnostic = CreateCredentialFreeCommandDiagnostic(
                        error.Trim(),
                        repository,
                        Path.GetDirectoryName(repositoryPath) ?? repositoryPath);
                    throw new InvalidOperationException(
                        $"Repository synchronization failed for '{repositoryPath}' with exit code " +
                        $"{result.ExitCode}: {diagnostic}");
                }

                if (reportingTask is not null)
                {
                    await reportingTask.SucceedAsync(
                        $"{GetOperationTitle(command.Operation)} completed",
                        cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                if (reportingTask is not null)
                {
                    await reportingTask.FailAsync(
                        exception.Message,
                        CancellationToken.None).ConfigureAwait(false);
                }

                throw;
            }
            finally
            {
                if (reportingTask is not null)
                {
                    await reportingTask.DisposeAsync().ConfigureAwait(false);
                }
            }

            stopwatch.Stop();
            lifecycle?.Invoke(new RepositorySyncLifecycleEvent(
                command.Operation,
                "completed",
                ElapsedMilliseconds: stopwatch.Elapsed.TotalMilliseconds));
        }

        progress?.Invoke($"Repository '{repositoryPath}' is synchronized.");
    }

    private static string GetOperationTitle(string operation) => operation switch
    {
        "clone" => "Clone",
        "fetch" => "Fetch",
        "checkout" => "Checkout",
        "fast-forward" => "Fast-forward",
        "submodule-update" => "Update submodules",
        _ => operation
    };

    private static void AddRevisionCommands(
        List<RepositorySyncCommand> commands,
        string repositoryPath,
        string? revision,
        string gitExecutablePath,
        string githubCliPath,
        string? repository)
    {
        if (string.IsNullOrWhiteSpace(revision))
        {
            return;
        }

        commands.Add(new RepositorySyncCommand(
            gitExecutablePath,
            GitHubGitAuthentication.ConfigureCredentialHelper(
                ["-C", repositoryPath, "fetch", "--tags", "origin", revision],
                repository,
                githubCliPath),
            "fetch"));
        commands.Add(new RepositorySyncCommand(
            gitExecutablePath,
            ["-C", repositoryPath, "checkout", "--detach", "FETCH_HEAD"],
            "checkout"));
        commands.Add(CreateSubmoduleUpdateCommand(
            repositoryPath,
            gitExecutablePath,
            githubCliPath,
            repository));
    }

    private static RepositorySyncCommand CreateSubmoduleUpdateCommand(
        string repositoryPath,
        string gitExecutablePath,
        string githubCliPath,
        string? repository) =>
        new(
            gitExecutablePath,
            GitHubGitAuthentication.ConfigureCredentialHelper(
                ["-C", repositoryPath, "submodule", "update", "--init", "--recursive"],
                repository,
                githubCliPath),
            "submodule-update");

    private static async Task<string?> EnsureExpectedOriginAsync(
        string repositoryPath,
        string? expectedRepository,
        string gitExecutablePath,
        TimeSpan? commandTimeout,
        CancellationToken cancellationToken)
    {
        var actualRepository = await RepositoryInspector.TryGetRemoteAsync(
            repositoryPath,
            gitExecutablePath,
            commandTimeout,
            cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(expectedRepository))
        {
            return actualRepository;
        }

        var baseDirectory = Path.GetDirectoryName(repositoryPath) ?? repositoryPath;
        if (!RepositoryIdentity.IsRemoteRepository(expectedRepository, baseDirectory) &&
            await LocalRepositoryRootsMatchAsync(
                expectedRepository,
                repositoryPath,
                baseDirectory,
                gitExecutablePath,
                commandTimeout,
                cancellationToken).ConfigureAwait(false))
        {
            return actualRepository;
        }

        var matches = !string.IsNullOrWhiteSpace(actualRepository) &&
            (RepositoryIdentity.RefersToSameRepository(expectedRepository, actualRepository, baseDirectory) ||
             LocalRepositoriesMatch(expectedRepository, actualRepository, baseDirectory));
        if (!matches)
        {
            var expectedIdentity = TryNormalizeDiagnosticIdentity(expectedRepository, baseDirectory) ??
                "(unavailable)";
            var actualIdentity = TryNormalizeDiagnosticIdentity(actualRepository, baseDirectory) ??
                "(missing or unavailable)";
            throw new InvalidOperationException(
                $"Repository '{repositoryPath}' has normalized origin '{actualIdentity}', which does not match " +
                $"configured normalized repository identity '{expectedIdentity}'. " +
                "Move the checkout or correct the module configuration.");
        }

        return actualRepository;
    }

    private static string? TryNormalizeDiagnosticIdentity(string? repository, string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(repository))
        {
            return null;
        }

        try
        {
            return RepositoryIdentity.NormalizeRepositoryIdentity(repository, baseDirectory);
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                          or ArgumentException
                                          or NotSupportedException)
        {
            return null;
        }
    }

    private static string CreateCredentialFreeCommandDiagnostic(
        string output,
        string? repository,
        string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(repository) ||
            !RepositoryIdentity.IsRemoteRepository(repository, baseDirectory))
        {
            return output;
        }

        var normalizedRepository = TryNormalizeDiagnosticIdentity(repository, baseDirectory) ??
            "the configured repository";
        return $"Git could not synchronize normalized repository identity '{normalizedRepository}'. " +
            "Verify repository access and configured credentials.";
    }

    private static bool LocalRepositoriesMatch(string first, string second, string baseDirectory)
    {
        if (RepositoryIdentity.IsRemoteRepository(first, baseDirectory) ||
            RepositoryIdentity.IsRemoteRepository(second, baseDirectory))
        {
            return false;
        }

        return PathSafety.AreEqual(
            Path.GetFullPath(first, baseDirectory),
            Path.GetFullPath(second, baseDirectory));
    }

    private static async Task<bool> LocalRepositoryRootsMatchAsync(
        string expectedRepository,
        string repositoryPath,
        string baseDirectory,
        string gitExecutablePath,
        TimeSpan? commandTimeout,
        CancellationToken cancellationToken)
    {
        var expectedPath = Path.GetFullPath(expectedRepository, baseDirectory);
        var expectedRoot = await RepositoryInspector.TryFindRepositoryRootAsync(
            expectedPath,
            gitExecutablePath,
            commandTimeout,
            cancellationToken).ConfigureAwait(false);
        var actualRoot = await RepositoryInspector.TryFindRepositoryRootAsync(
            repositoryPath,
            gitExecutablePath,
            commandTimeout,
            cancellationToken).ConfigureAwait(false);
        return expectedRoot is not null && actualRoot is not null &&
            PathSafety.AreEqual(expectedRoot, actualRoot);
    }

    private static async Task EnsureDirtyCheckoutMatchesRevisionAsync(
        string repositoryPath,
        string? revision,
        string gitExecutablePath,
        TimeSpan? commandTimeout,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(revision))
        {
            return;
        }

        var currentCommit = await RepositoryInspector.TryResolveCommitAsync(
            repositoryPath,
            gitExecutablePath: gitExecutablePath,
            commandTimeout: commandTimeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var expectedCommit = await RepositoryInspector.TryResolveCommitAsync(
            repositoryPath,
            revision,
            gitExecutablePath,
            commandTimeout,
            cancellationToken).ConfigureAwait(false);
        if (currentCommit is null || expectedCommit is null ||
            !string.Equals(currentCommit, expectedCommit, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Repository '{repositoryPath}' has local changes and is not at configured revision '{revision}'. " +
                "Commit or stash the changes before switching revisions.");
        }
    }
}
