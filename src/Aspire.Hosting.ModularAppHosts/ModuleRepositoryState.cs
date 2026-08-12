#pragma warning disable ASPIREPIPELINES002

using System.Text.Json;
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

internal sealed class AspireModuleRepositoryStateStore(IDeploymentStateManager stateManager)
    : IModuleRepositoryStateStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task<ModuleRepositoryInitializationState?> ReadAsync(
        ModuleRepositoryRequirement requirement,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        var section = await stateManager.AcquireSectionAsync(
            GetSectionName(requirement),
            cancellationToken).ConfigureAwait(false);
        try
        {
            var json = section.Data[string.Empty]?.GetValue<string>();
            return string.IsNullOrWhiteSpace(json)
                ? null
                : JsonSerializer.Deserialize<ModuleRepositoryInitializationState>(json, SerializerOptions);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
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
        var section = await stateManager.AcquireSectionAsync(
            GetSectionName(requirement),
            cancellationToken).ConfigureAwait(false);
        section.SetValue(JsonSerializer.Serialize(state, SerializerOptions));
        await stateManager.SaveSectionAsync(section, cancellationToken).ConfigureAwait(false);
    }

    internal static string GetSectionName(ModuleRepositoryRequirement requirement) =>
        $"modular-apphosts-repository-{requirement.StepKey}";
}
