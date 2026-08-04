using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.ModularAppHosts;

internal sealed class DistributedApplicationModule(string name) : IDistributedApplicationModule
{
    private readonly List<IDistributedApplicationModuleResource> _resources = [];
    private readonly List<DistributedApplicationModuleProject> _projects = [];
    private readonly List<DistributedApplicationModuleContainer> _containers = [];
    private readonly Dictionary<string, IResource> _materializedResources =
        new(StringComparer.OrdinalIgnoreCase);
    private IDistributedApplicationBuilder? _materializedApplicationBuilder;

    public string Name { get; } = name;

    public IReadOnlyList<IDistributedApplicationModuleResource> Resources => _resources;

    public IReadOnlyList<IDistributedApplicationModuleProject> Projects => _projects;

    public IReadOnlyList<IDistributedApplicationModuleContainer> Containers => _containers;

    internal IReadOnlyList<DistributedApplicationModuleProject> ProjectDefinitions => _projects;

    internal IReadOnlyList<DistributedApplicationModuleContainer> ContainerDefinitions => _containers;

    internal IReadOnlyList<IDistributedApplicationModuleResource> ResourceDefinitions => _resources;

    internal string? Repository { get; set; }

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
                $"Module '{Name}' has not been materialized. Call Add(module) or ImportModule('{Name}') first.");
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

    internal void TrackMaterializedResource(IDistributedApplicationBuilder builder, IResource resource)
    {
        _materializedApplicationBuilder = builder;
        _materializedResources[resource.Name] = resource;
    }

    internal void Validate()
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

        var repositoryRoots = _projects
            .Select(project => project.SourceRepositoryRoot)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (repositoryRoots.Length > 1)
        {
            throw new InvalidOperationException(
                $"All projects in module '{Name}' must belong to the same Git repository or source tree.");
        }

        if (repositoryRoots.Length == 1)
        {
            Repository ??= RepositoryInspector.TryGetRemote(repositoryRoots[0]);
        }
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
    string sourceRepositoryRoot) : IDistributedApplicationModuleProject
{
    public string Name { get; } = name;

    public Type ResourceType => typeof(IResourceWithEndpoints);

    public string ProjectPath { get; } = projectPath;

    public bool IsExportedAsContainer => Export is not null;

    internal string SourceRepositoryRoot { get; } = sourceRepositoryRoot;

    internal Action<IResourceBuilder<ProjectResource>>? ConfigureProject { get; set; }

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
    Action<IResourceBuilder<ContainerResource>>? ConfigureContainer);

internal sealed class DistributedApplicationModuleContainer(
    string name,
    string image,
    string tag) : IDistributedApplicationModuleContainer
{
    public string Name { get; } = name;

    public Type ResourceType => typeof(ContainerResource);

    public string Image { get; } = image;

    public string Tag { get; } = tag;

    internal Action<IResourceBuilder<ContainerResource>>? ConfigureContainer { get; set; }

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
    IResource Materialize(
        IDistributedApplicationModuleResourceContext context,
        DistributedApplicationModuleResourceAnnotation annotation);
}

internal sealed class DistributedApplicationModuleResource<TResource>(
    string name,
    Func<IDistributedApplicationModuleResourceContext, IResourceBuilder<TResource>> resourceFactory)
    : IDistributedApplicationModuleFactoryResource
    where TResource : IResource
{
    public string Name { get; } = name;

    public Type ResourceType => typeof(TResource);

    public IResource Materialize(
        IDistributedApplicationModuleResourceContext context,
        DistributedApplicationModuleResourceAnnotation annotation)
    {
        var resource = resourceFactory(context)
            ?? throw new InvalidOperationException($"The factory for module resource '{Name}' returned null.");

        if (!string.Equals(resource.Resource.Name, Name, StringComparison.OrdinalIgnoreCase))
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
    bool imported) : IDistributedApplicationModuleResourceContext
{
    public IDistributedApplicationBuilder ApplicationBuilder { get; } = applicationBuilder;

    public string ResourceName { get; } = resourceName;

    public string RepositoryPath { get; } = repositoryPath;

    public bool Imported { get; } = imported;

    public IResourceBuilder<TResource> GetResource<TResource>(string name)
        where TResource : IResource => module.GetResource<TResource>(name);
}
