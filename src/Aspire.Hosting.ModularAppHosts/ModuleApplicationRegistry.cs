using Aspire.Hosting.ApplicationModel;
using System.Collections.Concurrent;

namespace Aspire.Hosting.ModularAppHosts;

/// <summary>Provides read-only access to module definitions registered with an AppHost builder.</summary>
public interface IDistributedApplicationModuleCatalog
{
    /// <summary>Gets all exported modules.</summary>
    IReadOnlyCollection<IDistributedApplicationModule> Modules { get; }

    /// <summary>Looks up an exported module by name.</summary>
    bool TryGetModule(string name, out IDistributedApplicationModule? exportedModule);
}

internal sealed class ModuleApplicationRegistry : IDistributedApplicationModuleCatalog
{
    private readonly Dictionary<string, DistributedApplicationModule> _modules =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, IResource> _resources =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> _materializedModules =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, Lazy<Task>> _repositorySynchronizations =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<IDistributedApplicationModule> Modules => _modules.Values;

    public bool TryGetModule(string name, out IDistributedApplicationModule? exportedModule)
    {
        var found = _modules.TryGetValue(name, out var typedModule);
        exportedModule = typedModule;
        return found;
    }

    internal bool TryGetDefinition(string name, out DistributedApplicationModule? module)
    {
        return _modules.TryGetValue(name, out module);
    }

    internal void AddModule(DistributedApplicationModule module)
    {
        _modules.Add(module.Name, module);
    }

    internal bool IsMaterialized(string moduleName) => _materializedModules.Contains(moduleName);

    internal void MarkMaterialized(string moduleName) => _materializedModules.Add(moduleName);

    internal bool TryGetResource(string name, out IResource? resource) =>
        _resources.TryGetValue(name, out resource);

    internal void TrackResource(IResource resource)
    {
        _resources.TryAdd(resource.Name, resource);
    }

    internal Task SynchronizeRepositoryAsync(string repositoryPath, Func<Task> synchronize)
    {
        return _repositorySynchronizations.GetOrAdd(
            repositoryPath,
            _ => new Lazy<Task>(synchronize, LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }
}
