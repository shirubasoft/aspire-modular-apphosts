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
        var repositoryRoot = Path.GetDirectoryName(absoluteProjectPath)
            ?? throw new InvalidOperationException($"Unable to determine the directory for '{absoluteProjectPath}'.");

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
        return new DistributedApplicationModuleContainerBuilder(module, container);
    }

    public IDistributedApplicationModuleBuilder WithRepository(string repository)
    {
        return SetRepository(repository, revision: null);
    }

    public IDistributedApplicationModuleBuilder WithRepository(string repository, string revision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(revision);
        return SetRepository(repository, revision);
    }

    public IDistributedApplicationModuleBuilder RequiresRepository()
    {
        module.RequiresRepositoryContent = true;
        module.ExplicitlyRequiresRepositoryContent = true;
        return this;
    }

    private DistributedApplicationModuleBuilder SetRepository(string repository, string? revision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        module.Repository = repository;
        module.RepositoryRevision = string.IsNullOrWhiteSpace(revision) ? null : revision.Trim();
        return this;
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
    DistributedApplicationModule module,
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
            (!string.IsNullOrWhiteSpace(copiedOptions.ImageTag) &&
                !string.Equals(container.Tag, copiedOptions.ImageTag, StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                $"The explicitly configured publish image '{copiedOptions.ImageName}:{copiedOptions.ImageTag}' must match " +
                $"the container image '{container.Image}:{container.Tag}'.",
                nameof(options));
        }

        container.SetImagePublishOptions(copiedOptions);
        module.RequiresRepositoryContent = true;
        return this;
    }
}
