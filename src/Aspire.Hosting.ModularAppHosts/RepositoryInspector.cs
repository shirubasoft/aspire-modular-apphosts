using CliWrap;
using CliWrap.Buffered;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CliCommand = global::CliWrap.Cli;

namespace Aspire.Hosting.ModularAppHosts;

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

    /// <summary>
    /// Gets whether the checked out branch tracks a remote branch. A branch that does not, such as a local feature
    /// branch that has never been pushed, has nothing to fast-forward from.
    /// </summary>
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
        return result.Success && result.Output.Trim().Length > 0;
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

internal sealed record RepositoryCloneCommand(
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory);

internal static class ModuleRepositoryIdentity
{
    private const int MaximumCanonicalNameLength = 46;

    public static string GetCanonicalName(string? repository, string moduleName, string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        var repositorySlug = string.IsNullOrWhiteSpace(repository)
            ? "repository"
            : GetRepositorySlug(repository, baseDirectory);
        var moduleSlug = CreateSlug(moduleName, disambiguateChanges: true);
        var canonicalName = $"{repositorySlug}-{moduleSlug}";
        if (canonicalName.Length <= MaximumCanonicalNameLength)
        {
            return canonicalName;
        }

        var suffix = GetStableSuffix($"{repository}\n{moduleName}");
        return $"{canonicalName[..(MaximumCanonicalNameLength - suffix.Length - 1)].TrimEnd('-')}-{suffix}";
    }

    private static string GetRepositorySlug(string repository, string baseDirectory)
    {
        var value = repository.Trim().TrimEnd('/', '\\');
        if (!GitHubRepositoryCloner.IsRemoteRepository(value, baseDirectory))
        {
            var fullPath = Path.GetFullPath(value, baseDirectory);
            return CreateSlug(Path.GetFileName(fullPath), disambiguateChanges: true);
        }

        string repositoryPath;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            repositoryPath = uri.AbsolutePath;
        }
        else if (value.IndexOf(':', StringComparison.Ordinal) is var colon && colon >= 0)
        {
            repositoryPath = value[(colon + 1)..];
        }
        else
        {
            repositoryPath = value;
        }

        var components = repositoryPath
            .Replace('\\', '/')
            .Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (components.Length == 0)
        {
            return "repository";
        }

        var repositoryName = components[^1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? components[^1][..^4]
            : components[^1];
        var owner = components.Length > 1 ? components[^2] : null;
        return owner is null
            ? CreateSlug(repositoryName, disambiguateChanges: true)
            : $"{CreateSlug(owner, disambiguateChanges: true)}-{CreateSlug(repositoryName, disambiguateChanges: true)}";
    }

    private static string CreateSlug(string value, bool disambiguateChanges)
    {
        var normalized = value.Trim();
        var changed = false;
        var builder = new StringBuilder(normalized.Length);
        foreach (var originalCharacter in normalized)
        {
            var character = originalCharacter is >= 'A' and <= 'Z'
                ? (char)(originalCharacter + ('a' - 'A'))
                : originalCharacter;
            var safeCharacter = character is >= 'a' and <= 'z' or >= '0' and <= '9';
            if (safeCharacter)
            {
                builder.Append(character);
            }
            else if (character == '-')
            {
                builder.Append(character);
            }
            else
            {
                builder.Append('-');
                changed = true;
            }
        }

        var slug = builder.ToString();
        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
            changed = true;
        }

        slug = slug.Trim('-');
        if (slug.Length == 0)
        {
            slug = "module";
            changed = true;
        }

        if (disambiguateChanges && changed)
        {
            slug = $"{slug}-{GetStableSuffix(value)}";
        }

        const int maximumComponentLength = 22;
        if (slug.Length > maximumComponentLength)
        {
            var suffix = GetStableSuffix(value);
            slug = $"{slug[..(maximumComponentLength - suffix.Length - 1)].TrimEnd('-')}-{suffix}";
        }

        return slug;
    }

    private static string GetStableSuffix(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return string.Concat(hash.Take(3).Select(value => value.ToString("x2", CultureInfo.InvariantCulture)));
    }
}

internal static class GitHubRepositoryCloner
{
    public static bool RefersToSameRepository(string first, string second, string baseDirectory)
    {
        var firstIdentity = GetRemoteIdentity(first, baseDirectory);
        var secondIdentity = GetRemoteIdentity(second, baseDirectory);
        return firstIdentity is not null && secondIdentity is not null &&
            string.Equals(firstIdentity.Host, secondIdentity.Host, StringComparison.OrdinalIgnoreCase) &&
            firstIdentity.Port == secondIdentity.Port &&
            string.Equals(
                firstIdentity.Path,
                secondIdentity.Path,
                IsCaseInsensitiveRepositoryHost(firstIdentity.Host)
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal);
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

    public static async Task CloneAsync(
        string executable,
        string repository,
        string repositoryPath,
        TimeSpan? commandTimeout = null,
        string gitExecutablePath = "git",
        CancellationToken cancellationToken = default)
    {
        var command = CreateCommand(executable, repository, repositoryPath);
        Directory.CreateDirectory(command.WorkingDirectory);

        ModuleCliResult result;
        try
        {
            result = await ModuleCliRunner.RunAsync(
                command.Executable,
                command.Arguments,
                command.WorkingDirectory,
                commandTimeout ?? TimeSpan.FromMinutes(2),
                $"clone {repository}",
                cancellationToken).ConfigureAwait(false);
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

        if (!await RepositoryInspector.IsGitRepositoryAsync(
                repositoryPath,
                gitExecutablePath,
                commandTimeout,
                requireSuccessfulInspection: true,
                cancellationToken).ConfigureAwait(false))
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

    private static RepositoryRemoteIdentity? GetRemoteIdentity(string repository, string baseDirectory)
    {
        if (!IsRemoteRepository(repository, baseDirectory))
        {
            return null;
        }

        var value = repository.Trim().TrimEnd('/');
        string host;
        int? port = null;
        string path;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            host = uri.IdnHost;
            port = IsDefaultRepositoryPort(uri.Scheme, uri.Port) ? null : uri.Port;
            path = uri.GetComponents(UriComponents.Path, UriFormat.Unescaped).Trim('/');
        }
        else if (value.IndexOf(':', StringComparison.Ordinal) is var colon && colon >= 0)
        {
            var hostStart = value.LastIndexOf('@', colon);
            host = value[(hostStart >= 0 ? hostStart + 1 : 0)..colon];
            path = value[(colon + 1)..].Trim('/');
        }
        else
        {
            host = "github.com";
            path = value.Trim('/');
        }

        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^4];
        }

        return new RepositoryRemoteIdentity(host, port, path);
    }

    private static bool IsCaseInsensitiveRepositoryHost(string host) =>
        string.Equals(host, "github.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsDefaultRepositoryPort(string scheme, int port) =>
        port < 0 ||
        (string.Equals(scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && port == 80) ||
        (string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) && port == 443) ||
        (string.Equals(scheme, "ssh", StringComparison.OrdinalIgnoreCase) && port == 22) ||
        (string.Equals(scheme, "git", StringComparison.OrdinalIgnoreCase) && port == 9418);

    private sealed record RepositoryRemoteIdentity(string Host, int? Port, string Path);
}

internal sealed record ModuleRepositoryResolution(
    string RepositoryPath,
    bool UsesSiblingLayout);

internal static class ModuleRepositoryDiscovery
{
    public static async Task<ModuleRepositoryResolution> ResolveAsync(
        string appHostDirectory,
        DistributedApplicationModule module,
        string? repository,
        string githubCliPath,
        TimeSpan? commandTimeout = null,
        string gitExecutablePath = "git",
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appHostDirectory);
        ArgumentNullException.ThrowIfNull(module);

        var projectRepositoryRoot = module.ProjectDefinitions.Count == 0
            ? null
            : module.ProjectDefinitions[0].SourceRepositoryRoot;
        return await ResolveAsync(
            appHostDirectory,
            module.Name,
            projectRepositoryRoot,
            repository,
            githubCliPath,
            commandTimeout,
            gitExecutablePath,
            cancellationToken).ConfigureAwait(false);
    }

    public static async Task<ModuleRepositoryResolution> ResolveAsync(
        string appHostDirectory,
        string subjectName,
        string? sourceRepositoryRoot,
        string? repository,
        string githubCliPath,
        TimeSpan? commandTimeout = null,
        string gitExecutablePath = "git",
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appHostDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectName);

        var appHostRepositoryRoot = await RepositoryInspector.TryFindRepositoryRootAsync(
            appHostDirectory,
            gitExecutablePath,
            commandTimeout,
            cancellationToken).ConfigureAwait(false);
        if (appHostRepositoryRoot is null)
        {
            throw new InvalidOperationException(
                $"Automatic module discovery requires AppHost directory '{appHostDirectory}' to be inside a Git repository.");
        }

        if (PathSafety.AreEqual(sourceRepositoryRoot, appHostRepositoryRoot))
        {
            return new ModuleRepositoryResolution(appHostRepositoryRoot, UsesSiblingLayout: false);
        }

        var sameRepositoryPath = await TryGetSameRepositoryLocalPathAsync(
            repository,
            appHostDirectory,
            appHostRepositoryRoot,
            gitExecutablePath,
            commandTimeout,
            cancellationToken).ConfigureAwait(false);
        if (sameRepositoryPath is not null)
        {
            return new ModuleRepositoryResolution(sameRepositoryPath, UsesSiblingLayout: false);
        }

        var appHostRemote = await RepositoryInspector.TryGetRemoteAsync(
            appHostRepositoryRoot,
            gitExecutablePath,
            commandTimeout,
            cancellationToken).ConfigureAwait(false);
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
            sourceRepositoryRoot,
            repository);

        EnsureSiblingPath(appHostRepositoryRoot, siblingParent, siblingPath, subjectName);

        if (Directory.Exists(siblingPath))
        {
            if (!await RepositoryInspector.IsGitRepositoryAsync(
                    siblingPath,
                    gitExecutablePath,
                    commandTimeout,
                    requireSuccessfulInspection: true,
                    cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    $"Discovered repository for '{subjectName}' at '{siblingPath}', but that directory is not a Git repository.");
            }

            await EnsureExpectedOriginAsync(
                siblingPath,
                repository,
                appHostDirectory,
                subjectName,
                gitExecutablePath,
                commandTimeout,
                cancellationToken).ConfigureAwait(false);

            return new ModuleRepositoryResolution(siblingPath, UsesSiblingLayout: true);
        }

        if (string.IsNullOrWhiteSpace(repository) || IsLocalRepository(repository, appHostDirectory))
        {
            throw new InvalidOperationException(
                $"Repository for '{subjectName}' was not found at sibling path '{siblingPath}'. " +
                $"Automatic cloning requires a GitHub repository configured through " +
                "the module definition or AppHost configuration.");
        }

        await GitHubRepositoryCloner.CloneAsync(
            githubCliPath,
            repository,
            siblingPath,
            commandTimeout,
            gitExecutablePath,
            cancellationToken).ConfigureAwait(false);
        await EnsureExpectedOriginAsync(
            siblingPath,
            repository,
            appHostDirectory,
            subjectName,
            gitExecutablePath,
            commandTimeout,
            cancellationToken).ConfigureAwait(false);
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

    private static async Task<string?> TryGetSameRepositoryLocalPathAsync(
        string? repository,
        string appHostDirectory,
        string expectedRepositoryRoot,
        string gitExecutablePath,
        TimeSpan? commandTimeout,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(repository) || !IsLocalRepository(repository, appHostDirectory))
        {
            return null;
        }

        var localPath = Path.GetFullPath(repository, appHostDirectory);
        var repositoryRoot = await RepositoryInspector.TryFindRepositoryRootAsync(
            localPath,
            gitExecutablePath,
            commandTimeout,
            cancellationToken).ConfigureAwait(false);
        return repositoryRoot is not null && PathSafety.AreEqual(repositoryRoot, expectedRepositoryRoot)
            ? localPath
            : null;
    }

    private static bool IsLocalRepository(string repository, string appHostDirectory)
    {
        return !GitHubRepositoryCloner.IsRemoteRepository(repository, appHostDirectory);
    }

    private static async Task EnsureExpectedOriginAsync(
        string repositoryPath,
        string? expectedRepository,
        string baseDirectory,
        string moduleName,
        string gitExecutablePath,
        TimeSpan? commandTimeout,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(expectedRepository) ||
            !GitHubRepositoryCloner.IsRemoteRepository(expectedRepository, baseDirectory))
        {
            return;
        }

        var actualRepository = await RepositoryInspector.TryGetRemoteAsync(
            repositoryPath,
            gitExecutablePath,
            commandTimeout,
            cancellationToken).ConfigureAwait(false);
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
    public static async Task<RepositorySyncCommand?> CreateCommandAsync(
        string repositoryPath,
        string? repository,
        bool updateRepository,
        string? revision = null,
        string gitExecutablePath = "git",
        TimeSpan? commandTimeout = null,
        CancellationToken cancellationToken = default)
    {
        var commands = await CreateCommandsAsync(
            repositoryPath,
            repository,
            updateRepository,
            revision,
            gitExecutablePath,
            commandTimeout,
            cancellationToken).ConfigureAwait(false);
        return commands.Count == 0 ? null : commands[0];
    }

    public static async Task<IReadOnlyList<RepositorySyncCommand>> CreateCommandsAsync(
        string repositoryPath,
        string? repository,
        bool updateRepository,
        string? revision = null,
        string gitExecutablePath = "git",
        TimeSpan? commandTimeout = null,
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
                    GitHubRepositoryCloner.IsRemoteRepository(repository, baseDirectory))
                {
                    throw new InvalidOperationException(
                        $"Repository path '{repositoryPath}' already exists, but it is not a Git checkout of " +
                        $"configured repository '{repository}'. Move that directory or correct the module configuration.");
                }

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

        await EnsureExpectedOriginAsync(
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
            return [];
        }

        if (!string.IsNullOrWhiteSpace(revision))
        {
            var commands = new List<RepositorySyncCommand>();
            AddRevisionCommands(commands, repositoryPath, revision, gitExecutablePath);
            return commands;
        }

        // A branch with no upstream — a local feature branch that has never been pushed, which is the normal state of
        // a checkout a developer is working in — has nothing to fast-forward from, and `git pull` fails outright.
        // Leave it alone for the same reason a dirty checkout is left alone.
        if (!updateRepository ||
            !await RepositoryInspector.HasUpstreamAsync(
                repositoryPath,
                gitExecutablePath,
                commandTimeout,
                cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        return [new RepositorySyncCommand(
            gitExecutablePath,
            ["-C", repositoryPath, "pull", "--ff-only", "--recurse-submodules"])];
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
        var commands = await CreateCommandsAsync(
            repositoryPath,
            repository,
            updateRepository,
            revision,
            gitExecutablePath,
            commandTimeout,
            cancellationToken).ConfigureAwait(false);
        progress?.Invoke($"Synchronizing repository '{repositoryPath}'.");
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

        progress?.Invoke($"Repository '{repositoryPath}' is synchronized.");
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

    private static async Task EnsureExpectedOriginAsync(
        string repositoryPath,
        string? expectedRepository,
        string gitExecutablePath,
        TimeSpan? commandTimeout,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(expectedRepository))
        {
            return;
        }

        var baseDirectory = Path.GetDirectoryName(repositoryPath) ?? repositoryPath;
        if (!GitHubRepositoryCloner.IsRemoteRepository(expectedRepository, baseDirectory) &&
            await LocalRepositoryRootsMatchAsync(
                expectedRepository,
                repositoryPath,
                baseDirectory,
                gitExecutablePath,
                commandTimeout,
                cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var actualRepository = await RepositoryInspector.TryGetRemoteAsync(
            repositoryPath,
            gitExecutablePath,
            commandTimeout,
            cancellationToken).ConfigureAwait(false);
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
