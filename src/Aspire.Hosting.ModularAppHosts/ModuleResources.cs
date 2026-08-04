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
    string workingDirectory)
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

    /// <summary>Gets the caller-supplied image publish arguments.</summary>
    public IReadOnlyList<string> PublishArguments { get; } = publishArguments;
}

/// <summary>Associates a materialized resource with its module definition.</summary>
public sealed class DistributedApplicationModuleResourceAnnotation(
    string moduleName,
    string projectName,
    string repositoryPath,
    bool imported) : IResourceAnnotation
{
    /// <summary>Gets the module name.</summary>
    public string ModuleName { get; } = moduleName;

    /// <summary>Gets the project name.</summary>
    public string ProjectName { get; } = projectName;

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
