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

        if (healthCheckPath is not null &&
            (healthCheckPath.Length == 0 || healthCheckPath[0] != '/' ||
             !Uri.IsWellFormedUriString(healthCheckPath, UriKind.Relative)))
        {
            throw new ArgumentException(
                $"The health check path '{healthCheckPath}' must be a root-relative URI path.",
                nameof(healthCheckPath));
        }

        var annotation = endpoint.EndpointAnnotation;
        if (!annotation.IsExternal)
        {
            throw new InvalidOperationException(
                $"Endpoint '{endpoint.Resource.Name}/{endpoint.EndpointName}' must be external before it can be exported to tests.");
        }

        if (annotation.Port is not int port)
        {
            port = AvailableHostPortAllocator.Allocate();
            annotation.Port = port;
        }
        else if (port is <= 0 or > 65535)
        {
            throw new InvalidOperationException(
                $"Endpoint '{endpoint.Resource.Name}/{endpoint.EndpointName}' has invalid host port '{port}'.");
        }

        var endpointName = endpoint.EndpointName;
        var variableName = DockerComposeDeploymentTestingBuilder.GetEndpointVariableName(resourceName, endpointName);
        var endpointValue = new UriBuilder(annotation.UriScheme, host, port).Uri.AbsoluteUri;

        return environment.ConfigureEnvFile(values =>
        {
            values[variableName] = new CapturedEnvironmentVariable
            {
                Name = variableName,
                Description = $"External test endpoint {resourceName}/{endpointName}",
                DefaultValue = endpointValue,
                Resource = endpoint.Resource
            };

            if (healthCheckPath is not null)
            {
                var healthPathVariableName = DockerComposeDeploymentTestingBuilder
                    .GetEndpointHealthPathVariableName(resourceName, endpointName);
                values[healthPathVariableName] = new CapturedEnvironmentVariable
                {
                    Name = healthPathVariableName,
                    Description = $"External test endpoint health check {resourceName}/{endpointName}",
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

    /// <summary>
    /// Exports a resource connection string to the environment-specific Docker Compose environment file.
    /// </summary>
    public static IResourceBuilder<DockerComposeEnvironmentResource> WithTestConnectionString<T>(
        this IResourceBuilder<DockerComposeEnvironmentResource> environment,
        string connectionName,
        IResourceBuilder<T> resource)
        where T : IResourceWithConnectionString
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);
        ArgumentNullException.ThrowIfNull(resource);

        var configurationKey = $"ConnectionStrings:{connectionName}";
        var variableName = DockerComposeDeploymentTestingBuilder.GetValueVariableName(configurationKey);
        return environment.ConfigureEnvFile(values =>
        {
            values[variableName] = new CapturedEnvironmentVariable
            {
                Name = variableName,
                Description = $"External test connection string {connectionName}",
                Source = resource.Resource.ConnectionStringExpression,
                Resource = resource.Resource
            };
        });
    }
}
