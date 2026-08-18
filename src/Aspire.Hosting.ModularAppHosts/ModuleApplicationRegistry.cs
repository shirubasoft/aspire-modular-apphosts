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
    ModuleRepositoryPlanRegistry? repositoryPlans = null,
    ModuleProjectModeSwitchingPipeline? projectModeSwitching = null)
    : IDistributedApplicationModuleCatalog
{
    private readonly Dictionary<string, DistributedApplicationModule> _modules =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, IResource> _resources =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, string> _materializedModules =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly List<ModuleRequiredPath> _requiredPaths = [];
    private readonly Dictionary<string, int> _requiredPathIndices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ModuleRepositoryPreflightScopeBuilder> _preflightScopes =
        new(StringComparer.OrdinalIgnoreCase);
    private ModuleRepositoryPlanRegistry? _repositoryPlans = repositoryPlans;

    internal ModularAppHostsOptions Options { get; } = options ?? new ModularAppHostsOptions();

    internal ModuleRepositoryPlanRegistry? RepositoryPlans => _repositoryPlans;

    internal ModuleProjectModeSwitchingPipeline? ProjectModeSwitching { get; } = projectModeSwitching;

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
        bool updateRepository,
        bool requiredOnRun = true,
        string? checkoutDirectoryName = null,
        string? checkoutDirectoryNameConfigurationKey = null,
        string? resourceName = null,
        string? requirementName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (_repositoryPlans is null)
        {
            _repositoryPlans = new ModuleRepositoryPlanRegistry(builder.AppHostDirectory);
            ModuleRepositoryInitializationPipeline.Configure(builder);
        }

        var plans = _repositoryPlans;
        var registration = plans.Register(
            requirementName ?? moduleName,
            repository,
            revision,
            updateRepository,
            requiredOnRun,
            checkoutDirectoryName,
            checkoutDirectoryNameConfigurationKey);
        if (requiredOnRun)
        {
            GetOrCreatePreflightScope(moduleName, resourceName)
                .Repositories.Add(registration.Requirement);
        }

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

    internal void RequireFile(
        string moduleName,
        string description,
        string path,
        bool requiredOnRun = true,
        string? resourceName = null) =>
        RequirePath(
            moduleName,
            description,
            path,
            ModuleRequiredPathKind.File,
            requiredOnRun,
            resourceName);

    internal void RequireDirectory(
        string moduleName,
        string description,
        string path,
        bool requiredOnRun = true,
        string? resourceName = null) =>
        RequirePath(
            moduleName,
            description,
            path,
            ModuleRequiredPathKind.Directory,
            requiredOnRun,
            resourceName);

    internal ModuleRepositoryPreflightScope GetRepositoryPreflightScope(
        string moduleName,
        string resourceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        var repositories = new HashSet<ModuleRepositoryRequirement>();
        var requiredPaths = new HashSet<ModuleRequiredPath>();
        AddScope(moduleName, resourceName: null, repositories, requiredPaths);
        AddScope(moduleName, resourceName, repositories, requiredPaths);
        return new ModuleRepositoryPreflightScope(
            repositories.ToArray(),
            requiredPaths.ToArray());
    }

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

    internal static Task ValidateRepositoryPreflightAsync(
        ModuleRepositoryPreflightScope scope,
        IModuleRepositoryStateStore stateStore,
        ModuleRepositoryInitializationSettings settings,
        string appHostPath,
        Microsoft.Extensions.Logging.ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return ModuleRepositoryPreflight.ValidateAsync(
            scope.Repositories,
            scope.RequiredPaths,
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
        ModuleRequiredPathKind kind,
        bool requiredOnRun,
        string? resourceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var key = $"{moduleName}\n{description}\n{kind}\n{fullPath}";
        if (_requiredPathIndices.TryGetValue(key, out var index))
        {
            if (requiredOnRun && !_requiredPaths[index].RequiredOnRun)
            {
                _requiredPaths[index] = _requiredPaths[index] with { RequiredOnRun = true };
            }

            if (requiredOnRun)
            {
                GetOrCreatePreflightScope(moduleName, resourceName)
                    .RequiredPaths.Add(_requiredPaths[index]);
            }

            return;
        }

        _requiredPathIndices.Add(key, _requiredPaths.Count);
        var requiredPath = new ModuleRequiredPath(
            moduleName,
            description,
            fullPath,
            kind,
            requiredOnRun);
        _requiredPaths.Add(requiredPath);
        if (requiredOnRun)
        {
            GetOrCreatePreflightScope(moduleName, resourceName)
                .RequiredPaths.Add(requiredPath);
        }
    }

    private ModuleRepositoryPreflightScopeBuilder GetOrCreatePreflightScope(
        string moduleName,
        string? resourceName)
    {
        var key = GetPreflightScopeKey(moduleName, resourceName);
        if (!_preflightScopes.TryGetValue(key, out var scope))
        {
            scope = new ModuleRepositoryPreflightScopeBuilder();
            _preflightScopes.Add(key, scope);
        }

        return scope;
    }

    private void AddScope(
        string moduleName,
        string? resourceName,
        HashSet<ModuleRepositoryRequirement> repositories,
        HashSet<ModuleRequiredPath> requiredPaths)
    {
        if (!_preflightScopes.TryGetValue(GetPreflightScopeKey(moduleName, resourceName), out var scope))
        {
            return;
        }

        repositories.UnionWith(scope.Repositories);
        requiredPaths.UnionWith(scope.RequiredPaths);
    }

    private static string GetPreflightScopeKey(string moduleName, string? resourceName) =>
        $"{moduleName}\n{resourceName ?? string.Empty}";

    private static string? GetConfiguredValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

internal sealed record ModuleRepositoryPreflightScope(
    IReadOnlyCollection<ModuleRepositoryRequirement> Repositories,
    IReadOnlyCollection<ModuleRequiredPath> RequiredPaths);

internal sealed class ModuleRepositoryPreflightScopeBuilder
{
    public HashSet<ModuleRepositoryRequirement> Repositories { get; } = [];

    public HashSet<ModuleRequiredPath> RequiredPaths { get; } = [];
}
