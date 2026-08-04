using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.ModularAppHosts;

/// <summary>Extensions for composing reusable modules in an Aspire AppHost.</summary>
public static class DistributedApplicationModuleExtensions
{
    /// <summary>The legacy <c>Parameters</c> key used as the parent directory for managed repository clones.</summary>
    public const string RepositoryBaseLocationParameterName = "module-repository-base-location";

    /// <summary>Configures module materialization options after applying AppHost configuration.</summary>
    public static IDistributedApplicationBuilder ConfigureModularAppHosts(
        this IDistributedApplicationBuilder builder,
        Action<ModularAppHostsOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        configure(GetOrCreateRegistry(builder).Options);
        return builder;
    }

    /// <summary>Gets the Aspire parameter name used when an imported module has no configured repository.</summary>
    public static string GetRepositoryParameterName(string moduleName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);

        var slug = new string(moduleName
            .Select(character => char.IsLetterOrDigit(character)
                ? char.ToLowerInvariant(character)
                : '-')
            .ToArray());
        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        slug = slug.Trim('-');
        return slug.Length == 0 ? "module-repository" : $"module-{slug}-repository";
    }

    /// <summary>Gets the configuration key used to resolve an imported module's Git repository.</summary>
    public static string GetRepositoryConfigurationKey(string moduleName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);
        return $"{ModularAppHostsOptions.ConfigurationSectionName}:Modules:{moduleName}:Repository";
    }

    /// <summary>Exports a named module definition without adding its services to the application model.</summary>
    public static IDistributedApplicationModule ExportModule(
        this IDistributedApplicationBuilder builder,
        string name,
        Action<IDistributedApplicationModuleBuilder> moduleBuilder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(moduleBuilder);

        var registry = GetOrCreateRegistry(builder);
        if (registry.TryGetDefinition(name, out var existingModule) && existingModule is not null)
        {
            return existingModule;
        }

        var module = new DistributedApplicationModule(name);
        moduleBuilder(new DistributedApplicationModuleBuilder(builder, module));
        module.Validate();
        registry.AddModule(module);
        return module;
    }

    /// <summary>Adds an exported module using its local source worktree.</summary>
    public static IDistributedApplicationModule Add(
        this IDistributedApplicationBuilder builder,
        IDistributedApplicationModule module)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(module);

        if (module is not DistributedApplicationModule typedModule)
        {
            throw new ArgumentException(
                "The module must have been created by ExportModule on this extension.", nameof(module));
        }

        var registry = GetOrCreateRegistry(builder);
        if (!registry.TryGetDefinition(typedModule.Name, out _))
        {
            registry.AddModule(typedModule);
        }

        Materialize(builder, typedModule, registry, imported: false);
        return module;
    }

    /// <summary>Imports an exported module by name using a managed Git clone.</summary>
    public static IDistributedApplicationModule ImportModule(
        this IDistributedApplicationBuilder builder,
        string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var registry = GetOrCreateRegistry(builder);
        if (!registry.TryGetDefinition(name, out var module) || module is null)
        {
            throw new InvalidOperationException(
                $"Module '{name}' has not been exported. Call ExportModule before ImportModule.");
        }

        Materialize(builder, module, registry, imported: true);
        return module;
    }

    private static void Materialize(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        ModuleApplicationRegistry registry,
        bool imported)
    {
        if (registry.IsMaterialized(module.Name))
        {
            return;
        }

        var options = registry.Options;
        var moduleOptions = options.FindModule(module.Name);
        var repositoryConfigurationKey = GetRepositoryConfigurationKey(module.Name);
        var configuredRepository = GetConfiguredValue(builder.Configuration[repositoryConfigurationKey]);
        var repository = configuredRepository ?? GetConfiguredValue(moduleOptions?.Repository) ?? module.Repository;
        var requiresRepository = module.ProjectDefinitions.Count > 0;
        var repositoryParameter = imported &&
            (configuredRepository is not null || (requiresRepository && string.IsNullOrWhiteSpace(repository)))
            ? GetOrCreateRepositoryParameter(
                builder,
                registry,
                module.Name,
                repositoryConfigurationKey)
            : null;
        var repositoryPath = imported &&
            (requiresRepository || repositoryParameter is not null || !string.IsNullOrWhiteSpace(repository))
            ? GetImportedRepositoryPath(builder, options, module.Name)
            : GetLocalRepositoryPath(builder, module, repository);
        var repositoryDirty = RepositoryInspector.IsDirty(repositoryPath);
        var updateRepository = moduleOptions?.UpdateRepository ?? options.UpdateImportedRepositories;

        ValidateResourceNames(builder, module, registry, options, moduleOptions);
        ConfigureRepositorySynchronization(
            builder,
            registry,
            repositoryPath,
            repositoryParameter is null ? repository : null,
            repositoryParameter,
            imported,
            updateRepository);

        foreach (var definition in module.ResourceDefinitions)
        {
            switch (definition)
            {
                case DistributedApplicationModuleProject project:
                    MaterializeProject(
                        builder,
                        module,
                        project,
                        repositoryPath,
                        repositoryDirty,
                        imported,
                        registry,
                        options,
                        moduleOptions,
                        repository,
                        updateRepository);
                    break;
                case DistributedApplicationModuleContainer container:
                    MaterializeContainer(
                        builder,
                        module,
                        container,
                        repositoryPath,
                        repositoryDirty,
                        imported,
                        registry,
                        options,
                        moduleOptions,
                        repository,
                        updateRepository);
                    break;
                case IDistributedApplicationModuleFactoryResource resource:
                    MaterializeResource(builder, module, resource, repositoryPath, imported, registry);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Module resource definition '{definition.Name}' has an unsupported implementation type.");
            }
        }

        registry.MarkMaterialized(module.Name);
    }

    private static void MaterializeProject(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        DistributedApplicationModuleProject project,
        string repositoryPath,
        bool repositoryDirty,
        bool imported,
        ModuleApplicationRegistry registry,
        ModularAppHostsOptions options,
        DistributedApplicationModuleOptions? moduleOptions,
        string? repository,
        bool updateRepository)
    {
        var export = project.Export;
        var sourceProjectDirectory = Path.GetDirectoryName(project.ProjectPath)
            ?? throw new InvalidOperationException($"Unable to determine the directory for '{project.ProjectPath}'.");
        var projectDirectoryRelativePath = Path.GetRelativePath(project.SourceRepositoryRoot, sourceProjectDirectory);
        var projectOptions = moduleOptions?.FindProject(project.Name);
        var runAsContainer = !builder.ExecutionContext.IsRunMode ||
            (projectOptions?.RunAsContainer ??
                moduleOptions?.RunProjectsAsContainers ??
                options.RunProjectsAsContainers);

        if (!runAsContainer)
        {
            MaterializeProjectResource(
                builder,
                module,
                project,
                projectOptions,
                repositoryPath,
                imported,
                registry);
            return;
        }

        var effectiveExportOptions = ApplyImageOptions(export.Options, projectOptions);
        var publishImage = projectOptions?.PublishImage ?? moduleOptions?.PublishImages ?? options.PublishImages;
        var workingDirectoryRelativePath = effectiveExportOptions.WorkingDirectory ?? projectDirectoryRelativePath;
        var sourceWorkingDirectory = GetContainedPath(
            project.SourceRepositoryRoot,
            workingDirectoryRelativePath,
            nameof(ModuleContainerExportOptions.WorkingDirectory));
        var normalizedWorkingDirectoryRelativePath = Path.GetRelativePath(
            project.SourceRepositoryRoot,
            sourceWorkingDirectory);
        var publishWorkingDirectory = GetContainedPath(
            repositoryPath,
            normalizedWorkingDirectoryRelativePath,
            nameof(ModuleContainerExportOptions.WorkingDirectory));
        var publishPlan = CreateImagePublishPlan(
            builder,
            effectiveExportOptions,
            repositoryDirty && publishImage);

        var container = builder
            .AddContainer(project.Name, publishPlan.ImageName, publishPlan.ImageTag)
            .WithAnnotation(new DistributedApplicationModuleResourceAnnotation(
                module.Name,
                project.Name,
                repositoryPath,
                imported));

        export.ConfigureContainer?.Invoke(container);

        ApplyImagePullPolicy(
            container,
            projectOptions?.ImagePullPolicy ?? (publishImage ? ImagePullPolicy.Never : null));

        if (builder.ExecutionContext.IsRunMode && publishImage && publishPlan.ShouldPublish)
        {
            AddImagePublishInstaller(
                builder,
                project.Name,
                effectiveExportOptions,
                publishPlan,
                repositoryPath,
                publishWorkingDirectory,
                imported,
                repository,
                updateRepository,
                container,
                registry);
        }

        registry.TrackResource(container.Resource);
        module.TrackMaterializedResource(builder, container.Resource);
    }

    private static void MaterializeContainer(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        DistributedApplicationModuleContainer definition,
        string repositoryPath,
        bool repositoryDirty,
        bool imported,
        ModuleApplicationRegistry registry,
        ModularAppHostsOptions options,
        DistributedApplicationModuleOptions? moduleOptions,
        string? repository,
        bool updateRepository)
    {
        var containerOptions = moduleOptions?.FindContainer(definition.Name);
        var publishOptions = definition.ImagePublishOptions is null
            ? null
            : ApplyImageOptions(definition.ImagePublishOptions, containerOptions);
        ValidatePublishOverrides(definition, containerOptions);
        var publishImage = publishOptions is not null &&
            (containerOptions?.PublishImage ?? moduleOptions?.PublishImages ?? options.PublishImages);
        var publishPlan = publishOptions is null
            ? null
            : CreateImagePublishPlan(builder, publishOptions, repositoryDirty && publishImage);
        var container = builder
            .AddContainer(
                definition.Name,
                publishPlan?.ImageName ?? GetConfiguredValue(containerOptions?.ImageName) ?? definition.Image,
                publishPlan?.ImageTag ?? GetConfiguredValue(containerOptions?.ImageTag) ?? definition.Tag)
            .WithAnnotation(new DistributedApplicationModuleResourceAnnotation(
                module.Name,
                definition.Name,
                repositoryPath,
                imported));

        definition.ConfigureContainer?.Invoke(container);

        ApplyImagePullPolicy(
            container,
            containerOptions?.ImagePullPolicy ?? (publishImage ? ImagePullPolicy.Never : null));

        if (builder.ExecutionContext.IsRunMode && publishImage && publishPlan is { ShouldPublish: true })
        {
            var publishWorkingDirectory = GetContainedPath(
                repositoryPath,
                publishOptions!.WorkingDirectory ?? ".",
                nameof(ModuleContainerExportOptions.WorkingDirectory));
            AddImagePublishInstaller(
                builder,
                definition.Name,
                publishOptions,
                publishPlan,
                repositoryPath,
                publishWorkingDirectory,
                imported,
                repository,
                updateRepository,
                container,
                registry);
        }

        registry.TrackResource(container.Resource);
        module.TrackMaterializedResource(builder, container.Resource);
    }

    private static void MaterializeProjectResource(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        DistributedApplicationModuleProject project,
        DistributedApplicationModuleProjectOptions? options,
        string repositoryPath,
        bool imported,
        ModuleApplicationRegistry registry)
    {
        var projectRelativePath = Path.GetRelativePath(project.SourceRepositoryRoot, project.ProjectPath);
        var materializedProjectPath = GetContainedPath(repositoryPath, projectRelativePath, nameof(project.ProjectPath));
        if (!File.Exists(materializedProjectPath))
        {
            throw new InvalidOperationException(
                $"Project '{project.Name}' is configured to run directly, but '{materializedProjectPath}' does not exist. " +
                "Use an existing managed checkout or run the exported container instead.");
        }

        var resource = builder
            .AddProject(project.Name, materializedProjectPath, projectOptions =>
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
                imported));

        project.ConfigureProject?.Invoke(resource);
        registry.TrackResource(resource.Resource);
        module.TrackMaterializedResource(builder, resource.Resource);
    }

    private static void MaterializeResource(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        IDistributedApplicationModuleFactoryResource definition,
        string repositoryPath,
        bool imported,
        ModuleApplicationRegistry registry)
    {
        var context = new DistributedApplicationModuleResourceContext(
            builder,
            module,
            definition.Name,
            repositoryPath,
            imported);
        var resource = definition.Materialize(
            context,
            new DistributedApplicationModuleResourceAnnotation(
                module.Name,
                definition.Name,
                repositoryPath,
                imported));

        registry.TrackResource(resource);
        module.TrackMaterializedResource(builder, resource);
    }

    private static ModuleImagePublishPlan CreateImagePublishPlan(
        IDistributedApplicationBuilder builder,
        ModuleContainerExportOptions options,
        bool useDirtyImage)
    {
        return ModuleImagePublishPlan.Create(
            options,
            useDirtyImage,
            builder.ExecutionContext.IsRunMode
                ? ContainerImageInspector.Exists
                : _ => false);
    }

    private static ModuleContainerExportOptions ApplyImageOptions(
        ModuleContainerExportOptions declared,
        DistributedApplicationModuleImageOptions? configured)
    {
        var imageName = GetConfiguredValue(configured?.ImageName) ?? declared.ImageName;
        var imageTag = GetConfiguredValue(configured?.ImageTag) ?? declared.ImageTag;
        var publishCommand = GetConfiguredValue(configured?.PublishCommand) ?? declared.PublishCommand;
        var publishArguments = configured?.PublishArguments?.ToArray() ?? declared.PublishArguments.ToArray();

        ArgumentException.ThrowIfNullOrWhiteSpace(imageName);
        ArgumentException.ThrowIfNullOrWhiteSpace(imageTag);
        ArgumentException.ThrowIfNullOrWhiteSpace(publishCommand);

        return new ModuleContainerExportOptions(imageName, publishCommand, publishArguments)
        {
            ImageTag = imageTag,
            WorkingDirectory = configured?.PublishWorkingDirectory ?? declared.WorkingDirectory
        };
    }

    private static void ValidatePublishOverrides(
        DistributedApplicationModuleContainer definition,
        DistributedApplicationModuleContainerOptions? configured)
    {
        if (definition.ImagePublishOptions is not null || configured is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(configured.PublishCommand) ||
            configured.PublishArguments is not null ||
            configured.PublishWorkingDirectory is not null ||
            configured.PublishImage is true)
        {
            throw new InvalidOperationException(
                $"Container '{definition.Name}' configures image publishing, but its module definition does not call " +
                $"{nameof(IDistributedApplicationModuleContainerBuilder.WithImagePublishCommand)}().");
        }
    }

    private static void ApplyImagePullPolicy(
        IResourceBuilder<ContainerResource> container,
        ImagePullPolicy? policy)
    {
        if (policy is { } configuredPolicy)
        {
            container.WithImagePullPolicy(configuredPolicy);
        }
    }

    private static void AddImagePublishInstaller(
        IDistributedApplicationBuilder builder,
        string resourceName,
        ModuleContainerExportOptions options,
        ModuleImagePublishPlan publishPlan,
        string repositoryPath,
        string publishWorkingDirectory,
        bool imported,
        string? repository,
        bool updateRepository,
        IResourceBuilder<ContainerResource> container,
        ModuleApplicationRegistry registry)
    {
        var installerResource = new ModuleRepositoryInstallerResource(
            GetInstallerName(resourceName),
            repositoryPath,
            repository,
            imported && updateRepository,
            options.PublishCommand,
            publishPlan.PublishArguments,
            publishWorkingDirectory,
            publishPlan.ImageReference,
            publishPlan.RepositoryDirty);

        var installer = builder.AddResource(installerResource)
            .WithArgs(publishPlan.PublishArguments.ToArray())
            .WithEnvironment(
                "ASPIRE_MODULE_IMAGE",
                publishPlan.ImageReference)
            .WithParentRelationship(container.Resource)
            .ExcludeFromManifest()
            .WithCertificateTrustScope(CertificateTrustScope.None)
            .WithIconName("ArrowDownload");

        container
            .WaitForCompletion(installer)
            .WithAnnotation(new ModuleRepositoryInstallerAnnotation(installerResource));

        registry.TrackResource(installer.Resource);
    }

    private static void ConfigureRepositorySynchronization(
        IDistributedApplicationBuilder builder,
        ModuleApplicationRegistry registry,
        string repositoryPath,
        string? repository,
        IResourceBuilder<ParameterResource>? repositoryParameter,
        bool imported,
        bool updateRepository)
    {
        if (!builder.ExecutionContext.IsRunMode || !imported ||
            (string.IsNullOrWhiteSpace(repository) && repositoryParameter is null))
        {
            return;
        }

        builder.Eventing.Subscribe<BeforeStartEvent>(async (_, cancellationToken) =>
        {
            var resolvedRepository = repository ??
                await repositoryParameter!.Resource.GetValueAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(resolvedRepository))
            {
                throw new InvalidOperationException(
                    $"A Git repository is required for imported module content at '{repositoryPath}'.");
            }

            await registry.SynchronizeRepositoryAsync(
                repositoryPath,
                () => RepositorySynchronizer.SynchronizeAsync(
                    repositoryPath,
                    resolvedRepository,
                    updateRepository,
                    cancellationToken)).ConfigureAwait(false);
        });
    }

    private static ModuleApplicationRegistry GetOrCreateRegistry(IDistributedApplicationBuilder builder)
    {
        var existing = builder.Services
            .LastOrDefault(descriptor => descriptor.ServiceType == typeof(IDistributedApplicationModuleCatalog))?
            .ImplementationInstance as ModuleApplicationRegistry;

        if (existing is not null)
        {
            return existing;
        }

        var options = ModularAppHostsOptions.FromConfiguration(builder.Configuration);
        var registry = new ModuleApplicationRegistry(options);
        builder.Services.AddSingleton<IDistributedApplicationModuleCatalog>(registry);
        builder.Services.AddSingleton<IOptions<ModularAppHostsOptions>>(Options.Create(options));
        return registry;
    }

    private static string GetImportedRepositoryPath(
        IDistributedApplicationBuilder builder,
        ModularAppHostsOptions options,
        string moduleName)
    {
        var configuredLocation = GetConfiguredValue(options.RepositoryBasePath) ??
            builder.Configuration[$"Parameters:{RepositoryBaseLocationParameterName}"];
        var defaultLocation = Path.Combine(builder.AppHostDirectory, ".aspire", "module-repositories");
        var baseLocation = Path.GetFullPath(configuredLocation ?? defaultLocation, builder.AppHostDirectory);
        Directory.CreateDirectory(baseLocation);
        return Path.Combine(baseLocation, GetSafeDirectoryName(moduleName));
    }

    private static IResourceBuilder<ParameterResource> GetOrCreateRepositoryParameter(
        IDistributedApplicationBuilder builder,
        ModuleApplicationRegistry registry,
        string moduleName,
        string configurationKey)
    {
        var parameterName = GetRepositoryParameterName(moduleName);
        if (!builder.TryCreateResourceBuilder<ParameterResource>(parameterName, out var parameter))
        {
            parameter = builder
                .AddParameterFromConfiguration(parameterName, configurationKey)
                .WithDescription($"Git repository used to import module '{moduleName}'.");

#pragma warning disable ASPIREINTERACTION001
            parameter.WithCustomInput(resource => new InteractionInput
            {
                Name = resource.Name,
                Label = $"{moduleName} repository",
                Description = resource.Description,
                InputType = InputType.Text,
                Required = true,
                Placeholder = "https://github.com/organization/repository.git"
            });
#pragma warning restore ASPIREINTERACTION001
        }

        registry.TrackResource(parameter.Resource);
        return parameter;
    }

    private static string GetLocalRepositoryPath(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        string? repository)
    {
        if (module.ProjectDefinitions.Count > 0)
        {
            return module.ProjectDefinitions[0].SourceRepositoryRoot;
        }

        if (!string.IsNullOrWhiteSpace(repository) &&
            (!Uri.TryCreate(repository, UriKind.Absolute, out var repositoryUri) || repositoryUri.IsFile))
        {
            var candidate = Path.GetFullPath(repository, builder.AppHostDirectory);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return builder.AppHostDirectory;
    }

    private static void ValidateResourceNames(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        ModuleApplicationRegistry registry,
        ModularAppHostsOptions options,
        DistributedApplicationModuleOptions? moduleOptions)
    {
        var resourceNames = module.ResourceDefinitions.SelectMany(definition =>
            RequiresImagePublishInstaller(definition, options, moduleOptions) &&
                builder.ExecutionContext.IsRunMode
                ? new[] { definition.Name, GetInstallerName(definition.Name) }
                : new[] { definition.Name });

        foreach (var resourceName in resourceNames)
        {
            var existing = builder.Resources.FirstOrDefault(resource =>
                string.Equals(resource.Name, resourceName, StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
            {
                var tracked = registry.TryGetResource(resourceName, out _)
                    ? " and is already tracked by the module registry"
                    : string.Empty;
                throw new InvalidOperationException(
                    $"Cannot materialize module '{module.Name}' because resource '{resourceName}' already exists{tracked}.");
            }
        }
    }

    private static bool RequiresImagePublishInstaller(
        IDistributedApplicationModuleResource definition,
        ModularAppHostsOptions options,
        DistributedApplicationModuleOptions? moduleOptions)
    {
        return definition switch
        {
            DistributedApplicationModuleProject project =>
                (projectOptions(project)?.RunAsContainer ??
                    moduleOptions?.RunProjectsAsContainers ??
                    options.RunProjectsAsContainers) &&
                (projectOptions(project)?.PublishImage ?? moduleOptions?.PublishImages ?? options.PublishImages),
            DistributedApplicationModuleContainer container when container.ImagePublishOptions is not null =>
                moduleOptions?.FindContainer(container.Name)?.PublishImage ??
                    moduleOptions?.PublishImages ??
                    options.PublishImages,
            _ => false
        };

        DistributedApplicationModuleProjectOptions? projectOptions(DistributedApplicationModuleProject project) =>
            moduleOptions?.FindProject(project.Name);
    }

    private static string? GetConfiguredValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static string GetContainedPath(string root, string path, string parameterName)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(path, root);
        if (!candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(candidate.TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentOutOfRangeException(parameterName, path, "The path must remain inside the module repository.");
        }

        return candidate;
    }

    private static string GetInstallerName(string projectName) => $"{projectName}-installer";

    private static string GetSafeDirectoryName(string name)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars().ToHashSet();
        var safeName = new string(name.Select(character =>
            invalidCharacters.Contains(character) || character is '/' or '\\' ? '-' : character).ToArray());
        return safeName.Length == 0 ? "module" : safeName;
    }
}
