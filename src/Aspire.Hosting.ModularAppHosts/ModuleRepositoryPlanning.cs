using System.Globalization;
using System.Security.Cryptography;
using System.Text;

#pragma warning disable CA1308 // Remote identities and filesystem slugs use conventional lowercase canonical forms.

namespace Aspire.Hosting;

internal sealed class ModuleRepositoryRequirement
{
    private readonly HashSet<string> _moduleNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _checkoutDirectoryNameConfigurationKeys = new(StringComparer.Ordinal);

    internal ModuleRepositoryRequirement(
        string moduleName,
        string repository,
        string normalizedRepository,
        string repositoryPath,
        string? revision,
        bool updateOnInitialize,
        string stepKey,
        bool requiredOnRun,
        bool usesCanonicalCheckout,
        string checkoutDirectoryNameConfigurationKey)
    {
        AddConsumer(moduleName, checkoutDirectoryNameConfigurationKey);
        Repository = repository;
        NormalizedRepository = normalizedRepository;
        RepositoryPath = Path.GetFullPath(repositoryPath);
        Revision = NormalizeRevision(revision);
        UpdateOnInitialize = Revision is null && updateOnInitialize;
        StepKey = stepKey;
        RequiredOnRun = requiredOnRun;
        UsesCanonicalCheckout = usesCanonicalCheckout;
        ConfigurationFingerprint = CreateConfigurationFingerprint(
            NormalizedRepository,
            RepositoryPath,
            Revision,
            UpdateOnInitialize);
    }

    public IReadOnlyCollection<string> ModuleNames => _moduleNames;

    public IReadOnlyCollection<string> CheckoutDirectoryNameConfigurationKeys =>
        _checkoutDirectoryNameConfigurationKeys;

    public string Repository { get; }

    public string NormalizedRepository { get; }

    public string RepositoryPath { get; }

    public string? Revision { get; }

    public bool UpdateOnInitialize { get; }

    public string StepKey { get; }

    public string ConfigurationFingerprint { get; }

    public bool RequiredOnRun { get; private set; }

    public bool UsesCanonicalCheckout { get; }

    internal void AddModule(string moduleName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);
        _moduleNames.Add(moduleName.Trim());
    }

    internal void AddConsumer(string moduleName, string checkoutDirectoryNameConfigurationKey)
    {
        AddModule(moduleName);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkoutDirectoryNameConfigurationKey);
        _checkoutDirectoryNameConfigurationKeys.Add(checkoutDirectoryNameConfigurationKey.Trim());
    }

    internal void RequireOnRun() => RequiredOnRun = true;

    internal void EnsureCompatible(
        string normalizedRepository,
        string? revision,
        bool updateOnInitialize)
    {
        var normalizedRevision = NormalizeRevision(revision);
        var normalizedUpdateOnInitialize = normalizedRevision is null && updateOnInitialize;
        if (!string.Equals(
                NormalizedRepository,
                normalizedRepository,
                StringComparison.Ordinal) ||
            !string.Equals(Revision, normalizedRevision, StringComparison.Ordinal) ||
            UpdateOnInitialize != normalizedUpdateOnInitialize)
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
        bool updateOnInitialize)
    {
        var configuration = string.Join(
            '\n',
            "1",
            normalizedRepository,
            Path.GetFullPath(repositoryPath),
            revision ?? string.Empty,
            updateOnInitialize ? "update" : "preserve");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(configuration)))
            .ToLowerInvariant();
    }
}

internal readonly record struct ModuleRepositoryPlanRegistration(
    ModuleRepositoryRequirement Requirement,
    bool IsNew);

internal sealed class ModuleRepositoryPlanRegistry
{
    private readonly Dictionary<string, ModuleRepositoryRequirement> _requirements =
        new(PathSafety.Comparer);
    private readonly Dictionary<string, ModuleRepositoryRequirement> _requirementsByIdentity =
        new(StringComparer.Ordinal);

    public ModuleRepositoryPlanRegistry(string appHostDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appHostDirectory);
        AppHostDirectory = Path.GetFullPath(appHostDirectory);
        AppHostRepositoryRoot = RepositoryIdentity.FindGitRoot(AppHostDirectory);
        SiblingParent = Path.GetDirectoryName(AppHostRepositoryRoot)
            ?? throw new InvalidOperationException(
                $"Unable to determine the parent of AppHost repository '{AppHostRepositoryRoot}'.");
    }

    public string AppHostDirectory { get; }

    public string AppHostRepositoryRoot { get; }

    public string SiblingParent { get; }

    public IReadOnlyCollection<ModuleRepositoryRequirement> Requirements =>
        _requirements.Values.ToArray();

    public ModuleRepositoryPlanRegistration Register(
        string moduleName,
        string repository,
        string? revision,
        bool updateOnInitialize,
        bool requiredOnRun = true,
        string? checkoutDirectoryName = null,
        string? checkoutDirectoryNameConfigurationKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        var configurationKey = GetConfigurationKey(
            moduleName,
            checkoutDirectoryNameConfigurationKey);
        var repositoryPath = RepositoryIdentity.GetSiblingPath(
            SiblingParent,
            repository,
            revision,
            AppHostDirectory,
            checkoutDirectoryName,
            configurationKey);
        return Register(
            moduleName,
            repository,
            repositoryPath,
            revision,
            updateOnInitialize,
            requiredOnRun,
            checkoutDirectoryName,
            configurationKey);
    }

    public ModuleRepositoryPlanRegistration Register(
        string moduleName,
        string repository,
        string repositoryPath,
        string? revision,
        bool updateOnInitialize,
        bool requiredOnRun = true,
        string? checkoutDirectoryName = null,
        string? checkoutDirectoryNameConfigurationKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);

        var configurationKey = GetConfigurationKey(
            moduleName,
            checkoutDirectoryNameConfigurationKey);
        var normalizedRevision = string.IsNullOrWhiteSpace(revision) ? null : revision.Trim();
        var usesCanonicalCheckout = normalizedRevision is null &&
            RepositoryIdentity.IsRemoteRepository(repository, AppHostDirectory);
        if (checkoutDirectoryName is not null)
        {
            _ = RepositoryIdentity.ValidateCheckoutDirectoryName(
                checkoutDirectoryName,
                configurationKey,
                normalizedRevision,
                SiblingParent);
        }

        var fullRepositoryPath = Path.GetFullPath(repositoryPath);
        RepositoryIdentity.EnsureDirectSibling(
            AppHostRepositoryRoot,
            SiblingParent,
            fullRepositoryPath);

        var normalizedRepository = RepositoryIdentity.NormalizeRepositoryIdentity(
            repository,
            AppHostDirectory);
        var identityKey = CreateIdentityKey(normalizedRepository, normalizedRevision);
        if (_requirementsByIdentity.TryGetValue(identityKey, out var equivalent))
        {
            if (!PathSafety.AreEqual(equivalent.RepositoryPath, fullRepositoryPath))
            {
                throw CreateEquivalentIdentityPathException(
                    equivalent,
                    normalizedRepository,
                    fullRepositoryPath,
                    configurationKey);
            }

            equivalent.EnsureCompatible(normalizedRepository, normalizedRevision, updateOnInitialize);
            equivalent.AddConsumer(moduleName, configurationKey);
            if (requiredOnRun)
            {
                equivalent.RequireOnRun();
            }

            return new ModuleRepositoryPlanRegistration(equivalent, IsNew: false);
        }

        if (_requirements.TryGetValue(fullRepositoryPath, out var existing))
        {
            if (!string.Equals(
                    existing.NormalizedRepository,
                    normalizedRepository,
                    StringComparison.Ordinal))
            {
                throw CreateCanonicalPathCollisionException(
                    existing,
                    normalizedRepository,
                    fullRepositoryPath,
                    configurationKey);
            }

            existing.EnsureCompatible(normalizedRepository, revision, updateOnInitialize);
            existing.AddConsumer(moduleName, configurationKey);
            if (requiredOnRun)
            {
                existing.RequireOnRun();
            }

            return new ModuleRepositoryPlanRegistration(existing, IsNew: false);
        }

        var stepKey = RepositoryIdentity.GetStepKey(
            normalizedRepository,
            revision,
            Path.GetFileName(fullRepositoryPath));
        var requirement = new ModuleRepositoryRequirement(
            moduleName,
            repository.Trim(),
            normalizedRepository,
            fullRepositoryPath,
            revision,
            updateOnInitialize,
            stepKey,
            requiredOnRun,
            usesCanonicalCheckout,
            configurationKey);
        _requirements.Add(fullRepositoryPath, requirement);
        _requirementsByIdentity.Add(identityKey, requirement);
        return new ModuleRepositoryPlanRegistration(requirement, IsNew: true);
    }

    private static string GetConfigurationKey(string moduleName, string? configurationKey) =>
        string.IsNullOrWhiteSpace(configurationKey)
            ? $"{ModularAppHostsOptions.ConfigurationSectionName}:Modules:{moduleName}:CheckoutDirectoryName"
            : configurationKey.Trim();

    private static string CreateIdentityKey(string normalizedRepository, string? revision) =>
        $"{normalizedRepository}\n{revision ?? string.Empty}";

    private static InvalidOperationException CreateCanonicalPathCollisionException(
        ModuleRepositoryRequirement existing,
        string normalizedRepository,
        string repositoryPath,
        string configurationKey)
    {
        var existingKeys = FormatConfigurationKeys(existing.CheckoutDirectoryNameConfigurationKeys);
        return new InvalidOperationException(
            $"Repository identities '{existing.NormalizedRepository}' ({existingKeys}) and " +
            $"'{normalizedRepository}' (configuration key '{configurationKey}') resolve to the same canonical " +
            $"checkout path '{repositoryPath}'. Configure an explicit distinct CheckoutDirectoryName using " +
            $"{existingKeys} or configuration key '{configurationKey}'.");
    }

    private static InvalidOperationException CreateEquivalentIdentityPathException(
        ModuleRepositoryRequirement existing,
        string normalizedRepository,
        string repositoryPath,
        string configurationKey)
    {
        var existingKeys = FormatConfigurationKeys(existing.CheckoutDirectoryNameConfigurationKeys);
        return new InvalidOperationException(
            $"Equivalent repository identity '{normalizedRepository}' resolves to both " +
            $"'{existing.RepositoryPath}' ({existingKeys}) and '{repositoryPath}' " +
            $"(configuration key '{configurationKey}'). Equivalent repositories must share one repository plan; " +
            "configure the same CheckoutDirectoryName for every use.");
    }

    private static string FormatConfigurationKeys(IEnumerable<string> configurationKeys) =>
        string.Join(
            ", ",
            configurationKeys
                .Order(StringComparer.Ordinal)
                .Select(key => $"configuration key '{key}'"));
}

internal static class RepositoryIdentity
{
    private const int HashLength = 10;
    private const int MaximumDirectoryNameLength = 72;

    public static string FindRepositoryRoot(string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        return TryFindRepositoryRoot(projectPath) ?? GetWorkingDirectory(projectPath);
    }

    public static string? TryFindRepositoryRoot(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var current = new DirectoryInfo(GetWorkingDirectory(path));
        while (current is not null)
        {
            if (HasGitMetadata(current.FullName))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }

    public static string FindGitRoot(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return TryFindRepositoryRoot(path) ?? throw new InvalidOperationException(
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
        string? revision,
        string baseDirectory,
        string? checkoutDirectoryName = null,
        string? checkoutDirectoryNameConfigurationKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(siblingParent);
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        var normalizedRepository = NormalizeRepositoryIdentity(repository, baseDirectory);
        var normalizedRevision = string.IsNullOrWhiteSpace(revision) ? null : revision.Trim();
        var configurationKey = string.IsNullOrWhiteSpace(checkoutDirectoryNameConfigurationKey)
            ? $"{ModularAppHostsOptions.ConfigurationSectionName}:Modules:<module>:CheckoutDirectoryName"
            : checkoutDirectoryNameConfigurationKey.Trim();
        if (checkoutDirectoryName is not null)
        {
            checkoutDirectoryName = ValidateCheckoutDirectoryName(
                checkoutDirectoryName,
                configurationKey,
                normalizedRevision,
                siblingParent);
        }

        if (normalizedRevision is null && IsRemoteRepository(repository, baseDirectory))
        {
            var canonicalDirectoryName = checkoutDirectoryName ??
                CreateSlug(GetRepositoryName(normalizedRepository), MaximumDirectoryNameLength);
            return Path.Combine(Path.GetFullPath(siblingParent), canonicalDirectoryName);
        }

        var repositorySlug = CreateSlug(GetRepositoryName(normalizedRepository), 30);
        var repositoryHash = GetStableHash(normalizedRepository);
        var directoryName = $"{repositorySlug}-{repositoryHash}";
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

    public static string ValidateCheckoutDirectoryName(
        string value,
        string configurationKey,
        string? revision,
        string siblingParent)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(siblingParent);
        var reason = GetInvalidCheckoutDirectoryNameReason(value, revision);
        if (reason is not null)
        {
            throw new InvalidOperationException(
                $"Checkout directory name '{FormatDiagnosticValue(value)}' from configuration key " +
                $"'{configurationKey}' is invalid: {reason}.");
        }

        var siblingParentPath = Path.GetFullPath(siblingParent);
        var destination = Path.GetFullPath(Path.Combine(siblingParentPath, value));
        if (!PathSafety.AreEqual(Path.GetDirectoryName(destination), siblingParentPath))
        {
            throw new InvalidOperationException(
                $"Checkout directory name '{FormatDiagnosticValue(value)}' from configuration key " +
                $"'{configurationKey}' is invalid: it resolves outside sibling parent '{siblingParentPath}'.");
        }

        return value;
    }

    public static string NormalizeRepositoryIdentity(string repository, string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        var value = repository.Trim().TrimEnd('/', '\\');
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            if (uri.IsFile)
            {
                return NormalizeLocalIdentity(uri.LocalPath, baseDirectory);
            }

            var host = uri.IdnHost.ToLowerInvariant();
            var port = IsDefaultPort(uri.Scheme, uri.Port)
                ? string.Empty
                : $":{uri.Port.ToString(CultureInfo.InvariantCulture)}";
            return NormalizeHostAndPath(host, port, uri.AbsolutePath);
        }

        if (Path.IsPathRooted(value) ||
            value.StartsWith('.') ||
            Directory.Exists(Path.GetFullPath(value, baseDirectory)))
        {
            return NormalizeLocalIdentity(value, baseDirectory);
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

    public static string NormalizeRemoteIdentity(string repository) =>
        NormalizeRepositoryIdentity(repository, Directory.GetCurrentDirectory());

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

    public static bool RefersToSameRepository(string first, string second, string baseDirectory)
    {
        if (!IsRemoteRepository(first, baseDirectory) || !IsRemoteRepository(second, baseDirectory))
        {
            return false;
        }

        return string.Equals(
            NormalizeRepositoryIdentity(first, baseDirectory),
            NormalizeRepositoryIdentity(second, baseDirectory),
            StringComparison.Ordinal);
    }

    public static bool AreEquivalent(string first, string second, string baseDirectory)
    {
        var firstIsRemote = IsRemoteRepository(first, baseDirectory);
        var secondIsRemote = IsRemoteRepository(second, baseDirectory);
        if (firstIsRemote != secondIsRemote)
        {
            return false;
        }

        return firstIsRemote
            ? RefersToSameRepository(first, second, baseDirectory)
            : PathSafety.AreEqual(
                Path.GetFullPath(first, baseDirectory),
                Path.GetFullPath(second, baseDirectory));
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

    private static string GetWorkingDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return Directory.Exists(fullPath)
            ? fullPath
            : Path.GetDirectoryName(fullPath)
                ?? throw new InvalidOperationException(
                    $"Unable to determine the directory containing '{path}'.");
    }

    private static string NormalizeLocalIdentity(string repository, string baseDirectory)
    {
        var fullPath = Path.GetFullPath(repository, baseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return $"file:{fullPath.Replace(Path.DirectorySeparatorChar, '/')}";
    }

    private static string GetRepositoryName(string normalizedRepository)
    {
        var separator = normalizedRepository.LastIndexOf('/');
        return separator < 0 ? normalizedRepository : normalizedRepository[(separator + 1)..];
    }

    private static string? GetInvalidCheckoutDirectoryNameReason(string value, string? revision)
    {
        if (!string.IsNullOrWhiteSpace(revision))
        {
            return $"it cannot be used with pinned repository revision '{FormatDiagnosticValue(revision)}'";
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return "it must contain exactly one non-empty filename segment";
        }

        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            return "leading or trailing whitespace is not allowed";
        }

        if (value.Length > 255)
        {
            return "filename segments longer than 255 characters are not allowed";
        }

        if (value is "." or "..")
        {
            return "'.' and '..' are traversal segments";
        }

        if (Path.IsPathRooted(value) ||
            (value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':' &&
                value[2] is '/' or '\\'))
        {
            return "rooted paths are not allowed";
        }

        if (value.IndexOfAny(['/', '\\']) >= 0)
        {
            return "directory separators and multi-segment paths are not allowed";
        }

        if (value.Any(character =>
                char.IsControl(character) ||
                character is '<' or '>' or ':' or '"' or '|' or '?' or '*' ||
                Path.GetInvalidFileNameChars().Contains(character)))
        {
            return "invalid filename characters are not allowed";
        }

        if (value.EndsWith('.'))
        {
            return "filename segments ending in a period are not portable";
        }

        var stem = value.Split('.', 2)[0];
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            (stem.Length == 4 &&
                (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                 stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                stem[3] is >= '1' and <= '9'))
        {
            return "reserved filename segments are not allowed";
        }

        return null;
    }

    private static string FormatDiagnosticValue(string value) =>
        value.Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

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
