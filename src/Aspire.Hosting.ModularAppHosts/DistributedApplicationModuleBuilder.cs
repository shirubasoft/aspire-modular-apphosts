using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting;

internal sealed class DistributedApplicationModuleBuilder(
    IDistributedApplicationBuilder applicationBuilder,
    DistributedApplicationModule module,
    ModuleApplicationRegistry registry) : IDistributedApplicationModuleBuilder
{
    public IConfiguration Configuration => applicationBuilder.Configuration;

    public IConfigurationSection ConfigurationSection => Configuration.GetSection(
        DistributedApplicationModuleExtensions.GetModuleConfigurationKey(module.Name));

    public IOptions<TOptions> GetOptions<TOptions>()
        where TOptions : class, new()
    {
        var options = new TOptions();
        ConfigurationSection.Bind(options);
        return Options.Create(options);
    }

    public IDistributedApplicationModule GetRequiredModule(string name, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        if (!registry.TryGetDefinition(name, out var referencedModule) || referencedModule is null)
        {
            throw new InvalidOperationException(
                $"Module '{module.Name}' requires module '{name}' with contract version '{version}', but it has not " +
                "been defined. Add or import the required module first.");
        }

        if (!string.Equals(referencedModule.Version, version, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Module '{module.Name}' requires module '{name}' with contract version '{version}', but version " +
                $"'{referencedModule.Version}' is defined.");
        }

        return referencedModule;
    }

    public IDistributedApplicationModuleBuilder AddResource<TResource>(
        string name,
        Func<IDistributedApplicationModuleResourceContext, IResourceBuilder<TResource>> resourceFactory)
        where TResource : IResource
    {
        return AddResourceCore(name, resourceFactory, imagePublishOptions: null);
    }

    public IDistributedApplicationModuleBuilder AddResource<TResource>(
        string name,
        Func<IDistributedApplicationModuleResourceContext, IResourceBuilder<TResource>> resourceFactory,
        ModuleImageCommandOptions imagePublishOptions)
        where TResource : ContainerResource
    {
        ArgumentNullException.ThrowIfNull(imagePublishOptions);
        return AddResourceCore(
            name,
            resourceFactory,
            DistributedApplicationModuleProjectBuilder.CopyOptions(imagePublishOptions));
    }

    private DistributedApplicationModuleBuilder AddResourceCore<TResource>(
        string name,
        Func<IDistributedApplicationModuleResourceContext, IResourceBuilder<TResource>> resourceFactory,
        ModuleImageCommandOptions? imagePublishOptions)
        where TResource : IResource
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(resourceFactory);

        module.AddResource(new DistributedApplicationModuleResource<TResource>(
            name,
            resourceFactory,
            imagePublishOptions));
        return this;
    }

    public IDistributedApplicationModuleProjectBuilder AddProject<TProject>(string name)
        where TProject : IProjectMetadata, new()
    {
        return AddProject(name, new TProject().ProjectPath);
    }

    public IDistributedApplicationModuleProjectBuilder AddProject(string name, string projectPath)
    {
        return AddProject(name, projectPath, ModuleProjectPathBase.AppHost);
    }

    public IDistributedApplicationModuleProjectBuilder AddProject(
        string name,
        string projectPath,
        ModuleProjectPathBase pathBase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        if (!Enum.IsDefined(pathBase))
        {
            throw new ArgumentOutOfRangeException(nameof(pathBase));
        }

        if (pathBase == ModuleProjectPathBase.Repository && Path.IsPathRooted(projectPath))
        {
            throw new ArgumentException(
                "A repository-relative module project path cannot be rooted.",
                nameof(projectPath));
        }

        var declaredProjectPath = pathBase == ModuleProjectPathBase.AppHost
            ? Path.GetFullPath(projectPath, applicationBuilder.AppHostDirectory)
            : projectPath;
        var repositoryRoot = pathBase == ModuleProjectPathBase.AppHost
            ? Path.GetDirectoryName(declaredProjectPath)
                ?? throw new InvalidOperationException(
                    $"Unable to determine the directory for '{declaredProjectPath}'.")
            : null;

        var project = new DistributedApplicationModuleProject(
            name,
            declaredProjectPath,
            pathBase,
            repositoryRoot);
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
        Action<IDistributedApplicationModuleResourceContext, IResourceBuilder<ProjectResource>> configureProject)
    {
        ArgumentNullException.ThrowIfNull(configureProject);
        project.ConfigureProject += configureProject;
        return this;
    }

    public IDistributedApplicationModuleProjectBuilder ExportAsContainer(
        string imageName,
        Action<IDistributedApplicationModuleResourceContext, IResourceBuilder<ContainerResource>>? configureContainer = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageName);
        project.SetExport(new ModuleContainerExport(imageName.Trim(), CommandOptions: null, configureContainer));
        return this;
    }

    public IDistributedApplicationModuleProjectBuilder ExportAsContainerWithCommand(
        ModuleImageCommandOptions options,
        Action<IDistributedApplicationModuleResourceContext, IResourceBuilder<ContainerResource>>? configureContainer = null)
    {
        var copiedOptions = CopyOptions(options);
        project.SetExport(new ModuleContainerExport(copiedOptions.ImageName, copiedOptions, configureContainer));
        return this;
    }

    internal static ModuleImageCommandOptions CopyOptions(ModuleImageCommandOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ImageName);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.PublishCommand);

        return new ModuleImageCommandOptions(
            options.ImageName,
            options.PublishCommand,
            options.PublishArguments.ToArray())
        {
            ImageRegistry = options.ImageRegistry,
            ProducedImageReference = options.ProducedImageReference,
            PullBeforeBuild = options.PullBeforeBuild,
            ImageTag = options.ImageTag,
            WorkingDirectory = options.WorkingDirectory,
            BuildRepository = options.BuildRepository,
            BuildRepositoryRevision = options.BuildRepositoryRevision
        };
    }
}

internal sealed class DistributedApplicationModuleContainerBuilder(
    DistributedApplicationModuleContainer container) : IDistributedApplicationModuleContainerBuilder
{
    public IDistributedApplicationModuleContainer Container => container;

    public IDistributedApplicationModuleContainerBuilder Configure(
        Action<IDistributedApplicationModuleResourceContext, IResourceBuilder<ContainerResource>> configureContainer)
    {
        ArgumentNullException.ThrowIfNull(configureContainer);
        container.ConfigureContainer += configureContainer;
        return this;
    }

    public IDistributedApplicationModuleContainerBuilder WithImagePublishCommand(
        ModuleImageCommandOptions options)
    {
        var copiedOptions = DistributedApplicationModuleProjectBuilder.CopyOptions(options);
        var imageRepository = ModuleImageReference.GetRepository(copiedOptions);
        var imageMatches = string.Equals(container.Image, copiedOptions.ImageName, StringComparison.Ordinal) ||
            string.Equals(container.Image, imageRepository, StringComparison.Ordinal);
        if (!imageMatches ||
            (!string.IsNullOrWhiteSpace(copiedOptions.ImageTag) &&
                !string.Equals(container.Tag, copiedOptions.ImageTag, StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                $"The explicitly configured publish image '{imageRepository}:{copiedOptions.ImageTag}' must match " +
                $"the container image '{container.Image}:{container.Tag}'.",
                nameof(options));
        }

        container.SetImagePublishOptions(copiedOptions);
        return this;
    }
}
