using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

#pragma warning disable CA1308 // Aspire hashes normalized AppHost paths using lowercase text.

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
    string? StateFilePath { get; }

    Task<ModuleRepositoryInitializationState?> ReadAsync(
        ModuleRepositoryRequirement requirement,
        CancellationToken cancellationToken);

    Task WriteAsync(
        ModuleRepositoryRequirement requirement,
        ModuleRepositoryInitializationState state,
        CancellationToken cancellationToken);
}

internal sealed class FileModuleRepositoryStateStore(string stateFilePath)
    : IModuleRepositoryStateStore, IDisposable
{
    private const string StateFileName = "modular-apphosts.json";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _stateFilePath = Path.GetFullPath(
        string.IsNullOrWhiteSpace(stateFilePath)
            ? throw new ArgumentException("The repository state file path is required.", nameof(stateFilePath))
            : stateFilePath);

    public string? StateFilePath => _stateFilePath;

    public void Dispose() => _gate.Dispose();

    public static string ResolveStateFilePath(
        string? appHostPathSha256,
        string appHostDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appHostDirectory);
        var stateDirectoryName = string.IsNullOrWhiteSpace(appHostPathSha256)
            ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                Path.GetFullPath(appHostDirectory).ToLowerInvariant())))
            : appHostPathSha256.Trim();
        if (!string.Equals(
                Path.GetFileName(stateDirectoryName),
                stateDirectoryName,
                StringComparison.Ordinal) ||
            stateDirectoryName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException(
                $"AppHost path SHA '{stateDirectoryName}' is not a valid state-directory name.");
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".aspire",
            "deployments",
            stateDirectoryName,
            StateFileName);
    }

    public async Task<ModuleRepositoryInitializationState?> ReadAsync(
        ModuleRepositoryRequirement requirement,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = await LoadAsync(repairMalformed: false, cancellationToken).ConfigureAwait(false);
            return document?.Repositories.GetValueOrDefault(requirement.StepKey);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task WriteAsync(
        ModuleRepositoryRequirement requirement,
        ModuleRepositoryInitializationState state,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        ArgumentNullException.ThrowIfNull(state);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = await LoadAsync(repairMalformed: true, cancellationToken).ConfigureAwait(false)
                ?? new ModuleRepositoryStateDocument();
            document.Repositories[requirement.StepKey] = state;
            await SaveAsync(document, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ModuleRepositoryStateDocument?> LoadAsync(
        bool repairMalformed,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_stateFilePath))
        {
            return null;
        }

        ModuleRepositoryStateDocument? document;
        try
        {
            var stream = new FileStream(
                _stateFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (stream.ConfigureAwait(false))
            {
                document = await JsonSerializer.DeserializeAsync<ModuleRepositoryStateDocument>(
                    stream,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (
            repairMalformed && exception is JsonException or InvalidOperationException)
        {
            return null;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return null;
        }

        if (document is null || document.Repositories is null)
        {
            return null;
        }

        if (document.SchemaVersion == ModuleRepositoryStateDocument.CurrentSchemaVersion)
        {
            return document;
        }

        if (repairMalformed)
        {
            throw new InvalidOperationException(
                $"Repository state file '{_stateFilePath}' uses unsupported schema version " +
                $"'{document.SchemaVersion}'.");
        }

        return null;
    }

    private async Task SaveAsync(
        ModuleRepositoryStateDocument document,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_stateFilePath)
            ?? throw new InvalidOperationException(
                $"Repository state path '{_stateFilePath}' has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_stateFilePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await using (stream.ConfigureAwait(false))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    document,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, _stateFilePath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class ModuleRepositoryStateDocument
    {
        public const int CurrentSchemaVersion = 1;

        public int SchemaVersion { get; init; } = CurrentSchemaVersion;

        public Dictionary<string, ModuleRepositoryInitializationState> Repositories { get; init; } =
            new(StringComparer.Ordinal);
    }
}
