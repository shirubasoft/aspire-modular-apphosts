using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.ModularAppHosts;

/// <summary>A one-shot Git repository installer displayed as a child of an imported service.</summary>
public sealed class ModuleRepositoryInstallerResource(
    string name,
    string repositoryPath,
    string? repository,
    bool updatesRepository,
    string publishCommand,
    IReadOnlyList<string> publishArguments,
    string workingDirectory,
    string imageReference,
    bool repositoryDirty)
    : ExecutableResource(name, publishCommand, workingDirectory)
{
    /// <summary>Gets the local repository path.</summary>
    public string RepositoryPath { get; } = repositoryPath;

    /// <summary>Gets the configured Git remote.</summary>
    public string? Repository { get; } = repository;

    /// <summary>Gets whether the installer pulls updates for an existing clean worktree.</summary>
    public bool UpdatesRepository { get; } = updatesRepository;

    /// <summary>Gets the caller-supplied image publish executable.</summary>
    public string PublishCommand { get; } = publishCommand;

    /// <summary>Gets the effective image publish arguments after image placeholders are resolved.</summary>
    public IReadOnlyList<string> PublishArguments { get; } = publishArguments;

    /// <summary>Gets the effective image reference produced by this installer.</summary>
    public string ImageReference { get; } = imageReference;

    /// <summary>Gets whether the repository was dirty when the module was materialized.</summary>
    public bool RepositoryDirty { get; } = repositoryDirty;
}

/// <summary>Associates a materialized resource with its module definition.</summary>
public sealed class DistributedApplicationModuleResourceAnnotation(
    string moduleName,
    string resourceName,
    string repositoryPath,
    bool imported) : IResourceAnnotation
{
    /// <summary>Gets the module name.</summary>
    public string ModuleName { get; } = moduleName;

    /// <summary>Gets the exported resource name.</summary>
    public string ResourceName { get; } = resourceName;

    /// <summary>Gets the exported resource name.</summary>
    [Obsolete($"Use {nameof(ResourceName)} instead.")]
    public string ProjectName => ResourceName;

    /// <summary>Gets the worktree used by the service installer.</summary>
    public string RepositoryPath { get; } = repositoryPath;

    /// <summary>Gets whether the resource came from <c>ImportModule</c>.</summary>
    public bool Imported { get; } = imported;
}

/// <summary>Links a module service to the one-shot installer that prepares its repository.</summary>
public sealed class ModuleRepositoryInstallerAnnotation(ModuleRepositoryInstallerResource installer) : IResourceAnnotation
{
    /// <summary>Gets the installer resource.</summary>
    public ModuleRepositoryInstallerResource Installer { get; } = installer;
}
