#pragma warning disable ASPIREPIPELINES002

using System.Text.Json.Nodes;
using Aspire.Hosting.Pipelines;

namespace Aspire.Hosting.ModularAppHosts.Tests;

internal sealed class InMemoryModuleRepositoryStateStore : IModuleRepositoryStateStore
{
    private readonly Dictionary<string, ModuleRepositoryInitializationState> _states =
        new(StringComparer.Ordinal);

    public Task<ModuleRepositoryInitializationState?> ReadAsync(
        ModuleRepositoryRequirement requirement,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _states.TryGetValue(requirement.StepKey, out var state);
        return Task.FromResult(state);
    }

    public Task WriteAsync(
        ModuleRepositoryRequirement requirement,
        ModuleRepositoryInitializationState state,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _states[requirement.StepKey] = state;
        return Task.CompletedTask;
    }
}

internal sealed class InMemoryDeploymentStateManager : IDeploymentStateManager
{
    private readonly Dictionary<string, (JsonObject Data, long Version)> _sections =
        new(StringComparer.Ordinal);

    public string? StateFilePath => null;

    public Task<DeploymentStateSection> AcquireSectionAsync(
        string sectionName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var section = _sections.TryGetValue(sectionName, out var stored)
            ? new DeploymentStateSection(
                sectionName,
                stored.Data.DeepClone().AsObject(),
                stored.Version)
            : new DeploymentStateSection(sectionName, null, 0);
        return Task.FromResult(section);
    }

    public Task SaveSectionAsync(
        DeploymentStateSection section,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_sections.TryGetValue(section.SectionName, out var stored) && stored.Version != section.Version)
        {
            throw new InvalidOperationException("The deployment state section version is stale.");
        }

        section.Version++;
        _sections[section.SectionName] = (section.Data.DeepClone().AsObject(), section.Version);
        return Task.CompletedTask;
    }

    public Task DeleteSectionAsync(
        DeploymentStateSection section,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _sections.Remove(section.SectionName);
        return Task.CompletedTask;
    }

    public Task ClearAllStateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _sections.Clear();
        return Task.CompletedTask;
    }
}
