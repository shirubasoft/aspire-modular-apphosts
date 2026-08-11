using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>A managed one-shot resource that prepares a module image before application resources start.</summary>
public sealed class ModuleRepositoryInstallerResource
    : Resource
{
    internal ModuleRepositoryInstallerResource(
        string name,
        ModuleImagePublisherAnnotation publisher)
        : base(name)
    {
        Publisher = publisher;
    }

    internal ModuleImagePublisherAnnotation Publisher { get; }

    /// <summary>Gets the local repository path.</summary>
    public string RepositoryPath => Publisher.Recipe.RepositoryPath;

    /// <summary>Gets the configured Git remote.</summary>
    public string? Repository => Publisher.Recipe.Repository;

    /// <summary>Gets whether the installer pulls updates for an existing clean worktree.</summary>
    public bool UpdatesRepository => Publisher.Recipe.RefreshCleanCheckout;

    /// <summary>Gets the caller-supplied image publish executable.</summary>
    public string PublishCommand => Publisher.Recipe.Options.PublishCommand;

    /// <summary>Gets the declared image publish arguments; placeholders are resolved at execution time.</summary>
    public IReadOnlyList<string> PublishArguments => Publisher.Recipe.Options.PublishArguments;

    /// <summary>Gets the stable local image alias prepared by this installer.</summary>
    public string ImageReference => Publisher.Recipe.LocalImageReference;
}

/// <summary>Associates a materialized resource with its module definition.</summary>
public sealed class DistributedApplicationModuleResourceAnnotation(
    string moduleName,
    string resourceName,
    string repositoryPath,
    bool imported,
    string? packageId = null) : IResourceAnnotation
{
    /// <summary>Gets the module name.</summary>
    public string ModuleName { get; } = moduleName;

    /// <summary>Gets the exported resource name.</summary>
    public string ResourceName { get; } = resourceName;

    /// <summary>Gets the exported resource name.</summary>
    [Obsolete($"Use {nameof(ResourceName)} instead.")]
    public string ProjectName => ResourceName;

    /// <summary>Gets the module-definition worktree associated with the resource.</summary>
    public string RepositoryPath { get; } = repositoryPath;

    /// <summary>Gets whether the resource came from <c>ImportModule</c>.</summary>
    public bool Imported { get; } = imported;

    /// <summary>Gets the NuGet package ID that publishes the owning module contract, when declared.</summary>
    public string? PackageId { get; } = packageId;
}

/// <summary>Links a module service to the one-shot installer that prepares its repository.</summary>
public sealed class ModuleRepositoryInstallerAnnotation(ModuleRepositoryInstallerResource installer) : IResourceAnnotation
{
    /// <summary>Gets the installer resource.</summary>
    public ModuleRepositoryInstallerResource Installer { get; } = installer;
}
