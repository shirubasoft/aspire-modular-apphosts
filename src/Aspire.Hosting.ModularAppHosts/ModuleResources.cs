using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

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

    /// <summary>Gets the effective image reference prepared by this installer and any chained retag step.</summary>
    public string ImageReference { get; } = imageReference;

    /// <summary>Gets whether the repository was dirty when the module was materialized.</summary>
    public bool RepositoryDirty { get; } = repositoryDirty;
}

internal sealed class ModuleImageRetagResource(
    string name,
    string containerRuntime,
    string workingDirectory,
    string sourceImageReference,
    string targetImageReference)
    : ExecutableResource(name, containerRuntime, workingDirectory)
{
    public string SourceImageReference { get; } = sourceImageReference;

    public string TargetImageReference { get; } = targetImageReference;
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

    /// <summary>Gets whether the resource came from <c>ImportModuleAsync</c>.</summary>
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
