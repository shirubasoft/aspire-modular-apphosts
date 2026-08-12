using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Publishing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting;

/// <summary>Extensions for composing reusable modules in an Aspire AppHost.</summary>
public static partial class DistributedApplicationModuleExtensions
{
    /// <summary>Exports a named module definition without adding its services to the application model.</summary>
    public static IDistributedApplicationModule ExportModule(
        this IDistributedApplicationBuilder builder,
        string name,
        Action<IDistributedApplicationModuleBuilder> moduleBuilder)
    {
        return DefineModule(builder, name, "1", packageId: null, moduleBuilder);
    }

    /// <summary>Exports a named module definition with its NuGet contract package identity.</summary>
    public static IDistributedApplicationModule ExportModule(
        this IDistributedApplicationBuilder builder,
        string name,
        string packageId,
        Action<IDistributedApplicationModuleBuilder> moduleBuilder)
    {
        return DefineModule(builder, name, "1", packageId, moduleBuilder);
    }

    /// <summary>Defines a versioned module contract without adding its resources to the application model.</summary>
    public static IDistributedApplicationModule DefineModule(
        this IDistributedApplicationBuilder builder,
        string name,
        string version,
        Action<IDistributedApplicationModuleBuilder> moduleBuilder)
    {
        return DefineModule(builder, name, version, packageId: null, moduleBuilder);
    }

    /// <summary>Defines a versioned module contract with its NuGet package identity.</summary>
    public static IDistributedApplicationModule DefineModule(
        this IDistributedApplicationBuilder builder,
        string name,
        string version,
        string? packageId,
        Action<IDistributedApplicationModuleBuilder> moduleBuilder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ValidatePackageId(packageId);
        ArgumentNullException.ThrowIfNull(moduleBuilder);

        var registry = GetOrCreateRegistry(builder);
        registry.RefreshConfiguration();
        ValidateOptions(registry.Options);
        if (registry.TryGetDefinition(name, out var existingModule) && existingModule is not null)
        {
            ValidateExistingDefinition(existingModule, version, packageId);
            return existingModule;
        }

        var module = new DistributedApplicationModule(builder, name, version, packageId);
        moduleBuilder(new DistributedApplicationModuleBuilder(builder, module, registry));
        module.Validate();
        ValidateModuleConfiguration(module, registry.Options.FindModule(module.Name));
        registry.AddModule(module);
        return module;
    }

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
            configured.RefreshBuildRepositoryOnRun is not null ||
            configured.PublishImage is true)
        {
            throw new InvalidOperationException(
                $"Container resource '{resourceName}' configures image publishing, but its module definition does not " +
                $"call {declarationMethod}() with image publish options.");
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
        builder.Eventing.Subscribe<BeforeStartEvent>((@event, _) =>
        {
            registry.RefreshConfiguration();
            ValidateOptions(registry.Options);
            registry.ValidateConfiguredModules();
            if (!ModuleRepositoryInitializationPipeline.IsInitializeCommand(
                    Environment.GetCommandLineArgs()))
            {
                registry.ValidateRepositoryPreflight(
                    @event.Services.GetRequiredService<ILoggerFactory>()
                        .CreateLogger("Aspire.Hosting.ModuleRepositoryPreflight"));
            }

            return Task.CompletedTask;
        });
        builder.Eventing.Subscribe<BeforePublishEvent>((@event, _) =>
        {
            registry.RefreshConfiguration();
            ValidateOptions(registry.Options);
            registry.ValidateConfiguredModules();
            if (!ModuleRepositoryInitializationPipeline.IsInitializeCommand(
                    Environment.GetCommandLineArgs()))
            {
                registry.ValidateRepositoryPreflight(
                    @event.Services.GetRequiredService<ILoggerFactory>()
                        .CreateLogger("Aspire.Hosting.ModuleRepositoryPreflight"));
            }

            return Task.CompletedTask;
        });
        return registry;
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

    private static string FormatNames(IEnumerable<string> names)
    {
        var values = names.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        return values.Length == 0 ? "(none)" : string.Join(", ", values.Select(name => $"'{name}'"));
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
        return options?.PublishImage == false;
    }

    private static void ValidatePackageId(string? packageId)
    {
        if (packageId is null)
        {
            return;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        if (packageId.Length > 100 || packageId.Any(character =>
            !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_')))
        {
            throw new ArgumentException(
                $"'{packageId}' is not a valid NuGet package ID.",
                nameof(packageId));
        }
    }

    private static void ValidateExistingDefinition(
        DistributedApplicationModule existingModule,
        string version,
        string? packageId)
    {
        if (!string.Equals(existingModule.Version, version, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Module '{existingModule.Name}' is already defined with contract version '{existingModule.Version}', " +
                $"not requested version '{version}'.");
        }

        if (!string.Equals(existingModule.PackageId, packageId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Module '{existingModule.Name}' is already defined with contract package ID " +
                $"'{existingModule.PackageId ?? "none"}', not requested package ID '{packageId ?? "none"}'.");
        }
    }

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
                ValidateExternalImage(project, projectKey);
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
                ValidateExternalImage(container, containerKey);
                if (container.ImagePullPolicy is { } containerPullPolicy)
                {
                    ValidateEnum(
                        containerPullPolicy,
                        $"{containerKey}:{nameof(container.ImagePullPolicy)}");
                }
            }
        }
    }

    private static void ValidateExternalImage(
        DistributedApplicationModuleImageOptions options,
        string configurationKey)
    {
        if (options.PublishImage != false)
        {
            return;
        }

        if (GetConfiguredValue(options.ImageRegistry) is null)
        {
            throw new InvalidOperationException(
                $"{configurationKey}:{nameof(options.ImageRegistry)} is required when " +
                $"{configurationKey}:{nameof(options.PublishImage)} is false.");
        }

        if (GetConfiguredValue(options.ImageName) is null)
        {
            throw new InvalidOperationException(
                $"{configurationKey}:{nameof(options.ImageName)} is required when " +
                $"{configurationKey}:{nameof(options.PublishImage)} is false.");
        }

        var hasTag = GetConfiguredValue(options.ImageTag) is not null;
        var hasDigest = GetConfiguredValue(options.ImageSHA256) is not null;
        if (hasTag == hasDigest)
        {
            throw new InvalidOperationException(
                $"Configure exactly one of {configurationKey}:{nameof(options.ImageTag)} or " +
                $"{configurationKey}:{nameof(options.ImageSHA256)} when " +
                $"{configurationKey}:{nameof(options.PublishImage)} is false.");
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

}
