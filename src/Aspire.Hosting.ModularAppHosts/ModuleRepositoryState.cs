using System.Text.Json;
using System.Text.Json.Serialization;

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

internal sealed class FileModuleRepositoryStateStore : IModuleRepositoryStateStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    public async Task<ModuleRepositoryInitializationState?> ReadAsync(
        ModuleRepositoryRequirement requirement,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        if (!File.Exists(requirement.StatePath))
        {
            return null;
        }

        try
        {
            var stream = new FileStream(
                requirement.StatePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (stream.ConfigureAwait(false))
            {
                return await JsonSerializer.DeserializeAsync<ModuleRepositoryInitializationState>(
                    stream,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public async Task WriteAsync(
        ModuleRepositoryRequirement requirement,
        ModuleRepositoryInitializationState state,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        ArgumentNullException.ThrowIfNull(state);
        var stateDirectory = Path.GetDirectoryName(requirement.StatePath)
            ?? throw new InvalidOperationException(
                $"Unable to determine the state directory for '{requirement.StatePath}'.");
        Directory.CreateDirectory(stateDirectory);
        var temporaryPath = Path.Combine(
            stateDirectory,
            $".{Path.GetFileName(requirement.StatePath)}.{Guid.NewGuid():N}.tmp");
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
                    state,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, requirement.StatePath, overwrite: true);
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
}
