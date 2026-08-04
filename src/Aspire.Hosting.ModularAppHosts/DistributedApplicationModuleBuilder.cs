using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.ModularAppHosts;

internal sealed class DistributedApplicationModuleBuilder(
    IDistributedApplicationBuilder applicationBuilder,
    DistributedApplicationModule module) : IDistributedApplicationModuleBuilder
{
    public IDistributedApplicationModuleBuilder AddResource<TResource>(
        string name,
        Func<IDistributedApplicationModuleResourceContext, IResourceBuilder<TResource>> resourceFactory)
        where TResource : IResource
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(resourceFactory);

        module.AddResource(new DistributedApplicationModuleResource<TResource>(name, resourceFactory));
        return this;
    }

    public IDistributedApplicationModuleProjectBuilder AddProject<TProject>(string name)
        where TProject : IProjectMetadata, new()
    {
        return AddProject(name, new TProject().ProjectPath);
    }

    public IDistributedApplicationModuleProjectBuilder AddProject(string name, string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);

        var absoluteProjectPath = Path.GetFullPath(projectPath, applicationBuilder.AppHostDirectory);
        var repositoryRoot = RepositoryInspector.FindRepositoryRoot(absoluteProjectPath);
        var appHostDirectory = Path.GetFullPath(applicationBuilder.AppHostDirectory);
        var configuredRepositoryRoot = GetConfiguredLocalRepositoryRoot(
            module.Repository,
            appHostDirectory,
            absoluteProjectPath);

        if (configuredRepositoryRoot is not null)
        {
            repositoryRoot = configuredRepositoryRoot;
        }
        else if (!RepositoryInspector.IsGitRepository(repositoryRoot) &&
            IsContainedBy(appHostDirectory, absoluteProjectPath))
        {
            repositoryRoot = appHostDirectory;
        }

        var project = new DistributedApplicationModuleProject(name, absoluteProjectPath, repositoryRoot);
        module.AddProject(project);
        return new DistributedApplicationModuleProjectBuilder(project);
    }

    public IDistributedApplicationModuleContainerBuilder AddContainer(
        string name,
        string image,
        string tag = "latest")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(image);
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);

        var container = new DistributedApplicationModuleContainer(name, image, tag);
        module.AddContainer(container);
        return new DistributedApplicationModuleContainerBuilder(container);
    }

    public IDistributedApplicationModuleBuilder WithRepository(string repository)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        module.Repository = repository;
        return this;
    }

    private static string? GetConfiguredLocalRepositoryRoot(
        string? repository,
        string appHostDirectory,
        string projectPath)
    {
        if (string.IsNullOrWhiteSpace(repository))
        {
            return null;
        }

        if (Uri.TryCreate(repository, UriKind.Absolute, out var repositoryUri) && !repositoryUri.IsFile)
        {
            return null;
        }

        var candidate = Path.GetFullPath(repository, appHostDirectory);
        return Directory.Exists(candidate) && IsContainedBy(candidate, projectPath)
            ? candidate
            : null;
    }

    private static bool IsContainedBy(string root, string path)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedPath = Path.GetFullPath(path);
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class DistributedApplicationModuleProjectBuilder(DistributedApplicationModuleProject project)
    : IDistributedApplicationModuleProjectBuilder
{
    public IDistributedApplicationModuleProject Project => project;

    public IDistributedApplicationModuleProjectBuilder ConfigureProject(
        Action<IResourceBuilder<ProjectResource>> configureProject)
    {
        ArgumentNullException.ThrowIfNull(configureProject);
        project.ConfigureProject += configureProject;
        return this;
    }

    public IDistributedApplicationModuleProjectBuilder ExportAsContainer(
        string imageName,
        string publishCommand,
        IReadOnlyList<string> publishArguments,
        Action<IResourceBuilder<ContainerResource>>? configureContainer = null)
    {
        ArgumentNullException.ThrowIfNull(publishArguments);
        return ExportAsContainer(
            new ModuleContainerExportOptions(imageName, publishCommand, publishArguments.ToArray()),
            configureContainer);
    }

    public IDistributedApplicationModuleProjectBuilder ExportAsContainer(
        ModuleContainerExportOptions options,
        Action<IResourceBuilder<ContainerResource>>? configureContainer = null)
    {
        project.SetExport(new ModuleContainerExport(CopyOptions(options), configureContainer));
        return this;
    }

    internal static ModuleContainerExportOptions CopyOptions(ModuleContainerExportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ImageName);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.PublishCommand);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ImageTag);

        return new ModuleContainerExportOptions(
            options.ImageName,
            options.PublishCommand,
            options.PublishArguments.ToArray())
        {
            ImageTag = options.ImageTag,
            WorkingDirectory = options.WorkingDirectory
        };
    }
}

internal sealed class DistributedApplicationModuleContainerBuilder(
    DistributedApplicationModuleContainer container) : IDistributedApplicationModuleContainerBuilder
{
    public IDistributedApplicationModuleContainer Container => container;

    public IDistributedApplicationModuleContainerBuilder Configure(
        Action<IResourceBuilder<ContainerResource>> configureContainer)
    {
        ArgumentNullException.ThrowIfNull(configureContainer);
        container.ConfigureContainer += configureContainer;
        return this;
    }

    public IDistributedApplicationModuleContainerBuilder WithImagePublishCommand(
        ModuleContainerExportOptions options)
    {
        var copiedOptions = DistributedApplicationModuleProjectBuilder.CopyOptions(options);
        if (!string.Equals(container.Image, copiedOptions.ImageName, StringComparison.Ordinal) ||
            !string.Equals(container.Tag, copiedOptions.ImageTag, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The publish image '{copiedOptions.ImageName}:{copiedOptions.ImageTag}' must match " +
                $"the container image '{container.Image}:{container.Tag}'.",
                nameof(options));
        }

        container.SetImagePublishOptions(copiedOptions);
        return this;
    }
}
