namespace Aspire.Hosting;

internal sealed record ModuleRepositoryContext(
    string RepositoryPath,
    string? Repository,
    string? Revision,
    bool InitializerOwned,
    bool UsesModuleRepository);

internal static class ModuleMaterializationPlanning
{
    public static ModuleRepositoryContext ResolveDefinitionRepository(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        ModuleApplicationRegistry registry,
        DistributedApplicationModuleOptions? moduleOptions,
        bool imported,
        bool requiresRepository)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(registry);

        var configurationKey = DistributedApplicationModuleExtensions.GetRepositoryConfigurationKey(module.Name);
        var repository = GetConfiguredValue(builder.Configuration[configurationKey]) ??
            GetConfiguredValue(moduleOptions?.Repository) ??
            GetConfiguredValue(module.Repository);
        var revision = GetConfiguredValue(moduleOptions?.RepositoryRevision) ??
            GetConfiguredValue(module.RepositoryRevision);
        var checkoutDirectoryName = moduleOptions?.CheckoutDirectoryName ??
            module.CheckoutDirectoryName;

        if (!imported)
        {
            var localPath = GetLocalDefinitionPath(builder, module, repository);
            return new ModuleRepositoryContext(
                localPath,
                repository ?? localPath,
                Revision: null,
                InitializerOwned: false,
                UsesModuleRepository: true);
        }

        if (!requiresRepository)
        {
            var optionalLocalPath = repository is not null &&
                !RepositoryIdentity.IsRemoteRepository(repository, builder.AppHostDirectory)
                    ? Path.GetFullPath(repository, builder.AppHostDirectory)
                    : Path.GetFullPath(builder.AppHostDirectory);
            return new ModuleRepositoryContext(
                optionalLocalPath,
                repository,
                revision,
                InitializerOwned: false,
                UsesModuleRepository: true);
        }

        if (repository is null)
        {
            throw new InvalidOperationException(
                $"Imported module '{module.Name}' requires repository content. Configure '{configurationKey}' " +
                "or declare the repository with WithRepository().");
        }

        var isRemote = RepositoryIdentity.IsRemoteRepository(repository, builder.AppHostDirectory);
        if (!isRemote && revision is null)
        {
            RejectLocalCheckoutDirectoryName(checkoutDirectoryName, configurationKey);
            var localPath = Path.GetFullPath(repository, builder.AppHostDirectory);
            registry.RequireDirectory(module.Name, "repository checkout", localPath);
            return new ModuleRepositoryContext(
                localPath,
                localPath,
                Revision: null,
                InitializerOwned: false,
                UsesModuleRepository: true);
        }

        var updateRepository = moduleOptions?.UpdateRepositoryOnInitialize ??
            registry.Options.UpdateRepositoriesOnInitialize;
        var requirement = registry.RegisterRepository(
            builder,
            module.Name,
            isRemote ? repository : Path.GetFullPath(repository, builder.AppHostDirectory),
            revision,
            updateRepository,
            checkoutDirectoryName: checkoutDirectoryName,
            checkoutDirectoryNameConfigurationKey: GetCheckoutDirectoryNameConfigurationKey(module.Name));
        return new ModuleRepositoryContext(
            requirement.RepositoryPath,
            requirement.Repository,
            requirement.Revision,
            InitializerOwned: true,
            UsesModuleRepository: true);
    }

    public static ModuleRepositoryContext ResolveBuildRepository(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        string resourceName,
        ModuleImageCommandOptions declared,
        DistributedApplicationModuleImageOptions? configured,
        ModuleRepositoryContext definitionRepository,
        ModuleApplicationRegistry registry,
        DistributedApplicationModuleOptions? moduleOptions,
        bool allowMissingBuildRepository = false,
        ModuleResourceKind resourceKind = ModuleResourceKind.Container)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(module);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentNullException.ThrowIfNull(definitionRepository);
        ArgumentNullException.ThrowIfNull(registry);

        var requestedRepository = GetConfiguredValue(configured?.BuildRepository) ??
            GetConfiguredValue(declared.BuildRepository);
        var requestedRevision = GetConfiguredValue(configured?.BuildRepositoryRevision) ??
            GetConfiguredValue(declared.BuildRepositoryRevision);
        var checkoutDirectoryName = declared.CheckoutDirectoryName;
        var checkoutDirectoryNameConfigurationKey = GetBuildRepositoryCheckoutDirectoryNameConfigurationKey(
            module.Name,
            resourceName,
            resourceKind);
        if (requestedRepository is null && requestedRevision is null && checkoutDirectoryName is null)
        {
            return definitionRepository;
        }

        var repository = requestedRepository ??
            definitionRepository.Repository ??
            definitionRepository.RepositoryPath;
        var normalizedRepository = RepositoryIdentity.IsRemoteRepository(
            repository,
            builder.AppHostDirectory)
                ? repository
                : Path.GetFullPath(repository, builder.AppHostDirectory);
        if (RepositoryIdentity.AreEquivalent(
                normalizedRepository,
                definitionRepository.Repository ?? definitionRepository.RepositoryPath,
                builder.AppHostDirectory) &&
            string.Equals(requestedRevision, definitionRepository.Revision, StringComparison.Ordinal) &&
            checkoutDirectoryName is null)
        {
            return definitionRepository;
        }

        var isRemote = RepositoryIdentity.IsRemoteRepository(
            normalizedRepository,
            builder.AppHostDirectory);
        if (!isRemote && requestedRevision is null)
        {
            RejectLocalCheckoutDirectoryName(
                checkoutDirectoryName,
                checkoutDirectoryNameConfigurationKey);
            registry.RequireDirectory(
                module.Name,
                $"build repository for resource '{resourceName}'",
                normalizedRepository,
                requiredOnRun: !allowMissingBuildRepository,
                resourceName: resourceName);
            return new ModuleRepositoryContext(
                normalizedRepository,
                normalizedRepository,
                Revision: null,
                InitializerOwned: false,
                UsesModuleRepository: false);
        }

        var updateRepository = moduleOptions?.UpdateRepositoryOnInitialize ??
            registry.Options.UpdateRepositoriesOnInitialize;
        var requirement = registry.RegisterRepository(
            builder,
            module.Name,
            normalizedRepository,
            requestedRevision,
            updateRepository,
            requiredOnRun: !allowMissingBuildRepository,
            checkoutDirectoryName: checkoutDirectoryName,
            checkoutDirectoryNameConfigurationKey: checkoutDirectoryNameConfigurationKey,
            resourceName: resourceName,
            requirementName: $"{module.Name}/{resourceName} image");
        return new ModuleRepositoryContext(
            requirement.RepositoryPath,
            requirement.Repository,
            requirement.Revision,
            InitializerOwned: true,
            UsesModuleRepository: false);
    }

    private static string GetLocalDefinitionPath(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        string? repository)
    {
        if (repository is not null &&
            !RepositoryIdentity.IsRemoteRepository(repository, builder.AppHostDirectory))
        {
            return Path.GetFullPath(repository, builder.AppHostDirectory);
        }

        return module.ProjectDefinitions
            .Select(project => project.SourceRepositoryRoot)
            .OfType<string>()
            .Distinct(PathSafety.Comparer)
            .SingleOrDefault() ?? Path.GetFullPath(builder.AppHostDirectory);
    }

    private static string? GetConfiguredValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string GetCheckoutDirectoryNameConfigurationKey(string moduleName) =>
        $"{DistributedApplicationModuleExtensions.GetModuleConfigurationKey(moduleName)}:" +
        $"{nameof(DistributedApplicationModuleOptions.CheckoutDirectoryName)}";

    private static string GetBuildRepositoryCheckoutDirectoryNameConfigurationKey(
        string moduleName,
        string resourceName,
        ModuleResourceKind resourceKind)
    {
        var collection = resourceKind == ModuleResourceKind.Project
            ? nameof(DistributedApplicationModuleOptions.Projects)
            : nameof(DistributedApplicationModuleOptions.Containers);
        return $"{DistributedApplicationModuleExtensions.GetModuleConfigurationKey(moduleName)}:" +
            $"{collection}:{resourceName}:{nameof(DistributedApplicationModuleImageOptions.CheckoutDirectoryName)}";
    }

    private static void RejectLocalCheckoutDirectoryName(string? value, string repositoryConfigurationKey)
    {
        if (value is null)
        {
            return;
        }

        var configurationKey = repositoryConfigurationKey.EndsWith(
            $":{nameof(DistributedApplicationModuleOptions.Repository)}",
            StringComparison.Ordinal)
                ? repositoryConfigurationKey[..^nameof(DistributedApplicationModuleOptions.Repository).Length] +
                    nameof(DistributedApplicationModuleOptions.CheckoutDirectoryName)
                : repositoryConfigurationKey;
        throw new InvalidOperationException(
            $"Checkout directory name '{value}' from configuration key '{configurationKey}' is invalid: " +
            "CheckoutDirectoryName applies only to unpinned remote repositories; local-path repository behavior is unchanged.");
    }
}
