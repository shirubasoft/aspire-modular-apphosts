using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Docker;

namespace Aspire.Hosting.ModularAppHosts;

/// <summary>Exports Docker Compose deployment values for use by external tests.</summary>
public static class DockerComposeTestConfigurationExtensions
{
    /// <summary>
    /// Exports an externally reachable endpoint to the environment-specific Docker Compose environment file.
    /// </summary>
    public static IResourceBuilder<DockerComposeEnvironmentResource> WithTestEndpoint(
        this IResourceBuilder<DockerComposeEnvironmentResource> environment,
        string name,
        EndpointReference endpoint,
        string host = "localhost")
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        var annotation = endpoint.EndpointAnnotation;
        if (!annotation.IsExternal)
        {
            throw new InvalidOperationException(
                $"Endpoint '{endpoint.Resource.Name}/{endpoint.EndpointName}' must be external before it can be exported to tests.");
        }

        if (annotation.Port is not int port)
        {
            throw new InvalidOperationException(
                $"Endpoint '{endpoint.Resource.Name}/{endpoint.EndpointName}' must have an explicit host port before it can be exported to tests.");
        }

        var variableName = AspireDeploymentTestConfiguration.GetEndpointVariableName(name);
        var endpointValue = new UriBuilder(annotation.UriScheme, host, port).Uri.AbsoluteUri;

        return environment.ConfigureEnvFile(values =>
        {
            values[variableName] = new CapturedEnvironmentVariable
            {
                Name = variableName,
                Description = $"External test endpoint {name}",
                DefaultValue = endpointValue,
                Resource = endpoint.Resource
            };
        });
    }

    /// <summary>
    /// Exports a parameter or other Aspire value provider to the environment-specific Docker Compose environment file.
    /// </summary>
    public static IResourceBuilder<DockerComposeEnvironmentResource> WithTestValue(
        this IResourceBuilder<DockerComposeEnvironmentResource> environment,
        string name,
        IValueProvider value)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);

        var variableName = AspireDeploymentTestConfiguration.GetValueVariableName(name);
        return environment.ConfigureEnvFile(values =>
        {
            values[variableName] = new CapturedEnvironmentVariable
            {
                Name = variableName,
                Description = $"External test configuration value {name}",
                Source = value,
                Resource = value as IResource
            };
        });
    }
}
