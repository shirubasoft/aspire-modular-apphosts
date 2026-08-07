using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.ModularAppHosts;

internal sealed class DistributedApplicationModule(
    IDistributedApplicationBuilder definitionApplicationBuilder,
    string name,
    string version) : IDistributedApplicationModule
{
    private readonly List<IDistributedApplicationModuleResource> _resources = [];
    private readonly List<DistributedApplicationModuleProject> _projects = [];
    private readonly List<DistributedApplicationModuleContainer> _containers = [];
    private readonly Dictionary<string, IResource> _materializedResources =
        new(StringComparer.OrdinalIgnoreCase);
    private IDistributedApplicationBuilder? _materializedApplicationBuilder;

    public string Name { get; } = name;

    public string Version { get; } = version;

    public IReadOnlyList<IDistributedApplicationModuleResource> Resources => _resources;

    public IReadOnlyList<IDistributedApplicationModuleProject> Projects => _projects;

    public IReadOnlyList<IDistributedApplicationModuleContainer> Containers => _containers;

    internal IReadOnlyList<DistributedApplicationModuleProject> ProjectDefinitions => _projects;

    internal IReadOnlyList<DistributedApplicationModuleContainer> ContainerDefinitions => _containers;

    internal IReadOnlyList<IDistributedApplicationModuleResource> ResourceDefinitions => _resources;

    internal string? Repository { get; set; }

    internal string? RepositoryRevision { get; set; }

    internal IDistributedApplicationBuilder DefinitionApplicationBuilder { get; } = definitionApplicationBuilder;

    internal bool RequiresRepositoryContent { get; set; }

    internal bool ExplicitlyRequiresRepositoryContent { get; set; }

    internal void AddProject(DistributedApplicationModuleProject project)
    {
        ThrowIfNameIsAlreadyUsed(project.Name);
        _projects.Add(project);
        _resources.Add(project);
    }

    internal void AddContainer(DistributedApplicationModuleContainer container)
    {
        ThrowIfNameIsAlreadyUsed(container.Name);
        _containers.Add(container);
        _resources.Add(container);
    }

    internal void AddResource<TResource>(DistributedApplicationModuleResource<TResource> resource)
        where TResource : IResource
    {
        ThrowIfNameIsAlreadyUsed(resource.Name);
        _resources.Add(resource);
    }

    public IResourceBuilder<TResource> GetResource<TResource>(string name)
        where TResource : IResource
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (_materializedApplicationBuilder is null)
        {
            throw new InvalidOperationException(
                $"Module '{Name}' has not been materialized. Await AddAsync(module) or ImportModuleAsync('{Name}') first.");
        }

        if (!_materializedResources.TryGetValue(name, out var resource))
        {
            throw new KeyNotFoundException($"Module '{Name}' does not contain a materialized resource named '{name}'.");
        }

        if (resource is not TResource typedResource)
        {
            throw new InvalidOperationException(
                $"Module resource '{name}' is '{resource.GetType().Name}', not '{typeof(TResource).Name}'.");
        }

        return _materializedApplicationBuilder.CreateResourceBuilder(typedResource);
    }

    internal void TrackMaterializedResource(
        IDistributedApplicationBuilder builder,
        string declaredName,
        IResource resource)
    {
        _materializedApplicationBuilder = builder;
        _materializedResources[declaredName] = resource;
    }

    internal IResourceBuilder<TResource> GetResourceForCallback<TResource>(
        string name,
        string requestingResourceName)
        where TResource : IResource
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (!_materializedResources.TryGetValue(name, out var resource))
        {
            if (_resources.Any(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    $"Module resource '{requestingResourceName}' cannot resolve '{name}' because module resources " +
                    "are materialized in declaration order. Declare the dependency before the consuming resource.");
            }

            throw new KeyNotFoundException($"Module '{Name}' does not declare a resource named '{name}'.");
        }

        if (resource is not TResource typedResource)
        {
            throw new InvalidOperationException(
                $"Module resource '{name}' is '{resource.GetType().Name}', not '{typeof(TResource).Name}'.");
        }

        return _materializedApplicationBuilder!.CreateResourceBuilder(typedResource);
    }

    internal async Task ValidateAsync(
        string gitExecutablePath,
        TimeSpan repositoryCommandTimeout,
        CancellationToken cancellationToken)
    {
        if (_resources.Count == 0)
        {
            throw new InvalidOperationException($"Module '{Name}' does not contain any resources.");
        }

        var notExported = _projects.FirstOrDefault(project => !project.IsExportedAsContainer);
        if (notExported is not null)
        {
            throw new InvalidOperationException(
                $"Project '{notExported.Name}' in module '{Name}' must call ExportAsContainer().");
        }

        var appHostDirectory = Path.GetFullPath(DefinitionApplicationBuilder.AppHostDirectory);
        foreach (var project in _projects)
        {
            if (project.PathBase == ModuleProjectPathBase.Repository)
            {
                project.SourceRepositoryRoot = GetDefinitionRepositoryRoot(
                    Repository,
                    appHostDirectory);
                continue;
            }

            var repositoryRoot = await RepositoryInspector.FindRepositoryRootAsync(
                project.ProjectPath,
                gitExecutablePath,
                repositoryCommandTimeout,
                cancellationToken).ConfigureAwait(false);
            var configuredRepositoryRoot = await TryGetConfiguredLocalRepositoryRootAsync(
                Repository,
                appHostDirectory,
                project.ProjectPath,
                gitExecutablePath,
                repositoryCommandTimeout,
                cancellationToken).ConfigureAwait(false);

            if (configuredRepositoryRoot is not null)
            {
                repositoryRoot = configuredRepositoryRoot;
            }
            else if (!await RepositoryInspector.IsGitRepositoryAsync(
                    repositoryRoot,
                    gitExecutablePath,
                    repositoryCommandTimeout,
                    requireSuccessfulInspection: true,
                    cancellationToken).ConfigureAwait(false) &&
                PathSafety.IsContainedBy(appHostDirectory, project.ProjectPath))
            {
                repositoryRoot = appHostDirectory;
            }

            project.SourceRepositoryRoot = repositoryRoot;
        }

        var repositoryRoots = _projects
            .Select(project => project.SourceRepositoryRoot)
            .Where(repositoryRoot => repositoryRoot is not null)
            .Select(repositoryRoot => repositoryRoot!)
            .Distinct(PathSafety.Comparer)
            .ToArray();

        if (repositoryRoots.Length > 1)
        {
            throw new InvalidOperationException(
                $"All projects in module '{Name}' must belong to the same Git repository or source tree.");
        }

        if (repositoryRoots.Length == 1)
        {
            Repository ??= await RepositoryInspector.TryGetRemoteAsync(
                repositoryRoots[0],
                gitExecutablePath,
                repositoryCommandTimeout,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static string? GetDefinitionRepositoryRoot(
        string? repository,
        string appHostDirectory)
    {
        if (!string.IsNullOrWhiteSpace(repository) &&
            !GitHubRepositoryCloner.IsRemoteRepository(repository, appHostDirectory))
        {
            return Path.GetFullPath(repository, appHostDirectory);
        }

        return null;
    }

    private static async Task<string?> TryGetConfiguredLocalRepositoryRootAsync(
        string? repository,
        string appHostDirectory,
        string projectPath,
        string gitExecutablePath,
        TimeSpan repositoryCommandTimeout,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(repository))
        {
            return null;
        }

        string candidate;
        if (GitHubRepositoryCloner.IsRemoteRepository(repository, appHostDirectory))
        {
            var appHostRepositoryRoot = await RepositoryInspector.TryFindRepositoryRootAsync(
                appHostDirectory,
                gitExecutablePath,
                repositoryCommandTimeout,
                cancellationToken).ConfigureAwait(false);
            if (appHostRepositoryRoot is null)
            {
                return null;
            }

            var repositoryParent = Path.GetDirectoryName(appHostRepositoryRoot);
            if (repositoryParent is null)
            {
                return null;
            }

            candidate = Path.Combine(
                repositoryParent,
                GitHubRepositoryCloner.GetRepositoryDirectoryName(repository));
        }
        else
        {
            candidate = Path.GetFullPath(repository, appHostDirectory);
        }

        return PathSafety.IsContainedBy(candidate, projectPath)
            ? candidate
            : null;
    }

    private void ThrowIfNameIsAlreadyUsed(string name)
    {
        if (_resources.Any(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Module '{Name}' already contains a resource named '{name}'.");
        }
    }
}

internal sealed class DistributedApplicationModuleProject(
    string name,
    string projectPath,
    ModuleProjectPathBase pathBase,
    string? sourceRepositoryRoot) : IDistributedApplicationModuleProject
{
    public string Name { get; } = name;

    public Type ResourceType => typeof(IResourceWithEndpoints);

    public string ProjectPath { get; } = projectPath;

    internal ModuleProjectPathBase PathBase { get; } = pathBase;

    public bool IsExportedAsContainer => Export is not null;

    internal string? SourceRepositoryRoot { get; set; } = sourceRepositoryRoot;

    internal Action<IDistributedApplicationModuleResourceContext, IResourceBuilder<ProjectResource>>? ConfigureProject { get; set; }

    internal string GetRepositoryRelativeProjectPath()
    {
        return PathBase == ModuleProjectPathBase.Repository
            ? ProjectPath
            : Path.GetRelativePath(SourceRepositoryRoot!, ProjectPath);
    }

    internal ModuleContainerExport Export => _export
        ?? throw new InvalidOperationException($"Project '{Name}' has not been exported as a container.");

    private ModuleContainerExport? _export;

    internal void SetExport(ModuleContainerExport export)
    {
        _export = export;
    }
}

internal sealed record ModuleContainerExport(
    ModuleContainerExportOptions Options,
    Action<IDistributedApplicationModuleResourceContext, IResourceBuilder<ContainerResource>>? ConfigureContainer);

internal sealed class DistributedApplicationModuleContainer(
    string name,
    string image,
    string tag) : IDistributedApplicationModuleContainer
{
    public string Name { get; } = name;

    public Type ResourceType => typeof(ContainerResource);

    public string Image { get; } = image;

    public string Tag { get; } = tag;

    internal Action<IDistributedApplicationModuleResourceContext, IResourceBuilder<ContainerResource>>? ConfigureContainer { get; set; }

    internal ModuleContainerExportOptions? ImagePublishOptions { get; private set; }

    internal void SetImagePublishOptions(ModuleContainerExportOptions options)
    {
        if (ImagePublishOptions is not null)
        {
            throw new InvalidOperationException(
                $"Container '{Name}' already has an image publish command.");
        }

        ImagePublishOptions = options;
    }
}

internal interface IDistributedApplicationModuleFactoryResource : IDistributedApplicationModuleResource
{
    ModuleContainerExportOptions? ImagePublishOptions { get; }

    IResource Materialize(
        IDistributedApplicationModuleResourceContext context,
        DistributedApplicationModuleResourceAnnotation annotation);
}

internal sealed class DistributedApplicationModuleResource<TResource>(
    string name,
    Func<IDistributedApplicationModuleResourceContext, IResourceBuilder<TResource>> resourceFactory,
    ModuleContainerExportOptions? imagePublishOptions)
    : IDistributedApplicationModuleFactoryResource
    where TResource : IResource
{
    public string Name { get; } = name;

    public Type ResourceType => typeof(TResource);

    public ModuleContainerExportOptions? ImagePublishOptions { get; } = imagePublishOptions;

    public IResource Materialize(
        IDistributedApplicationModuleResourceContext context,
        DistributedApplicationModuleResourceAnnotation annotation)
    {
        var resource = resourceFactory(context)
            ?? throw new InvalidOperationException($"The factory for module resource '{Name}' returned null.");

        if (!string.Equals(resource.Resource.Name, context.ResourceName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The factory for module resource '{Name}' returned a resource named '{resource.Resource.Name}'. " +
                "Use context.ResourceName when creating the resource.");
        }

        resource.WithAnnotation(annotation);
        return resource.Resource;
    }
}

internal sealed class DistributedApplicationModuleResourceContext(
    IDistributedApplicationBuilder applicationBuilder,
    DistributedApplicationModule module,
    string resourceName,
    string repositoryPath,
    bool imported,
    ModuleResourceImage? image = null) : IDistributedApplicationModuleResourceContext
{
    public IDistributedApplicationBuilder ApplicationBuilder { get; } = applicationBuilder;

    public string ResourceName { get; } = resourceName;

    public string RepositoryPath { get; } = repositoryPath;

    public bool Imported { get; } = imported;

    public ModuleResourceImage? Image { get; } = image;

    public IResourceBuilder<TResource> GetResource<TResource>(string name)
        where TResource : IResource => module.GetResourceForCallback<TResource>(name, ResourceName);
}
