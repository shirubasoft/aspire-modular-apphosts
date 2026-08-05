using CliWrap;
using CliWrap.Buffered;
using CliCommand = global::CliWrap.Cli;

namespace Aspire.Hosting.ModularAppHosts;

internal static class RepositoryInspector
{
    public static string FindRepositoryRoot(string projectPath)
    {
        if (TryFindRepositoryRoot(projectPath, out var root))
        {
            return root;
        }

        var startDirectory = Directory.Exists(projectPath)
            ? projectPath
            : Path.GetDirectoryName(projectPath)
                ?? throw new InvalidOperationException($"Unable to determine the directory for '{projectPath}'.");
        return Path.GetFullPath(startDirectory);
    }

    public static bool TryFindRepositoryRoot(string path, out string repositoryRoot)
    {
        var startDirectory = GetWorkingDirectory(path);
        if (TryRunGit(startDirectory, ["rev-parse", "--show-toplevel"], out var root) &&
            !string.IsNullOrWhiteSpace(root))
        {
            repositoryRoot = Path.GetFullPath(root.Trim());
            return true;
        }

        repositoryRoot = string.Empty;
        return false;
    }

    public static string? TryGetRemote(string repositoryPath)
    {
        return TryRunGit(repositoryPath, ["config", "--get", "remote.origin.url"], out var remote)
            ? remote.Trim() is { Length: > 0 } value ? value : null
            : null;
    }

    public static bool IsGitRepository(string repositoryPath)
    {
        return Directory.Exists(repositoryPath) &&
               TryRunGit(repositoryPath, ["rev-parse", "--is-inside-work-tree"], out var result) &&
               string.Equals(result.Trim(), "true", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsDirty(string repositoryPath)
    {
        return IsGitRepository(repositoryPath) &&
               TryRunGit(repositoryPath, ["status", "--porcelain", "--untracked-files=normal"], out var status) &&
               !string.IsNullOrWhiteSpace(status);
    }

    public static string? TryGetBranch(string repositoryPath)
    {
        return TryRunGit(repositoryPath, ["branch", "--show-current"], out var branch)
            ? branch.Trim() is { Length: > 0 } value ? value : null
            : null;
    }

    public static string? TryGetCommit(string repositoryPath)
    {
        return TryRunGit(repositoryPath, ["rev-parse", "--short=12", "HEAD"], out var commit)
            ? commit.Trim() is { Length: > 0 } value ? value : null
            : null;
    }

    public static string? TryResolveCommit(string repositoryPath, string revision = "HEAD")
    {
        return TryRunGit(repositoryPath, ["rev-parse", $"{revision}^{{commit}}"], out var commit)
            ? commit.Trim() is { Length: > 0 } value ? value : null
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

    private static bool TryRunGit(string workingDirectory, IReadOnlyList<string> arguments, out string output)
    {
        output = string.Empty;

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var result = CliCommand.Wrap("git")
                .WithArguments(arguments)
                .WithWorkingDirectory(Directory.Exists(workingDirectory)
                    ? workingDirectory
                    : Path.GetDirectoryName(workingDirectory) ?? workingDirectory)
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(timeout.Token)
                .GetAwaiter()
                .GetResult();

            output = result.StandardOutput;
            return result.IsSuccess;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or System.ComponentModel.Win32Exception
                or IOException
                or OperationCanceledException)
        {
            return false;
        }
    }
}

internal sealed record RepositoryCloneCommand(
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory);

internal static class GitHubRepositoryCloner
{
    public static bool RefersToSameRepository(string first, string second, string baseDirectory)
    {
        var firstIdentity = GetRemoteIdentity(first, baseDirectory);
        var secondIdentity = GetRemoteIdentity(second, baseDirectory);
        return firstIdentity is not null && secondIdentity is not null &&
            string.Equals(firstIdentity, secondIdentity, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsRemoteRepository(string repository, string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        if (Uri.TryCreate(repository, UriKind.Absolute, out var uri))
        {
            return !uri.IsFile;
        }

        if (Path.IsPathRooted(repository) || repository.StartsWith('.') ||
            Directory.Exists(Path.GetFullPath(repository, baseDirectory)))
        {
            return false;
        }

        return repository.Contains('/', StringComparison.Ordinal) ||
            repository.Contains(':', StringComparison.Ordinal);
    }

    public static RepositoryCloneCommand CreateCommand(
        string executable,
        string repository,
        string repositoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);

        var workingDirectory = Path.GetDirectoryName(repositoryPath)
            ?? throw new InvalidOperationException($"Unable to determine the parent of '{repositoryPath}'.");
        return new RepositoryCloneCommand(
            executable,
            ["repo", "clone", repository, repositoryPath, "--", "--recurse-submodules"],
            workingDirectory);
    }

    public static void Clone(
        string executable,
        string repository,
        string repositoryPath,
        TimeSpan? commandTimeout = null)
    {
        var command = CreateCommand(executable, repository, repositoryPath);
        Directory.CreateDirectory(command.WorkingDirectory);

        ModuleCliResult result;
        try
        {
            result = ModuleCliRunner.RunAsync(
                    command.Executable,
                    command.Arguments,
                    command.WorkingDirectory,
                    commandTimeout ?? TimeSpan.FromMinutes(2),
                    $"clone {repository}",
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
        catch (TimeoutException exception)
        {
            throw new InvalidOperationException(
                $"Automatic module cloning of '{repository}' timed out while using '{executable}'.",
                exception);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or System.ComponentModel.Win32Exception
                or IOException)
        {
            throw new InvalidOperationException(
                $"Automatic module cloning requires the GitHub CLI executable '{executable}'. " +
                $"Install GitHub CLI or disable {nameof(ModularAppHostsOptions.AutoCloneRepositories)}.",
                exception);
        }

        if (!result.IsSuccess)
        {
            var error = string.IsNullOrWhiteSpace(result.StandardError)
                ? result.StandardOutput
                : result.StandardError;
            throw new InvalidOperationException(
                $"GitHub CLI failed to clone module repository '{repository}' to '{repositoryPath}' " +
                $"with exit code {result.ExitCode}: {error.Trim()}");
        }

        if (!RepositoryInspector.IsGitRepository(repositoryPath))
        {
            throw new InvalidOperationException(
                $"GitHub CLI reported success, but '{repositoryPath}' is not a Git repository.");
        }
    }

    public static string GetRepositoryDirectoryName(string repository)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        var value = repository.Trim().TrimEnd('/');
        var separator = value.LastIndexOfAny(['/', ':']);
        var name = separator >= 0 ? value[(separator + 1)..] : value;
        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            name.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Unable to infer a sibling directory name from module repository '{repository}'.");
        }

        return name;
    }

    private static string? GetRemoteIdentity(string repository, string baseDirectory)
    {
        if (!IsRemoteRepository(repository, baseDirectory))
        {
            return null;
        }

        var value = repository.Trim().TrimEnd('/');
        string identity;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            identity = $"{uri.Host}/{uri.AbsolutePath.Trim('/')}";
        }
        else if (value.IndexOf(':', StringComparison.Ordinal) is var colon && colon >= 0)
        {
            var hostStart = value.LastIndexOf('@', colon);
            var host = value[(hostStart >= 0 ? hostStart + 1 : 0)..colon];
            identity = $"{host}/{value[(colon + 1)..].Trim('/')}";
        }
        else
        {
            identity = $"github.com/{value.Trim('/')}";
        }

        return identity.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? identity[..^4]
            : identity;
    }
}

internal sealed record ModuleRepositoryResolution(
    string RepositoryPath,
    bool UsesSiblingLayout);

internal static class ModuleRepositoryDiscovery
{
    public static ModuleRepositoryResolution Resolve(
        string appHostDirectory,
        DistributedApplicationModule module,
        string? repository,
        string githubCliPath,
        TimeSpan? commandTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appHostDirectory);
        ArgumentNullException.ThrowIfNull(module);

        if (!RepositoryInspector.TryFindRepositoryRoot(appHostDirectory, out var appHostRepositoryRoot))
        {
            throw new InvalidOperationException(
                $"Automatic module discovery requires AppHost directory '{appHostDirectory}' to be inside a Git repository.");
        }

        var projectRepositoryRoot = module.ProjectDefinitions.Count == 0
            ? null
            : module.ProjectDefinitions[0].SourceRepositoryRoot;
        if (PathSafety.AreEqual(projectRepositoryRoot, appHostRepositoryRoot))
        {
            return new ModuleRepositoryResolution(appHostRepositoryRoot, UsesSiblingLayout: false);
        }

        if (TryGetSameRepositoryLocalPath(
            repository,
            appHostDirectory,
            appHostRepositoryRoot,
            out var sameRepositoryPath))
        {
            return new ModuleRepositoryResolution(sameRepositoryPath, UsesSiblingLayout: false);
        }

        var appHostRemote = RepositoryInspector.TryGetRemote(appHostRepositoryRoot);
        if (!string.IsNullOrWhiteSpace(repository) && !string.IsNullOrWhiteSpace(appHostRemote) &&
            GitHubRepositoryCloner.RefersToSameRepository(repository, appHostRemote, appHostDirectory))
        {
            return new ModuleRepositoryResolution(appHostRepositoryRoot, UsesSiblingLayout: false);
        }

        var siblingParent = Path.GetDirectoryName(appHostRepositoryRoot)
            ?? throw new InvalidOperationException(
                $"Unable to determine the parent of AppHost repository '{appHostRepositoryRoot}'.");
        var siblingPath = GetSiblingPath(
            appHostDirectory,
            siblingParent,
            projectRepositoryRoot,
            repository);

        EnsureSiblingPath(appHostRepositoryRoot, siblingParent, siblingPath, module.Name);

        if (Directory.Exists(siblingPath))
        {
            if (!RepositoryInspector.IsGitRepository(siblingPath))
            {
                throw new InvalidOperationException(
                    $"Discovered module '{module.Name}' at '{siblingPath}', but that directory is not a Git repository.");
            }

            EnsureExpectedOrigin(siblingPath, repository, appHostDirectory, module.Name);

            return new ModuleRepositoryResolution(siblingPath, UsesSiblingLayout: true);
        }

        if (string.IsNullOrWhiteSpace(repository) || IsLocalRepository(repository, appHostDirectory))
        {
            throw new InvalidOperationException(
                $"Module '{module.Name}' was not found at sibling path '{siblingPath}'. " +
                $"Automatic cloning requires a GitHub repository configured through " +
                $"{DistributedApplicationModuleExtensions.GetRepositoryConfigurationKey(module.Name)} or WithRepository().");
        }

        GitHubRepositoryCloner.Clone(githubCliPath, repository, siblingPath, commandTimeout);
        EnsureExpectedOrigin(siblingPath, repository, appHostDirectory, module.Name);
        return new ModuleRepositoryResolution(siblingPath, UsesSiblingLayout: true);
    }

    private static string GetSiblingPath(
        string appHostDirectory,
        string siblingParent,
        string? projectRepositoryRoot,
        string? repository)
    {
        if (!string.IsNullOrWhiteSpace(projectRepositoryRoot) &&
            PathSafety.AreEqual(Path.GetDirectoryName(projectRepositoryRoot), siblingParent))
        {
            return Path.GetFullPath(projectRepositoryRoot);
        }

        if (!string.IsNullOrWhiteSpace(repository) && IsLocalRepository(repository, appHostDirectory))
        {
            return Path.GetFullPath(repository, appHostDirectory);
        }

        if (string.IsNullOrWhiteSpace(repository))
        {
            return projectRepositoryRoot is null
                ? Path.Combine(siblingParent, "module")
                : Path.GetFullPath(projectRepositoryRoot);
        }

        return Path.Combine(siblingParent, GitHubRepositoryCloner.GetRepositoryDirectoryName(repository));
    }

    private static void EnsureSiblingPath(
        string appHostRepositoryRoot,
        string siblingParent,
        string siblingPath,
        string moduleName)
    {
        var actualParent = Path.GetDirectoryName(Path.GetFullPath(siblingPath));
        if (PathSafety.AreEqual(siblingPath, appHostRepositoryRoot) || !PathSafety.AreEqual(actualParent, siblingParent))
        {
            throw new InvalidOperationException(
                $"Module '{moduleName}' resolved to '{siblingPath}'. Automatic discovery only accepts the AppHost " +
                $"Git repository '{appHostRepositoryRoot}' or one direct sibling under '{siblingParent}'.");
        }
    }

    private static bool TryGetSameRepositoryLocalPath(
        string? repository,
        string appHostDirectory,
        string expectedRepositoryRoot,
        out string localPath)
    {
        localPath = string.Empty;
        if (string.IsNullOrWhiteSpace(repository) || !IsLocalRepository(repository, appHostDirectory))
        {
            return false;
        }

        localPath = Path.GetFullPath(repository, appHostDirectory);
        return RepositoryInspector.TryFindRepositoryRoot(localPath, out var repositoryRoot) &&
            PathSafety.AreEqual(repositoryRoot, expectedRepositoryRoot);
    }

    private static bool IsLocalRepository(string repository, string appHostDirectory)
    {
        return !GitHubRepositoryCloner.IsRemoteRepository(repository, appHostDirectory);
    }

    private static void EnsureExpectedOrigin(
        string repositoryPath,
        string? expectedRepository,
        string baseDirectory,
        string moduleName)
    {
        if (string.IsNullOrWhiteSpace(expectedRepository) ||
            !GitHubRepositoryCloner.IsRemoteRepository(expectedRepository, baseDirectory))
        {
            return;
        }

        var actualRepository = RepositoryInspector.TryGetRemote(repositoryPath);
        if (string.IsNullOrWhiteSpace(actualRepository) ||
            !GitHubRepositoryCloner.RefersToSameRepository(expectedRepository, actualRepository, baseDirectory))
        {
            throw new InvalidOperationException(
                $"Module '{moduleName}' resolved to '{repositoryPath}', but its origin '{actualRepository ?? "(missing)"}' " +
                $"does not match configured repository '{expectedRepository}'.");
        }
    }
}

internal sealed record RepositorySyncCommand(string Executable, IReadOnlyList<string> Arguments);

internal static class RepositorySynchronizer
{
    public static RepositorySyncCommand? CreateCommand(
        string repositoryPath,
        string? repository,
        bool updateRepository,
        string? revision = null,
        string gitExecutablePath = "git")
    {
        var commands = CreateCommands(repositoryPath, repository, updateRepository, revision, gitExecutablePath);
        return commands.Count == 0 ? null : commands[0];
    }

    public static IReadOnlyList<RepositorySyncCommand> CreateCommands(
        string repositoryPath,
        string? repository,
        bool updateRepository,
        string? revision = null,
        string gitExecutablePath = "git")
    {
        if (!RepositoryInspector.IsGitRepository(repositoryPath))
        {
            if (Directory.Exists(repositoryPath))
            {
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
                new(gitExecutablePath, ["clone", "--recurse-submodules", "--", repository, repositoryPath])
            };
            AddRevisionCommands(commands, repositoryPath, revision, gitExecutablePath);
            return commands;
        }

        EnsureExpectedOrigin(repositoryPath, repository);

        if (RepositoryInspector.IsDirty(repositoryPath))
        {
            EnsureDirtyCheckoutMatchesRevision(repositoryPath, revision);
            return [];
        }

        if (!string.IsNullOrWhiteSpace(revision))
        {
            var commands = new List<RepositorySyncCommand>();
            AddRevisionCommands(commands, repositoryPath, revision, gitExecutablePath);
            return commands;
        }

        return updateRepository
            ? [new RepositorySyncCommand(
                gitExecutablePath,
                ["-C", repositoryPath, "pull", "--ff-only", "--recurse-submodules"])]
            : [];
    }

    public static async Task SynchronizeAsync(
        string repositoryPath,
        string? repository,
        bool updateRepository,
        CancellationToken cancellationToken,
        string? revision = null,
        string gitExecutablePath = "git",
        TimeSpan? commandTimeout = null,
        Action<string>? progress = null)
    {
        var commands = CreateCommands(
            repositoryPath,
            repository,
            updateRepository,
            revision,
            gitExecutablePath);
        foreach (var command in commands)
        {
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
                throw new InvalidOperationException(
                    $"Repository synchronization failed for '{repositoryPath}' with exit code {result.ExitCode}: {error.Trim()}");
            }
        }
    }

    private static void AddRevisionCommands(
        List<RepositorySyncCommand> commands,
        string repositoryPath,
        string? revision,
        string gitExecutablePath)
    {
        if (string.IsNullOrWhiteSpace(revision))
        {
            return;
        }

        commands.Add(new RepositorySyncCommand(
            gitExecutablePath,
            ["-C", repositoryPath, "fetch", "--tags", "origin", revision]));
        commands.Add(new RepositorySyncCommand(
            gitExecutablePath,
            ["-C", repositoryPath, "checkout", "--detach", "FETCH_HEAD"]));
        commands.Add(new RepositorySyncCommand(
            gitExecutablePath,
            ["-C", repositoryPath, "submodule", "update", "--init", "--recursive"]));
    }

    private static void EnsureExpectedOrigin(string repositoryPath, string? expectedRepository)
    {
        if (string.IsNullOrWhiteSpace(expectedRepository))
        {
            return;
        }

        var actualRepository = RepositoryInspector.TryGetRemote(repositoryPath);
        var baseDirectory = Path.GetDirectoryName(repositoryPath) ?? repositoryPath;
        var matches = !string.IsNullOrWhiteSpace(actualRepository) &&
            (GitHubRepositoryCloner.RefersToSameRepository(expectedRepository, actualRepository, baseDirectory) ||
             LocalRepositoriesMatch(expectedRepository, actualRepository, baseDirectory));
        if (!matches)
        {
            throw new InvalidOperationException(
                $"Repository '{repositoryPath}' has origin '{actualRepository ?? "(missing)"}', which does not match " +
                $"configured repository '{expectedRepository}'. Move the checkout or correct the module configuration.");
        }
    }

    private static bool LocalRepositoriesMatch(string first, string second, string baseDirectory)
    {
        if (GitHubRepositoryCloner.IsRemoteRepository(first, baseDirectory) ||
            GitHubRepositoryCloner.IsRemoteRepository(second, baseDirectory))
        {
            return false;
        }

        return PathSafety.AreEqual(
            Path.GetFullPath(first, baseDirectory),
            Path.GetFullPath(second, baseDirectory));
    }

    private static void EnsureDirtyCheckoutMatchesRevision(string repositoryPath, string? revision)
    {
        if (string.IsNullOrWhiteSpace(revision))
        {
            return;
        }

        var currentCommit = RepositoryInspector.TryResolveCommit(repositoryPath);
        var expectedCommit = RepositoryInspector.TryResolveCommit(repositoryPath, revision);
        if (currentCommit is null || expectedCommit is null ||
            !string.Equals(currentCommit, expectedCommit, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Repository '{repositoryPath}' has local changes and is not at configured revision '{revision}'. " +
                "Commit or stash the changes before switching revisions.");
        }
    }
}
