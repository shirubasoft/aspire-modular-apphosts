using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.ModularAppHosts;

/// <summary>
/// A reusable group of Aspire resources that can be added locally or imported from its Git repository.
/// </summary>
public interface IDistributedApplicationModule
{
    /// <summary>Gets the module name.</summary>
    string Name { get; }

    /// <summary>Gets the module contract version.</summary>
    string Version { get; }

    /// <summary>Gets every resource exported by the module in declaration order.</summary>
    IReadOnlyList<IDistributedApplicationModuleResource> Resources { get; }

    /// <summary>Gets the projects exported by the module.</summary>
    IReadOnlyList<IDistributedApplicationModuleProject> Projects { get; }

    /// <summary>Gets the containers exported by the module.</summary>
    IReadOnlyList<IDistributedApplicationModuleContainer> Containers { get; }

    /// <summary>Gets a materialized module resource by name.</summary>
    IResourceBuilder<TResource> GetResource<TResource>(string name)
        where TResource : IResource;
}

/// <summary>Controls resource names when a module is imported into an AppHost.</summary>
public sealed class ModuleImportOptions
{
    /// <summary>Gets or sets a prefix prepended to every imported Aspire resource name.</summary>
    public string? ResourcePrefix { get; set; }

    /// <summary>Gets resource-name overrides keyed by the name declared in the module contract.</summary>
    public IDictionary<string, string> ResourceAliases { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Builds an <see cref="IDistributedApplicationModule"/> definition.</summary>
public interface IDistributedApplicationModuleBuilder
{
    /// <summary>
    /// Adds any Aspire resource type through a factory that runs when the module is materialized.
    /// </summary>
    IDistributedApplicationModuleBuilder AddResource<TResource>(
        string name,
        Func<IDistributedApplicationModuleResourceContext, IResourceBuilder<TResource>> resourceFactory)
        where TResource : IResource;

    /// <summary>Adds a generated Aspire project reference to the module.</summary>
    IDistributedApplicationModuleProjectBuilder AddProject<TProject>(string name)
        where TProject : IProjectMetadata, new();

    /// <summary>Adds a project path to the module.</summary>
    IDistributedApplicationModuleProjectBuilder AddProject(string name, string projectPath);

    /// <summary>Adds an existing container image to the module.</summary>
    IDistributedApplicationModuleContainerBuilder AddContainer(
        string name,
        string image,
        string tag = "latest");

    /// <summary>
    /// Overrides the Git repository used by <c>ImportModule</c>. When omitted, the origin remote is inferred
    /// from the projects' common Git worktree.
    /// </summary>
    IDistributedApplicationModuleBuilder WithRepository(string repository);

    /// <summary>Overrides the Git repository and pins its imported branch, tag, or commit.</summary>
    IDistributedApplicationModuleBuilder WithRepository(string repository, string revision);
}

/// <summary>Describes a resource exported by a distributed application module.</summary>
public interface IDistributedApplicationModuleResource
{
    /// <summary>Gets the Aspire resource name.</summary>
    string Name { get; }

    /// <summary>Gets the resource type returned by the materialization factory.</summary>
    Type ResourceType { get; }
}

/// <summary>Provides state to a generic resource factory when its module is materialized.</summary>
public interface IDistributedApplicationModuleResourceContext
{
    /// <summary>Gets the AppHost builder receiving the resource.</summary>
    IDistributedApplicationBuilder ApplicationBuilder { get; }

    /// <summary>Gets the declared name that the factory must assign to its resource.</summary>
    string ResourceName { get; }

    /// <summary>Gets the local source or managed repository path for this module.</summary>
    string RepositoryPath { get; }

    /// <summary>Gets whether the module is being imported rather than added from local source.</summary>
    bool Imported { get; }

    /// <summary>Gets a previously materialized resource exported by the same module.</summary>
    IResourceBuilder<TResource> GetResource<TResource>(string name)
        where TResource : IResource;
}

/// <summary>A container contained in a distributed application module.</summary>
public interface IDistributedApplicationModuleContainer : IDistributedApplicationModuleResource
{
    /// <summary>Gets the container image name.</summary>
    string Image { get; }

    /// <summary>Gets the container image tag.</summary>
    string Tag { get; }
}

/// <summary>Configures one existing container in a module.</summary>
public interface IDistributedApplicationModuleContainerBuilder
{
    /// <summary>Gets the container being configured.</summary>
    IDistributedApplicationModuleContainer Container { get; }

    /// <summary>Applies Aspire container-resource configuration when the module is materialized.</summary>
    IDistributedApplicationModuleContainerBuilder Configure(
        Action<IResourceBuilder<ContainerResource>> configureContainer);

    /// <summary>Publishes the container image with an explicit command before the container starts.</summary>
    IDistributedApplicationModuleContainerBuilder WithImagePublishCommand(
        ModuleContainerExportOptions options);
}

/// <summary>A project contained in a distributed application module.</summary>
public interface IDistributedApplicationModuleProject : IDistributedApplicationModuleResource
{
    /// <summary>Gets the source project path used when the module is added locally.</summary>
    string ProjectPath { get; }

    /// <summary>Gets whether this project is exported as a container.</summary>
    bool IsExportedAsContainer { get; }
}

/// <summary>Configures one project in a module.</summary>
public interface IDistributedApplicationModuleProjectBuilder
{
    /// <summary>Gets the project being configured.</summary>
    IDistributedApplicationModuleProject Project { get; }

    /// <summary>Applies Aspire project-resource configuration when the project runs directly.</summary>
    IDistributedApplicationModuleProjectBuilder ConfigureProject(
        Action<IResourceBuilder<ProjectResource>> configureProject);

    /// <summary>Exports the project as a container built by the supplied publish command.</summary>
    IDistributedApplicationModuleProjectBuilder ExportAsContainer(
        string imageName,
        string publishCommand,
        IReadOnlyList<string> publishArguments,
        Action<IResourceBuilder<ContainerResource>>? configureContainer = null);

    /// <summary>Exports the project as a container with explicit publish settings.</summary>
    IDistributedApplicationModuleProjectBuilder ExportAsContainer(
        ModuleContainerExportOptions options,
        Action<IResourceBuilder<ContainerResource>>? configureContainer = null);
}

/// <summary>Controls how a module project is converted into a container resource.</summary>
public sealed class ModuleContainerExportOptions(
    string imageName,
    string publishCommand,
    params string[] publishArguments)
{
    /// <summary>Placeholder for the effective image name in a publish argument.</summary>
    public const string ImageNamePlaceholder = "{image-name}";

    /// <summary>Placeholder for the effective image tag in a publish argument.</summary>
    public const string ImageTagPlaceholder = "{image-tag}";

    /// <summary>Placeholder for the complete effective image reference in a publish argument.</summary>
    public const string ImageReferencePlaceholder = "{image}";

    /// <summary>Gets the image name that the publish command must create.</summary>
    public string ImageName { get; } = imageName;

    /// <summary>Gets the executable invoked by the service installer.</summary>
    public string PublishCommand { get; } = publishCommand;

    /// <summary>
    /// Gets the arguments supplied to <see cref="PublishCommand"/>. Image placeholders are resolved before invocation.
    /// </summary>
    public IReadOnlyList<string> PublishArguments { get; } = publishArguments;

    /// <summary>
    /// Gets or sets the clean image tag. When omitted, the current repository branch is sanitized and used.
    /// The effective tag has <c>-dirty</c> appended for a dirty repository.
    /// </summary>
    public string? ImageTag { get; set; }

    /// <summary>
    /// Gets or sets the publish working directory relative to the repository root. The project directory is used
    /// when this is not set.
    /// </summary>
    public string? WorkingDirectory { get; set; }
}
