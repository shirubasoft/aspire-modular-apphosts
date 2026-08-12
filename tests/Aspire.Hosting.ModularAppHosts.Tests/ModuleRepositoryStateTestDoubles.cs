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
