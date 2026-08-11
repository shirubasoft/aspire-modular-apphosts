using System.Globalization;
using System.Security.Cryptography;
using System.Text;

#pragma warning disable CA1308 // Remote identities and filesystem slugs use conventional lowercase canonical forms.

namespace Aspire.Hosting;

internal sealed class ModuleRepositoryRequirement
{
    private readonly HashSet<string> _moduleNames = new(StringComparer.OrdinalIgnoreCase);

    internal ModuleRepositoryRequirement(
        string moduleName,
        string repository,
        string normalizedRepository,
        string repositoryPath,
        string? revision,
        bool updateRepository,
        string stepKey,
        string receiptPath)
    {
        AddModule(moduleName);
        Repository = repository;
        NormalizedRepository = normalizedRepository;
        RepositoryPath = Path.GetFullPath(repositoryPath);
        Revision = NormalizeRevision(revision);
        UpdateRepository = Revision is null && updateRepository;
        StepKey = stepKey;
        ReceiptPath = Path.GetFullPath(receiptPath);
        ConfigurationFingerprint = CreateConfigurationFingerprint(
            NormalizedRepository,
            RepositoryPath,
            Revision,
            UpdateRepository);
    }

    public IReadOnlyCollection<string> ModuleNames => _moduleNames;

    public string Repository { get; }

    public string NormalizedRepository { get; }

    public string RepositoryPath { get; }

    public string? Revision { get; }

    public bool UpdateRepository { get; }

    public string StepKey { get; }

    public string ReceiptPath { get; }

    public string ConfigurationFingerprint { get; }

    internal void AddModule(string moduleName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);
        _moduleNames.Add(moduleName.Trim());
    }

    internal void EnsureCompatible(
        string normalizedRepository,
        string? revision,
        bool updateRepository)
    {
        var normalizedRevision = NormalizeRevision(revision);
        var normalizedUpdateRepository = normalizedRevision is null && updateRepository;
        if (!string.Equals(
                NormalizedRepository,
                normalizedRepository,
                StringComparison.Ordinal) ||
            !string.Equals(Revision, normalizedRevision, StringComparison.Ordinal) ||
            UpdateRepository != normalizedUpdateRepository)
        {
            throw new InvalidOperationException(
                $"Modules sharing repository checkout '{RepositoryPath}' configure conflicting repositories, " +
                "revisions, or initialization update policies. Each checkout must have one initialization policy.");
        }
    }

    private static string? NormalizeRevision(string? revision) =>
        string.IsNullOrWhiteSpace(revision) ? null : revision.Trim();

    private static string CreateConfigurationFingerprint(
        string normalizedRepository,
        string repositoryPath,
        string? revision,
        bool updateRepository)
    {
        var configuration = string.Join(
            '\n',
            "1",
            normalizedRepository,
            Path.GetFullPath(repositoryPath),
            revision ?? string.Empty,
            updateRepository ? "update" : "preserve");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(configuration)))
            .ToLowerInvariant();
    }
}

internal readonly record struct ModuleRepositoryPlanRegistration(
    ModuleRepositoryRequirement Requirement,
    bool IsNew);

internal sealed class ModuleRepositoryPlanRegistry
{
    private const string ReceiptDirectoryName = "repositories";
    private readonly Dictionary<string, ModuleRepositoryRequirement> _requirements =
        new(PathSafety.Comparer);

    public ModuleRepositoryPlanRegistry(string appHostDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appHostDirectory);
        AppHostRepositoryRoot = ModuleRepositoryPathPlanner.FindGitRoot(appHostDirectory);
        SiblingParent = Path.GetDirectoryName(AppHostRepositoryRoot)
            ?? throw new InvalidOperationException(
                $"Unable to determine the parent of AppHost repository '{AppHostRepositoryRoot}'.");
        ReceiptDirectory = Path.Combine(
            AppHostRepositoryRoot,
            ".aspire",
            "modular-apphosts",
            ReceiptDirectoryName);
    }

    public string AppHostRepositoryRoot { get; }

    public string SiblingParent { get; }

    public string ReceiptDirectory { get; }

    public IReadOnlyCollection<ModuleRepositoryRequirement> Requirements =>
        _requirements.Values.ToArray();

    public ModuleRepositoryPlanRegistration Register(
        string moduleName,
        string repository,
        string? revision,
        bool updateRepository)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        var repositoryPath = ModuleRepositoryPathPlanner.GetSiblingPath(
            SiblingParent,
            repository,
            revision);
        return Register(
            moduleName,
            repository,
            repositoryPath,
            revision,
            updateRepository);
    }

    public ModuleRepositoryPlanRegistration Register(
        string moduleName,
        string repository,
        string repositoryPath,
        string? revision,
        bool updateRepository)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);

        var fullRepositoryPath = Path.GetFullPath(repositoryPath);
        ModuleRepositoryPathPlanner.EnsureDirectSibling(
            AppHostRepositoryRoot,
            SiblingParent,
            fullRepositoryPath);

        var normalizedRepository = ModuleRepositoryPathPlanner.NormalizeRemoteIdentity(repository);
        if (_requirements.TryGetValue(fullRepositoryPath, out var existing))
        {
            existing.EnsureCompatible(normalizedRepository, revision, updateRepository);
            existing.AddModule(moduleName);
            return new ModuleRepositoryPlanRegistration(existing, IsNew: false);
        }

        var stepKey = ModuleRepositoryPathPlanner.GetStepKey(
            normalizedRepository,
            revision,
            Path.GetFileName(fullRepositoryPath));
        var requirement = new ModuleRepositoryRequirement(
            moduleName,
            repository.Trim(),
            normalizedRepository,
            fullRepositoryPath,
            revision,
            updateRepository,
            stepKey,
            Path.Combine(ReceiptDirectory, $"{stepKey}.json"));
        _requirements.Add(fullRepositoryPath, requirement);
        return new ModuleRepositoryPlanRegistration(requirement, IsNew: true);
    }
}

internal static class ModuleRepositoryPathPlanner
{
    private const int HashLength = 10;
    private const int MaximumDirectoryNameLength = 72;

    public static string FindGitRoot(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var current = Directory.Exists(fullPath)
            ? new DirectoryInfo(fullPath)
            : new DirectoryInfo(Path.GetDirectoryName(fullPath)
                ?? throw new InvalidOperationException(
                    $"Unable to determine the directory containing '{path}'."));

        while (current is not null)
        {
            if (HasGitMetadata(current.FullName))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException(
            $"AppHost directory '{path}' is not inside a Git repository. " +
            "Repository initialization requires an AppHost Git root.");
    }

    public static bool HasGitMetadata(string repositoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        var gitMetadata = Path.Combine(Path.GetFullPath(repositoryPath), ".git");
        return Directory.Exists(gitMetadata) || File.Exists(gitMetadata);
    }

    public static string GetSiblingPath(
        string siblingParent,
        string repository,
        string? revision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(siblingParent);
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        var normalizedRepository = NormalizeRemoteIdentity(repository);
        var repositorySlug = CreateSlug(GetRepositoryName(normalizedRepository), 30);
        var repositoryHash = GetStableHash(normalizedRepository);
        var directoryName = $"{repositorySlug}-{repositoryHash}";
        var normalizedRevision = string.IsNullOrWhiteSpace(revision) ? null : revision.Trim();
        if (normalizedRevision is not null)
        {
            var revisionSlug = CreateSlug(normalizedRevision, 18);
            var revisionHash = GetStableHash(normalizedRevision);
            directoryName = $"{directoryName}-rev-{revisionSlug}-{revisionHash}";
        }

        if (directoryName.Length > MaximumDirectoryNameLength)
        {
            var stableSuffix = GetStableHash($"{normalizedRepository}\n{normalizedRevision}");
            directoryName =
                $"{directoryName[..(MaximumDirectoryNameLength - stableSuffix.Length - 1)].TrimEnd('-')}-{stableSuffix}";
        }

        return Path.Combine(Path.GetFullPath(siblingParent), directoryName);
    }

    public static string NormalizeRemoteIdentity(string repository)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        var value = repository.Trim().TrimEnd('/', '\\');
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            if (uri.IsFile)
            {
                throw new InvalidOperationException(
                    $"Repository '{repository}' is a local path. Only remote repositories require initialization.");
            }

            var host = uri.IdnHost.ToLowerInvariant();
            var port = IsDefaultPort(uri.Scheme, uri.Port)
                ? string.Empty
                : $":{uri.Port.ToString(CultureInfo.InvariantCulture)}";
            return NormalizeHostAndPath(host, port, uri.AbsolutePath);
        }

        var queryOrFragment = value.IndexOfAny(['?', '#']);
        if (queryOrFragment >= 0)
        {
            value = value[..queryOrFragment];
        }

        var colon = value.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0 && value[..colon].IndexOfAny(['/', '\\']) < 0)
        {
            var authority = value[..colon];
            var userSeparator = authority.LastIndexOf('@');
            var host = authority[(userSeparator + 1)..].ToLowerInvariant();
            return NormalizeHostAndPath(host, string.Empty, value[(colon + 1)..]);
        }

        var components = value
            .Replace('\\', '/')
            .Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (components.Length < 2)
        {
            throw new InvalidOperationException(
                $"Repository '{repository}' is not a recognizable remote repository identity.");
        }

        var firstComponentIsHost = components[0].Contains('.', StringComparison.Ordinal);
        var hostName = firstComponentIsHost
            ? components[0].ToLowerInvariant()
            : "github.com";
        var pathStart = firstComponentIsHost ? 1 : 0;
        return NormalizeHostAndPath(
            hostName,
            string.Empty,
            string.Join('/', components[pathStart..]));
    }

    public static string GetStepKey(
        string normalizedRepository,
        string? revision,
        string repositoryDirectoryName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedRepository);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryDirectoryName);
        var slug = CreateSlug(repositoryDirectoryName, 38);
        var suffix = GetStableHash(
            $"{normalizedRepository}\n{(string.IsNullOrWhiteSpace(revision) ? string.Empty : revision.Trim())}");
        return $"{slug}-{suffix}";
    }

    public static void EnsureDirectSibling(
        string appHostRepositoryRoot,
        string siblingParent,
        string repositoryPath)
    {
        var fullRepositoryPath = Path.GetFullPath(repositoryPath);
        var actualParent = Path.GetDirectoryName(fullRepositoryPath);
        if (PathSafety.AreEqual(fullRepositoryPath, appHostRepositoryRoot) ||
            !PathSafety.AreEqual(actualParent, siblingParent))
        {
            throw new InvalidOperationException(
                $"Repository initialization target '{repositoryPath}' must be a direct sibling of " +
                $"the AppHost Git root '{appHostRepositoryRoot}'.");
        }
    }

    private static string NormalizeHostAndPath(string host, string port, string path)
    {
        var normalizedPath = path.Replace('\\', '/').Trim('/');
        if (normalizedPath.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            normalizedPath = normalizedPath[..^4];
        }

        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(normalizedPath))
        {
            throw new InvalidOperationException("A remote repository must include a host and repository path.");
        }

        if (string.Equals(host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            normalizedPath = normalizedPath.ToLowerInvariant();
        }

        return $"{host}{port}/{normalizedPath}";
    }

    private static string GetRepositoryName(string normalizedRepository)
    {
        var separator = normalizedRepository.LastIndexOf('/');
        return separator < 0 ? normalizedRepository : normalizedRepository[(separator + 1)..];
    }

    private static string CreateSlug(string value, int maximumLength)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.ToLowerInvariant())
        {
            if (character is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                builder.Append(character);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var slug = builder.ToString().Trim('-');
        if (slug.Length == 0)
        {
            slug = "repository";
        }

        return slug.Length <= maximumLength
            ? slug
            : slug[..maximumLength].TrimEnd('-');
    }

    private static string GetStableHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            [..HashLength]
            .ToLowerInvariant();

    private static bool IsDefaultPort(string scheme, int port) =>
        port < 0 ||
        (string.Equals(scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && port == 80) ||
        (string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) && port == 443) ||
        (string.Equals(scheme, "ssh", StringComparison.OrdinalIgnoreCase) && port == 22) ||
        (string.Equals(scheme, "git", StringComparison.OrdinalIgnoreCase) && port == 9418);
}
