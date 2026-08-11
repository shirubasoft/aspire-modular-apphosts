using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Publishing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.ModularAppHosts;

/// <summary>Extensions for composing reusable modules in an Aspire AppHost.</summary>
public static partial class DistributedApplicationModuleExtensions
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
    public static string GetRepositoryParameterName(
        this IDistributedApplicationBuilder builder,
        string moduleName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);
        return $"module-{ModuleRepositoryIdentity.GetCanonicalName(null, moduleName, builder.AppHostDirectory)}-repository";
    }

    /// <summary>Gets the repository-specific Aspire parameter name used to import a module.</summary>
    public static string GetRepositoryParameterName(
        this IDistributedApplicationBuilder builder,
        string repository,
        string moduleName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);
        return $"module-{ModuleRepositoryIdentity.GetCanonicalName(repository, moduleName, builder.AppHostDirectory)}-repository";
    }

    /// <summary>Gets the configuration key used to resolve an imported module's Git repository.</summary>
    public static string GetRepositoryConfigurationKey(string moduleName)
    {
        return $"{GetModuleConfigurationKey(moduleName)}:Repository";
    }

    /// <summary>Gets the conventional configuration section key for a module.</summary>
    public static string GetModuleConfigurationKey(string moduleName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);
        return $"{ModularAppHostsOptions.ConfigurationSectionName}:Modules:{moduleName}";
    }

    /// <summary>Exports a named module definition without adding its services to the application model.</summary>
    public static Task<IDistributedApplicationModule> ExportModuleAsync(
        this IDistributedApplicationBuilder builder,
        string name,
        Action<IDistributedApplicationModuleBuilder> moduleBuilder,
        CancellationToken cancellationToken = default)
    {
        return DefineModuleAsync(builder, name, "1", packageId: null, moduleBuilder, cancellationToken);
    }

    /// <summary>Exports a named module definition with its NuGet contract package identity.</summary>
    public static Task<IDistributedApplicationModule> ExportModuleAsync(
        this IDistributedApplicationBuilder builder,
        string name,
        string packageId,
        Action<IDistributedApplicationModuleBuilder> moduleBuilder,
        CancellationToken cancellationToken = default)
    {
        return DefineModuleAsync(builder, name, "1", packageId, moduleBuilder, cancellationToken);
    }

    /// <summary>Defines a versioned module contract without adding its resources to the application model.</summary>
    public static async Task<IDistributedApplicationModule> DefineModuleAsync(
        this IDistributedApplicationBuilder builder,
        string name,
        string version,
        Action<IDistributedApplicationModuleBuilder> moduleBuilder,
        CancellationToken cancellationToken = default)
    {
        return await DefineModuleAsync(
            builder,
            name,
            version,
            packageId: null,
            moduleBuilder,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Defines a versioned module contract with its NuGet package identity.</summary>
    public static async Task<IDistributedApplicationModule> DefineModuleAsync(
        this IDistributedApplicationBuilder builder,
        string name,
        string version,
        string? packageId,
        Action<IDistributedApplicationModuleBuilder> moduleBuilder,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        if (packageId is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
            if (packageId.Length > 100 || packageId.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_')))
            {
                throw new ArgumentException(
                    $"'{packageId}' is not a valid NuGet package ID.",
                    nameof(packageId));
            }
        }
        ArgumentNullException.ThrowIfNull(moduleBuilder);

        var registry = GetOrCreateRegistry(builder);
        return await registry.RunModuleOperationAsync(async () =>
        {
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

                if (!string.Equals(existingModule.PackageId, packageId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Module '{name}' is already defined with contract package ID " +
                        $"'{existingModule.PackageId ?? "none"}', not requested package ID '{packageId ?? "none"}'.");
                }

                return existingModule;
            }

            var gitExecutablePath = GetConfiguredValue(registry.Options.GitExecutablePath) ?? "git";
            var module = new DistributedApplicationModule(builder, name, version, packageId);
            moduleBuilder(new DistributedApplicationModuleBuilder(builder, module, registry));
            await module.ValidateAsync(
                gitExecutablePath,
                registry.Options.RepositoryCommandTimeout,
                cancellationToken).ConfigureAwait(false);
            ValidateModuleConfiguration(module, registry.Options.FindModule(module.Name));
            registry.AddModule(module);
            return module;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Adds an exported module using its local source worktree.</summary>
    public static async Task<IDistributedApplicationModule> AddAsync(
        this IDistributedApplicationBuilder builder,
        IDistributedApplicationModule module,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(module);

        if (module is not DistributedApplicationModule typedModule)
        {
            throw new ArgumentException(
                "The module must have been created by ExportModuleAsync on this extension.", nameof(module));
        }

        if (!ReferenceEquals(typedModule.DefinitionApplicationBuilder, builder))
        {
            throw new ArgumentException(
                "The module definition belongs to a different distributed application builder. " +
                "Define and materialize the module on the same AppHost builder.",
                nameof(module));
        }

        var registry = GetOrCreateRegistry(builder);
        return await registry.RunModuleOperationAsync(async () =>
        {
            if (!registry.TryGetDefinition(typedModule.Name, out _))
            {
                registry.AddModule(typedModule);
            }

            await MaterializeAsync(
                builder,
                typedModule,
                registry,
                imported: false,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return module;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Imports an exported module by name using a managed Git clone.</summary>
    public static Task<IDistributedApplicationModule> ImportModuleAsync(
        this IDistributedApplicationBuilder builder,
        string name,
        CancellationToken cancellationToken = default)
    {
        return ImportModuleAsync(builder, name, new ModuleImportOptions(), cancellationToken);
    }

    /// <summary>Imports an exported module by name with resource aliases or a common prefix.</summary>
    public static async Task<IDistributedApplicationModule> ImportModuleAsync(
        this IDistributedApplicationBuilder builder,
        string name,
        ModuleImportOptions importOptions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(importOptions);

        var registry = GetOrCreateRegistry(builder);
        return await registry.RunModuleOperationAsync(async () =>
        {
            if (!registry.TryGetDefinition(name, out var module) || module is null)
            {
                throw new InvalidOperationException(
                    $"Module '{name}' has not been exported. Call ExportModuleAsync before ImportModuleAsync.");
            }

            await MaterializeAsync(
                builder,
                module,
                registry,
                imported: true,
                importOptions,
                cancellationToken).ConfigureAwait(false);
            return module;
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task MaterializeAsync(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        ModuleApplicationRegistry registry,
        bool imported,
        ModuleImportOptions? importOptions = null,
        CancellationToken cancellationToken = default)
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
        var repository = configuredRepository ??
            GetConfiguredValue(moduleOptions?.Repository) ??
            module.Repository;
        if (!string.IsNullOrWhiteSpace(repository) &&
            !GitHubRepositoryCloner.IsRemoteRepository(repository, builder.AppHostDirectory))
        {
            repository = Path.GetFullPath(repository, builder.AppHostDirectory);
        }

        var repositoryRevision = GetConfiguredValue(moduleOptions?.RepositoryRevision) ??
            module.RepositoryRevision;
        var factoryRequiresRepository = module.ExplicitlyRequiresRepositoryContent &&
            module.ResourceDefinitions.Any(resource => resource is IDistributedApplicationModuleFactoryResource);
        var containerPublishersRequireModuleRepository =
            await ContainerPublishersRequireModuleRepositoryAsync(
                    builder,
                    module,
                    options,
                    moduleOptions,
                    repository,
                    repositoryRevision,
                    cancellationToken)
                .ConfigureAwait(false);
        var requiresRepository = module.ProjectDefinitions.Any(project =>
                !UsesExternalImage(moduleOptions?.FindProject(project.Name))) ||
            module.ExplicitlyRequiresRepositoryContent ||
            containerPublishersRequireModuleRepository;
        ValidateModuleConfiguration(module, moduleOptions);
        var resourceNames = new ModuleResourceNameMap(module, imported ? importOptions : null);

        var autoCloneRepository = moduleOptions?.AutoCloneRepository ?? options.AutoCloneRepositories;
        var useIsolatedRevisionCheckout = imported && !string.IsNullOrWhiteSpace(repositoryRevision);
        var repositoryResolution = !useIsolatedRevisionCheckout &&
            autoCloneRepository &&
            (requiresRepository || !string.IsNullOrWhiteSpace(repository))
            ? await ModuleRepositoryDiscovery.ResolveAsync(
                builder.AppHostDirectory,
                module,
                repository,
                GetConfiguredValue(options.GitHubCliPath) ?? "gh",
                options.RepositoryCommandTimeout,
                GetConfiguredValue(options.GitExecutablePath) ?? "git",
                cancellationToken).ConfigureAwait(false)
            : null;
        if (imported && factoryRequiresRepository && repositoryResolution is null &&
            string.IsNullOrWhiteSpace(repository))
        {
            throw new InvalidOperationException(
                $"Imported module '{module.Name}' uses a repository-backed resource factory, so its repository " +
                $"must be available while the application model is constructed. Configure " +
                $"'{repositoryConfigurationKey}' or call WithRepository() in the module definition.");
        }

        var existingSameWorktreeRepository = imported && !useIsolatedRevisionCheckout
            ? await TryGetExistingSameWorktreeRepositoryAsync(
                builder.AppHostDirectory,
                repository,
                GetConfiguredValue(options.GitExecutablePath) ?? "git",
                options.RepositoryCommandTimeout,
                cancellationToken).ConfigureAwait(false)
            : null;
        var repositoryParameter = imported &&
            (configuredRepository is not null ||
                (!autoCloneRepository && requiresRepository && string.IsNullOrWhiteSpace(repository)))
            ? GetOrCreateRepositoryParameter(
                builder,
                registry,
                module.Name,
                repository,
                repositoryConfigurationKey)
            : null;
        var repositoryPath = repositoryResolution?.RepositoryPath ??
            existingSameWorktreeRepository ??
            (imported &&
                (requiresRepository || repositoryParameter is not null || !string.IsNullOrWhiteSpace(repository))
                ? GetImportedRepositoryPath(builder, options, repository, module.Name)
                : await GetLocalRepositoryPathAsync(
                    builder,
                    module,
                    repository,
                    options,
                    cancellationToken).ConfigureAwait(false));
        var discoveredRepositoryRoot =
            await RepositoryInspector.TryFindRepositoryRootAsync(
                repositoryPath,
                GetConfiguredValue(options.GitExecutablePath) ?? "git",
                options.RepositoryCommandTimeout,
                cancellationToken).ConfigureAwait(false) ??
            Path.GetFullPath(repositoryPath);
        var repositorySynchronizationKey = PathSafety.AreEqual(discoveredRepositoryRoot, repositoryPath)
            ? discoveredRepositoryRoot
            : Path.GetFullPath(repositoryPath);
        var updateRepository =
            (moduleOptions?.UpdateRepository ?? options.UpdateImportedRepositories) &&
            repositoryResolution?.UsesSiblingLayout is not false &&
            existingSameWorktreeRepository is null;
        var repositorySynchronizationPolicy = RepositorySynchronizationPolicy.Create(
            updateRepository,
            repositoryRevision);

        var synchronizationRequired =
            ((repositoryResolution?.UsesSiblingLayout == true && !string.IsNullOrWhiteSpace(repositoryRevision)) ||
             (builder.ExecutionContext.IsRunMode && imported) ||
             (imported && factoryRequiresRepository)) &&
            repositoryResolution?.UsesSiblingLayout is not false &&
            !string.IsNullOrWhiteSpace(repository);
        var repositoryPathIsGit = synchronizationRequired &&
            await RepositoryInspector.IsGitRepositoryAsync(
                repositoryPath,
                GetConfiguredValue(options.GitExecutablePath) ?? "git",
                options.RepositoryCommandTimeout,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        var repositoryCanSupplyImageTag = synchronizationRequired &&
            RequiresRepositoryImageTag(module, options, moduleOptions, imported) &&
            (GitHubRepositoryCloner.IsRemoteRepository(repository!, builder.AppHostDirectory) ||
             await RepositoryInspector.IsGitRepositoryAsync(
                 repository!,
                 GetConfiguredValue(options.GitExecutablePath) ?? "git",
                 options.RepositoryCommandTimeout,
                 cancellationToken: cancellationToken).ConfigureAwait(false));
        if (synchronizationRequired &&
            (factoryRequiresRepository ||
                containerPublishersRequireModuleRepository ||
                repositoryPathIsGit ||
                repositoryCanSupplyImageTag))
        {
            await registry.SynchronizeRepositoryAsync(
                repositorySynchronizationKey,
                repositorySynchronizationPolicy,
                progress => RepositorySynchronizer.SynchronizeAsync(
                    repositoryPath,
                    repository,
                    updateRepository,
                    cancellationToken,
                    repositoryRevision,
                    GetConfiguredValue(options.GitExecutablePath) ?? "git",
                    GetConfiguredValue(options.GitHubCliPath) ?? "gh",
                    options.RepositoryCommandTimeout,
                    progress)).ConfigureAwait(false);
        }

        var repositoryDirty = requiresRepository &&
            await RepositoryInspector.IsDirtyAsync(
                repositoryPath,
                GetConfiguredValue(options.GitExecutablePath) ?? "git",
                options.RepositoryCommandTimeout,
                requireSuccessfulInspection: true,
                cancellationToken).ConfigureAwait(false);
        var defaultImageTag = !requiresRepository
            ? "latest"
            : await GetDefaultImageTagAsync(
                builder,
                repositoryPath,
                options,
                cancellationToken).ConfigureAwait(false);
        var definitionRepository = new MaterializedModuleRepository(
            repositoryPath,
            repository,
            repositoryRevision,
            imported && updateRepository,
            repositoryDirty,
            defaultImageTag,
            UsesModuleRepository: true);

        ValidateResourceNames(
            builder,
            module,
            registry,
            options,
            moduleOptions,
            imported,
            resourceNames,
            definitionRepository);
        if (repositoryResolution is not null || existingSameWorktreeRepository is not null || !imported)
        {
            ValidateProjectFiles(module, moduleOptions, repositoryPath);
        }

        ConfigureRepositorySynchronization(
            builder,
            registry,
            module,
            repositoryPath,
            repositorySynchronizationKey,
            repositorySynchronizationPolicy,
            repositoryParameter is null ? repository : null,
            repositoryParameter,
            imported,
            updateRepository,
            repositoryRevision,
            GetConfiguredValue(options.GitExecutablePath) ?? "git",
            options.RepositoryCommandTimeout);

        foreach (var definition in module.ResourceDefinitions)
        {
            switch (definition)
            {
                case DistributedApplicationModuleProject project:
                    await MaterializeProjectAsync(
                        builder,
                        module,
                        project,
                        resourceNames[project.Name],
                        definitionRepository,
                        imported,
                        registry,
                        options,
                        moduleOptions,
                        cancellationToken).ConfigureAwait(false);
                    break;
                case DistributedApplicationModuleContainer container:
                    await MaterializeContainerAsync(
                        builder,
                        module,
                        container,
                        resourceNames[container.Name],
                        definitionRepository,
                        imported,
                        registry,
                        options,
                        moduleOptions,
                        cancellationToken).ConfigureAwait(false);
                    break;
                case IDistributedApplicationModuleFactoryResource resource:
                    await MaterializeResourceAsync(
                        builder,
                        module,
                        resource,
                        resourceNames[resource.Name],
                        definitionRepository,
                        imported,
                        registry,
                        options,
                        moduleOptions,
                        cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Module resource definition '{definition.Name}' has an unsupported implementation type.");
            }
        }

        registry.MarkMaterialized(module.Name, materializationKey);
    }

    private static async Task MaterializeProjectAsync(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        DistributedApplicationModuleProject project,
        string resourceName,
        MaterializedModuleRepository definitionRepository,
        bool imported,
        ModuleApplicationRegistry registry,
        ModularAppHostsOptions options,
        DistributedApplicationModuleOptions? moduleOptions,
        CancellationToken cancellationToken)
    {
        var export = project.Export;
        var projectRelativePath = project.GetRepositoryRelativeProjectPath();
        var projectDirectoryRelativePath = Path.GetDirectoryName(projectRelativePath) ?? ".";
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
                definitionRepository.RepositoryPath,
                imported,
                registry);
            return;
        }

        var publishImage = projectOptions?.PublishImage ?? moduleOptions?.PublishImages ?? options.PublishImages;
        var prepareImageBuild = !UsesExternalImage(projectOptions) &&
            (builder.ExecutionContext.IsRunMode
                ? publishImage
                : ModuleImageBuildPipeline.ShouldPrepareBuildRepository(
                    Environment.GetCommandLineArgs(),
                    module.Name,
                    project.Name,
                    resourceName));
        var acquisition = await TryAcquireImageBeforeBuildRepositoryAsync(
            builder,
            module,
            project.Name,
            export.Options,
            projectOptions,
            definitionRepository,
            publishImage,
            options,
            cancellationToken).ConfigureAwait(false);
        var imageAcquired = acquisition is { Plan.ShouldPublish: false };
        var buildRepository = imageAcquired
            ? definitionRepository
            : await ResolveBuildRepositoryAsync(
                builder,
                module,
                project.Name,
                export.Options,
                projectOptions,
                definitionRepository,
                prepareImageBuild,
                registry,
                options,
                moduleOptions,
                cancellationToken).ConfigureAwait(false);
        var effectiveExportOptions = imageAcquired
            ? acquisition!.Options
            : ApplyImageOptions(
                export.Options,
                projectOptions,
                buildRepository.DefaultImageTag);
        var workingDirectoryRelativePath = effectiveExportOptions.WorkingDirectory ?? projectDirectoryRelativePath;
        if (!buildRepository.UsesModuleRepository && effectiveExportOptions.WorkingDirectory is null)
        {
            workingDirectoryRelativePath = ".";
        }

        var normalizedWorkingDirectoryRelativePath = buildRepository.UsesModuleRepository
            ? project.PathBase == ModuleProjectPathBase.Repository
                ? workingDirectoryRelativePath
                : Path.GetRelativePath(
                    project.SourceRepositoryRoot!,
                    PathSafety.GetContainedPath(
                        project.SourceRepositoryRoot!,
                        workingDirectoryRelativePath,
                        nameof(ModuleContainerExportOptions.WorkingDirectory)))
            : workingDirectoryRelativePath;
        var publishWorkingDirectory = PathSafety.GetContainedPath(
            buildRepository.RepositoryPath,
            normalizedWorkingDirectoryRelativePath,
            nameof(ModuleContainerExportOptions.WorkingDirectory));
        var publishPlan = imageAcquired
            ? acquisition!.Plan
            : await CreateImagePublishPlanAsync(
                builder,
                effectiveExportOptions,
                buildRepository.RepositoryDirty && prepareImageBuild,
                inspectExistingImage: publishImage && acquisition is null,
                cancellationToken).ConfigureAwait(false);

        var container = builder
            .AddContainer(resourceName, publishPlan.ImageName, publishPlan.ImageTag)
            .WithAnnotation(new DistributedApplicationModuleResourceAnnotation(
                module.Name,
                project.Name,
                definitionRepository.RepositoryPath,
                imported,
                module.PackageId));

        ApplyImageRegistry(container, publishPlan.ImageRegistry);

        var context = new DistributedApplicationModuleResourceContext(
            builder,
            module,
            resourceName,
            definitionRepository.RepositoryPath,
            imported,
            new ModuleResourceImage(
                publishPlan.ImageRegistry,
                publishPlan.ImageName,
                publishPlan.ImageTag,
                GetConfiguredValue(projectOptions?.ImageSHA256)));
        export.ConfigureContainer?.Invoke(context, container);
        if (!UsesExternalImage(projectOptions))
        {
            container.WithAnnotation(new ModuleImagePublisherAnnotation(
                module.Name,
                project.Name,
                ModuleResourceKind.Project,
                effectiveExportOptions,
                publishPlan,
                publishWorkingDirectory,
                effectiveExportOptions.BuildRepository ?? buildRepository.Repository ?? buildRepository.RepositoryPath,
                effectiveExportOptions.BuildRepositoryRevision ?? buildRepository.RepositoryRevision));
            ModuleImageBuildPipeline.AddBuildStep(container);
            ModuleImagePushPipeline.AddPushStep(container);
        }

        ModuleImagePullPipeline.AddPullStep(container);

        ApplyImageSHA256(container, projectOptions?.ImageSHA256);
        ApplyImagePullPolicy(
            container,
            projectOptions?.ImagePullPolicy ?? (publishImage ? ImagePullPolicy.Never : null));

        if (builder.ExecutionContext.IsRunMode && publishImage && publishPlan.ShouldPublish)
        {
            await AddImagePublishInstallerAsync(
                builder,
                resourceName,
                effectiveExportOptions,
                publishPlan,
                buildRepository.RepositoryPath,
                publishWorkingDirectory,
                buildRepository.Repository,
                buildRepository.UpdateRepository,
                container,
                registry,
                cancellationToken).ConfigureAwait(false);
        }

        registry.TrackResource(container.Resource);
        module.TrackMaterializedResource(builder, project.Name, container.Resource);
    }

    private static async Task MaterializeContainerAsync(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        DistributedApplicationModuleContainer definition,
        string resourceName,
        MaterializedModuleRepository definitionRepository,
        bool imported,
        ModuleApplicationRegistry registry,
        ModularAppHostsOptions options,
        DistributedApplicationModuleOptions? moduleOptions,
        CancellationToken cancellationToken)
    {
        var containerOptions = moduleOptions?.FindContainer(definition.Name);
        ValidatePublishOverrides(definition, containerOptions);
        var publishImage = definition.ImagePublishOptions is not null &&
            (containerOptions?.PublishImage ?? moduleOptions?.PublishImages ?? options.PublishImages);
        var prepareImageBuild = !UsesExternalImage(containerOptions) &&
            (builder.ExecutionContext.IsRunMode
                ? publishImage
                : ModuleImageBuildPipeline.ShouldPrepareBuildRepository(
                    Environment.GetCommandLineArgs(),
                    module.Name,
                    definition.Name,
                    resourceName));
        var acquisition = definition.ImagePublishOptions is null
            ? null
            : await TryAcquireImageBeforeBuildRepositoryAsync(
                builder,
                module,
                definition.Name,
                definition.ImagePublishOptions,
                containerOptions,
                definitionRepository,
                publishImage,
                options,
                cancellationToken).ConfigureAwait(false);
        var imageAcquired = acquisition is { Plan.ShouldPublish: false };
        var buildRepository = definition.ImagePublishOptions is null || imageAcquired
            ? definitionRepository
            : await ResolveBuildRepositoryAsync(
                builder,
                module,
                definition.Name,
                definition.ImagePublishOptions,
                containerOptions,
                definitionRepository,
                prepareImageBuild,
                registry,
                options,
                moduleOptions,
                cancellationToken).ConfigureAwait(false);
        var publishOptions = definition.ImagePublishOptions is null
            ? null
            : imageAcquired
                ? acquisition!.Options
                : ApplyImageOptions(
                    definition.ImagePublishOptions,
                    containerOptions,
                    buildRepository.DefaultImageTag);
        var publishPlan = publishOptions is null
            ? null
            : imageAcquired
                ? acquisition!.Plan
                : await CreateImagePublishPlanAsync(
                    builder,
                    publishOptions!,
                    buildRepository.RepositoryDirty && prepareImageBuild,
                    inspectExistingImage: publishImage && acquisition is null,
                    cancellationToken).ConfigureAwait(false);
        var publishWorkingDirectory = publishOptions is null
            ? null
            : PathSafety.GetContainedPath(
                buildRepository.RepositoryPath,
                publishOptions.WorkingDirectory ?? ".",
                nameof(ModuleContainerExportOptions.WorkingDirectory));
        var container = builder
            .AddContainer(
                resourceName,
                publishPlan?.ImageName ?? GetConfiguredValue(containerOptions?.ImageName) ?? definition.Image,
                publishPlan?.ImageTag ?? GetConfiguredValue(containerOptions?.ImageTag) ?? definition.Tag)
            .WithAnnotation(new DistributedApplicationModuleResourceAnnotation(
                module.Name,
                definition.Name,
                definitionRepository.RepositoryPath,
                imported,
                module.PackageId));

        ApplyImageRegistry(
            container,
            publishPlan?.ImageRegistry ?? GetConfiguredValue(containerOptions?.ImageRegistry));

        var context = new DistributedApplicationModuleResourceContext(
            builder,
            module,
            resourceName,
            definitionRepository.RepositoryPath,
            imported,
            new ModuleResourceImage(
                publishPlan?.ImageRegistry ?? GetConfiguredValue(containerOptions?.ImageRegistry),
                publishPlan?.ImageName ?? GetConfiguredValue(containerOptions?.ImageName) ?? definition.Image,
                publishPlan?.ImageTag ?? GetConfiguredValue(containerOptions?.ImageTag) ?? definition.Tag,
                GetConfiguredValue(containerOptions?.ImageSHA256)));
        definition.ConfigureContainer?.Invoke(context, container);

        if (publishPlan is not null && !UsesExternalImage(containerOptions))
        {
            container.WithAnnotation(new ModuleImagePublisherAnnotation(
                module.Name,
                definition.Name,
                ModuleResourceKind.Container,
                publishOptions!,
                publishPlan,
                publishWorkingDirectory!,
                publishOptions!.BuildRepository ?? buildRepository.Repository ?? buildRepository.RepositoryPath,
                publishOptions.BuildRepositoryRevision ?? buildRepository.RepositoryRevision));
            ModuleImageBuildPipeline.AddBuildStep(container);
            ModuleImagePushPipeline.AddPushStep(container);
        }

        ModuleImagePullPipeline.AddPullStep(container);

        ApplyImageSHA256(container, containerOptions?.ImageSHA256);
        ApplyImagePullPolicy(
            container,
            containerOptions?.ImagePullPolicy ?? (publishImage ? ImagePullPolicy.Never : null));

        if (builder.ExecutionContext.IsRunMode && publishImage && publishPlan is { ShouldPublish: true })
        {
            await AddImagePublishInstallerAsync(
                builder,
                resourceName,
                publishOptions!,
                publishPlan,
                buildRepository.RepositoryPath,
                publishWorkingDirectory!,
                buildRepository.Repository,
                buildRepository.UpdateRepository,
                container,
                registry,
                cancellationToken).ConfigureAwait(false);
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
        var projectRelativePath = project.GetRepositoryRelativeProjectPath();
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

    private static async Task MaterializeResourceAsync(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        IDistributedApplicationModuleFactoryResource definition,
        string resourceName,
        MaterializedModuleRepository definitionRepository,
        bool imported,
        ModuleApplicationRegistry registry,
        ModularAppHostsOptions options,
        DistributedApplicationModuleOptions? moduleOptions,
        CancellationToken cancellationToken)
    {
        var configured = moduleOptions?.FindContainer(definition.Name);
        ValidatePublishOverrides(
            definition.Name,
            definition.ImagePublishOptions is not null,
            configured,
            nameof(IDistributedApplicationModuleBuilder.AddResource));
        var publishImage = definition.ImagePublishOptions is not null &&
            (configured?.PublishImage ?? moduleOptions?.PublishImages ?? options.PublishImages);
        var prepareImageBuild = !UsesExternalImage(configured) &&
            (builder.ExecutionContext.IsRunMode
                ? publishImage
                : ModuleImageBuildPipeline.ShouldPrepareBuildRepository(
                    Environment.GetCommandLineArgs(),
                    module.Name,
                    definition.Name,
                    resourceName));
        var acquisition = definition.ImagePublishOptions is null
            ? null
            : await TryAcquireImageBeforeBuildRepositoryAsync(
                builder,
                module,
                definition.Name,
                definition.ImagePublishOptions,
                configured,
                definitionRepository,
                publishImage,
                options,
                cancellationToken).ConfigureAwait(false);
        var imageAcquired = acquisition is { Plan.ShouldPublish: false };
        var buildRepository = definition.ImagePublishOptions is null || imageAcquired
            ? definitionRepository
            : await ResolveBuildRepositoryAsync(
                builder,
                module,
                definition.Name,
                definition.ImagePublishOptions,
                configured,
                definitionRepository,
                prepareImageBuild,
                registry,
                options,
                moduleOptions,
                cancellationToken).ConfigureAwait(false);
        var publishOptions = definition.ImagePublishOptions is null
            ? null
            : imageAcquired
                ? acquisition!.Options
                : ApplyImageOptions(
                    definition.ImagePublishOptions,
                    configured,
                    buildRepository.DefaultImageTag);
        var publishPlan = publishOptions is null
            ? null
            : imageAcquired
                ? acquisition!.Plan
                : await CreateImagePublishPlanAsync(
                    builder,
                    publishOptions!,
                    buildRepository.RepositoryDirty && prepareImageBuild,
                    inspectExistingImage: publishImage && acquisition is null,
                    cancellationToken).ConfigureAwait(false);
        var publishWorkingDirectory = publishOptions is null
            ? null
            : PathSafety.GetContainedPath(
                buildRepository.RepositoryPath,
                publishOptions.WorkingDirectory ?? ".",
                nameof(ModuleContainerExportOptions.WorkingDirectory));
        var image = publishPlan is null
            ? null
            : new ModuleResourceImage(
                publishPlan.ImageRegistry,
                publishPlan.ImageName,
                publishPlan.ImageTag,
                GetConfiguredValue(configured?.ImageSHA256));
        var context = new DistributedApplicationModuleResourceContext(
            builder,
            module,
            resourceName,
            definitionRepository.RepositoryPath,
            imported,
            image);
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
            if (publishPlan is not null)
            {
                ApplyImageIdentity(container, publishPlan);
                ApplyImageSHA256(container, configured?.ImageSHA256);
                ApplyImagePullPolicy(
                    container,
                    configured?.ImagePullPolicy ?? (publishImage ? ImagePullPolicy.Never : null));
                if (!UsesExternalImage(configured))
                {
                    container.WithAnnotation(new ModuleImagePublisherAnnotation(
                        module.Name,
                        definition.Name,
                        ModuleResourceKind.Container,
                        publishOptions!,
                        publishPlan,
                        publishWorkingDirectory!,
                        publishOptions!.BuildRepository ?? buildRepository.Repository ?? buildRepository.RepositoryPath,
                        publishOptions.BuildRepositoryRevision ?? buildRepository.RepositoryRevision));
                    ModuleImageBuildPipeline.AddBuildStep(container);
                    ModuleImagePushPipeline.AddPushStep(container);
                }
            }

            ModuleImagePullPipeline.AddPullStep(container);

            if (builder.ExecutionContext.IsRunMode && publishImage && publishPlan is { ShouldPublish: true })
            {
                await AddImagePublishInstallerAsync(
                    builder,
                    resourceName,
                    publishOptions!,
                    publishPlan,
                    buildRepository.RepositoryPath,
                    publishWorkingDirectory!,
                    buildRepository.Repository,
                    buildRepository.UpdateRepository,
                    container,
                    registry,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        else if (publishPlan is not null)
        {
            throw new InvalidOperationException(
                $"Image-published module resource '{definition.Name}' did not create a container resource.");
        }

        registry.TrackResource(resource);
        module.TrackMaterializedResource(builder, definition.Name, resource);
    }

    private static Task<ModuleImagePublishPlan> CreateImagePublishPlanAsync(
        IDistributedApplicationBuilder builder,
        ModuleContainerExportOptions options,
        bool useDirtyImage,
        bool inspectExistingImage,
        CancellationToken cancellationToken)
    {
        return ModuleImagePublishPlan.CreateAsync(
            options,
            useDirtyImage,
            builder.ExecutionContext.IsRunMode && inspectExistingImage
                ? ContainerImageInspector.ExistsAsync
                : (_, _) => Task.FromResult(false),
            builder.ExecutionContext.IsRunMode && inspectExistingImage
                ? ContainerImageInspector.PullAsync
                : (_, _) => Task.FromResult(false),
            cancellationToken);
    }

    private static async Task<ModuleImageAcquisition?> TryAcquireImageBeforeBuildRepositoryAsync(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        string resourceName,
        ModuleContainerExportOptions declared,
        DistributedApplicationModuleImageOptions? configured,
        MaterializedModuleRepository definitionRepository,
        bool publishImage,
        ModularAppHostsOptions options,
        CancellationToken cancellationToken)
    {
        var explicitTag = GetConfiguredValue(configured?.ImageTag) ?? GetConfiguredValue(declared.ImageTag);
        var separateBuildRepository = GetConfiguredValue(configured?.BuildRepository) ??
            GetConfiguredValue(declared.BuildRepository);
        var pullBeforeBuild = configured?.PullBeforeBuild ?? declared.PullBeforeBuild;
        if (!builder.ExecutionContext.IsRunMode ||
            !publishImage ||
            !pullBeforeBuild ||
            explicitTag is null ||
            separateBuildRepository is null ||
            await BuildRepositoryCheckoutExistsAsync(
                builder,
                module,
                resourceName,
                separateBuildRepository,
                definitionRepository,
                options,
                cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var effectiveOptions = ApplyImageOptions(declared, configured, explicitTag);
        var plan = await CreateImagePublishPlanAsync(
            builder,
            effectiveOptions,
            useDirtyImage: false,
            inspectExistingImage: true,
            cancellationToken).ConfigureAwait(false);
        return new ModuleImageAcquisition(effectiveOptions, plan);
    }

    private static async Task<bool> BuildRepositoryCheckoutExistsAsync(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        string resourceName,
        string requestedRepository,
        MaterializedModuleRepository definitionRepository,
        ModularAppHostsOptions options,
        CancellationToken cancellationToken)
    {
        var effectiveRepository = GitHubRepositoryCloner.IsRemoteRepository(
            requestedRepository,
            builder.AppHostDirectory)
                ? requestedRepository
                : Path.GetFullPath(requestedRepository, builder.AppHostDirectory);
        if (RepositoryIdentitiesMatch(
            effectiveRepository,
            definitionRepository.Repository ?? definitionRepository.RepositoryPath,
            builder.AppHostDirectory))
        {
            return true;
        }

        if (!GitHubRepositoryCloner.IsRemoteRepository(effectiveRepository, builder.AppHostDirectory))
        {
            return Directory.Exists(effectiveRepository);
        }

        var importedPath = GetImportedRepositoryPath(
            builder,
            options,
            effectiveRepository,
            $"{module.Name}-{resourceName}-build");
        if (Directory.Exists(importedPath))
        {
            return true;
        }

        var gitExecutablePath = GetConfiguredValue(options.GitExecutablePath) ?? "git";
        var appHostRepositoryRoot = await RepositoryInspector.TryFindRepositoryRootAsync(
            builder.AppHostDirectory,
            gitExecutablePath,
            options.RepositoryCommandTimeout,
            cancellationToken).ConfigureAwait(false);
        if (appHostRepositoryRoot is null)
        {
            return false;
        }

        var appHostRemote = await RepositoryInspector.TryGetRemoteAsync(
            appHostRepositoryRoot,
            gitExecutablePath,
            options.RepositoryCommandTimeout,
            cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(appHostRemote) &&
            GitHubRepositoryCloner.RefersToSameRepository(
                effectiveRepository,
                appHostRemote,
                builder.AppHostDirectory))
        {
            return true;
        }

        var siblingParent = Path.GetDirectoryName(appHostRepositoryRoot);
        return siblingParent is not null && Directory.Exists(Path.Combine(
            siblingParent,
            GitHubRepositoryCloner.GetRepositoryDirectoryName(effectiveRepository)));
    }

    private static ModuleContainerExportOptions ApplyImageOptions(
        ModuleContainerExportOptions declared,
        DistributedApplicationModuleImageOptions? configured,
        string defaultImageTag)
    {
        var imageName = GetConfiguredValue(configured?.ImageName) ?? declared.ImageName;
        var imageRegistry = configured?.ImageRegistry is null
            ? GetConfiguredValue(declared.ImageRegistry)
            : GetConfiguredValue(configured.ImageRegistry);
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
            ImageRegistry = imageRegistry,
            ProducedImageReference = configured?.ProducedImageReference ?? declared.ProducedImageReference,
            PullBeforeBuild = configured?.PullBeforeBuild ?? declared.PullBeforeBuild,
            ImageTag = imageTag,
            WorkingDirectory = configured?.PublishWorkingDirectory ?? declared.WorkingDirectory,
            BuildRepository = GetConfiguredValue(configured?.BuildRepository) ??
                GetConfiguredValue(declared.BuildRepository),
            BuildRepositoryRevision = GetConfiguredValue(configured?.BuildRepositoryRevision) ??
                GetConfiguredValue(declared.BuildRepositoryRevision)
        };
    }

    private static async Task<MaterializedModuleRepository> ResolveBuildRepositoryAsync(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        string resourceName,
        ModuleContainerExportOptions declared,
        DistributedApplicationModuleImageOptions? configured,
        MaterializedModuleRepository definitionRepository,
        bool prepareBuildRepository,
        ModuleApplicationRegistry registry,
        ModularAppHostsOptions options,
        DistributedApplicationModuleOptions? moduleOptions,
        CancellationToken cancellationToken)
    {
        var requestedRepository = GetConfiguredValue(configured?.BuildRepository) ??
            GetConfiguredValue(declared.BuildRepository);
        var requestedRevision = GetConfiguredValue(configured?.BuildRepositoryRevision) ??
            GetConfiguredValue(declared.BuildRepositoryRevision);
        if (requestedRepository is null && requestedRevision is null)
        {
            return definitionRepository;
        }

        var effectiveRepository = requestedRepository ??
            definitionRepository.Repository ??
            definitionRepository.RepositoryPath;
        if (!GitHubRepositoryCloner.IsRemoteRepository(effectiveRepository, builder.AppHostDirectory))
        {
            effectiveRepository = Path.GetFullPath(effectiveRepository, builder.AppHostDirectory);
        }

        var sameRepository = RepositoryIdentitiesMatch(
            effectiveRepository,
            definitionRepository.Repository ?? definitionRepository.RepositoryPath,
            builder.AppHostDirectory);
        if (sameRepository && string.Equals(
                requestedRevision,
                definitionRepository.RepositoryRevision,
                StringComparison.Ordinal))
        {
            return definitionRepository;
        }

        var hasExplicitTag = GetConfiguredValue(configured?.ImageTag) is not null ||
            GetConfiguredValue(declared.ImageTag) is not null;
        var hasImmutableImage = GetConfiguredValue(configured?.ImageSHA256) is not null;
        if (!prepareBuildRepository && (hasExplicitTag || hasImmutableImage))
        {
            return definitionRepository;
        }

        var gitExecutablePath = GetConfiguredValue(options.GitExecutablePath) ?? "git";
        var localRepository = !GitHubRepositoryCloner.IsRemoteRepository(
            effectiveRepository,
            builder.AppHostDirectory);
        var useDirectLocalRepository = localRepository &&
            requestedRevision is null &&
            Directory.Exists(effectiveRepository);
        var autoCloneRepository = configured?.AutoCloneBuildRepository ??
            moduleOptions?.AutoCloneRepository ??
            options.AutoCloneRepositories;
        var repositoryResolution = !useDirectLocalRepository && autoCloneRepository &&
            requestedRevision is null
            ? await ModuleRepositoryDiscovery.ResolveAsync(
                builder.AppHostDirectory,
                $"{module.Name}/{resourceName} build",
                sourceRepositoryRoot: null,
                effectiveRepository,
                GetConfiguredValue(options.GitHubCliPath) ?? "gh",
                options.RepositoryCommandTimeout,
                gitExecutablePath,
                cancellationToken).ConfigureAwait(false)
            : null;
        var repositoryPath = useDirectLocalRepository
            ? effectiveRepository
            : repositoryResolution?.RepositoryPath ?? GetImportedRepositoryPath(
                builder,
                options,
                effectiveRepository,
                $"{module.Name}-{resourceName}-build");
        var discoveredRepositoryRoot = await RepositoryInspector.TryFindRepositoryRootAsync(
                repositoryPath,
                gitExecutablePath,
                options.RepositoryCommandTimeout,
                cancellationToken).ConfigureAwait(false) ??
            Path.GetFullPath(repositoryPath);
        var synchronizationKey = PathSafety.AreEqual(discoveredRepositoryRoot, repositoryPath)
            ? discoveredRepositoryRoot
            : Path.GetFullPath(repositoryPath);
        var updateRepository = (configured?.UpdateBuildRepository ??
                (useDirectLocalRepository
                    ? false
                    : moduleOptions?.UpdateRepository ?? options.UpdateImportedRepositories)) &&
            repositoryResolution?.UsesSiblingLayout is not false;
        var synchronizationPolicy = RepositorySynchronizationPolicy.Create(
            updateRepository,
            requestedRevision);

        if (!useDirectLocalRepository || updateRepository || requestedRevision is not null)
        {
            await registry.SynchronizeRepositoryAsync(
                synchronizationKey,
                synchronizationPolicy,
                progress => RepositorySynchronizer.SynchronizeAsync(
                    repositoryPath,
                    effectiveRepository,
                    updateRepository,
                    cancellationToken,
                    requestedRevision,
                    gitExecutablePath,
                    GetConfiguredValue(options.GitHubCliPath) ?? "gh",
                    options.RepositoryCommandTimeout,
                    progress)).ConfigureAwait(false);
        }

        if (!await RepositoryInspector.IsGitRepositoryAsync(
                repositoryPath,
                gitExecutablePath,
                options.RepositoryCommandTimeout,
                requireSuccessfulInspection: true,
                cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"Build repository '{effectiveRepository}' for module '{module.Name}' resource '{resourceName}' " +
                $"did not resolve to a Git checkout at '{repositoryPath}'.");
        }

        var repositoryDirty = await RepositoryInspector.IsDirtyAsync(
            repositoryPath,
            gitExecutablePath,
            options.RepositoryCommandTimeout,
            requireSuccessfulInspection: true,
            cancellationToken).ConfigureAwait(false);
        var defaultImageTag = await GetDefaultImageTagAsync(
            builder,
            repositoryPath,
            options,
            cancellationToken).ConfigureAwait(false);
        return new MaterializedModuleRepository(
            repositoryPath,
            effectiveRepository,
            requestedRevision,
            updateRepository,
            repositoryDirty,
            defaultImageTag,
            UsesModuleRepository: false);
    }

    private static bool RepositoryIdentitiesMatch(
        string first,
        string second,
        string baseDirectory)
    {
        var firstIsRemote = GitHubRepositoryCloner.IsRemoteRepository(first, baseDirectory);
        var secondIsRemote = GitHubRepositoryCloner.IsRemoteRepository(second, baseDirectory);
        if (firstIsRemote != secondIsRemote)
        {
            return false;
        }

        return firstIsRemote
            ? GitHubRepositoryCloner.RefersToSameRepository(first, second, baseDirectory)
            : PathSafety.AreEqual(
                Path.GetFullPath(first, baseDirectory),
                Path.GetFullPath(second, baseDirectory));
    }

    private static void ValidatePublishOverrides(
        DistributedApplicationModuleContainer definition,
        DistributedApplicationModuleContainerOptions? configured)
    {
        ValidatePublishOverrides(
            definition.Name,
            definition.ImagePublishOptions is not null,
            configured,
            nameof(IDistributedApplicationModuleContainerBuilder.WithImagePublishCommand));
    }

    private static void ValidatePublishOverrides(
        string resourceName,
        bool hasDeclaredPublisher,
        DistributedApplicationModuleContainerOptions? configured,
        string declarationMethod)
    {
        if (hasDeclaredPublisher || configured is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(configured.PublishCommand) ||
            configured.PublishArguments is not null ||
            configured.PublishWorkingDirectory is not null ||
            configured.ProducedImageReference is not null ||
            configured.PullBeforeBuild is not null ||
            configured.BuildRepository is not null ||
            configured.BuildRepositoryRevision is not null ||
            configured.AutoCloneBuildRepository is not null ||
            configured.UpdateBuildRepository is not null ||
            configured.PublishImage is true)
        {
            throw new InvalidOperationException(
                $"Container resource '{resourceName}' configures image publishing, but its module definition does not " +
                $"call {declarationMethod}() with image publish options.");
        }
    }

    private static async Task<bool> ContainerPublishersRequireModuleRepositoryAsync(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        ModularAppHostsOptions options,
        DistributedApplicationModuleOptions? moduleOptions,
        string? definitionRepository,
        string? definitionRevision,
        CancellationToken cancellationToken)
    {
        var definitionRepositoryIdentity = definitionRepository ??
            await GetLocalRepositoryPathAsync(
                builder,
                module,
                repository: null,
                options,
                cancellationToken).ConfigureAwait(false);
        foreach (var (resourceName, declared) in GetContainerPublishers(module))
        {
            var configured = moduleOptions?.FindContainer(resourceName);
            var buildRepository = GetConfiguredValue(configured?.BuildRepository) ??
                GetConfiguredValue(declared.BuildRepository);
            var buildRevision = GetConfiguredValue(configured?.BuildRepositoryRevision) ??
                GetConfiguredValue(declared.BuildRepositoryRevision);
            var effectiveBuildRepository = buildRepository ?? definitionRepositoryIdentity;
            var usesSeparateBuildCheckout =
                !RepositoryIdentitiesMatch(
                    effectiveBuildRepository,
                    definitionRepositoryIdentity,
                    builder.AppHostDirectory) ||
                !string.Equals(buildRevision, definitionRevision, StringComparison.Ordinal);
            if (usesSeparateBuildCheckout)
            {
                continue;
            }

            var publishImage = configured?.PublishImage ?? moduleOptions?.PublishImages ?? options.PublishImages;
            var hasExplicitImageIdentity = GetConfiguredValue(configured?.ImageTag) is not null ||
                GetConfiguredValue(declared.ImageTag) is not null ||
                GetConfiguredValue(configured?.ImageSHA256) is not null;
            if (publishImage || !hasExplicitImageIdentity)
            {
                return true;
            }
        }

        return false;
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

    private static void ApplyImageIdentity(
        IResourceBuilder<ContainerResource> container,
        ModuleImagePublishPlan publishPlan)
    {
        container.WithImageRegistry(publishPlan.ImageRegistry);
        container.WithImage(publishPlan.ImageName);
        container.WithImageTag(publishPlan.ImageTag);
    }

    private static void ApplyImageRegistry(
        IResourceBuilder<ContainerResource> container,
        string? registry)
    {
        if (registry is not null)
        {
            container.WithImageRegistry(registry);
        }
    }

    private static void ApplyImageSHA256(
        IResourceBuilder<ContainerResource> container,
        string? sha256)
    {
        var configuredSha256 = GetConfiguredValue(sha256);
        if (configuredSha256 is not null)
        {
            container.WithImageSHA256(configuredSha256["sha256:".Length..]);
        }
    }

    private static async Task AddImagePublishInstallerAsync(
        IDistributedApplicationBuilder builder,
        string resourceName,
        ModuleContainerExportOptions options,
        ModuleImagePublishPlan publishPlan,
        string repositoryPath,
        string publishWorkingDirectory,
        string? repository,
        bool updateRepository,
        IResourceBuilder<ContainerResource> container,
        ModuleApplicationRegistry registry,
        CancellationToken cancellationToken)
    {
        var installerResource = new ModuleRepositoryInstallerResource(
            GetInstallerName(resourceName),
            repositoryPath,
            repository,
            updateRepository,
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

        container.WithAnnotation(new ModuleRepositoryInstallerAnnotation(installerResource));

        if (publishPlan.RequiresRetag)
        {
            var containerRuntime = await ContainerRuntimeResolver.ResolveAsync(cancellationToken).ConfigureAwait(false);
            var retagResource = new ModuleImageRetagResource(
                GetRetagName(resourceName),
                containerRuntime,
                publishWorkingDirectory,
                publishPlan.ProducedImageReference!,
                publishPlan.ImageReference);
            var retagger = builder.AddResource(retagResource)
                .WithArgs(
                    "tag",
                    publishPlan.ProducedImageReference!,
                    publishPlan.ImageReference)
                .WaitForCompletion(installer)
                .WithParentRelationship(container.Resource)
                .ExcludeFromManifest()
                .WithCertificateTrustScope(CertificateTrustScope.None)
                .WithIconName("Tag");

            container.WaitForCompletion(retagger);
            registry.TrackResource(retagger.Resource);
        }
        else
        {
            container.WaitForCompletion(installer);
        }

        registry.TrackResource(installer.Resource);
    }

    private static void ConfigureRepositorySynchronization(
        IDistributedApplicationBuilder builder,
        ModuleApplicationRegistry registry,
        DistributedApplicationModule module,
        string repositoryPath,
        string repositorySynchronizationKey,
        RepositorySynchronizationPolicy repositorySynchronizationPolicy,
        string? repository,
        IResourceBuilder<ParameterResource>? repositoryParameter,
        bool imported,
        bool updateRepository,
        string? repositoryRevision,
        string gitExecutablePath,
        TimeSpan repositoryCommandTimeout)
    {
        if (!builder.ExecutionContext.IsRunMode || !imported ||
            (string.IsNullOrWhiteSpace(repository) && repositoryParameter is null))
        {
            return;
        }

        builder.Eventing.Subscribe<BeforeStartEvent>(async (@event, cancellationToken) =>
        {
            var resolvedRepository = repository ??
                await repositoryParameter!.Resource.GetValueAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(resolvedRepository))
            {
                throw new InvalidOperationException(
                    $"A Git repository is required for imported module content at '{repositoryPath}'.");
            }

            var logResource = @event.Model.Resources.FirstOrDefault(resource =>
                resource.Annotations.OfType<DistributedApplicationModuleResourceAnnotation>().Any(annotation =>
                    string.Equals(annotation.ModuleName, module.Name, StringComparison.OrdinalIgnoreCase) &&
                    PathSafety.AreEqual(annotation.RepositoryPath, repositoryPath)));
            var resourceLoggerService = @event.Services.GetRequiredService<ResourceLoggerService>();
            var logger = logResource is null
                ? resourceLoggerService.GetLogger(module.Name)
                : resourceLoggerService.GetLogger(logResource);

            await registry.SynchronizeRepositoryAsync(
                repositorySynchronizationKey,
                repositorySynchronizationPolicy,
                progress => RepositorySynchronizer.SynchronizeAsync(
                    repositoryPath,
                    resolvedRepository,
                    updateRepository,
                    cancellationToken,
                    repositoryRevision,
                    gitExecutablePath,
                    GetConfiguredValue(registry.Options.GitHubCliPath) ?? "gh",
                    repositoryCommandTimeout,
                    progress),
                progress => LogRepositoryProgress(logger, progress)).ConfigureAwait(false);

            ValidateProjectFiles(module, registry.Options.FindModule(module.Name), repositoryPath);
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
        ModuleImageBuildPipeline.ConfigureResourceSelection(builder);
        ModuleImagePushPipeline.ConfigureResourceSelection(builder);
        ModuleImagePullPipeline.Configure(builder);
        ModuleImageDescriptionPipeline.Configure(builder);
        ModuleImageManifestPipeline.Configure(builder);
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
        string? repository,
        string moduleName)
    {
        var configuredLocation = GetConfiguredValue(options.RepositoryBasePath) ??
            builder.Configuration[$"Parameters:{RepositoryBaseLocationParameterName}"];
        var defaultLocation = Path.Combine(builder.AppHostDirectory, ".aspire", "module-repositories");
        var baseLocation = Path.GetFullPath(configuredLocation ?? defaultLocation, builder.AppHostDirectory);
        Directory.CreateDirectory(baseLocation);
        var canonicalName = ModuleRepositoryIdentity.GetCanonicalName(
            repository,
            moduleName,
            builder.AppHostDirectory);
        return Path.Combine(baseLocation, canonicalName);
    }

    private static IResourceBuilder<ParameterResource> GetOrCreateRepositoryParameter(
        IDistributedApplicationBuilder builder,
        ModuleApplicationRegistry registry,
        string moduleName,
        string? repository,
        string configurationKey)
    {
        var parameterName = string.IsNullOrWhiteSpace(repository)
            ? builder.GetRepositoryParameterName(moduleName)
            : builder.GetRepositoryParameterName(repository, moduleName);
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

    private static async Task<string> GetLocalRepositoryPathAsync(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        string? repository,
        ModularAppHostsOptions options,
        CancellationToken cancellationToken)
    {
        var projectRepositoryRoot = module.ProjectDefinitions
            .Select(project => project.SourceRepositoryRoot)
            .FirstOrDefault(repositoryRoot => repositoryRoot is not null);
        if (projectRepositoryRoot is not null)
        {
            return projectRepositoryRoot;
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

        if (!module.ProjectDefinitions.Any(project => project.PathBase == ModuleProjectPathBase.Repository))
        {
            return builder.AppHostDirectory;
        }

        return await RepositoryInspector.TryFindRepositoryRootAsync(
                builder.AppHostDirectory,
                GetConfiguredValue(options.GitExecutablePath) ?? "git",
                options.RepositoryCommandTimeout,
                cancellationToken).ConfigureAwait(false) ??
            builder.AppHostDirectory;
    }

    private static async Task<string?> TryGetExistingSameWorktreeRepositoryAsync(
        string appHostDirectory,
        string? repository,
        string gitExecutablePath,
        TimeSpan repositoryCommandTimeout,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(repository) ||
            GitHubRepositoryCloner.IsRemoteRepository(repository, appHostDirectory))
        {
            return null;
        }

        var repositoryPath = Path.GetFullPath(repository, appHostDirectory);
        if (!Directory.Exists(repositoryPath))
        {
            return null;
        }

        var appHostRoot = await RepositoryInspector.TryFindRepositoryRootAsync(
            appHostDirectory,
            gitExecutablePath,
            repositoryCommandTimeout,
            cancellationToken).ConfigureAwait(false);
        var repositoryRoot = await RepositoryInspector.TryFindRepositoryRootAsync(
            repositoryPath,
            gitExecutablePath,
            repositoryCommandTimeout,
            cancellationToken).ConfigureAwait(false);
        return appHostRoot is not null && repositoryRoot is not null &&
            PathSafety.AreEqual(appHostRoot, repositoryRoot)
                ? repositoryPath
                : null;
    }

    private static void ValidateResourceNames(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        ModuleApplicationRegistry registry,
        ModularAppHostsOptions options,
        DistributedApplicationModuleOptions? moduleOptions,
        bool imported,
        ModuleResourceNameMap resourceNames,
        MaterializedModuleRepository definitionRepository)
    {
        var plannedResourceNames = module.ResourceDefinitions.SelectMany(definition =>
            GetPlannedResourceNames(
                definition,
                resourceNames[definition.Name],
                options,
                moduleOptions,
                imported,
                builder.ExecutionContext.IsRunMode,
                builder.AppHostDirectory,
                definitionRepository))
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
            .Concat(module.ResourceDefinitions
                .OfType<IDistributedApplicationModuleFactoryResource>()
                .Where(resource => resource.ImagePublishOptions is not null)
                .Select(resource => resource.Name))
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
        DistributedApplicationModuleOptions? moduleOptions,
        string repositoryPath)
    {
        foreach (var project in module.ProjectDefinitions.Where(project =>
                     !UsesExternalImage(moduleOptions?.FindProject(project.Name))))
        {
            var relativePath = project.GetRepositoryRelativeProjectPath();
            var materializedPath = PathSafety.GetContainedPath(repositoryPath, relativePath, nameof(project.ProjectPath));
            if (!File.Exists(materializedPath))
            {
                throw new InvalidOperationException(
                    $"Module '{module.Name}' declares project service '{project.Name}', but its project file was not " +
                    $"found at '{materializedPath}' in discovered repository '{repositoryPath}'.");
            }
        }
    }

    private static async Task<string> GetDefaultImageTagAsync(
        IDistributedApplicationBuilder builder,
        string repositoryPath,
        ModularAppHostsOptions options,
        CancellationToken cancellationToken)
    {
        var gitExecutablePath = GetConfiguredValue(options.GitExecutablePath) ?? "git";
        var branch = await RepositoryInspector.TryGetBranchAsync(
            repositoryPath,
            gitExecutablePath,
            options.RepositoryCommandTimeout,
            cancellationToken).ConfigureAwait(false);
        var commit = await RepositoryInspector.TryGetCommitAsync(
            repositoryPath,
            gitExecutablePath,
            options.RepositoryCommandTimeout,
            cancellationToken).ConfigureAwait(false);
        if (commit is not null)
        {
            return ModuleImageTag.FromRepository(branch, commit);
        }

        var appHostRepositoryRoot = await RepositoryInspector.TryFindRepositoryRootAsync(
            builder.AppHostDirectory,
            gitExecutablePath,
            options.RepositoryCommandTimeout,
            cancellationToken).ConfigureAwait(false);
        if (appHostRepositoryRoot is not null)
        {
            var appHostBranch = await RepositoryInspector.TryGetBranchAsync(
                appHostRepositoryRoot,
                gitExecutablePath,
                options.RepositoryCommandTimeout,
                cancellationToken).ConfigureAwait(false);
            var appHostCommit = await RepositoryInspector.TryGetCommitAsync(
                appHostRepositoryRoot,
                gitExecutablePath,
                options.RepositoryCommandTimeout,
                cancellationToken).ConfigureAwait(false);
            if (appHostCommit is not null)
            {
                return ModuleImageTag.FromRepository(appHostBranch, appHostCommit);
            }
        }

        var ciBranch = GetConfiguredValue(Environment.GetEnvironmentVariable("GITHUB_HEAD_REF")) ??
            GetConfiguredValue(Environment.GetEnvironmentVariable("GITHUB_REF_NAME"));
        return ModuleImageTag.FromRepository(ciBranch, commit: null);
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
            IDistributedApplicationModuleFactoryResource resource when resource.ImagePublishOptions is not null =>
                moduleOptions?.FindContainer(resource.Name)?.PublishImage ??
                    moduleOptions?.PublishImages ??
                    options.PublishImages,
            _ => false
        };

        DistributedApplicationModuleProjectOptions? projectOptions(DistributedApplicationModuleProject project) =>
            moduleOptions?.FindProject(project.Name);
    }

    private static IReadOnlyList<string> GetPlannedResourceNames(
        IDistributedApplicationModuleResource definition,
        string resourceName,
        ModularAppHostsOptions options,
        DistributedApplicationModuleOptions? moduleOptions,
        bool imported,
        bool runMode,
        string appHostDirectory,
        MaterializedModuleRepository definitionRepository)
    {
        if (!runMode || !RequiresImagePublishInstaller(definition, options, moduleOptions, imported))
        {
            return [resourceName];
        }

        return RequiresImageRetagInstaller(
            definition,
            moduleOptions,
            appHostDirectory,
            definitionRepository)
            ? [resourceName, GetInstallerName(resourceName), GetRetagName(resourceName)]
            : [resourceName, GetInstallerName(resourceName)];
    }

    private static bool RequiresImageRetagInstaller(
        IDistributedApplicationModuleResource definition,
        DistributedApplicationModuleOptions? moduleOptions,
        string appHostDirectory,
        MaterializedModuleRepository definitionRepository)
    {
        var imagePublisher = definition switch
        {
            DistributedApplicationModuleProject project =>
                (Declared: project.Export.Options,
                    Configured: (DistributedApplicationModuleImageOptions?)moduleOptions?.FindProject(project.Name)),
            DistributedApplicationModuleContainer container when container.ImagePublishOptions is not null =>
                (Declared: container.ImagePublishOptions,
                    Configured: (DistributedApplicationModuleImageOptions?)moduleOptions?.FindContainer(container.Name)),
            IDistributedApplicationModuleFactoryResource resource when resource.ImagePublishOptions is not null =>
                (Declared: resource.ImagePublishOptions,
                    Configured: (DistributedApplicationModuleImageOptions?)moduleOptions?.FindContainer(resource.Name)),
            _ => default
        };
        if (imagePublisher.Declared is null)
        {
            return false;
        }

        var effectiveOptions = ApplyImageOptions(
            imagePublisher.Declared,
            imagePublisher.Configured,
            "module-image-tag");
        var buildRepository = GetConfiguredValue(imagePublisher.Configured?.BuildRepository) ??
            GetConfiguredValue(imagePublisher.Declared.BuildRepository);
        var buildRevision = GetConfiguredValue(imagePublisher.Configured?.BuildRepositoryRevision) ??
            GetConfiguredValue(imagePublisher.Declared.BuildRepositoryRevision);
        var usesSeparateBuildCheckout =
            (buildRepository is not null || buildRevision is not null) &&
            ((buildRepository is not null && !RepositoryIdentitiesMatch(
                    buildRepository,
                    definitionRepository.Repository ?? definitionRepository.RepositoryPath,
                    appHostDirectory)) ||
                !string.Equals(
                    buildRevision,
                    definitionRepository.RepositoryRevision,
                    StringComparison.Ordinal));
        return usesSeparateBuildCheckout
            ? ModuleImagePublishPlan.WouldRequireRetag(effectiveOptions, repositoryDirty: false) ||
                ModuleImagePublishPlan.WouldRequireRetag(effectiveOptions, repositoryDirty: true)
            : ModuleImagePublishPlan.WouldRequireRetag(
                effectiveOptions,
                definitionRepository.RepositoryDirty);
    }

    private static bool RequiresRepositoryImageTag(
        DistributedApplicationModule module,
        ModularAppHostsOptions options,
        DistributedApplicationModuleOptions? moduleOptions,
        bool imported)
    {
        return module.ResourceDefinitions.Any(definition => definition switch
        {
            DistributedApplicationModuleProject project =>
                ResolveProjectMode(options, moduleOptions, moduleOptions?.FindProject(project.Name), imported) ==
                    ModuleProjectMode.Container &&
                GetConfiguredValue(moduleOptions?.FindProject(project.Name)?.ImageTag) is null &&
                GetConfiguredValue(project.Export.Options.ImageTag) is null,
            DistributedApplicationModuleContainer container when container.ImagePublishOptions is not null =>
                GetConfiguredValue(moduleOptions?.FindContainer(container.Name)?.ImageSHA256) is null &&
                GetConfiguredValue(moduleOptions?.FindContainer(container.Name)?.ImageTag) is null &&
                GetConfiguredValue(container.ImagePublishOptions.ImageTag) is null &&
                GetConfiguredValue(moduleOptions?.FindContainer(container.Name)?.BuildRepository) is null &&
                GetConfiguredValue(container.ImagePublishOptions.BuildRepository) is null,
            IDistributedApplicationModuleFactoryResource resource when resource.ImagePublishOptions is not null =>
                GetConfiguredValue(moduleOptions?.FindContainer(resource.Name)?.ImageSHA256) is null &&
                GetConfiguredValue(moduleOptions?.FindContainer(resource.Name)?.ImageTag) is null &&
                GetConfiguredValue(resource.ImagePublishOptions.ImageTag) is null &&
                GetConfiguredValue(moduleOptions?.FindContainer(resource.Name)?.BuildRepository) is null &&
                GetConfiguredValue(resource.ImagePublishOptions.BuildRepository) is null,
            _ => false
        });
    }

    private static IEnumerable<(string ResourceName, ModuleContainerExportOptions Options)> GetContainerPublishers(
        DistributedApplicationModule module)
    {
        foreach (var container in module.ContainerDefinitions)
        {
            if (container.ImagePublishOptions is not null)
            {
                yield return (container.Name, container.ImagePublishOptions);
            }
        }

        foreach (var resource in module.ResourceDefinitions.OfType<IDistributedApplicationModuleFactoryResource>())
        {
            if (resource.ImagePublishOptions is not null)
            {
                yield return (resource.Name, resource.ImagePublishOptions);
            }
        }
    }

    private static ModuleProjectMode ResolveProjectMode(
        ModularAppHostsOptions options,
        DistributedApplicationModuleOptions? moduleOptions,
        DistributedApplicationModuleProjectOptions? projectOptions,
        bool imported)
    {
        var mode = projectOptions?.ProjectMode ??
            moduleOptions?.ProjectMode ??
            options.ProjectMode;

        return mode == ModuleProjectMode.Auto
            ? imported ? ModuleProjectMode.Container : ModuleProjectMode.Project
            : mode;
    }

    private static string? GetConfiguredValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static bool UsesExternalImage(DistributedApplicationModuleImageOptions? options)
    {
        if (options?.PublishImage != false ||
            GetConfiguredValue(options.ImageRegistry) is null ||
            GetConfiguredValue(options.ImageName) is null)
        {
            return false;
        }

        var hasTag = GetConfiguredValue(options.ImageTag) is not null;
        var hasDigest = GetConfiguredValue(options.ImageSHA256) is not null;
        return hasTag != hasDigest;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "{Progress}")]
    private static partial void LogRepositoryProgress(ILogger logger, string progress);

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
                ValidateImageSHA256(project.ImageSHA256, $"{projectKey}:{nameof(project.ImageSHA256)}");
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
                var containerKey = $"{moduleKey}:{nameof(module.Containers)}:{containerName}";
                ValidateImageSHA256(container.ImageSHA256, $"{containerKey}:{nameof(container.ImageSHA256)}");
                if (container.ImagePullPolicy is { } containerPullPolicy)
                {
                    ValidateEnum(
                        containerPullPolicy,
                        $"{containerKey}:{nameof(container.ImagePullPolicy)}");
                }
            }
        }
    }

    private static void ValidateImageSHA256(string? sha256, string configurationKey)
    {
        if (string.IsNullOrWhiteSpace(sha256))
        {
            return;
        }

        const string prefix = "sha256:";
        if (!sha256.StartsWith(prefix, StringComparison.Ordinal) ||
            sha256.Length != prefix.Length + 64 ||
            sha256[prefix.Length..].Any(character =>
                !(character is >= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new InvalidOperationException(
                $"{configurationKey} must use the form 'sha256:<64 lowercase hexadecimal characters>'.");
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

    private static string GetRetagName(string resourceName) => $"{resourceName}-image-tagger";

    private sealed record MaterializedModuleRepository(
        string RepositoryPath,
        string? Repository,
        string? RepositoryRevision,
        bool UpdateRepository,
        bool RepositoryDirty,
        string DefaultImageTag,
        bool UsesModuleRepository);

    private sealed record ModuleImageAcquisition(
        ModuleContainerExportOptions Options,
        ModuleImagePublishPlan Plan);

}
