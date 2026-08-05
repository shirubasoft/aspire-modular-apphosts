using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting.ModularAppHosts;

/// <summary>Controls how exported modules are materialized by a receiving AppHost.</summary>
/// <remarks>
/// Values are loaded from <c>Aspire:ModularAppHosts</c>. Configure these options before calling
/// <c>AddAsync</c> or <c>ImportModuleAsync</c> because they shape the Aspire application model.
/// </remarks>
public sealed class ModularAppHostsOptions
{
    /// <summary>The configuration section bound to these options.</summary>
    public const string ConfigurationSectionName = "Aspire:ModularAppHosts";

    /// <summary>
    /// Gets or sets the parent directory for managed repository checkouts. When omitted,
    /// <c>&lt;AppHost&gt;/.aspire/module-repositories</c> is used.
    /// </summary>
    public string? RepositoryBasePath { get; set; }

    /// <summary>
    /// Gets or sets whether missing module repositories are cloned beside the current AppHost Git repository.
    /// This opt-in convention uses the GitHub CLI and does not apply to modules in the AppHost repository.
    /// </summary>
    public bool AutoCloneRepositories { get; set; }

    /// <summary>Gets or sets the GitHub CLI executable used by automatic sibling clones.</summary>
    public string GitHubCliPath { get; set; } = "gh";

    /// <summary>Gets or sets the Git executable used to synchronize managed repositories.</summary>
    public string GitExecutablePath { get; set; } = "git";

    /// <summary>Gets or sets the maximum duration of one repository CLI command.</summary>
    public TimeSpan RepositoryCommandTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Gets or sets whether existing clean imported repositories are fast-forwarded before startup.</summary>
    public bool UpdateImportedRepositories { get; set; }

    /// <summary>Gets or sets how exported projects run in Aspire run mode.</summary>
    public ModuleProjectMode ProjectMode { get; set; } = ModuleProjectMode.Auto;

    /// <summary>Gets or sets whether declared image publish commands may run in Aspire run mode.</summary>
    public bool PublishImages { get; set; }

    /// <summary>Gets module-specific overrides keyed by module name.</summary>
    public IDictionary<string, DistributedApplicationModuleOptions> Modules { get; } =
        new Dictionary<string, DistributedApplicationModuleOptions>(StringComparer.OrdinalIgnoreCase);

    internal static ModularAppHostsOptions FromConfiguration(IConfiguration configuration)
    {
        var options = new ModularAppHostsOptions();
        configuration.GetSection(ConfigurationSectionName).Bind(options);
        return options;
    }

    internal DistributedApplicationModuleOptions? FindModule(string name) =>
        Modules.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
}

/// <summary>Overrides materialization behavior for one distributed application module.</summary>
public sealed class DistributedApplicationModuleOptions
{
    /// <summary>
    /// Gets or sets the Git repository used when the module is imported. Configuration values are
    /// represented by an Aspire parameter; values set in code are used directly.
    /// </summary>
    public string? Repository { get; set; }

    /// <summary>Gets or sets the branch, tag, or commit checked out for this module.</summary>
    public string? RepositoryRevision { get; set; }

    /// <summary>
    /// Gets or sets whether a missing repository for this module is cloned beside the AppHost repository.
    /// </summary>
    public bool? AutoCloneRepository { get; set; }

    /// <summary>Gets or sets whether an existing clean checkout is fast-forwarded before startup.</summary>
    public bool? UpdateRepository { get; set; }

    /// <summary>Gets or sets how this module's exported projects run in Aspire run mode.</summary>
    public ModuleProjectMode? ProjectMode { get; set; }

    /// <summary>Gets or sets whether image publish commands declared by this module may run.</summary>
    public bool? PublishImages { get; set; }

    /// <summary>Gets project-specific overrides keyed by resource name.</summary>
    public IDictionary<string, DistributedApplicationModuleProjectOptions> Projects { get; } =
        new Dictionary<string, DistributedApplicationModuleProjectOptions>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets container-specific overrides keyed by resource name.</summary>
    public IDictionary<string, DistributedApplicationModuleContainerOptions> Containers { get; } =
        new Dictionary<string, DistributedApplicationModuleContainerOptions>(StringComparer.OrdinalIgnoreCase);

    internal DistributedApplicationModuleProjectOptions? FindProject(string name) =>
        Projects.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    internal DistributedApplicationModuleContainerOptions? FindContainer(string name) =>
        Containers.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
}

/// <summary>Overrides image identity and publishing for one module resource.</summary>
public abstract class DistributedApplicationModuleImageOptions
{
    /// <summary>Gets or sets the effective container image name.</summary>
    public string? ImageName { get; set; }

    /// <summary>Gets or sets the effective container image tag.</summary>
    public string? ImageTag { get; set; }

    /// <summary>Gets or sets whether the declared image publish command may run.</summary>
    public bool? PublishImage { get; set; }

    /// <summary>Gets or sets the executable used to publish the image.</summary>
    public string? PublishCommand { get; set; }

    /// <summary>Gets or sets the complete argument list used to publish the image.</summary>
    public IReadOnlyList<string>? PublishArguments { get; set; }

    /// <summary>Gets or sets the publish working directory relative to the module repository.</summary>
    public string? PublishWorkingDirectory { get; set; }

    /// <summary>Gets or sets the container runtime image pull policy.</summary>
    public ImagePullPolicy? ImagePullPolicy { get; set; }
}

/// <summary>Overrides materialization behavior for one exported project.</summary>
public sealed class DistributedApplicationModuleProjectOptions : DistributedApplicationModuleImageOptions
{
    /// <summary>Gets or sets how this project runs in Aspire run mode.</summary>
    public ModuleProjectMode? ProjectMode { get; set; }

    /// <summary>Gets or sets the launch profile used when the project runs directly.</summary>
    public string? LaunchProfileName { get; set; }

    /// <summary>Gets or sets whether launch profile discovery is disabled when the project runs directly.</summary>
    public bool? ExcludeLaunchProfile { get; set; }

    /// <summary>Gets or sets whether Kestrel endpoints are excluded when the project runs directly.</summary>
    public bool? ExcludeKestrelEndpoints { get; set; }
}

/// <summary>Overrides materialization behavior for one declared container.</summary>
public sealed class DistributedApplicationModuleContainerOptions : DistributedApplicationModuleImageOptions;

/// <summary>Controls how an exported project is represented in Aspire run mode.</summary>
public enum ModuleProjectMode
{
    /// <summary>Runs local modules as projects and imported modules as containers.</summary>
    Auto,

    /// <summary>Runs the project directly for local debugging.</summary>
    Project,

    /// <summary>Runs the project's exported container representation.</summary>
    Container
}
