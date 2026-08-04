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
        string resourceName,
        EndpointReference endpoint,
        string host = "localhost",
        string? healthCheckPath = null)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        if (healthCheckPath is not null && !Uri.IsWellFormedUriString(healthCheckPath, UriKind.Relative))
        {
            throw new ArgumentException($"The health check path '{healthCheckPath}' is not a valid relative URI.", nameof(healthCheckPath));
        }

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

        var variableName = DockerComposeDeploymentTestingBuilder.GetEndpointVariableName(resourceName);
        var endpointValue = new UriBuilder(annotation.UriScheme, host, port).Uri.AbsoluteUri;

        return environment.ConfigureEnvFile(values =>
        {
            values[variableName] = new CapturedEnvironmentVariable
            {
                Name = variableName,
                Description = $"External test endpoint {resourceName}",
                DefaultValue = endpointValue,
                Resource = endpoint.Resource
            };

            if (healthCheckPath is not null)
            {
                var healthPathVariableName = DockerComposeDeploymentTestingBuilder
                    .GetEndpointHealthPathVariableName(resourceName);
                values[healthPathVariableName] = new CapturedEnvironmentVariable
                {
                    Name = healthPathVariableName,
                    Description = $"External test endpoint health check {resourceName}",
                    DefaultValue = healthCheckPath,
                    Resource = endpoint.Resource
                };
            }
        });
    }

    /// <summary>
    /// Exports a parameter or other Aspire value provider to the environment-specific Docker Compose environment file.
    /// </summary>
    public static IResourceBuilder<DockerComposeEnvironmentResource> WithTestValue(
        this IResourceBuilder<DockerComposeEnvironmentResource> environment,
        string configurationKey,
        IValueProvider value)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationKey);
        ArgumentNullException.ThrowIfNull(value);

        var variableName = DockerComposeDeploymentTestingBuilder.GetValueVariableName(configurationKey);
        return environment.ConfigureEnvFile(values =>
        {
            values[variableName] = new CapturedEnvironmentVariable
            {
                Name = variableName,
                Description = $"External test configuration value {configurationKey}",
                Source = value,
                Resource = value as IResource
            };
        });
    }
}
