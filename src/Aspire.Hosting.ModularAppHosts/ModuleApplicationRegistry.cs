using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting;

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
    ModuleRepositoryPlanRegistry? repositoryPlans = null)
    : IDistributedApplicationModuleCatalog
{
    private readonly Dictionary<string, DistributedApplicationModule> _modules =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, IResource> _resources =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, string> _materializedModules =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly List<ModuleRequiredPath> _requiredPaths = [];
    private readonly HashSet<string> _requiredPathKeys = new(StringComparer.Ordinal);
    private ModuleRepositoryPlanRegistry? _repositoryPlans = repositoryPlans;

    internal ModularAppHostsOptions Options { get; } = options ?? new ModularAppHostsOptions();

    internal ModuleRepositoryPlanRegistry? RepositoryPlans => _repositoryPlans;

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

    internal IReadOnlyCollection<IDistributedApplicationModule> GetMaterializedModules() =>
        _materializedModules.Keys
            .Select(name => (IDistributedApplicationModule)_modules[name])
            .ToArray();

    internal void MarkMaterialized(string moduleName, string materializationKey) =>
        _materializedModules.Add(moduleName, materializationKey);

    internal bool TryGetResource(string name, out IResource? resource) =>
        _resources.TryGetValue(name, out resource);

    internal void TrackResource(IResource resource)
    {
        _resources.TryAdd(resource.Name, resource);
    }

    internal ModuleRepositoryRequirement RegisterRepository(
        IDistributedApplicationBuilder builder,
        string moduleName,
        string repository,
        string? revision,
        bool updateRepository)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (_repositoryPlans is null)
        {
            _repositoryPlans = new ModuleRepositoryPlanRegistry(builder.AppHostDirectory);
            ModuleRepositoryInitializationPipeline.Configure(builder);
        }

        var plans = _repositoryPlans;
        var registration = plans.Register(
            moduleName,
            repository,
            revision,
            updateRepository);
        if (registration.IsNew)
        {
            var settings = new ModuleRepositoryInitializationSettings(
                GetConfiguredValue(Options.GitExecutablePath) ?? "git",
                GetConfiguredValue(Options.GitHubCliPath) ?? "gh",
                Options.RepositoryCommandTimeout);
            ModuleRepositoryInitializationPipeline.AddRepositoryStep(
                builder,
                registration.Requirement,
                () => settings);
        }

        return registration.Requirement;
    }

    internal void RequireFile(string moduleName, string description, string path) =>
        RequirePath(moduleName, description, path, ModuleRequiredPathKind.File);

    internal void RequireDirectory(string moduleName, string description, string path) =>
        RequirePath(moduleName, description, path, ModuleRequiredPathKind.Directory);

    internal Task ValidateRepositoryPreflightAsync(
        IModuleRepositoryStateStore stateStore,
        ModuleRepositoryInitializationSettings settings,
        string appHostPath,
        Microsoft.Extensions.Logging.ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        return ModuleRepositoryPreflight.ValidateAsync(
            RepositoryPlans?.Requirements ?? [],
            _requiredPaths,
            stateStore,
            settings,
            appHostPath,
            logger,
            cancellationToken);
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

    private void RequirePath(
        string moduleName,
        string description,
        string path,
        ModuleRequiredPathKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var key = $"{moduleName}\n{description}\n{kind}\n{fullPath}";
        if (_requiredPathKeys.Add(key))
        {
            _requiredPaths.Add(new ModuleRequiredPath(
                moduleName,
                description,
                fullPath,
                kind));
        }
    }

    private static string? GetConfiguredValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
