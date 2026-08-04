using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.ModularAppHosts;

/// <summary>Extensions for composing reusable modules in an Aspire AppHost.</summary>
public static class DistributedApplicationModuleExtensions
{
    /// <summary>The Aspire parameter used as the parent directory for managed repository clones.</summary>
    public const string RepositoryBaseLocationParameterName = "module-repository-base-location";

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

        if (module.ProjectDefinitions.Count > 0 && string.IsNullOrWhiteSpace(module.Repository))
        {
            throw new InvalidOperationException(
                $"Module '{name}' exports projects but does not have a repository location. " +
                "Configure one with moduleBuilder.WithRepository(...).");
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

        var hasRepository = !string.IsNullOrWhiteSpace(module.Repository);
        var repositoryPath = imported && hasRepository
            ? GetImportedRepositoryPath(builder, registry, module.Name)
            : GetLocalRepositoryPath(builder, module);

        ValidateResourceNames(builder, module, registry);
        ConfigureRepositorySynchronization(builder, module, registry, repositoryPath, imported);

        foreach (var definition in module.ResourceDefinitions)
        {
            switch (definition)
            {
                case DistributedApplicationModuleProject project:
                    MaterializeProject(builder, module, project, repositoryPath, imported, registry);
                    break;
                case DistributedApplicationModuleContainer container:
                    MaterializeContainer(builder, module, container, repositoryPath, imported, registry);
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
        bool imported,
        ModuleApplicationRegistry registry)
    {
        var export = project.Export;
        var sourceProjectDirectory = Path.GetDirectoryName(project.ProjectPath)
            ?? throw new InvalidOperationException($"Unable to determine the directory for '{project.ProjectPath}'.");
        var projectDirectoryRelativePath = Path.GetRelativePath(project.SourceRepositoryRoot, sourceProjectDirectory);
        var workingDirectoryRelativePath = export.Options.WorkingDirectory ?? projectDirectoryRelativePath;
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

        var container = builder
            .AddContainer(project.Name, export.Options.ImageName, export.Options.ImageTag)
            .WithImagePullPolicy(ImagePullPolicy.Missing)
            .WithAnnotation(new DistributedApplicationModuleResourceAnnotation(
                module.Name,
                project.Name,
                repositoryPath,
                imported));

        export.ConfigureContainer?.Invoke(container);

        if (builder.ExecutionContext.IsRunMode)
        {
            AddRepositoryInstaller(
                builder,
                module,
                project,
                repositoryPath,
                publishWorkingDirectory,
                imported,
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
        bool imported,
        ModuleApplicationRegistry registry)
    {
        var container = builder
            .AddContainer(definition.Name, definition.Image, definition.Tag)
            .WithAnnotation(new DistributedApplicationModuleResourceAnnotation(
                module.Name,
                definition.Name,
                repositoryPath,
                imported));

        definition.ConfigureContainer?.Invoke(container);

        registry.TrackResource(container.Resource);
        module.TrackMaterializedResource(builder, container.Resource);
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

    private static void AddRepositoryInstaller(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        DistributedApplicationModuleProject project,
        string repositoryPath,
        string publishWorkingDirectory,
        bool imported,
        IResourceBuilder<ContainerResource> container,
        ModuleApplicationRegistry registry)
    {
        var installerResource = new ModuleRepositoryInstallerResource(
            GetInstallerName(project.Name),
            repositoryPath,
            module.Repository,
            imported,
            project.Export.Options.PublishCommand,
            project.Export.Options.PublishArguments,
            publishWorkingDirectory);

        var installer = builder.AddResource(installerResource)
            .WithArgs(project.Export.Options.PublishArguments.ToArray())
            .WithEnvironment(
                "ASPIRE_MODULE_IMAGE",
                $"{project.Export.Options.ImageName}:{project.Export.Options.ImageTag}")
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
        DistributedApplicationModule module,
        ModuleApplicationRegistry registry,
        string repositoryPath,
        bool imported)
    {
        if (!builder.ExecutionContext.IsRunMode || !imported || string.IsNullOrWhiteSpace(module.Repository))
        {
            return;
        }

        builder.Eventing.Subscribe<BeforeStartEvent>(async (_, cancellationToken) =>
        {
            await registry.SynchronizeRepositoryAsync(
                repositoryPath,
                () => RepositorySynchronizer.SynchronizeAsync(
                    repositoryPath,
                    module.Repository,
                    updateRepository: true,
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

        var registry = new ModuleApplicationRegistry();
        builder.Services.AddSingleton<IDistributedApplicationModuleCatalog>(registry);
        return registry;
    }

    private static string GetImportedRepositoryPath(
        IDistributedApplicationBuilder builder,
        ModuleApplicationRegistry registry,
        string moduleName)
    {
        var configuredLocation = builder.Configuration[$"Parameters:{RepositoryBaseLocationParameterName}"];
        var defaultLocation = Path.Combine(builder.AppHostDirectory, ".aspire", "module-repositories");
        var baseLocation = Path.GetFullPath(configuredLocation ?? defaultLocation, builder.AppHostDirectory);

        if (!builder.TryCreateResourceBuilder<ParameterResource>(RepositoryBaseLocationParameterName, out var parameter))
        {
            parameter = builder
                .AddParameter(RepositoryBaseLocationParameterName, baseLocation, publishValueAsDefault: true)
                .WithDescription("Parent directory used for repositories cloned by ImportModule.");
        }

        registry.TrackResource(parameter.Resource);
        Directory.CreateDirectory(baseLocation);
        return Path.Combine(baseLocation, GetSafeDirectoryName(moduleName));
    }

    private static string GetLocalRepositoryPath(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module)
    {
        if (module.ProjectDefinitions.Count > 0)
        {
            return module.ProjectDefinitions[0].SourceRepositoryRoot;
        }

        if (!string.IsNullOrWhiteSpace(module.Repository) &&
            (!Uri.TryCreate(module.Repository, UriKind.Absolute, out var repositoryUri) || repositoryUri.IsFile))
        {
            var candidate = Path.GetFullPath(module.Repository, builder.AppHostDirectory);
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
        ModuleApplicationRegistry registry)
    {
        var resourceNames = module.ResourceDefinitions.SelectMany(definition =>
            definition is DistributedApplicationModuleProject && builder.ExecutionContext.IsRunMode
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
