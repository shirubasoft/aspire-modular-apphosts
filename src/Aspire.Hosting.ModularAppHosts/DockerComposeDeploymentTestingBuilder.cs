#pragma warning disable ASPIREFILESYSTEM001
#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREUSERSECRETS001

using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Testing;
using HealthChecks.Uris;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aspire.Hosting.ModularAppHosts;

/// <summary>
/// Builds an Aspire testing application that represents services already deployed through Docker Compose.
/// </summary>
public sealed class DockerComposeDeploymentTestingBuilder : IDistributedApplicationTestingBuilder
{
    /// <summary>The environment variable that identifies the deployment environment file to load.</summary>
    public const string FilePathEnvironmentVariableName = "ASPIRE_TEST_CONFIGURATION_FILE";

    private const string EndpointPrefix = "ASPIRE_TEST_ENDPOINT__";
    private const string EndpointHealthPathPrefix = "ASPIRE_TEST_ENDPOINT_HEALTH_PATH__";
    private const string ValuePrefix = "ASPIRE_TEST_VALUE__";
    private readonly IDistributedApplicationBuilder _innerBuilder;
    private DistributedApplication? _application;

    private DockerComposeDeploymentTestingBuilder(IDistributedApplicationBuilder innerBuilder)
    {
        _innerBuilder = innerBuilder;
    }

    /// <summary>
    /// Creates a testing builder from an Aspire-generated Docker Compose environment file.
    /// </summary>
    /// <typeparam name="TEntryPoint">A type in the AppHost assembly used by the deployment.</typeparam>
    /// <param name="filePath">The path to the environment-specific deployment file.</param>
    public static DockerComposeDeploymentTestingBuilder Create<TEntryPoint>(string filePath)
        where TEntryPoint : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var absolutePath = Path.GetFullPath(filePath);
        if (!File.Exists(absolutePath))
        {
            throw new FileNotFoundException(
                $"The Aspire deployment test configuration file '{absolutePath}' does not exist.",
                absolutePath);
        }

        var appHostAssembly = typeof(TEntryPoint).Assembly;
        var innerBuilder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = [],
            AssemblyName = appHostAssembly.GetName().Name,
            DisableDashboard = true,
            ProjectDirectory = ResolveProjectDirectory(appHostAssembly)
        });
        innerBuilder.Services.AddHttpClient();

        var values = LoadValues(absolutePath);
        ImportConfiguration(innerBuilder, values);
        ImportEndpoints(innerBuilder, values);

        return new DockerComposeDeploymentTestingBuilder(innerBuilder);
    }

    /// <summary>
    /// Creates a testing builder from the file identified by <see cref="FilePathEnvironmentVariableName"/>.
    /// </summary>
    /// <typeparam name="TEntryPoint">A type in the AppHost assembly used by the deployment.</typeparam>
    public static DockerComposeDeploymentTestingBuilder CreateFromEnvironment<TEntryPoint>()
        where TEntryPoint : class
    {
        var filePath = System.Environment.GetEnvironmentVariable(FilePathEnvironmentVariableName);
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new InvalidOperationException(
                $"Set {FilePathEnvironmentVariableName} to the Aspire deployment environment file before running external tests.");
        }

        return Create<TEntryPoint>(filePath);
    }

    /// <inheritdoc />
    public ConfigurationManager Configuration => _innerBuilder.Configuration;

    /// <inheritdoc />
    public string AppHostDirectory => _innerBuilder.AppHostDirectory;

    /// <inheritdoc />
    public Assembly? AppHostAssembly => _innerBuilder.AppHostAssembly;

    /// <inheritdoc />
    public IHostEnvironment Environment => _innerBuilder.Environment;

    /// <inheritdoc />
    public IServiceCollection Services => _innerBuilder.Services;

    /// <inheritdoc />
    public DistributedApplicationExecutionContext ExecutionContext => _innerBuilder.ExecutionContext;

    /// <inheritdoc />
    public IDistributedApplicationEventing Eventing => _innerBuilder.Eventing;

    /// <inheritdoc />
    public IDistributedApplicationPipeline Pipeline => _innerBuilder.Pipeline;

    /// <inheritdoc />
    public IResourceCollection Resources => _innerBuilder.Resources;

    /// <inheritdoc />
    public IFileSystemService FileSystemService => _innerBuilder.FileSystemService;

    /// <inheritdoc />
    public IUserSecretsManager UserSecretsManager => _innerBuilder.UserSecretsManager;

    /// <inheritdoc />
    public IResourceBuilder<T> AddResource<T>(T resource)
        where T : IResource => _innerBuilder.AddResource(resource);

    /// <inheritdoc />
    public IResourceBuilder<T> CreateResourceBuilder<T>(T resource)
        where T : IResource => _innerBuilder.CreateResourceBuilder(resource);

    /// <inheritdoc />
    public DistributedApplication Build()
    {
        if (_application is not null)
        {
            throw new InvalidOperationException("The distributed application has already been built.");
        }

        return _application = _innerBuilder.Build();
    }

    /// <inheritdoc />
    public Task<DistributedApplication> BuildAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Build());
    }

    /// <inheritdoc />
    public void Dispose()
    {
        EnsureApplicationBuiltForDisposal();
        _application?.Dispose();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        EnsureApplicationBuiltForDisposal();
        if (_application is not null)
        {
            await _application.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal static string GetEndpointVariableName(string resourceName) =>
        EndpointPrefix + EncodeName(resourceName);

    internal static string GetEndpointHealthPathVariableName(string resourceName) =>
        EndpointHealthPathPrefix + EncodeName(resourceName);

    internal static string GetValueVariableName(string configurationKey) =>
        ValuePrefix + EncodeName(configurationKey);

    private static string ResolveProjectDirectory(Assembly appHostAssembly)
    {
        var projectPath = appHostAssembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => string.Equals(
                attribute.Key,
                "AppHostProjectPath",
                StringComparison.OrdinalIgnoreCase))
            ?.Value;

        return string.IsNullOrWhiteSpace(projectPath)
            ? Path.GetDirectoryName(appHostAssembly.Location)!
            : Path.GetFullPath(projectPath);
    }

    private static Dictionary<string, string> LoadValues(string filePath)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(filePath))
        {
            var content = line.TrimStart('\uFEFF').TrimStart();
            if (content.Length == 0 || content[0] == '#')
            {
                continue;
            }

            var separator = content.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var key = content[..separator].Trim();
            values[key] = content[(separator + 1)..];
        }

        return values;
    }

    private static void ImportConfiguration(
        IDistributedApplicationBuilder builder,
        Dictionary<string, string> values)
    {
        var configuration = values
            .Where(pair => pair.Key.StartsWith(ValuePrefix, StringComparison.Ordinal))
            .ToDictionary(
                pair => DecodeName(pair.Key[ValuePrefix.Length..]),
                pair => (string?)pair.Value,
                StringComparer.OrdinalIgnoreCase);

        builder.Configuration.AddInMemoryCollection(configuration);
    }

    private static void ImportEndpoints(
        IDistributedApplicationBuilder builder,
        Dictionary<string, string> values)
    {
        foreach (var pair in values.Where(pair => pair.Key.StartsWith(EndpointPrefix, StringComparison.Ordinal)))
        {
            var encodedResourceName = pair.Key[EndpointPrefix.Length..];
            var resourceName = DecodeName(encodedResourceName);
            if (!Uri.TryCreate(pair.Value, UriKind.Absolute, out var endpoint)
                || !(string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    $"The exported test endpoint '{resourceName}' has invalid HTTP URI value '{pair.Value}'.");
            }

            var resource = new DeployedEndpointResource(resourceName);
            var annotation = new EndpointAnnotation(
                ProtocolType.Tcp,
                uriScheme: endpoint.Scheme,
                transport: endpoint.Scheme,
                name: endpoint.Scheme,
                port: endpoint.Port,
                targetPort: endpoint.Port,
                isExternal: true,
                isProxied: false)
            {
                TargetHost = endpoint.Host
            };
            annotation.AllocatedEndpoint = new AllocatedEndpoint(annotation, endpoint.Host, endpoint.Port);
            resource.Annotations.Add(annotation);

            var resourceBuilder = builder.AddResource(resource)
                .WithInitialState(new CustomResourceSnapshot
                {
                    ResourceType = "ExternalService",
                    State = KnownResourceStates.Running,
                    Properties = []
                })
                .ExcludeFromManifest()
                .WithUrl(endpoint.AbsoluteUri);

            if (values.TryGetValue(EndpointHealthPathPrefix + encodedResourceName, out var healthPath))
            {
                var healthCheckKey = $"{resourceName}_deployment_{healthPath}_check";
                var healthCheckUri = new Uri(endpoint, healthPath);
                builder.Services.AddHealthChecks().AddUrlGroup(
                    options => options.AddUri(healthCheckUri),
                    healthCheckKey);
                resourceBuilder.WithHealthCheck(healthCheckKey);
            }
        }
    }

    private static string EncodeName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Convert.ToHexString(Encoding.UTF8.GetBytes(name));
    }

    private static string DecodeName(string encodedName)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromHexString(encodedName));
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                $"The Aspire deployment test configuration contains invalid encoded name '{encodedName}'.",
                exception);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Disposal matches Aspire's testing builder and must not hide the original test failure.")]
    private void EnsureApplicationBuiltForDisposal()
    {
        if (_application is not null)
        {
            return;
        }

        try
        {
            Build();
        }
        catch
        {
            // Match Aspire's testing builder by suppressing build failures during disposal.
        }
    }

    private sealed class DeployedEndpointResource(string name) : Resource(name), IResourceWithEndpoints;
}
