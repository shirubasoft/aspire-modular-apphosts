using System.Collections.Concurrent;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting.ModularAppHosts;

/// <summary>Provides read-only access to module definitions registered with an AppHost builder.</summary>
public interface IDistributedApplicationModuleCatalog
{
    /// <summary>Gets all exported modules.</summary>
    IReadOnlyCollection<IDistributedApplicationModule> Modules { get; }

    /// <summary>Looks up an exported module by name.</summary>
    bool TryGetModule(string name, out IDistributedApplicationModule? exportedModule);
}

internal sealed class ModuleApplicationRegistry(
    ModularAppHostsOptions? options = null,
    IConfiguration? configuration = null)
    : IDistributedApplicationModuleCatalog
{
    private readonly Dictionary<string, DistributedApplicationModule> _modules =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, IResource> _resources =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, string> _materializedModules =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, Lazy<Task>> _repositorySynchronizations =
        new(StringComparer.OrdinalIgnoreCase);

    internal ModularAppHostsOptions Options { get; } = options ?? new ModularAppHostsOptions();

    private bool _programmaticallyConfigured;

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

    internal bool TryGetMaterialization(string moduleName, out string? materializationKey) =>
        _materializedModules.TryGetValue(moduleName, out materializationKey);

    internal void MarkMaterialized(string moduleName, string materializationKey) =>
        _materializedModules.Add(moduleName, materializationKey);

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

    internal void RefreshConfiguration()
    {
        if (!_programmaticallyConfigured && configuration is not null)
        {
            ResetOptions();
            configuration.GetSection(ModularAppHostsOptions.ConfigurationSectionName).Bind(Options);
        }
    }

    internal void Configure(Action<ModularAppHostsOptions> configure)
    {
        RefreshConfiguration();
        configure(Options);
        _programmaticallyConfigured = true;
    }

    private void ResetOptions()
    {
        var defaults = new ModularAppHostsOptions();
        Options.RepositoryBasePath = defaults.RepositoryBasePath;
        Options.AutoCloneRepositories = defaults.AutoCloneRepositories;
        Options.GitHubCliPath = defaults.GitHubCliPath;
        Options.GitExecutablePath = defaults.GitExecutablePath;
        Options.RepositoryCommandTimeout = defaults.RepositoryCommandTimeout;
        Options.UpdateImportedRepositories = defaults.UpdateImportedRepositories;
        Options.ProjectMode = defaults.ProjectMode;
#pragma warning disable CS0618
        Options.RunProjectsAsContainers = defaults.RunProjectsAsContainers;
#pragma warning restore CS0618
        Options.PublishImages = defaults.PublishImages;
        Options.Modules.Clear();
    }

    internal void ValidateConfiguredModules()
    {
        var missingModule = Options.Modules.Keys.FirstOrDefault(name => !_modules.ContainsKey(name));
        if (missingModule is null)
        {
            return;
        }

        var availableModules = _modules.Count == 0
            ? "(none)"
            : string.Join(", ", _modules.Keys
                .Order(StringComparer.OrdinalIgnoreCase)
                .Select(name => $"'{name}'"));
        throw new InvalidOperationException(
            $"Configuration references module '{missingModule}', but no exported module with that name was found. " +
            $"Available modules: {availableModules}.");
    }
}
