using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Publishing;
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

        var registry = GetOrCreateRegistry(builder);
        registry.Configure(configure);
        ValidateOptions(registry.Options);
        return builder;
    }

    /// <summary>Runs every exported project directly in Aspire run mode for local debugging.</summary>
    public static IDistributedApplicationBuilder UseLocalModuleProjects(
        this IDistributedApplicationBuilder builder)
    {
        return builder.ConfigureModularAppHosts(options => options.ProjectMode = ModuleProjectMode.Project);
    }

    /// <summary>Runs every exported project through its portable container representation.</summary>
    public static IDistributedApplicationBuilder UseModuleContainers(
        this IDistributedApplicationBuilder builder)
    {
        return builder.ConfigureModularAppHosts(options => options.ProjectMode = ModuleProjectMode.Container);
    }

    /// <summary>Opts into executing module-declared image build commands in Aspire run mode.</summary>
    public static IDistributedApplicationBuilder BuildModuleImages(
        this IDistributedApplicationBuilder builder)
    {
        return builder.ConfigureModularAppHosts(options => options.PublishImages = true);
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
        return DefineModule(builder, name, "1", moduleBuilder);
    }

    /// <summary>Defines a versioned module contract without adding its resources to the application model.</summary>
    public static IDistributedApplicationModule DefineModule(
        this IDistributedApplicationBuilder builder,
        string name,
        string version,
        Action<IDistributedApplicationModuleBuilder> moduleBuilder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentNullException.ThrowIfNull(moduleBuilder);

        var registry = GetOrCreateRegistry(builder);
        registry.RefreshConfiguration();
        ValidateOptions(registry.Options);
        if (registry.TryGetDefinition(name, out var existingModule) && existingModule is not null)
        {
            if (!string.Equals(existingModule.Version, version, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Module '{name}' is already defined with contract version '{existingModule.Version}', " +
                    $"not requested version '{version}'.");
            }

            return existingModule;
        }

        var gitExecutablePath = GetConfiguredValue(registry.Options.GitExecutablePath) ?? "git";
        var module = new DistributedApplicationModule(builder, name, version);
        moduleBuilder(new DistributedApplicationModuleBuilder(
            builder,
            module,
            gitExecutablePath,
            registry.Options.RepositoryCommandTimeout));
        module.Validate(
            gitExecutablePath,
            registry.Options.RepositoryCommandTimeout);
        ValidateModuleConfiguration(module, registry.Options.FindModule(module.Name));
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

        Materialize(builder, typedModule, registry, imported: false);
        return module;
    }

    /// <summary>Imports an exported module by name using a managed Git clone.</summary>
    public static IDistributedApplicationModule ImportModule(
        this IDistributedApplicationBuilder builder,
        string name)
    {
        return ImportModule(builder, name, new ModuleImportOptions());
    }

    /// <summary>Imports an exported module by name with resource aliases or a common prefix.</summary>
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
                $"Module '{name}' has not been exported. Call ExportModule before ImportModule.");
        }

        Materialize(builder, module, registry, imported: true, importOptions);
        return module;
    }

    private static void Materialize(
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
        var repositoryConfigurationKey = GetRepositoryConfigurationKey(module.Name);
        var configuredRepository = GetConfiguredValue(builder.Configuration[repositoryConfigurationKey]);
        var repository = configuredRepository ?? GetConfiguredValue(moduleOptions?.Repository) ?? module.Repository;
        var repositoryRevision = GetConfiguredValue(moduleOptions?.RepositoryRevision) ?? module.RepositoryRevision;
        var requiresRepository = module.ProjectDefinitions.Count > 0 || module.RequiresRepositoryContent;
        ValidateModuleConfiguration(module, moduleOptions);
        var resourceNames = new ModuleResourceNameMap(module, imported ? importOptions : null);

        var autoCloneRepository = moduleOptions?.AutoCloneRepository ?? options.AutoCloneRepositories;
        var repositoryResolution = autoCloneRepository &&
            (requiresRepository || !string.IsNullOrWhiteSpace(repository))
            ? ModuleRepositoryDiscovery.Resolve(
                builder.AppHostDirectory,
                module,
                repository,
                GetConfiguredValue(options.GitHubCliPath) ?? "gh",
                options.RepositoryCommandTimeout,
                GetConfiguredValue(options.GitExecutablePath) ?? "git")
            : null;
        var repositoryParameter = imported &&
            (configuredRepository is not null ||
                (!autoCloneRepository && requiresRepository && string.IsNullOrWhiteSpace(repository)))
            ? GetOrCreateRepositoryParameter(
                builder,
                registry,
                module.Name,
                repositoryConfigurationKey)
            : null;
        var repositoryPath = repositoryResolution?.RepositoryPath ??
            (imported &&
                (requiresRepository || repositoryParameter is not null || !string.IsNullOrWhiteSpace(repository))
                ? GetImportedRepositoryPath(builder, options, module.Name)
                : GetLocalRepositoryPath(builder, module, repository));
        var updateRepository =
            (moduleOptions?.UpdateRepository ?? options.UpdateImportedRepositories) &&
            repositoryResolution?.UsesSiblingLayout is not false;

        var synchronizedRepository =
            ((repositoryResolution?.UsesSiblingLayout == true && !string.IsNullOrWhiteSpace(repositoryRevision)) ||
             (builder.ExecutionContext.IsRunMode && imported)) &&
            repositoryResolution?.UsesSiblingLayout is not false &&
            !string.IsNullOrWhiteSpace(repository) &&
            RepositoryInspector.IsGitRepository(
                repositoryPath,
                GetConfiguredValue(options.GitExecutablePath) ?? "git",
                options.RepositoryCommandTimeout);
        if (synchronizedRepository)
        {
            registry.SynchronizeRepositoryAsync(
                    repositoryPath,
                    () => RepositorySynchronizer.SynchronizeAsync(
                        repositoryPath,
                        repository,
                        updateRepository,
                        CancellationToken.None,
                        repositoryRevision,
                        GetConfiguredValue(options.GitExecutablePath) ?? "git",
                        options.RepositoryCommandTimeout))
                .GetAwaiter()
                .GetResult();
        }

        var repositoryDirty = RepositoryInspector.IsDirty(
            repositoryPath,
            GetConfiguredValue(options.GitExecutablePath) ?? "git",
            options.RepositoryCommandTimeout,
            requireSuccessfulInspection: true);
        var defaultImageTag = GetDefaultImageTag(builder, repositoryPath, options);

        ValidateResourceNames(builder, module, registry, options, moduleOptions, imported, resourceNames);
        if (repositoryResolution is not null || !imported)
        {
            ValidateProjectFiles(module, repositoryPath);
        }

        ConfigureRepositorySynchronization(
            builder,
            registry,
            module,
            repositoryPath,
            repositoryParameter is null ? repository : null,
            repositoryParameter,
            imported,
            updateRepository,
            repositoryRevision,
            GetConfiguredValue(options.GitExecutablePath) ?? "git",
            options.RepositoryCommandTimeout,
            deferSynchronization: !synchronizedRepository);

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
                        repositoryPath,
                        repositoryDirty,
                        defaultImageTag,
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
                        resourceNames[container.Name],
                        repositoryPath,
                        repositoryDirty,
                        defaultImageTag,
                        imported,
                        registry,
                        options,
                        moduleOptions,
                        repository,
                        updateRepository);
                    break;
                case IDistributedApplicationModuleFactoryResource resource:
                    MaterializeResource(
                        builder,
                        module,
                        resource,
                        resourceNames[resource.Name],
                        repositoryPath,
                        imported,
                        registry);
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
        string repositoryPath,
        bool repositoryDirty,
        string defaultImageTag,
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
            ResolveProjectMode(options, moduleOptions, projectOptions, imported) == ModuleProjectMode.Container;

        if (!runAsContainer)
        {
            MaterializeProjectResource(
                builder,
                module,
                project,
                resourceName,
                projectOptions,
                repositoryPath,
                imported,
                registry);
            return;
        }

        var effectiveExportOptions = ApplyImageOptions(export.Options, projectOptions, defaultImageTag);
        var publishImage = projectOptions?.PublishImage ?? moduleOptions?.PublishImages ?? options.PublishImages;
        var workingDirectoryRelativePath = effectiveExportOptions.WorkingDirectory ?? projectDirectoryRelativePath;
        var sourceWorkingDirectory = PathSafety.GetContainedPath(
            project.SourceRepositoryRoot,
            workingDirectoryRelativePath,
            nameof(ModuleContainerExportOptions.WorkingDirectory));
        var normalizedWorkingDirectoryRelativePath = Path.GetRelativePath(
            project.SourceRepositoryRoot,
            sourceWorkingDirectory);
        var publishWorkingDirectory = PathSafety.GetContainedPath(
            repositoryPath,
            normalizedWorkingDirectoryRelativePath,
            nameof(ModuleContainerExportOptions.WorkingDirectory));
        var publishPlan = CreateImagePublishPlan(
            builder,
            effectiveExportOptions,
            repositoryDirty && publishImage,
            inspectExistingImage: publishImage);

        var container = builder
            .AddContainer(resourceName, publishPlan.ImageName, publishPlan.ImageTag)
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
                resourceName,
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
        module.TrackMaterializedResource(builder, project.Name, container.Resource);
    }

    private static void MaterializeContainer(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        DistributedApplicationModuleContainer definition,
        string resourceName,
        string repositoryPath,
        bool repositoryDirty,
        string defaultImageTag,
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
            : ApplyImageOptions(definition.ImagePublishOptions, containerOptions, defaultImageTag);
        ValidatePublishOverrides(definition, containerOptions);
        var publishImage = publishOptions is not null &&
            (containerOptions?.PublishImage ?? moduleOptions?.PublishImages ?? options.PublishImages);
        var publishPlan = publishOptions is null
            ? null
            : CreateImagePublishPlan(
                builder,
                publishOptions,
                repositoryDirty && publishImage,
                inspectExistingImage: publishImage);
        var container = builder
            .AddContainer(
                resourceName,
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
            var publishWorkingDirectory = PathSafety.GetContainedPath(
                repositoryPath,
                publishOptions!.WorkingDirectory ?? ".",
                nameof(ModuleContainerExportOptions.WorkingDirectory));
            AddImagePublishInstaller(
                builder,
                resourceName,
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
        module.TrackMaterializedResource(builder, definition.Name, container.Resource);
    }

    private static void MaterializeProjectResource(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        DistributedApplicationModuleProject project,
        string resourceName,
        DistributedApplicationModuleProjectOptions? options,
        string repositoryPath,
        bool imported,
        ModuleApplicationRegistry registry)
    {
        var projectRelativePath = Path.GetRelativePath(project.SourceRepositoryRoot, project.ProjectPath);
        var materializedProjectPath = PathSafety.GetContainedPath(repositoryPath, projectRelativePath, nameof(project.ProjectPath));
        if (!File.Exists(materializedProjectPath))
        {
            throw new InvalidOperationException(
                $"Project '{project.Name}' is configured to run directly, but '{materializedProjectPath}' does not exist. " +
                "Use an existing managed checkout or run the exported container instead.");
        }

        var resource = builder
            .AddProject(resourceName, materializedProjectPath, projectOptions =>
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
        module.TrackMaterializedResource(builder, project.Name, resource.Resource);
    }

    private static void MaterializeResource(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        IDistributedApplicationModuleFactoryResource definition,
        string resourceName,
        string repositoryPath,
        bool imported,
        ModuleApplicationRegistry registry)
    {
        var context = new DistributedApplicationModuleResourceContext(
            builder,
            module,
            resourceName,
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
        module.TrackMaterializedResource(builder, definition.Name, resource);
    }

    private static ModuleImagePublishPlan CreateImagePublishPlan(
        IDistributedApplicationBuilder builder,
        ModuleContainerExportOptions options,
        bool useDirtyImage,
        bool inspectExistingImage = true)
    {
        return ModuleImagePublishPlan.Create(
            options,
            useDirtyImage,
            builder.ExecutionContext.IsRunMode && inspectExistingImage
                ? ContainerImageInspector.Exists
                : _ => false);
    }

    private static ModuleContainerExportOptions ApplyImageOptions(
        ModuleContainerExportOptions declared,
        DistributedApplicationModuleImageOptions? configured,
        string defaultImageTag)
    {
        var imageName = GetConfiguredValue(configured?.ImageName) ?? declared.ImageName;
        var imageTag = GetConfiguredValue(configured?.ImageTag) ??
            GetConfiguredValue(declared.ImageTag) ??
            defaultImageTag;
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
        DistributedApplicationModule module,
        string repositoryPath,
        string? repository,
        IResourceBuilder<ParameterResource>? repositoryParameter,
        bool imported,
        bool updateRepository,
        string? repositoryRevision,
        string gitExecutablePath,
        TimeSpan repositoryCommandTimeout,
        bool deferSynchronization)
    {
        if (!builder.ExecutionContext.IsRunMode || !imported ||
            !deferSynchronization ||
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
                    cancellationToken,
                    repositoryRevision,
                    gitExecutablePath,
                    repositoryCommandTimeout)).ConfigureAwait(false);

            ValidateProjectFiles(module, repositoryPath);
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
        ValidateOptions(options);
        var registry = new ModuleApplicationRegistry(options, builder.Configuration);
        builder.Services.AddSingleton<IDistributedApplicationModuleCatalog>(registry);
        builder.Services.AddSingleton<IOptions<ModularAppHostsOptions>>(Options.Create(options));
        builder.Eventing.Subscribe<BeforeStartEvent>((_, _) =>
        {
            registry.RefreshConfiguration();
            ValidateOptions(registry.Options);
            registry.ValidateConfiguredModules();
            return Task.CompletedTask;
        });
        builder.Eventing.Subscribe<BeforePublishEvent>((_, _) =>
        {
            registry.RefreshConfiguration();
            ValidateOptions(registry.Options);
            registry.ValidateConfiguredModules();
            return Task.CompletedTask;
        });
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
        DistributedApplicationModuleOptions? moduleOptions,
        bool imported,
        ModuleResourceNameMap resourceNames)
    {
        var plannedResourceNames = module.ResourceDefinitions.SelectMany(definition =>
            RequiresImagePublishInstaller(definition, options, moduleOptions, imported) &&
                builder.ExecutionContext.IsRunMode
                ? new[] { resourceNames[definition.Name], GetInstallerName(resourceNames[definition.Name]) }
                : new[] { resourceNames[definition.Name] })
            .ToArray();

        var duplicateResourceName = plannedResourceNames
            .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicateResourceName is not null)
        {
            throw new InvalidOperationException(
                $"Cannot materialize module '{module.Name}' because its aliases, prefix, and installer names " +
                $"produce duplicate resource '{duplicateResourceName}'.");
        }

        foreach (var resourceName in plannedResourceNames)
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

    private static void ValidateModuleConfiguration(
        DistributedApplicationModule module,
        DistributedApplicationModuleOptions? configured)
    {
        if (configured is null)
        {
            return;
        }

        var projectNames = module.ProjectDefinitions
            .Select(project => project.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var containerNames = module.ContainerDefinitions
            .Select(container => container.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missingProject = configured.Projects.Keys.FirstOrDefault(name => !projectNames.Contains(name));
        if (missingProject is not null)
        {
            throw new InvalidOperationException(
                $"Configuration for module '{module.Name}' references project service '{missingProject}', but no " +
                $"exported project with that name was found. Available projects: {FormatNames(projectNames)}.");
        }

        var missingContainer = configured.Containers.Keys.FirstOrDefault(name => !containerNames.Contains(name));
        if (missingContainer is not null)
        {
            throw new InvalidOperationException(
                $"Configuration for module '{module.Name}' references container service '{missingContainer}', but no " +
                $"exported container with that name was found. Available containers: {FormatNames(containerNames)}.");
        }
    }

    private static void ValidateProjectFiles(
        DistributedApplicationModule module,
        string repositoryPath)
    {
        foreach (var project in module.ProjectDefinitions)
        {
            var relativePath = Path.GetRelativePath(project.SourceRepositoryRoot, project.ProjectPath);
            var materializedPath = PathSafety.GetContainedPath(repositoryPath, relativePath, nameof(project.ProjectPath));
            if (!File.Exists(materializedPath))
            {
                throw new InvalidOperationException(
                    $"Module '{module.Name}' declares project service '{project.Name}', but its project file was not " +
                    $"found at '{materializedPath}' in discovered repository '{repositoryPath}'.");
            }
        }
    }

    private static string GetDefaultImageTag(
        IDistributedApplicationBuilder builder,
        string repositoryPath,
        ModularAppHostsOptions options)
    {
        var gitExecutablePath = GetConfiguredValue(options.GitExecutablePath) ?? "git";
        var branch = RepositoryInspector.TryGetBranch(
            repositoryPath,
            gitExecutablePath,
            options.RepositoryCommandTimeout);
        var commit = RepositoryInspector.TryGetCommit(
            repositoryPath,
            gitExecutablePath,
            options.RepositoryCommandTimeout);
        if ((branch is null || commit is null) &&
            RepositoryInspector.TryFindRepositoryRoot(
                builder.AppHostDirectory,
                out var appHostRepositoryRoot,
                gitExecutablePath,
                options.RepositoryCommandTimeout))
        {
            branch ??= RepositoryInspector.TryGetBranch(
                appHostRepositoryRoot,
                gitExecutablePath,
                options.RepositoryCommandTimeout);
            commit ??= RepositoryInspector.TryGetCommit(
                appHostRepositoryRoot,
                gitExecutablePath,
                options.RepositoryCommandTimeout);
        }

        branch ??= GetConfiguredValue(Environment.GetEnvironmentVariable("GITHUB_HEAD_REF"));
        branch ??= GetConfiguredValue(Environment.GetEnvironmentVariable("GITHUB_REF_NAME"));
        return ModuleImageTag.FromRepository(branch, commit);
    }

    private static string FormatNames(IEnumerable<string> names)
    {
        var values = names.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        return values.Length == 0 ? "(none)" : string.Join(", ", values.Select(name => $"'{name}'"));
    }

    private static bool RequiresImagePublishInstaller(
        IDistributedApplicationModuleResource definition,
        ModularAppHostsOptions options,
        DistributedApplicationModuleOptions? moduleOptions,
        bool imported)
    {
        return definition switch
        {
            DistributedApplicationModuleProject project =>
                ResolveProjectMode(options, moduleOptions, projectOptions(project), imported) == ModuleProjectMode.Container &&
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

    private static ModuleProjectMode ResolveProjectMode(
        ModularAppHostsOptions options,
        DistributedApplicationModuleOptions? moduleOptions,
        DistributedApplicationModuleProjectOptions? projectOptions,
        bool imported)
    {
#pragma warning disable CS0618
        var mode = projectOptions?.ProjectMode ??
            (projectOptions?.RunAsContainer is { } projectRunsAsContainer
                ? (ModuleProjectMode?)(projectRunsAsContainer
                    ? ModuleProjectMode.Container
                    : ModuleProjectMode.Project)
                : null) ??
            moduleOptions?.ProjectMode ??
            (moduleOptions?.RunProjectsAsContainers is { } moduleRunsAsContainers
                ? (ModuleProjectMode?)(moduleRunsAsContainers
                    ? ModuleProjectMode.Container
                    : ModuleProjectMode.Project)
                : null) ??
            (options.RunProjectsAsContainers ? ModuleProjectMode.Container : options.ProjectMode);
#pragma warning restore CS0618

        return mode == ModuleProjectMode.Auto
            ? imported ? ModuleProjectMode.Container : ModuleProjectMode.Project
            : mode;
    }

    private static string? GetConfiguredValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static string GetMaterializationKey(bool imported, ModuleImportOptions? options)
    {
        var aliases = options?.ResourceAliases
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => $"{pair.Key}={pair.Value}") ?? [];
        return $"{imported}|{options?.ResourcePrefix}|{string.Join("|", aliases)}";
    }

    private static void ValidateOptions(ModularAppHostsOptions options)
    {
        if (options.RepositoryCommandTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"{ModularAppHostsOptions.ConfigurationSectionName}:{nameof(options.RepositoryCommandTimeout)} must be positive.");
        }

        ValidateEnum(
            options.ProjectMode,
            $"{ModularAppHostsOptions.ConfigurationSectionName}:{nameof(options.ProjectMode)}");

        foreach (var (moduleName, module) in options.Modules)
        {
            var moduleKey = $"{ModularAppHostsOptions.ConfigurationSectionName}:{nameof(options.Modules)}:{moduleName}";
            if (module.ProjectMode is { } moduleMode)
            {
                ValidateEnum(moduleMode, $"{moduleKey}:{nameof(module.ProjectMode)}");
            }

            foreach (var (projectName, project) in module.Projects)
            {
                var projectKey = $"{moduleKey}:{nameof(module.Projects)}:{projectName}";
                if (project.ProjectMode is { } projectMode)
                {
                    ValidateEnum(projectMode, $"{projectKey}:{nameof(project.ProjectMode)}");
                }

                if (project.ImagePullPolicy is { } projectPullPolicy)
                {
                    ValidateEnum(projectPullPolicy, $"{projectKey}:{nameof(project.ImagePullPolicy)}");
                }
            }

            foreach (var (containerName, container) in module.Containers)
            {
                if (container.ImagePullPolicy is { } containerPullPolicy)
                {
                    ValidateEnum(
                        containerPullPolicy,
                        $"{moduleKey}:{nameof(module.Containers)}:{containerName}:{nameof(container.ImagePullPolicy)}");
                }
            }
        }
    }

    private static void ValidateEnum<TEnum>(TEnum value, string configurationKey)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new InvalidOperationException(
                $"{configurationKey} has unsupported value '{value}'. Expected one of: " +
                $"{string.Join(", ", Enum.GetNames<TEnum>())}.");
        }
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
