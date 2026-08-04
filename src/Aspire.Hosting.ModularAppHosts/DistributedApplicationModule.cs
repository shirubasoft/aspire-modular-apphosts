using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.ModularAppHosts;

internal sealed class DistributedApplicationModule(string name) : IDistributedApplicationModule
{
    private readonly List<DistributedApplicationModuleProject> _projects = [];
    private readonly List<DistributedApplicationModuleContainer> _containers = [];
    private readonly Dictionary<string, IResource> _materializedResources =
        new(StringComparer.OrdinalIgnoreCase);
    private IDistributedApplicationBuilder? _materializedApplicationBuilder;

    public string Name { get; } = name;

    public IReadOnlyList<IDistributedApplicationModuleProject> Projects => _projects;

    public IReadOnlyList<IDistributedApplicationModuleContainer> Containers => _containers;

    internal IReadOnlyList<DistributedApplicationModuleProject> ProjectDefinitions => _projects;

    internal IReadOnlyList<DistributedApplicationModuleContainer> ContainerDefinitions => _containers;

    internal string? Repository { get; set; }

    internal void AddProject(DistributedApplicationModuleProject project)
    {
        ThrowIfNameIsAlreadyUsed(project.Name);
        _projects.Add(project);
    }

    internal void AddContainer(DistributedApplicationModuleContainer container)
    {
        ThrowIfNameIsAlreadyUsed(container.Name);
        _containers.Add(container);
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
        if (_projects.Count == 0 && _containers.Count == 0)
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
        if (_projects.Any(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase)) ||
            _containers.Any(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase)))
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

    public string ProjectPath { get; } = projectPath;

    public bool IsExportedAsContainer => Export is not null;

    internal string SourceRepositoryRoot { get; } = sourceRepositoryRoot;

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

    public string Image { get; } = image;

    public string Tag { get; } = tag;

    internal Action<IResourceBuilder<ContainerResource>>? ConfigureContainer { get; set; }
}
