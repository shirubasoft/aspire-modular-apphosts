#pragma warning disable ASPIREPIPELINES002
#pragma warning disable CA1308 // Aspire deployment state uses lowercase environment file names.

using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting.Pipelines;

namespace Aspire.Hosting;

internal sealed record ModuleRepositoryInitializationState(
    int SchemaVersion,
    string Repository,
    string Destination,
    string? Revision,
    string ConfigurationFingerprint,
    string Origin,
    string ResolvedCommit,
    DateTimeOffset InitializedAtUtc)
{
    public const int CurrentSchemaVersion = 1;

    public bool Matches(ModuleRepositoryRequirement requirement) =>
        SchemaVersion == CurrentSchemaVersion &&
        string.Equals(Repository, requirement.NormalizedRepository, StringComparison.Ordinal) &&
        PathSafety.AreEqual(Destination, requirement.RepositoryPath) &&
        string.Equals(Revision, requirement.Revision, StringComparison.Ordinal) &&
        string.Equals(ConfigurationFingerprint, requirement.ConfigurationFingerprint, StringComparison.Ordinal);
}

internal interface IModuleRepositoryStateStore
{
    Task<ModuleRepositoryInitializationState?> ReadAsync(
        ModuleRepositoryRequirement requirement,
        CancellationToken cancellationToken);

    Task WriteAsync(
        ModuleRepositoryRequirement requirement,
        ModuleRepositoryInitializationState state,
        CancellationToken cancellationToken);
}

internal sealed class AspireModuleRepositoryStateStore(
    IDeploymentStateManager stateManager,
    IDeploymentStateManager? fallbackStateManager = null)
    : IModuleRepositoryStateStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task<ModuleRepositoryInitializationState?> ReadAsync(
        ModuleRepositoryRequirement requirement,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        var state = await ReadSafelyAsync(
            stateManager,
            requirement,
            cancellationToken).ConfigureAwait(false);
        if (state is not null || !ShouldUseFallback())
        {
            return state;
        }

        return await ReadSafelyAsync(
            fallbackStateManager!,
            requirement,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteAsync(
        ModuleRepositoryRequirement requirement,
        ModuleRepositoryInitializationState state,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        ArgumentNullException.ThrowIfNull(state);
        await WriteAsync(stateManager, requirement, state, cancellationToken).ConfigureAwait(false);
        if (ShouldUseFallback())
        {
            await WriteAsync(fallbackStateManager!, requirement, state, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static string GetSectionName(ModuleRepositoryRequirement requirement) =>
        $"modular-apphosts:repositories:{requirement.StepKey}";

    private bool ShouldUseFallback()
    {
        if (fallbackStateManager is null)
        {
            return false;
        }

        var primaryPath = stateManager.StateFilePath;
        var fallbackPath = fallbackStateManager.StateFilePath;
        return string.IsNullOrWhiteSpace(primaryPath) || string.IsNullOrWhiteSpace(fallbackPath)
            ? !string.Equals(primaryPath, fallbackPath, StringComparison.Ordinal)
            : !PathSafety.AreEqual(primaryPath, fallbackPath);
    }

    private static async Task<ModuleRepositoryInitializationState?> ReadAsync(
        IDeploymentStateManager manager,
        ModuleRepositoryRequirement requirement,
        CancellationToken cancellationToken)
    {
        var section = await manager.AcquireSectionAsync(
            GetSectionName(requirement),
            cancellationToken).ConfigureAwait(false);
        var json = section.Data[string.Empty]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize<ModuleRepositoryInitializationState>(json, SerializerOptions);
    }

    private static async Task<ModuleRepositoryInitializationState?> ReadSafelyAsync(
        IDeploymentStateManager manager,
        ModuleRepositoryRequirement requirement,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ReadAsync(manager, requirement, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static async Task WriteAsync(
        IDeploymentStateManager manager,
        ModuleRepositoryRequirement requirement,
        ModuleRepositoryInitializationState state,
        CancellationToken cancellationToken)
    {
        var section = await manager.AcquireSectionAsync(
            GetSectionName(requirement),
            cancellationToken).ConfigureAwait(false);
        section.SetValue(JsonSerializer.Serialize(state, SerializerOptions));
        await manager.SaveSectionAsync(section, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Provides normal run mode with the same deployment-state file that Aspire pipeline mode uses.
/// Aspire 13.4 otherwise selects user secrets for run mode, which is unavailable in AppHosts
/// without a user-secrets ID.
/// </summary>
internal sealed class ModuleRepositoryDeploymentStateManager(string? stateFilePath)
    : IDeploymentStateManager, IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, long> _versions = new(StringComparer.Ordinal);

    public string? StateFilePath { get; } = stateFilePath;

    public void Dispose() => _gate.Dispose();

    public static string? ResolveStateFilePath(string? appHostPathSha256, string environmentName)
    {
        if (string.IsNullOrWhiteSpace(appHostPathSha256))
        {
            return null;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);
        var normalizedEnvironment = environmentName.ToLowerInvariant();
        if (normalizedEnvironment.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-'))
        {
            throw new ArgumentException(
                "The environment name must contain only letters, digits, underscores, and hyphens.",
                nameof(environmentName));
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".aspire",
            "deployments",
            appHostPathSha256,
            $"{normalizedEnvironment}.json");
    }

    public async Task<DeploymentStateSection> AcquireSectionAsync(
        string sectionName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionName);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await LoadAsync(cancellationToken).ConfigureAwait(false);
            var section = new DeploymentStateSection(
                sectionName,
                data: null,
                _versions.GetValueOrDefault(sectionName));
            if (state[sectionName] is JsonValue value &&
                value.TryGetValue<string>(out var serialized))
            {
                section.SetValue(serialized);
            }

            return section;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveSectionAsync(
        DeploymentStateSection section,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(section);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureCurrentVersion(section);
            var state = await LoadAsync(cancellationToken).ConfigureAwait(false);
            state[section.SectionName] = section.Data[string.Empty]?.DeepClone();
            await SaveAsync(state, cancellationToken).ConfigureAwait(false);
            section.Version++;
            _versions[section.SectionName] = section.Version;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteSectionAsync(
        DeploymentStateSection section,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(section);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureCurrentVersion(section);
            var state = await LoadAsync(cancellationToken).ConfigureAwait(false);
            state.Remove(section.SectionName);
            await SaveAsync(state, cancellationToken).ConfigureAwait(false);
            section.Version++;
            _versions[section.SectionName] = section.Version;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAllStateAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SaveAsync(new JsonObject(), cancellationToken).ConfigureAwait(false);
            _versions.Clear();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void EnsureCurrentVersion(DeploymentStateSection section)
    {
        if (_versions.GetValueOrDefault(section.SectionName) != section.Version)
        {
            throw new InvalidOperationException(
                $"Deployment state section '{section.SectionName}' was modified after it was acquired.");
        }
    }

    private async Task<JsonObject> LoadAsync(CancellationToken cancellationToken)
    {
        if (StateFilePath is null || !File.Exists(StateFilePath))
        {
            return new JsonObject();
        }

        var json = await File.ReadAllTextAsync(StateFilePath, cancellationToken).ConfigureAwait(false);
        return JsonNode.Parse(
                json,
                documentOptions: new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                })?
            .AsObject() ?? new JsonObject();
    }

    private async Task SaveAsync(JsonObject state, CancellationToken cancellationToken)
    {
        if (StateFilePath is null)
        {
            return;
        }

        var directory = Path.GetDirectoryName(StateFilePath)
            ?? throw new InvalidOperationException(
                $"Deployment state path '{StateFilePath}' has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(StateFilePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                state.ToJsonString(SerializerOptions),
                cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, StateFilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
