#pragma warning disable ASPIREPIPELINES003

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

public static partial class DistributedApplicationModuleExtensions
{
    private static readonly Action<ILogger, string, string, Exception?> LogImagePreparationFailed =
        LoggerMessage.Define<string, string>(
            LogLevel.Error,
            new EventId(1, nameof(LogImagePreparationFailed)),
            "Image preparation failed for module {ModuleName} resource {ResourceName}.");

    /// <summary>Adds a defined module using its local source checkout.</summary>
    public static IDistributedApplicationModule AddModule(
        this IDistributedApplicationBuilder builder,
        IDistributedApplicationModule module)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(module);
        if (module is not DistributedApplicationModule typedModule)
        {
            throw new ArgumentException(
                "The module must have been created by DefineModule or ExportModule on this extension.",
                nameof(module));
        }

        if (!ReferenceEquals(typedModule.DefinitionApplicationBuilder, builder))
        {
            throw new ArgumentException(
                "The module definition belongs to a different distributed application builder. " +
                "Define and materialize the module on the same AppHost builder.",
                nameof(module));
        }

        var registry = GetOrCreateRegistry(builder);
        if (!registry.TryGetDefinition(typedModule.Name, out _))
        {
            registry.AddModule(typedModule);
        }

        MaterializeModule(builder, typedModule, registry, imported: false);
        return module;
    }

    /// <summary>Imports a defined module by name.</summary>
    public static IDistributedApplicationModule ImportModule(
        this IDistributedApplicationBuilder builder,
        string name) =>
        ImportModule(builder, name, new ModuleImportOptions());

    /// <summary>Imports a defined module by name with resource aliases or a common prefix.</summary>
    public static IDistributedApplicationModule ImportModule(
        this IDistributedApplicationBuilder builder,
        string name,
        ModuleImportOptions importOptions)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(importOptions);

        var registry = GetOrCreateRegistry(builder);
        if (!registry.TryGetDefinition(name, out var module) || module is null)
        {
            throw new InvalidOperationException(
                $"Module '{name}' has not been defined. Call DefineModule or ExportModule before ImportModule.");
        }

        MaterializeModule(builder, module, registry, imported: true, importOptions);
        return module;
    }

    private static void MaterializeModule(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        ModuleApplicationRegistry registry,
        bool imported,
        ModuleImportOptions? importOptions = null)
    {
        ValidateOptions(registry.Options);
        var materializationKey = GetMaterializationKey(imported, importOptions);
        if (registry.TryGetMaterialization(module.Name, out var existingMaterialization))
        {
            if (string.Equals(existingMaterialization, materializationKey, StringComparison.Ordinal))
            {
                return;
            }

            throw new InvalidOperationException(
                $"Module '{module.Name}' is already materialized with different local/import or resource naming options.");
        }

        var options = registry.Options;
        var moduleOptions = options.FindModule(module.Name);
        ValidateModuleConfiguration(module, moduleOptions);
        var resourceNames = new ModuleResourceNameMap(module, imported ? importOptions : null);
        ValidateSynchronousResourceNames(
            builder,
            module,
            registry,
            options,
            moduleOptions,
            imported,
            resourceNames);

        var requiresRepository = RequiresRepositoryForSynchronousMaterialization(
            builder,
            module,
            options,
            moduleOptions,
            imported);
        var definitionRepository = ModuleMaterializationPlanning.ResolveDefinitionRepository(
            builder,
            module,
            registry,
            moduleOptions,
            imported,
            requiresRepository);

        foreach (var definition in module.ResourceDefinitions)
        {
            switch (definition)
            {
                case DistributedApplicationModuleProject project:
                    MaterializeProject(
                        builder,
                        module,
                        project,
                        resourceNames[project.Name],
                        definitionRepository,
                        imported,
                        registry,
                        options,
                        moduleOptions);
                    break;
                case DistributedApplicationModuleContainer container:
                    MaterializeContainer(
                        builder,
                        module,
                        container,
                        resourceNames[container.Name],
                        definitionRepository,
                        imported,
                        registry,
                        options,
                        moduleOptions);
                    break;
                case IDistributedApplicationModuleFactoryResource resource:
                    MaterializeFactoryResource(
                        builder,
                        module,
                        resource,
                        resourceNames[resource.Name],
                        definitionRepository,
                        imported,
                        registry,
                        options,
                        moduleOptions);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Module resource definition '{definition.Name}' has an unsupported implementation type.");
            }
        }

        registry.MarkMaterialized(module.Name, materializationKey);
    }

    private static void MaterializeProject(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        DistributedApplicationModuleProject project,
        string resourceName,
        ModuleRepositoryContext definitionRepository,
        bool imported,
        ModuleApplicationRegistry registry,
        ModularAppHostsOptions options,
        DistributedApplicationModuleOptions? moduleOptions)
    {
        var projectOptions = moduleOptions?.FindProject(project.Name);
        ValidatePublisherDigest(project.Name, project.IsExportedAsContainer, projectOptions);
        if (project.Export.CommandOptions is null && !UsesExternalImage(projectOptions))
        {
            MaterializeNativeProjectResource(
                builder,
                module,
                project,
                resourceName,
                projectOptions,
                definitionRepository.RepositoryPath,
                imported,
                registry);
            return;
        }

        var runAsContainer = !builder.ExecutionContext.IsRunMode ||
            ResolveProjectMode(options, moduleOptions, projectOptions, imported) == ModuleProjectMode.Container;
        if (!runAsContainer)
        {
            MaterializeProjectResourceSynchronously(
                builder,
                module,
                project,
                resourceName,
                projectOptions,
                definitionRepository.RepositoryPath,
                imported,
                registry);
            return;
        }

        var publisher = UsesExternalImage(projectOptions)
            ? null
            : CreateImagePublisher(
                builder,
                module,
                project.Name,
                ModuleResourceKind.Project,
                project.Export.CommandOptions!,
                projectOptions,
                definitionRepository,
                registry,
                options,
                moduleOptions,
                GetProjectWorkingDirectory(project),
                project.GetRepositoryRelativeProjectPath());
        var container = builder
            .AddContainer(
                resourceName,
                publisher?.Recipe.Options.ImageName ?? GetConfiguredValue(projectOptions?.ImageName) ?? project.Export.ImageName,
                publisher is null
                    ? GetConfiguredValue(projectOptions?.ImageTag) ??
                        GetConfiguredValue(project.Export.CommandOptions?.ImageTag) ??
                        "latest"
                    : ModuleImageBuildRecipe.LocalRunTag);
        ApplyImageRegistry(
            container,
            publisher?.Recipe.Options.ImageRegistry ?? GetConfiguredValue(projectOptions?.ImageRegistry));
        var image = CreateResourceImage(container, publisher, projectOptions);
        FinalizeContainerResource(
            builder,
            container,
            new NormalizedContainerDescriptor(
                new ModuleContainerIdentity(
                    module,
                    project.Name,
                    resourceName,
                    definitionRepository.RepositoryPath,
                    imported),
                new ModuleContainerImagePlan(
                    publisher,
                    projectOptions,
                    image,
                    publisher is null && !UsesExternalImage(projectOptions) ? null : image),
                project.Export.ConfigureContainer,
                ModuleAnnotationAlreadyApplied: false),
            registry);
    }

    private static void MaterializeContainer(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        DistributedApplicationModuleContainer definition,
        string resourceName,
        ModuleRepositoryContext definitionRepository,
        bool imported,
        ModuleApplicationRegistry registry,
        ModularAppHostsOptions options,
        DistributedApplicationModuleOptions? moduleOptions)
    {
        var configured = moduleOptions?.FindContainer(definition.Name);
        ValidatePublishOverrides(definition, configured);
        var publisher = definition.ImagePublishOptions is null || UsesExternalImage(configured)
            ? null
            : CreateImagePublisher(
                builder,
                module,
                definition.Name,
                ModuleResourceKind.Container,
                definition.ImagePublishOptions,
                configured,
                definitionRepository,
                registry,
                options,
                moduleOptions,
                defaultWorkingDirectory: ".");
        var container = builder
            .AddContainer(
                resourceName,
                publisher?.Recipe.Options.ImageName ?? GetConfiguredValue(configured?.ImageName) ?? definition.Image,
                publisher is null
                    ? GetConfiguredValue(configured?.ImageTag) ?? definition.Tag
                    : ModuleImageBuildRecipe.LocalRunTag);
        ApplyImageRegistry(
            container,
            publisher?.Recipe.Options.ImageRegistry ?? GetConfiguredValue(configured?.ImageRegistry));
        var image = CreateResourceImage(container, publisher, configured);
        FinalizeContainerResource(
            builder,
            container,
            new NormalizedContainerDescriptor(
                new ModuleContainerIdentity(
                    module,
                    definition.Name,
                    resourceName,
                    definitionRepository.RepositoryPath,
                    imported),
                new ModuleContainerImagePlan(
                    publisher,
                    configured,
                    image,
                    publisher is null && !UsesExternalImage(configured) ? null : image),
                definition.ConfigureContainer,
                ModuleAnnotationAlreadyApplied: false),
            registry);
    }

    private static void MaterializeFactoryResource(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        IDistributedApplicationModuleFactoryResource definition,
        string resourceName,
        ModuleRepositoryContext definitionRepository,
        bool imported,
        ModuleApplicationRegistry registry,
        ModularAppHostsOptions options,
        DistributedApplicationModuleOptions? moduleOptions)
    {
        var configured = moduleOptions?.FindContainer(definition.Name);
        ValidatePublishOverrides(
            definition.Name,
            definition.ImagePublishOptions is not null,
            configured,
            nameof(IDistributedApplicationModuleBuilder.AddResource));
        var publisher = definition.ImagePublishOptions is null || UsesExternalImage(configured)
            ? null
            : CreateImagePublisher(
                builder,
                module,
                definition.Name,
                ModuleResourceKind.Container,
                definition.ImagePublishOptions,
                configured,
                definitionRepository,
                registry,
                options,
                moduleOptions,
                defaultWorkingDirectory: ".");
        var resourceImage = CreateFactoryResourceImage(publisher, configured);
        var context = new DistributedApplicationModuleResourceContext(
            builder,
            module,
            resourceName,
            definitionRepository.RepositoryPath,
            imported,
            resourceImage);
        var moduleAnnotation = new DistributedApplicationModuleResourceAnnotation(
            module.Name,
            definition.Name,
            definitionRepository.RepositoryPath,
            imported,
            module.PackageId);
        IResource resource;
        try
        {
            resource = definition.Materialize(context, moduleAnnotation);
        }
        catch (DistributedApplicationException exception) when (
            typeof(ProjectResource).IsAssignableFrom(definition.ResourceType) &&
            definitionRepository.InitializerOwned &&
            !Directory.Exists(definitionRepository.RepositoryPath))
        {
            if (!builder.ExecutionContext.IsRunMode)
            {
                // Aspire adds a ProjectResource before it discovers launch settings. Keep that partial model in
                // publish mode so the initialization pipeline can create the repository that discovery needs.
                var deferredProject = builder.Resources
                    .OfType<ProjectResource>()
                    .LastOrDefault(candidate => string.Equals(
                        candidate.Name,
                        resourceName,
                        StringComparison.OrdinalIgnoreCase)) ??
                    builder.AddResource(new ProjectResource(resourceName)).Resource;
                builder.CreateResourceBuilder(deferredProject).WithAnnotation(moduleAnnotation);
                resource = deferredProject;
            }
            else
            {
                var initializeCommand = ModuleRepositoryPreflight.CreateInitializeCommand(builder.AppHostDirectory);
                throw new InvalidOperationException(
                    $"Module resource '{definition.Name}' requires repository " +
                    $"'{definitionRepository.Repository}' at '{definitionRepository.RepositoryPath}', but the " +
                    $"initializer-owned checkout is missing. Run '{initializeCommand}'.",
                    exception);
            }
        }
        if (resource is ContainerResource containerResource)
        {
            var container = builder.CreateResourceBuilder(containerResource);
            FinalizeContainerResource(
                builder,
                container,
                new NormalizedContainerDescriptor(
                    new ModuleContainerIdentity(
                        module,
                        definition.Name,
                        resourceName,
                        definitionRepository.RepositoryPath,
                        imported),
                    new ModuleContainerImagePlan(
                        publisher,
                        configured,
                        resourceImage,
                        resourceImage),
                    ConfigureContainer: null,
                    ModuleAnnotationAlreadyApplied: true),
                registry);
        }
        else if (publisher is not null)
        {
            throw new InvalidOperationException(
                $"Image-published module resource '{definition.Name}' did not create a container resource.");
        }

        if (resource is not ContainerResource)
        {
            ConfigureRepositoryLifecycle(
                builder,
                builder.CreateResourceBuilder(resource),
                registry);
        }
        if (resource is not ContainerResource)
        {
            registry.TrackResource(resource);
            module.TrackMaterializedResource(builder, definition.Name, resource);
        }
    }

    private static void FinalizeContainerResource(
        IDistributedApplicationBuilder builder,
        IResourceBuilder<ContainerResource> container,
        NormalizedContainerDescriptor descriptor,
        ModuleApplicationRegistry registry)
    {
        var identity = descriptor.Identity;
        if (!descriptor.ModuleAnnotationAlreadyApplied)
        {
            container.WithAnnotation(new DistributedApplicationModuleResourceAnnotation(
                identity.Module.Name,
                identity.DeclaredResourceName,
                identity.RepositoryPath,
                identity.Imported,
                identity.Module.PackageId));
        }

        var context = new DistributedApplicationModuleResourceContext(
            builder,
            identity.Module,
            identity.EffectiveResourceName,
            identity.RepositoryPath,
            identity.Imported,
            descriptor.Image.ContextImage);
        descriptor.ConfigureContainer?.Invoke(context, container);
        ConfigureNativeDockerfilePublisher(builder, container, descriptor, registry.Options);
        ConfigureRepositoryLifecycle(builder, container, registry);
        ConfigureContainerImage(
            builder,
            container,
            descriptor.Image.Publisher,
            descriptor.Image.Configured,
            descriptor.Image.OwnedImage);
        registry.TrackResource(container.Resource);
        identity.Module.TrackMaterializedResource(
            builder,
            identity.DeclaredResourceName,
            container.Resource);
    }

    private static void ConfigureNativeDockerfilePublisher(
        IDistributedApplicationBuilder builder,
        IResourceBuilder<ContainerResource> container,
        NormalizedContainerDescriptor descriptor,
        ModularAppHostsOptions options)
    {
        if (descriptor.Image.Publisher is not null ||
            UsesExternalImage(descriptor.Image.Configured) ||
            container.Resource.Annotations.OfType<ModuleNativeImagePublisherAnnotation>().Any())
        {
            return;
        }

        var dockerfile = container.Resource.Annotations
            .OfType<DockerfileBuildAnnotation>()
            .LastOrDefault();
        var image = container.Resource.Annotations
            .OfType<ContainerImageAnnotation>()
            .LastOrDefault();
        if (dockerfile is null || image is null)
        {
            return;
        }

        var workflowTag = ModuleImageWorkflowConfiguration
            .Read(builder.Configuration)
            .ResolveTag(
                descriptor.Identity.Module.Name,
                descriptor.Identity.DeclaredResourceName);
        var defaultImageName = GetConfiguredValue(dockerfile.ImageName) ?? image.Image;
        var defaultImageTag = workflowTag ??
            GetConfiguredValue(dockerfile.ImageTag) ??
            GetConfiguredValue(image.Tag) ??
            "latest";
        container.WithAnnotation(new ModuleNativeImagePublisherAnnotation(ModuleResourceKind.Container));
        ModuleNativeImageValidationPipeline.AddValidationStep(
            container,
            descriptor.Identity.RepositoryPath,
            options);
        container.WithImagePushOptions(context =>
            FillDefaultImagePushOptions(context, defaultImageName, defaultImageTag));
    }

    private static ModuleImagePublisherAnnotation CreateImagePublisher(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        string declaredResourceName,
        ModuleResourceKind resourceKind,
        ModuleImageCommandOptions declared,
        DistributedApplicationModuleImageOptions? configured,
        ModuleRepositoryContext definitionRepository,
        ModuleApplicationRegistry registry,
        ModularAppHostsOptions options,
        DistributedApplicationModuleOptions? moduleOptions,
        string defaultWorkingDirectory,
        string? requiredProjectRelativePath = null)
    {
        var workflow = ModuleImageWorkflowConfiguration.Read(builder.Configuration);
        var effectiveOptions = ApplyImageRecipeOptions(
            declared,
            configured,
            workflow.ResolveTag(module.Name, declaredResourceName));
        var canPrepareWithoutBuildRepository =
            effectiveOptions.PullBeforeBuild &&
            !string.IsNullOrWhiteSpace(effectiveOptions.ImageTag);
        var buildRepository = ModuleMaterializationPlanning.ResolveBuildRepository(
            builder,
            module,
            declaredResourceName,
            effectiveOptions,
            configured,
            definitionRepository,
            registry,
            moduleOptions,
            allowMissingBuildRepository: canPrepareWithoutBuildRepository);
        var allowsUnavailableSource =
            canPrepareWithoutBuildRepository &&
            !buildRepository.UsesModuleRepository;
        var workingDirectoryRelativePath = effectiveOptions.WorkingDirectory ??
            (buildRepository.UsesModuleRepository ? defaultWorkingDirectory : ".");
        var workingDirectory = PathSafety.GetContainedPath(
            buildRepository.RepositoryPath,
            workingDirectoryRelativePath,
            nameof(ModuleImageCommandOptions.WorkingDirectory));
        registry.RequireDirectory(
            module.Name,
            $"image build directory for resource '{declaredResourceName}'",
            workingDirectory,
            requiredOnRun: !allowsUnavailableSource);
        if (requiredProjectRelativePath is not null)
        {
            registry.RequireFile(
                module.Name,
                $"project '{declaredResourceName}'",
                PathSafety.GetContainedPath(
                    definitionRepository.RepositoryPath,
                    requiredProjectRelativePath,
                    nameof(IDistributedApplicationModuleProject.ProjectPath)));
        }

        var refresh = configured?.RefreshBuildRepositoryOnRun ??
            options.RefreshBuildRepositoriesOnRun;
        var recipe = new ModuleImageBuildRecipe(
            new ModuleImageRecipeIdentity(module.Name, declaredResourceName),
            new ModuleImageRepositorySettings(
                buildRepository.RepositoryPath,
                workingDirectory,
                buildRepository.Repository,
                buildRepository.Revision,
                refresh,
                GetConfiguredValue(options.GitExecutablePath) ?? "git",
                GetConfiguredValue(options.GitHubCliPath) ?? "gh",
                options.RepositoryCommandTimeout,
                GetDetachedAppHostBranchAlias(builder, buildRepository),
                builder.AppHostDirectory,
                buildRepository.InitializerOwned,
                allowsUnavailableSource),
            new ModuleImageCommandSettings(
                effectiveOptions,
                options.ImageBuildTimeout,
                options.ImageTransferTimeout));
        return new ModuleImagePublisherAnnotation(resourceKind, recipe);
    }

    private static string GetProjectWorkingDirectory(DistributedApplicationModuleProject project)
    {
        var directory = Path.GetDirectoryName(project.GetRepositoryRelativeProjectPath());
        return string.IsNullOrWhiteSpace(directory) ? "." : directory;
    }

    private static string? GetDetachedAppHostBranchAlias(
        IDistributedApplicationBuilder builder,
        ModuleRepositoryContext buildRepository)
    {
        if (buildRepository.Revision is not null)
        {
            return null;
        }

        var appHostRepositoryRoot = RepositoryIdentity.TryFindRepositoryRoot(builder.AppHostDirectory);
        var buildRepositoryRoot = RepositoryIdentity.TryFindRepositoryRoot(buildRepository.RepositoryPath);
        if (appHostRepositoryRoot is null ||
            buildRepositoryRoot is null ||
            !PathSafety.AreEqual(appHostRepositoryRoot, buildRepositoryRoot))
        {
            return null;
        }

        return GetConfiguredValue(builder.Configuration["GITHUB_HEAD_REF"]) ??
            GetConfiguredValue(builder.Configuration["GITHUB_REF_NAME"]);
    }

    private static void ConfigureContainerImage(
        IDistributedApplicationBuilder builder,
        IResourceBuilder<ContainerResource> container,
        ModuleImagePublisherAnnotation? publisher,
        DistributedApplicationModuleImageOptions? configured,
        ModuleResourceImage? ownedImage)
    {
        if (ownedImage is not null)
        {
            ApplyOwnedImage(container, ownedImage);
        }

        ApplyImageSHA256(container, configured?.ImageSHA256);
        ApplyImagePullPolicy(container, configured?.ImagePullPolicy);
        ModuleImagePullPipeline.AddPullStep(container);
        if (publisher is null)
        {
            return;
        }

        container.WithAnnotation(publisher);
        container.WithImagePushOptions(context =>
        {
            if (string.IsNullOrWhiteSpace(context.Options.RemoteImageName))
            {
                context.Options.RemoteImageName = publisher.Options.ImageName;
            }

            if (string.IsNullOrWhiteSpace(context.Options.RemoteImageTag))
            {
                context.Options.RemoteImageTag = publisher.TryGetPreparedImage(out var preparedImage)
                    ? ModuleImageReference.GetTag(preparedImage.CanonicalImageReference)
                    : publisher.Options.ImageTag ?? "latest";
            }
        });
        ModuleImageBuildPipeline.AddBuildStep(container);
        ModuleImagePushPipeline.AddPushStep(container);
        if (builder.ExecutionContext.IsRunMode)
        {
            container.OnBeforeResourceStarted(async (resource, @event, cancellationToken) =>
            {
                var resourceLogger = @event.Services
                    .GetRequiredService<ResourceLoggerService>()
                    .GetLogger(resource);
                try
                {
                    await publisher.PrepareAsync(
                        @event.Services,
                        resourceLogger,
                        resourceLogger,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    exception is not OperationCanceledException ||
                    !cancellationToken.IsCancellationRequested)
                {
                    LogImagePreparationFailed(
                        resourceLogger,
                        publisher.ModuleName,
                        publisher.ResourceName,
                        exception);
                    throw;
                }
            });
        }
    }

    private static void ConfigureRepositoryLifecycle<TResource>(
        IDistributedApplicationBuilder builder,
        IResourceBuilder<TResource> resource,
        ModuleApplicationRegistry registry)
        where TResource : IResource
    {
        var requirements = registry.RepositoryPlans?.Requirements;
        if (requirements is null || requirements.Count == 0)
        {
            return;
        }

        resource.WithRequiredCommand(registry.Options.GitExecutablePath);
        if (requirements.Any(requirement =>
                GitHubGitAuthentication.UsesCredentialProvider(requirement.Repository)))
        {
            resource.WithRequiredCommand(registry.Options.GitHubCliPath);
        }

        if (!builder.ExecutionContext.IsRunMode)
        {
            return;
        }

        resource.OnBeforeResourceStarted(async (startedResource, @event, cancellationToken) =>
        {
            var logger = @event.Services
                .GetRequiredService<ResourceLoggerService>()
                .GetLogger(startedResource);
            await registry.ValidateRepositoryPreflightAsync(
                @event.Services.GetRequiredService<IModuleRepositoryStateStore>(),
                new ModuleRepositoryInitializationSettings(
                    registry.Options.GitExecutablePath,
                    registry.Options.GitHubCliPath,
                    registry.Options.RepositoryCommandTimeout),
                builder.AppHostDirectory,
                logger,
                cancellationToken).ConfigureAwait(false);
        });
    }

    private static void MaterializeProjectResourceSynchronously(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        DistributedApplicationModuleProject project,
        string resourceName,
        DistributedApplicationModuleProjectOptions? options,
        string repositoryPath,
        bool imported,
        ModuleApplicationRegistry registry)
    {
        var projectPath = PathSafety.GetContainedPath(
            repositoryPath,
            project.GetRepositoryRelativeProjectPath(),
            nameof(project.ProjectPath));
        registry.RequireFile(module.Name, $"project '{project.Name}'", projectPath);
        var resource = builder
            .AddProject(resourceName, projectPath, projectOptions =>
            {
                if (options?.LaunchProfileName is not null)
                {
                    projectOptions.LaunchProfileName = options.LaunchProfileName;
                }

                if (options?.ExcludeLaunchProfile is { } excludeLaunchProfile)
                {
                    projectOptions.ExcludeLaunchProfile = excludeLaunchProfile;
                }

                if (options?.ExcludeKestrelEndpoints is { } excludeKestrelEndpoints)
                {
                    projectOptions.ExcludeKestrelEndpoints = excludeKestrelEndpoints;
                }
            })
            .WithAnnotation(new DistributedApplicationModuleResourceAnnotation(
                module.Name,
                project.Name,
                repositoryPath,
                imported,
                module.PackageId));
        var context = new DistributedApplicationModuleResourceContext(
            builder,
            module,
            resourceName,
            repositoryPath,
            imported);
        project.ConfigureProject?.Invoke(context, resource);
        ConfigureRepositoryLifecycle(builder, resource, registry);
        registry.TrackResource(resource.Resource);
        module.TrackMaterializedResource(builder, project.Name, resource.Resource);
    }

    private static void MaterializeNativeProjectResource(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        DistributedApplicationModuleProject project,
        string resourceName,
        DistributedApplicationModuleProjectOptions? options,
        string repositoryPath,
        bool imported,
        ModuleApplicationRegistry registry)
    {
        var projectPath = PathSafety.GetContainedPath(
            repositoryPath,
            project.GetRepositoryRelativeProjectPath(),
            nameof(project.ProjectPath));
        registry.RequireFile(module.Name, $"project '{project.Name}'", projectPath);
        var workflow = ModuleImageWorkflowConfiguration.Read(builder.Configuration);
        var imageName = GetConfiguredValue(options?.ImageName) ?? project.Export.ImageName;
        var imageTag = workflow.ResolveTag(module.Name, project.Name) ??
            GetConfiguredValue(options?.ImageTag) ??
            "latest";
        var imageRegistry = GetConfiguredValue(options?.ImageRegistry);
        var resource = builder
            .AddProject(resourceName, projectPath, projectOptions =>
            {
                if (options?.LaunchProfileName is not null)
                {
                    projectOptions.LaunchProfileName = options.LaunchProfileName;
                }

                if (options?.ExcludeLaunchProfile is { } excludeLaunchProfile)
                {
                    projectOptions.ExcludeLaunchProfile = excludeLaunchProfile;
                }

                if (options?.ExcludeKestrelEndpoints is { } excludeKestrelEndpoints)
                {
                    projectOptions.ExcludeKestrelEndpoints = excludeKestrelEndpoints;
                }
            })
            .WithAnnotation(new DistributedApplicationModuleResourceAnnotation(
                module.Name,
                project.Name,
                repositoryPath,
                imported,
                module.PackageId))
            .WithAnnotation(new ModuleNativeImagePublisherAnnotation(ModuleResourceKind.Project));
        var contextImage = new ModuleResourceImage(imageRegistry, imageName, imageTag);
        var context = new DistributedApplicationModuleResourceContext(
            builder,
            module,
            resourceName,
            repositoryPath,
            imported,
            contextImage);
        project.ConfigureProject?.Invoke(context, resource);
        resource.PublishAsDockerFile(container =>
        {
            container.WithImage(imageName, imageTag);
            ApplyImageRegistry(container, imageRegistry);
            ApplyImagePullPolicy(container, options?.ImagePullPolicy);
            project.Export.ConfigureContainer?.Invoke(context, container);
            container.WithImagePushOptions(pushContext =>
                FillDefaultImagePushOptions(pushContext, imageName, imageTag));
        });
        ModuleNativeImageValidationPipeline.AddValidationStep(resource, repositoryPath, registry.Options);
        ConfigureRepositoryLifecycle(builder, resource, registry);
        registry.TrackResource(resource.Resource);
        module.TrackMaterializedResource(builder, project.Name, resource.Resource);
    }

    private static ModuleImageCommandOptions ApplyImageRecipeOptions(
        ModuleImageCommandOptions declared,
        DistributedApplicationModuleImageOptions? configured,
        string? workflowTag)
    {
        var imageName = GetConfiguredValue(configured?.ImageName) ?? declared.ImageName;
        var imageRegistry = configured?.ImageRegistry is null
            ? GetConfiguredValue(declared.ImageRegistry)
            : GetConfiguredValue(configured.ImageRegistry);
        var publishCommand = GetConfiguredValue(configured?.PublishCommand) ?? declared.PublishCommand;
        var publishArguments = configured?.PublishArguments?.ToArray() ?? declared.PublishArguments.ToArray();
        ArgumentException.ThrowIfNullOrWhiteSpace(imageName);
        ArgumentException.ThrowIfNullOrWhiteSpace(publishCommand);
        return new ModuleImageCommandOptions(imageName, publishCommand, publishArguments)
        {
            ImageRegistry = imageRegistry,
            ProducedImageReference = configured?.ProducedImageReference ?? declared.ProducedImageReference,
            PullBeforeBuild = configured?.PullBeforeBuild ?? declared.PullBeforeBuild,
            ImageTag = workflowTag ??
                GetConfiguredValue(configured?.ImageTag) ??
                GetConfiguredValue(declared.ImageTag),
            WorkingDirectory = configured?.PublishWorkingDirectory ?? declared.WorkingDirectory,
            BuildRepository = GetConfiguredValue(configured?.BuildRepository) ??
                GetConfiguredValue(declared.BuildRepository),
            BuildRepositoryRevision = GetConfiguredValue(configured?.BuildRepositoryRevision) ??
                GetConfiguredValue(declared.BuildRepositoryRevision)
        };
    }

    private static ModuleResourceImage CreateResourceImage(
        IResourceBuilder<ContainerResource> container,
        ModuleImagePublisherAnnotation? publisher,
        DistributedApplicationModuleImageOptions? configured)
    {
        var image = container.Resource.Annotations.OfType<ContainerImageAnnotation>().Last();
        return new ModuleResourceImage(
            publisher?.Recipe.Options.ImageRegistry ?? image.Registry,
            publisher?.Recipe.Options.ImageName ?? image.Image,
            publisher is null ? image.Tag ?? "latest" : ModuleImageBuildRecipe.LocalRunTag,
            GetConfiguredValue(configured?.ImageSHA256));
    }

    private static ModuleResourceImage? CreateFactoryResourceImage(
        ModuleImagePublisherAnnotation? publisher,
        DistributedApplicationModuleImageOptions? configured)
    {
        if (publisher is not null)
        {
            return new ModuleResourceImage(
                publisher.Recipe.Options.ImageRegistry,
                publisher.Recipe.Options.ImageName,
                ModuleImageBuildRecipe.LocalRunTag,
                GetConfiguredValue(configured?.ImageSHA256));
        }

        if (!UsesExternalImage(configured))
        {
            return null;
        }

        return new ModuleResourceImage(
            GetConfiguredValue(configured!.ImageRegistry),
            GetConfiguredValue(configured.ImageName)!,
            GetConfiguredValue(configured.ImageTag) ?? "latest",
            GetConfiguredValue(configured.ImageSHA256));
    }

    private static void ApplyOwnedImage(
        IResourceBuilder<ContainerResource> container,
        ModuleResourceImage image)
    {
        var annotation = container.Resource.Annotations
            .OfType<ContainerImageAnnotation>()
            .LastOrDefault() ?? throw new InvalidOperationException(
                $"Image-published module resource '{container.Resource.Name}' created a container without an image. " +
                "Create it with AddContainer and use context.Image for the managed image identity.");
        annotation.Registry = image.Registry;
        annotation.Image = image.Name;
        container.WithImageTag(image.Tag);
    }

    private static void FillDefaultImagePushOptions(
        ContainerImagePushOptionsCallbackContext context,
        string imageName,
        string imageTag)
    {
#pragma warning disable CA1308
        var aspireDefaultName = context.Resource.Name.ToLowerInvariant();
#pragma warning restore CA1308
        if (string.IsNullOrWhiteSpace(context.Options.RemoteImageName) ||
            string.Equals(
                context.Options.RemoteImageName,
                aspireDefaultName,
                StringComparison.OrdinalIgnoreCase))
        {
            context.Options.RemoteImageName = imageName;
        }

        if (string.IsNullOrWhiteSpace(context.Options.RemoteImageTag) ||
            string.Equals(context.Options.RemoteImageTag, "latest", StringComparison.OrdinalIgnoreCase))
        {
            context.Options.RemoteImageTag = imageTag;
        }
    }

    private sealed record ModuleContainerIdentity(
        DistributedApplicationModule Module,
        string DeclaredResourceName,
        string EffectiveResourceName,
        string RepositoryPath,
        bool Imported);

    private sealed record ModuleContainerImagePlan(
        ModuleImagePublisherAnnotation? Publisher,
        DistributedApplicationModuleImageOptions? Configured,
        ModuleResourceImage? ContextImage,
        ModuleResourceImage? OwnedImage);

    private sealed record NormalizedContainerDescriptor(
        ModuleContainerIdentity Identity,
        ModuleContainerImagePlan Image,
        Action<
            IDistributedApplicationModuleResourceContext,
            IResourceBuilder<ContainerResource>>? ConfigureContainer,
        bool ModuleAnnotationAlreadyApplied);

    private static bool RequiresRepositoryForSynchronousMaterialization(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        ModularAppHostsOptions options,
        DistributedApplicationModuleOptions? moduleOptions,
        bool imported)
    {
        if (module.ExplicitlyRequiresRepositoryContent)
        {
            return true;
        }

        foreach (var project in module.ProjectDefinitions)
        {
            var configured = moduleOptions?.FindProject(project.Name);
            var runAsProject = builder.ExecutionContext.IsRunMode &&
                ResolveProjectMode(options, moduleOptions, configured, imported) == ModuleProjectMode.Project;
            if (runAsProject ||
                (!UsesExternalImage(configured) &&
                 GetConfiguredValue(configured?.BuildRepository) is null &&
                 GetConfiguredValue(project.Export.CommandOptions?.BuildRepository) is null))
            {
                return true;
            }
        }

        foreach (var publisher in GetContainerPublishers(module))
        {
            var configured = moduleOptions?.FindContainer(publisher.ResourceName);
            if (!UsesExternalImage(configured) &&
                GetConfiguredValue(configured?.BuildRepository) is null &&
                GetConfiguredValue(publisher.Options.BuildRepository) is null)
            {
                return true;
            }
        }

        return false;
    }

    private static void ValidateSynchronousResourceNames(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        ModuleApplicationRegistry registry,
        ModularAppHostsOptions options,
        DistributedApplicationModuleOptions? moduleOptions,
        bool imported,
        ModuleResourceNameMap resourceNames)
    {
        var planned = module.ResourceDefinitions
            .Select(definition => resourceNames[definition.Name])
            .ToArray();

        var duplicate = planned
            .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Cannot materialize module '{module.Name}' because its aliases and prefix " +
                $"produce duplicate resource '{duplicate}'.");
        }

        foreach (var resourceName in planned)
        {
            if (builder.Resources.Any(resource => string.Equals(
                    resource.Name,
                    resourceName,
                    StringComparison.OrdinalIgnoreCase)))
            {
                var tracked = registry.TryGetResource(resourceName, out _)
                    ? " and is already tracked by the module registry"
                    : string.Empty;
                throw new InvalidOperationException(
                    $"Cannot materialize module '{module.Name}' because resource '{resourceName}' already exists{tracked}.");
            }
        }
    }

}
