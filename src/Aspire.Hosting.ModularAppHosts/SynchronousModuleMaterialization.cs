using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

public static partial class DistributedApplicationModuleExtensions
{
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
        registry.RefreshConfiguration();
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
                project.Export.Options,
                projectOptions,
                definitionRepository,
                registry,
                options,
                moduleOptions,
                project.GetRepositoryRelativeProjectPath());
        var container = builder
            .AddContainer(
                resourceName,
                publisher?.Recipe.Options.ImageName ?? GetConfiguredValue(projectOptions?.ImageName) ?? project.Export.Options.ImageName,
                publisher is null
                    ? GetConfiguredValue(projectOptions?.ImageTag) ?? GetConfiguredValue(project.Export.Options.ImageTag) ?? "latest"
                    : ModuleImageBuildRecipe.LocalRunTag)
            .WithAnnotation(new DistributedApplicationModuleResourceAnnotation(
                module.Name,
                project.Name,
                definitionRepository.RepositoryPath,
                imported,
                module.PackageId));
        ApplyImageRegistry(
            container,
            publisher?.Recipe.Options.ImageRegistry ?? GetConfiguredValue(projectOptions?.ImageRegistry));
        var image = CreateResourceImage(container, publisher, projectOptions);
        var context = new DistributedApplicationModuleResourceContext(
            builder,
            module,
            resourceName,
            definitionRepository.RepositoryPath,
            imported,
            image);
        project.Export.ConfigureContainer?.Invoke(context, container);
        ConfigureContainerImage(
            builder,
            module,
            project.Name,
            container,
            publisher,
            projectOptions,
            registry);
        registry.TrackResource(container.Resource);
        module.TrackMaterializedResource(builder, project.Name, container.Resource);
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
                    : ModuleImageBuildRecipe.LocalRunTag)
            .WithAnnotation(new DistributedApplicationModuleResourceAnnotation(
                module.Name,
                definition.Name,
                definitionRepository.RepositoryPath,
                imported,
                module.PackageId));
        ApplyImageRegistry(
            container,
            publisher?.Recipe.Options.ImageRegistry ?? GetConfiguredValue(configured?.ImageRegistry));
        var image = CreateResourceImage(container, publisher, configured);
        var context = new DistributedApplicationModuleResourceContext(
            builder,
            module,
            resourceName,
            definitionRepository.RepositoryPath,
            imported,
            image);
        definition.ConfigureContainer?.Invoke(context, container);
        ConfigureContainerImage(
            builder,
            module,
            definition.Name,
            container,
            publisher,
            configured,
            registry);
        registry.TrackResource(container.Resource);
        module.TrackMaterializedResource(builder, definition.Name, container.Resource);
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
        var context = new DistributedApplicationModuleResourceContext(
            builder,
            module,
            resourceName,
            definitionRepository.RepositoryPath,
            imported,
            publisher is null
                ? null
                : new ModuleResourceImage(
                    publisher.Recipe.Options.ImageRegistry,
                    publisher.Recipe.Options.ImageName,
                    ModuleImageBuildRecipe.LocalRunTag,
                    GetConfiguredValue(configured?.ImageSHA256)));
        var resource = definition.Materialize(
            context,
            new DistributedApplicationModuleResourceAnnotation(
                module.Name,
                definition.Name,
                definitionRepository.RepositoryPath,
                imported,
                module.PackageId));
        if (resource is ContainerResource containerResource)
        {
            var container = builder.CreateResourceBuilder(containerResource);
            if (publisher is not null)
            {
                container
                    .WithImage(publisher.Recipe.Options.ImageName)
                    .WithImageTag(ModuleImageBuildRecipe.LocalRunTag);
                ApplyImageRegistry(container, publisher.Recipe.Options.ImageRegistry);
            }

            ConfigureContainerImage(
                builder,
                module,
                definition.Name,
                container,
                publisher,
                configured,
                registry);
        }
        else if (publisher is not null)
        {
            throw new InvalidOperationException(
                $"Image-published module resource '{definition.Name}' did not create a container resource.");
        }

        registry.TrackResource(resource);
        module.TrackMaterializedResource(builder, definition.Name, resource);
    }

    private static ModuleImagePublisherAnnotation CreateImagePublisher(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        string declaredResourceName,
        ModuleResourceKind resourceKind,
        ModuleContainerExportOptions declared,
        DistributedApplicationModuleImageOptions? configured,
        ModuleRepositoryContext definitionRepository,
        ModuleApplicationRegistry registry,
        ModularAppHostsOptions options,
        DistributedApplicationModuleOptions? moduleOptions,
        string defaultWorkingDirectory)
    {
        var effectiveOptions = ApplyImageRecipeOptions(declared, configured);
        var buildRepository = ModuleMaterializationPlanning.ResolveBuildRepository(
            builder,
            module,
            declaredResourceName,
            effectiveOptions,
            configured,
            definitionRepository,
            registry,
            moduleOptions);
        var workingDirectoryRelativePath = effectiveOptions.WorkingDirectory ??
            (buildRepository.UsesModuleRepository ? defaultWorkingDirectory : ".");
        var workingDirectory = PathSafety.GetContainedPath(
            buildRepository.RepositoryPath,
            workingDirectoryRelativePath,
            nameof(ModuleContainerExportOptions.WorkingDirectory));
        registry.RequireDirectory(
            module.Name,
            $"image build directory for resource '{declaredResourceName}'",
            workingDirectory);
        var refresh = configured?.RefreshBuildRepositoryOnRun ??
            options.RefreshBuildRepositoriesOnRun;
        var recipe = new ModuleImageBuildRecipe(
            module.Name,
            declaredResourceName,
            effectiveOptions,
            buildRepository.RepositoryPath,
            workingDirectory,
            buildRepository.Repository,
            buildRepository.Revision,
            refresh,
            GetConfiguredValue(options.GitExecutablePath) ?? "git",
            GetConfiguredValue(options.GitHubCliPath) ?? "gh",
            options.RepositoryCommandTimeout);
        return new ModuleImagePublisherAnnotation(resourceKind, recipe);
    }

    private static void ConfigureContainerImage(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        string declaredResourceName,
        IResourceBuilder<ContainerResource> container,
        ModuleImagePublisherAnnotation? publisher,
        DistributedApplicationModuleImageOptions? configured,
        ModuleApplicationRegistry registry)
    {
        ApplyImageSHA256(container, configured?.ImageSHA256);
        ApplyImagePullPolicy(container, configured?.ImagePullPolicy);
        ModuleImagePullPipeline.AddPullStep(container);
        if (publisher is null)
        {
            return;
        }

        container.WithAnnotation(publisher);
        ModuleImageBuildPipeline.AddBuildStep(container);
        ModuleImagePushPipeline.AddPushStep(container);
        if (builder.ExecutionContext.IsRunMode)
        {
            AddImagePreparationInstaller(
                builder,
                module,
                declaredResourceName,
                container,
                publisher,
                registry);
        }
    }

    private static void AddImagePreparationInstaller(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        string declaredResourceName,
        IResourceBuilder<ContainerResource> container,
        ModuleImagePublisherAnnotation publisher,
        ModuleApplicationRegistry registry)
    {
        var installerResource = new ModuleRepositoryInstallerResource(
            GetInstallerName(container.Resource.Name),
            publisher);
        var installer = builder.AddResource(installerResource)
            .WithParentRelationship(container.Resource)
            .ExcludeFromManifest()
            .WithExplicitStart()
            .WithIconName("ArrowDownload");
        container.WithAnnotation(new ModuleRepositoryInstallerAnnotation(installerResource));
        builder.Eventing.Subscribe<BeforeStartEvent>(async (@event, cancellationToken) =>
        {
            var loggerFactory = @event.Services.GetRequiredService<ILoggerFactory>();
            var lifecycleLogger = loggerFactory.CreateLogger("Aspire.Hosting.ModuleImagePreparation");
            var resourceLogger = @event.Services
                .GetRequiredService<ResourceLoggerService>()
                .GetLogger(installerResource);
            await publisher.PrepareAsync(
                lifecycleLogger,
                resourceLogger,
                cancellationToken).ConfigureAwait(false);
        });
        registry.TrackResource(installer.Resource);
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
        registry.TrackResource(resource.Resource);
        module.TrackMaterializedResource(builder, project.Name, resource.Resource);
    }

    private static ModuleContainerExportOptions ApplyImageRecipeOptions(
        ModuleContainerExportOptions declared,
        DistributedApplicationModuleImageOptions? configured)
    {
        var imageName = GetConfiguredValue(configured?.ImageName) ?? declared.ImageName;
        var imageRegistry = configured?.ImageRegistry is null
            ? GetConfiguredValue(declared.ImageRegistry)
            : GetConfiguredValue(configured.ImageRegistry);
        var publishCommand = GetConfiguredValue(configured?.PublishCommand) ?? declared.PublishCommand;
        var publishArguments = configured?.PublishArguments?.ToArray() ?? declared.PublishArguments.ToArray();
        ArgumentException.ThrowIfNullOrWhiteSpace(imageName);
        ArgumentException.ThrowIfNullOrWhiteSpace(publishCommand);
        return new ModuleContainerExportOptions(imageName, publishCommand, publishArguments)
        {
            ImageRegistry = imageRegistry,
            ProducedImageReference = configured?.ProducedImageReference ?? declared.ProducedImageReference,
            PullBeforeBuild = configured?.PullBeforeBuild ?? declared.PullBeforeBuild,
            ImageTag = GetConfiguredValue(configured?.ImageTag) ?? GetConfiguredValue(declared.ImageTag),
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
                 GetConfiguredValue(project.Export.Options.BuildRepository) is null))
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
        var planned = new List<string>();
        foreach (var definition in module.ResourceDefinitions)
        {
            var effectiveName = resourceNames[definition.Name];
            planned.Add(effectiveName);
            if (builder.ExecutionContext.IsRunMode && HasRuntimeImagePublisher(
                    definition,
                    options,
                    moduleOptions,
                    imported))
            {
                planned.Add(GetInstallerName(effectiveName));
            }
        }

        var duplicate = planned
            .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Cannot materialize module '{module.Name}' because its aliases, prefix, and installer names " +
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

    private static bool HasRuntimeImagePublisher(
        IDistributedApplicationModuleResource definition,
        ModularAppHostsOptions options,
        DistributedApplicationModuleOptions? moduleOptions,
        bool imported) =>
        definition switch
        {
            DistributedApplicationModuleProject project =>
                ResolveProjectMode(options, moduleOptions, moduleOptions?.FindProject(project.Name), imported) ==
                    ModuleProjectMode.Container &&
                !UsesExternalImage(moduleOptions?.FindProject(project.Name)),
            DistributedApplicationModuleContainer container =>
                container.ImagePublishOptions is not null &&
                !UsesExternalImage(moduleOptions?.FindContainer(container.Name)),
            IDistributedApplicationModuleFactoryResource resource =>
                resource.ImagePublishOptions is not null &&
                !UsesExternalImage(moduleOptions?.FindContainer(resource.Name)),
            _ => false
        };
}
