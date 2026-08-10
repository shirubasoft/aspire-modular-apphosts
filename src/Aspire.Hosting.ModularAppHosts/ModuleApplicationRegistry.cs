using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
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

[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "The registry and its operation gate live for the AppHost builder lifetime; disposing the gate could race active module operations.")]
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

    private readonly ConcurrentDictionary<string, RepositorySynchronization> _repositorySynchronizations =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private readonly List<Action<ModularAppHostsOptions>> _configurations = [];
    private readonly SemaphoreSlim _moduleOperationGate = new(1, 1);

    internal ModularAppHostsOptions Options { get; } = options ?? new ModularAppHostsOptions();

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

    internal async Task<T> RunModuleOperationAsync<T>(
        Func<Task<T>> operation,
        CancellationToken cancellationToken)
    {
        await _moduleOperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await operation().ConfigureAwait(false);
        }
        finally
        {
            _moduleOperationGate.Release();
        }
    }

    internal Task SynchronizeRepositoryAsync(
        string repositoryKey,
        RepositorySynchronizationPolicy policy,
        Func<Action<string>, Task> synchronize,
        Action<string>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryKey);
        ArgumentNullException.ThrowIfNull(synchronize);

        var normalizedKey = Path.GetFullPath(repositoryKey);
        var synchronization = _repositorySynchronizations.GetOrAdd(
            normalizedKey,
            _ => new RepositorySynchronization(policy, synchronize));
        synchronization.EnsureCompatiblePolicy(policy, normalizedKey);
        if (progress is not null)
        {
            synchronization.AttachProgress(progress);
        }

        return synchronization.Task;
    }

    internal void RefreshConfiguration()
    {
        ResetOptions();
        if (configuration is not null)
        {
            configuration.GetSection(ModularAppHostsOptions.ConfigurationSectionName).Bind(Options);
        }

        foreach (var configure in _configurations)
        {
            configure(Options);
        }

    }

    internal void Configure(Action<ModularAppHostsOptions> configure)
    {
        _configurations.Add(configure);
        try
        {
            RefreshConfiguration();
        }
        catch
        {
            _configurations.RemoveAt(_configurations.Count - 1);
            RefreshConfiguration();
            throw;
        }
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

internal sealed class RepositorySynchronization
{
    private readonly object _progressGate = new();
    private readonly List<string> _progressBacklog = [];
    private readonly Lazy<Task> _task;
    private readonly RepositorySynchronizationPolicy _policy;
    private Action<string>? _progress;

    public RepositorySynchronization(
        RepositorySynchronizationPolicy policy,
        Func<Action<string>, Task> synchronize)
    {
        ArgumentNullException.ThrowIfNull(synchronize);
        _policy = policy;
        _task = new Lazy<Task>(
            () => synchronize(ReportProgress),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public Task Task => _task.Value;

    public void EnsureCompatiblePolicy(
        RepositorySynchronizationPolicy policy,
        string repositoryPath)
    {
        if (_policy != policy)
        {
            throw new InvalidOperationException(
                $"Modules sharing repository '{repositoryPath}' configure conflicting update or revision policies. " +
                $"All modules using the same checkout must use the same {nameof(DistributedApplicationModuleOptions.UpdateRepository)} " +
                $"and {nameof(DistributedApplicationModuleOptions.RepositoryRevision)} values.");
        }
    }

    public void AttachProgress(Action<string> progress)
    {
        ArgumentNullException.ThrowIfNull(progress);

        lock (_progressGate)
        {
            if (_progress is not null)
            {
                return;
            }

            _progress = progress;
            foreach (var line in _progressBacklog)
            {
                progress(line);
            }

            _progressBacklog.Clear();
        }
    }

    private void ReportProgress(string line)
    {
        lock (_progressGate)
        {
            if (_progress is null)
            {
                _progressBacklog.Add(line);
            }
            else
            {
                _progress(line);
            }
        }
    }
}

internal readonly record struct RepositorySynchronizationPolicy(bool UpdateRepository, string? Revision)
{
    public static RepositorySynchronizationPolicy Create(bool updateRepository, string? revision)
    {
        var normalizedRevision = string.IsNullOrWhiteSpace(revision) ? null : revision.Trim();
        return new RepositorySynchronizationPolicy(
            normalizedRevision is null && updateRepository,
            normalizedRevision);
    }
}
