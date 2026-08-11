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
                !GitHubRepositoryCloner.IsRemoteRepository(repository, builder.AppHostDirectory)
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

        var isRemote = GitHubRepositoryCloner.IsRemoteRepository(repository, builder.AppHostDirectory);
        if (!isRemote && revision is null)
        {
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
            updateRepository);
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
        ModuleContainerExportOptions declared,
        DistributedApplicationModuleImageOptions? configured,
        ModuleRepositoryContext definitionRepository,
        ModuleApplicationRegistry registry,
        DistributedApplicationModuleOptions? moduleOptions)
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
        if (requestedRepository is null && requestedRevision is null)
        {
            return definitionRepository;
        }

        var repository = requestedRepository ??
            definitionRepository.Repository ??
            definitionRepository.RepositoryPath;
        var normalizedRepository = GitHubRepositoryCloner.IsRemoteRepository(
            repository,
            builder.AppHostDirectory)
                ? repository
                : Path.GetFullPath(repository, builder.AppHostDirectory);
        if (RepositoryIdentitiesMatch(
                normalizedRepository,
                definitionRepository.Repository ?? definitionRepository.RepositoryPath,
                builder.AppHostDirectory) &&
            string.Equals(requestedRevision, definitionRepository.Revision, StringComparison.Ordinal))
        {
            return definitionRepository;
        }

        var isRemote = GitHubRepositoryCloner.IsRemoteRepository(
            normalizedRepository,
            builder.AppHostDirectory);
        if (!isRemote && requestedRevision is null)
        {
            registry.RequireDirectory(
                module.Name,
                $"build repository for resource '{resourceName}'",
                normalizedRepository);
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
            $"{module.Name}/{resourceName} image",
            normalizedRepository,
            requestedRevision,
            updateRepository);
        return new ModuleRepositoryContext(
            requirement.RepositoryPath,
            requirement.Repository,
            requirement.Revision,
            InitializerOwned: true,
            UsesModuleRepository: false);
    }

    public static bool RepositoryIdentitiesMatch(
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

    private static string GetLocalDefinitionPath(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        string? repository)
    {
        if (repository is not null &&
            !GitHubRepositoryCloner.IsRemoteRepository(repository, builder.AppHostDirectory))
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
}
