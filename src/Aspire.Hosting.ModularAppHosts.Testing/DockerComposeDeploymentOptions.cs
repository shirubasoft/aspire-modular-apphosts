namespace Aspire.Hosting.ModularAppHosts;

/// <summary>Controls an Aspire-owned Docker Compose test deployment.</summary>
public sealed class DockerComposeDeploymentOptions
{
    /// <summary>
    /// Gets or sets the Aspire deployment environment name. The default is unique per options instance so concurrent
    /// test deployments do not share Compose state.
    /// </summary>
    public string EnvironmentName { get; set; } =
        DockerComposeDeploymentTestingBuilder.CreateDefaultDeploymentEnvironmentName();

    /// <summary>
    /// Gets or sets the deployment output directory. A temporary directory is used and removed on disposal when omitted.
    /// </summary>
    public string? OutputPath { get; set; }

    /// <summary>Gets or sets the path to the Aspire CLI executable.</summary>
    public string AspireCliPath { get; set; } = "aspire";

    /// <summary>Gets or sets the maximum time allowed for <c>aspire deploy</c>.</summary>
    public TimeSpan DeploymentTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Gets or sets the maximum time allowed for <c>aspire destroy</c>.</summary>
    public TimeSpan CleanupTimeout { get; set; } = TimeSpan.FromMinutes(2);
}
