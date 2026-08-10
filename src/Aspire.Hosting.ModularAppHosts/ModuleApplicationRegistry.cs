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

    private readonly Dictionary<string, ModulePreviewSelection> _previewSelections =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, ModulePreviewImageArtifact> _previewImages =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, string> _fullControlPreviewTags =
        new(StringComparer.OrdinalIgnoreCase);

    private FullControlModulePreviewSource? _fullControlPreviewSource;

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

    internal void ApplyPreviewManifest(ModulePreviewManifest manifest, string baseDirectory)
    {
        EnsureFullControlPreviewIsNotApplied();
        ApplyPreviewSelections(manifest.Modules, baseDirectory);
        RefreshConfiguration();
    }

    internal void ApplyPreviewResolution(ModulePreviewResolution resolution, string baseDirectory)
    {
        EnsureFullControlPreviewIsNotApplied();
        ApplyPreviewSelections(resolution.Modules, baseDirectory);

        foreach (var image in resolution.Images)
        {
            var key = GetPreviewImageKey(image.Module, image.Resource);
            if (_previewImages.TryGetValue(key, out var existing) &&
                (!string.Equals(existing.Repository, image.Repository, StringComparison.Ordinal) ||
                 !string.Equals(existing.Sha256, image.Sha256, StringComparison.Ordinal) ||
                 existing.ResourceKind != image.ResourceKind))
            {
                throw new InvalidOperationException(
                    $"Module '{image.Module}' resource '{image.Resource}' already has a different preview image.");
            }
        }

        foreach (var image in resolution.Images)
        {
            _previewImages[GetPreviewImageKey(image.Module, image.Resource)] = new ModulePreviewImageArtifact
            {
                Module = image.Module,
                Resource = image.Resource,
                ResourceKind = image.ResourceKind,
                Repository = image.Repository,
                Sha256 = image.Sha256
            };
        }

        RefreshConfiguration();
    }

    internal void ApplyFullControlPreview(
        FullControlModulePreviewManifest manifest,
        FullControlModulePreviewSource source,
        string baseDirectory)
    {
        if (_materializedModules.Count > 0)
        {
            throw new InvalidOperationException(
                "A full-control module preview must be applied before importing or adding modules.");
        }

        if (_previewSelections.Count > 0 || _previewImages.Count > 0)
        {
            throw new InvalidOperationException(
                "A full-control module preview cannot be combined with an immutable module preview manifest or resolution.");
        }

        var resolvedTags = manifest.ResolveContainerTags(source.Ref);
        if (_fullControlPreviewSource is not null &&
            (!GitHubRepositoryCloner.RefersToSameRepository(
                _fullControlPreviewSource.Repository,
                source.Repository,
                baseDirectory) ||
             !string.Equals(_fullControlPreviewSource.Ref, source.Ref, StringComparison.Ordinal) ||
             !DictionaryEquals(_fullControlPreviewTags, resolvedTags)))
        {
            throw new InvalidOperationException(
                "A different full-control module preview has already been applied to this AppHost builder.");
        }

        _fullControlPreviewSource = new FullControlModulePreviewSource
        {
            Repository = source.Repository,
            Ref = source.Ref
        };
        _fullControlPreviewTags.Clear();
        foreach (var (resource, tag) in resolvedTags)
        {
            _fullControlPreviewTags.Add(resource, tag);
        }
    }

    internal void ValidatePreviewSelection(DistributedApplicationModule module, string baseDirectory)
    {
        if (_previewSelections.TryGetValue(module.Name, out var selection) &&
            !string.IsNullOrWhiteSpace(module.Repository) &&
            GitHubRepositoryCloner.IsRemoteRepository(module.Repository, baseDirectory) &&
            !GitHubRepositoryCloner.RefersToSameRepository(
                selection.Repository,
                module.Repository,
                baseDirectory))
        {
            throw new InvalidOperationException(
                $"Preview module '{module.Name}' selects repository '{selection.Repository}', but its " +
                $"contract declares repository '{module.Repository}'.");
        }

        foreach (var image in _previewImages.Values.Where(
                     image => string.Equals(image.Module, module.Name, StringComparison.OrdinalIgnoreCase)))
        {
            var resource = module.ResourceDefinitions.FirstOrDefault(
                candidate => string.Equals(candidate.Name, image.Resource, StringComparison.OrdinalIgnoreCase));
            if (resource is null)
            {
                throw new InvalidOperationException(
                    $"Preview image selects resource '{image.Resource}' in module '{module.Name}', but its " +
                    "contract does not declare that resource.");
            }

            var actualKind = resource switch
            {
                DistributedApplicationModuleProject => ModulePreviewResourceKind.Project,
                DistributedApplicationModuleContainer => ModulePreviewResourceKind.Container,
                IDistributedApplicationModuleFactoryResource { ImagePublishOptions: not null } =>
                    ModulePreviewResourceKind.Container,
                _ => (ModulePreviewResourceKind?)null
            };
            if (actualKind != image.ResourceKind)
            {
                var declaredKind = actualKind?.ToString() ?? resource.GetType().Name;
                throw new InvalidOperationException(
                    $"Preview image selects resource '{image.Resource}' in module '{module.Name}' as " +
                    $"'{image.ResourceKind}', but its contract declares kind '{declaredKind}'.");
            }
        }
    }

    internal bool TryGetPreviewSelection(string moduleName, out ModulePreviewSelection? selection) =>
        _previewSelections.TryGetValue(moduleName, out selection);

    internal bool CanMaterializePreviewWithoutRepository(DistributedApplicationModule module)
    {
        return CanMaterializePreviewWithoutRepository(module, resourceNames: null);
    }

    internal bool CanMaterializePreviewWithoutRepository(
        DistributedApplicationModule module,
        ModuleResourceNameMap? resourceNames)
    {
        var images = _previewImages.Values
            .Where(image => string.Equals(image.Module, module.Name, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(image => image.Resource, StringComparer.OrdinalIgnoreCase);
        if (module.ExplicitlyRequiresRepositoryContent)
        {
            return false;
        }

        bool HasFullControlTag(string resource) =>
            resourceNames is not null &&
            _fullControlPreviewTags.ContainsKey(resourceNames[resource]);

        var projectsAreDigestSatisfied = module.ProjectDefinitions.All(project =>
            (images.TryGetValue(project.Name, out var image) &&
             image.ResourceKind == ModulePreviewResourceKind.Project) ||
            HasFullControlTag(project.Name));
        var publishableContainersAreDigestSatisfied = module.ContainerDefinitions
            .Where(container => container.ImagePublishOptions is not null)
            .All(container =>
                (images.TryGetValue(container.Name, out var image) &&
                 image.ResourceKind == ModulePreviewResourceKind.Container) ||
                HasFullControlTag(container.Name));
        var publishableFactoryResourcesAreDigestSatisfied = module.ResourceDefinitions
            .OfType<IDistributedApplicationModuleFactoryResource>()
            .Where(resource => resource.ImagePublishOptions is not null)
            .All(resource =>
                (images.TryGetValue(resource.Name, out var image) &&
                 image.ResourceKind == ModulePreviewResourceKind.Container) ||
                HasFullControlTag(resource.Name));
        var hasRepositoryIndependentPreviewImage = (images.Count > 0 ||
            (resourceNames is not null && module.ResourceDefinitions.Any(resource => HasFullControlTag(resource.Name)))) &&
            (module.ProjectDefinitions.Count > 0 ||
                module.ContainerDefinitions.Count > 0 ||
                module.ResourceDefinitions.OfType<IDistributedApplicationModuleFactoryResource>()
                    .Any(resource => resource.ImagePublishOptions is not null));
        return hasRepositoryIndependentPreviewImage &&
            projectsAreDigestSatisfied &&
            publishableContainersAreDigestSatisfied &&
            publishableFactoryResourcesAreDigestSatisfied;
    }

    internal void ApplyFullControlPreviewOptions(
        DistributedApplicationModule module,
        ModuleResourceNameMap resourceNames)
    {
        foreach (var definition in module.ResourceDefinitions)
        {
            if (!_fullControlPreviewTags.TryGetValue(resourceNames[definition.Name], out var tag))
            {
                continue;
            }

            if (!Options.Modules.TryGetValue(module.Name, out var moduleOptions))
            {
                moduleOptions = new DistributedApplicationModuleOptions();
                Options.Modules.Add(module.Name, moduleOptions);
            }

            DistributedApplicationModuleImageOptions imageOptions;
            if (definition is DistributedApplicationModuleProject)
            {
                if (!moduleOptions.Projects.TryGetValue(definition.Name, out var projectOptions))
                {
                    projectOptions = new DistributedApplicationModuleProjectOptions();
                    moduleOptions.Projects.Add(definition.Name, projectOptions);
                }

                projectOptions.ProjectMode = ModuleProjectMode.Container;
                imageOptions = projectOptions;
            }
            else
            {
                if (!moduleOptions.Containers.TryGetValue(definition.Name, out var containerOptions))
                {
                    containerOptions = new DistributedApplicationModuleContainerOptions();
                    moduleOptions.Containers.Add(definition.Name, containerOptions);
                }

                imageOptions = containerOptions;
            }

            imageOptions.ImageTag = tag;
            imageOptions.ImageSHA256 = null;
            imageOptions.PublishImage = false;
        }
    }

    internal void ValidateAndApplyFullControlPreview(
        DistributedApplicationModel model,
        string baseDirectory)
    {
        if (_fullControlPreviewSource is null)
        {
            return;
        }

        var allowedRepositories = _modules.Values
            .Select(module => module.Repository)
            .Where(repository => !string.IsNullOrWhiteSpace(repository))
            .Select(repository => repository!)
            .Where(repository => GitHubRepositoryCloner.IsRemoteRepository(repository, baseDirectory))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (!allowedRepositories.Any(repository => GitHubRepositoryCloner.RefersToSameRepository(
                _fullControlPreviewSource.Repository,
                repository,
                baseDirectory)))
        {
            var allowed = allowedRepositories.Length == 0
                ? "(none)"
                : string.Join(", ", allowedRepositories.Select(repository => $"'{repository}'"));
            throw new InvalidOperationException(
                $"Full-control preview source repository '{_fullControlPreviewSource.Repository}' is not declared " +
                $"by an AppHost module. Allowed repositories: {allowed}.");
        }

        ApplyFullControlPreviewTags(model.Resources, requireEveryOverride: true);
    }

    internal void ApplyFullControlPreviewTags(
        IEnumerable<IResource> modelResources,
        bool requireEveryOverride = false)
    {
        var resources = modelResources.ToDictionary(resource => resource.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var (resourceName, tag) in _fullControlPreviewTags)
        {
            if (!resources.TryGetValue(resourceName, out var resource))
            {
                if (requireEveryOverride)
                {
                    throw new InvalidOperationException(
                        $"Full-control preview selects unknown AppHost resource '{resourceName}'.");
                }

                continue;
            }

            if (resource is not ContainerResource container)
            {
                throw new InvalidOperationException(
                    $"Full-control preview resource '{resourceName}' is not a container resource.");
            }

            var image = container.Annotations.OfType<ContainerImageAnnotation>().LastOrDefault()
                ?? throw new InvalidOperationException(
                    $"Full-control preview resource '{resourceName}' does not declare a container image.");
            image.SHA256 = null;
            image.Tag = tag;
        }
    }

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

        foreach (var selection in _previewSelections.Values)
        {
            if (!Options.Modules.TryGetValue(selection.Name, out var moduleOptions))
            {
                moduleOptions = new DistributedApplicationModuleOptions();
                Options.Modules.Add(selection.Name, moduleOptions);
            }

            moduleOptions.Repository = selection.Repository;
            moduleOptions.RepositoryRevision = selection.Commit;
        }

        foreach (var image in _previewImages.Values)
        {
            if (!Options.Modules.TryGetValue(image.Module, out var moduleOptions))
            {
                moduleOptions = new DistributedApplicationModuleOptions();
                Options.Modules.Add(image.Module, moduleOptions);
            }

            DistributedApplicationModuleImageOptions imageOptions;
            if (image.ResourceKind == ModulePreviewResourceKind.Project)
            {
                if (!moduleOptions.Projects.TryGetValue(image.Resource, out var projectOptions))
                {
                    projectOptions = new DistributedApplicationModuleProjectOptions();
                    moduleOptions.Projects.Add(image.Resource, projectOptions);
                }

                projectOptions.ProjectMode = ModuleProjectMode.Container;
                imageOptions = projectOptions;
            }
            else
            {
                if (!moduleOptions.Containers.TryGetValue(image.Resource, out var containerOptions))
                {
                    containerOptions = new DistributedApplicationModuleContainerOptions();
                    moduleOptions.Containers.Add(image.Resource, containerOptions);
                }

                imageOptions = containerOptions;
            }

            var (registry, name) = ModuleImageReference.ParseRepository(image.Repository);
            imageOptions.ImageRegistry = registry ?? string.Empty;
            imageOptions.ImageName = name;
            imageOptions.ImageSHA256 = image.Sha256;
            imageOptions.PublishImage = false;
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

    private void ApplyPreviewSelections(
        IEnumerable<ModulePreviewSelection> selections,
        string baseDirectory)
    {
        var selectionArray = selections.ToArray();
        if (_materializedModules.Count > 0)
        {
            throw new InvalidOperationException(
                "A module preview manifest or resolution must be applied before importing or adding modules.");
        }

        foreach (var selection in selectionArray)
        {
            if (_previewSelections.TryGetValue(selection.Name, out var existing) &&
                (!GitHubRepositoryCloner.RefersToSameRepository(
                    existing.Repository,
                    selection.Repository,
                    baseDirectory) ||
                 !string.Equals(existing.Commit, selection.Commit, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    $"Module '{selection.Name}' already has a different preview selection.");
            }
        }

        foreach (var selection in selectionArray)
        {
            _previewSelections[selection.Name] = new ModulePreviewSelection
            {
                Name = selection.Name,
                Repository = selection.Repository,
                Commit = selection.Commit,
                Branch = selection.Branch,
                BaseRef = selection.BaseRef,
                BaseCommit = selection.BaseCommit
            };
        }
    }

    private static string GetPreviewImageKey(string module, string resource) => $"{module}\0{resource}";

    private void EnsureFullControlPreviewIsNotApplied()
    {
        if (_fullControlPreviewSource is not null)
        {
            throw new InvalidOperationException(
                "An immutable module preview manifest or resolution cannot be combined with a full-control module preview.");
        }
    }

    private static bool DictionaryEquals(
        Dictionary<string, string> first,
        IReadOnlyDictionary<string, string> second) =>
        first.Count == second.Count && first.All(pair =>
            second.TryGetValue(pair.Key, out var value) &&
            string.Equals(pair.Value, value, StringComparison.Ordinal));

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
