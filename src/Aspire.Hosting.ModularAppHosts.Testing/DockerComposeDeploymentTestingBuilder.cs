#pragma warning disable ASPIREFILESYSTEM001
#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREUSERSECRETS001

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.ExceptionServices;
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
/// Deploys or imports a Docker Compose environment and represents its services as an Aspire testing application.
/// </summary>
public sealed class DockerComposeDeploymentTestingBuilder : IDistributedApplicationTestingBuilder
{
    /// <summary>The environment variable that identifies the deployment environment file to load.</summary>
    public const string FilePathEnvironmentVariableName = "ASPIRE_TEST_CONFIGURATION_FILE";

    /// <summary>The environment variable that selects the Aspire deployment environment used by <see cref="DeployAsync{TEntryPoint}(CancellationToken)"/>.</summary>
    public const string DeploymentEnvironmentVariableName = "ASPIRE_TEST_DEPLOYMENT_ENVIRONMENT";

    /// <summary>The environment variable that selects the Aspire deployment output path used by <see cref="DeployAsync{TEntryPoint}(CancellationToken)"/>.</summary>
    public const string DeploymentOutputPathEnvironmentVariableName = "ASPIRE_TEST_DEPLOYMENT_OUTPUT_PATH";

    /// <summary>The default Aspire deployment environment name.</summary>
    public const string DefaultDeploymentEnvironmentName = "Tests";

    private const string EndpointPrefix = "ASPIRE_TEST_ENDPOINT__";
    private const string EndpointHealthPathPrefix = "ASPIRE_TEST_ENDPOINT_HEALTH_PATH__";
    private const string ValuePrefix = "ASPIRE_TEST_VALUE__";
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromMinutes(2);
    private readonly IDistributedApplicationBuilder _innerBuilder;
    private readonly OwnedDeployment? _ownedDeployment;
    private DistributedApplication? _application;
    private int _disposeState;

    private DockerComposeDeploymentTestingBuilder(
        IDistributedApplicationBuilder innerBuilder,
        OwnedDeployment? ownedDeployment)
    {
        _innerBuilder = innerBuilder;
        _ownedDeployment = ownedDeployment;
    }

    /// <summary>
    /// Creates a testing builder from an Aspire-generated Docker Compose environment file.
    /// </summary>
    /// <typeparam name="TEntryPoint">A type in the AppHost assembly used by the deployment.</typeparam>
    /// <param name="filePath">The path to the environment-specific deployment file.</param>
    public static DockerComposeDeploymentTestingBuilder Create<TEntryPoint>(string filePath)
        where TEntryPoint : class
        => Create<TEntryPoint>(filePath, ownedDeployment: null);

    /// <summary>
    /// Deploys the AppHost to Docker Compose and creates a testing builder that owns the deployment.
    /// </summary>
    /// <typeparam name="TEntryPoint">A type in the AppHost assembly to deploy.</typeparam>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <remarks>
    /// The deployment environment defaults to <see cref="DefaultDeploymentEnvironmentName"/> and can be overridden with
    /// <see cref="DeploymentEnvironmentVariableName"/>. The output path defaults to a temporary directory and can be
    /// overridden with <see cref="DeploymentOutputPathEnvironmentVariableName"/>. Disposing the builder destroys the deployment.
    /// </remarks>
    public static Task<DockerComposeDeploymentTestingBuilder> DeployAsync<TEntryPoint>(
        CancellationToken cancellationToken = default)
        where TEntryPoint : class
    {
        var environmentName = System.Environment.GetEnvironmentVariable(DeploymentEnvironmentVariableName);
        var outputPath = System.Environment.GetEnvironmentVariable(DeploymentOutputPathEnvironmentVariableName);
        return DeployAsync<TEntryPoint>(
            environmentName ?? DefaultDeploymentEnvironmentName,
            outputPath,
            cancellationToken);
    }

    /// <summary>
    /// Deploys the AppHost to Docker Compose and creates a testing builder that owns the deployment.
    /// </summary>
    /// <typeparam name="TEntryPoint">A type in the AppHost assembly to deploy.</typeparam>
    /// <param name="environmentName">The Aspire deployment environment name.</param>
    /// <param name="outputPath">
    /// The deployment output path. When omitted, a temporary directory is created and removed with the deployment.
    /// </param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <remarks>Disposing the returned builder runs <c>aspire destroy</c>.</remarks>
    public static Task<DockerComposeDeploymentTestingBuilder> DeployAsync<TEntryPoint>(
        string environmentName,
        string? outputPath = null,
        CancellationToken cancellationToken = default)
        where TEntryPoint : class
    {
        ValidateEnvironmentName(environmentName);
        var appHostPath = ResolveAppHostPath(typeof(TEntryPoint).Assembly);
        return DeployCoreAsync<TEntryPoint>(
            environmentName,
            outputPath,
            appHostPath,
            ProcessAspireCommandRunner.Instance,
            cancellationToken);
    }

    internal static Task<DockerComposeDeploymentTestingBuilder> DeployAsync<TEntryPoint>(
        string environmentName,
        string? outputPath,
        string appHostPath,
        IAspireCommandRunner commandRunner,
        CancellationToken cancellationToken = default)
        where TEntryPoint : class
    {
        ValidateEnvironmentName(environmentName);
        ArgumentException.ThrowIfNullOrWhiteSpace(appHostPath);
        ArgumentNullException.ThrowIfNull(commandRunner);
        return DeployCoreAsync<TEntryPoint>(
            environmentName,
            outputPath,
            Path.GetFullPath(appHostPath),
            commandRunner,
            cancellationToken);
    }

    private static async Task<DockerComposeDeploymentTestingBuilder> DeployCoreAsync<TEntryPoint>(
        string environmentName,
        string? outputPath,
        string appHostPath,
        IAspireCommandRunner commandRunner,
        CancellationToken cancellationToken)
        where TEntryPoint : class
    {
        var deleteOutputDirectory = string.IsNullOrWhiteSpace(outputPath);
        var absoluteOutputPath = deleteOutputDirectory
            ? CreateTemporaryOutputPath(appHostPath)
            : Path.GetFullPath(outputPath!);
        Directory.CreateDirectory(absoluteOutputPath);

        var deployment = new OwnedDeployment(
            appHostPath,
            absoluteOutputPath,
            environmentName,
            deleteOutputDirectory,
            commandRunner);

        try
        {
            await RunAspireCommandAsync("deploy", deployment, cancellationToken).ConfigureAwait(false);
            var configurationFilePath = Path.Combine(absoluteOutputPath, $".env.{environmentName}");
            return Create<TEntryPoint>(configurationFilePath, deployment);
        }
        catch
        {
            await TryDestroyDeploymentAsync(deployment).ConfigureAwait(false);
            TryDeleteOwnedOutputDirectory(deployment);
            throw;
        }
    }

    private static DockerComposeDeploymentTestingBuilder Create<TEntryPoint>(
        string filePath,
        OwnedDeployment? ownedDeployment)
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

        return new DockerComposeDeploymentTestingBuilder(innerBuilder, ownedDeployment);
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
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "All cleanup paths must run before disposal propagates failures.")]
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        Exception? failure = null;
        try
        {
            EnsureApplicationBuiltForDisposal();
            if (_application is not null)
            {
                await _application.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        if (_ownedDeployment is not null)
        {
            try
            {
                using var timeout = new CancellationTokenSource(CleanupTimeout);
                await RunAspireCommandAsync("destroy", _ownedDeployment, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure = failure is null
                    ? exception
                    : new AggregateException(failure, exception);
            }

            try
            {
                DeleteOwnedOutputDirectory(_ownedDeployment);
            }
            catch (Exception exception)
            {
                failure = failure is null
                    ? exception
                    : new AggregateException(failure, exception);
            }
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
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

        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return Path.GetDirectoryName(appHostAssembly.Location)!;
        }

        var absolutePath = Path.GetFullPath(projectPath);
        return string.Equals(Path.GetExtension(absolutePath), ".csproj", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(absolutePath)!
            : absolutePath;
    }

    private static string ResolveAppHostPath(Assembly appHostAssembly)
    {
        var metadata = appHostAssembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToArray();
        var projectPath = metadata
            .FirstOrDefault(attribute => string.Equals(
                attribute.Key,
                "AppHostProjectPath",
                StringComparison.OrdinalIgnoreCase))
            ?.Value;
        var projectName = metadata
            .FirstOrDefault(attribute => string.Equals(
                attribute.Key,
                "AppHostProjectName",
                StringComparison.OrdinalIgnoreCase))
            ?.Value;

        if (string.IsNullOrWhiteSpace(projectPath))
        {
            throw new InvalidOperationException(
                $"Assembly '{appHostAssembly.GetName().Name}' does not identify an Aspire AppHost project path.");
        }

        var absoluteProjectPath = Path.GetFullPath(projectPath);
        if (string.Equals(Path.GetExtension(absoluteProjectPath), ".csproj", StringComparison.OrdinalIgnoreCase))
        {
            return absoluteProjectPath;
        }

        if (string.IsNullOrWhiteSpace(projectName))
        {
            throw new InvalidOperationException(
                $"Assembly '{appHostAssembly.GetName().Name}' does not identify an Aspire AppHost project name.");
        }

        var projectFileName = string.Equals(Path.GetExtension(projectName), ".csproj", StringComparison.OrdinalIgnoreCase)
            ? projectName
            : $"{projectName}.csproj";
        return Path.Combine(absoluteProjectPath, projectFileName);
    }

    private static string CreateTemporaryOutputPath(string appHostPath)
    {
        var appHostName = Path.GetFileNameWithoutExtension(appHostPath);
        return Path.Combine(
            Path.GetTempPath(),
            "aspire-compose-tests",
            appHostName,
            Guid.NewGuid().ToString("N"));
    }

    private static void ValidateEnvironmentName(string environmentName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);
        if (environmentName is "." or ".."
            || environmentName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException(
                $"The deployment environment name '{environmentName}' is not valid as an environment file suffix.",
                nameof(environmentName));
        }
    }

    private static Task RunAspireCommandAsync(
        string command,
        OwnedDeployment deployment,
        CancellationToken cancellationToken) =>
        deployment.CommandRunner.RunAsync(
            command,
            deployment.AppHostPath,
            deployment.OutputPath,
            deployment.EnvironmentName,
            cancellationToken);

    private static async Task RunAspireProcessAsync(
        string command,
        string appHostPath,
        string outputPath,
        string environmentName,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "aspire",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(appHostPath)!
            }
        };
        process.StartInfo.ArgumentList.Add(command);
        process.StartInfo.ArgumentList.Add("--apphost");
        process.StartInfo.ArgumentList.Add(appHostPath);
        process.StartInfo.ArgumentList.Add("--output-path");
        process.StartInfo.ArgumentList.Add(outputPath);
        process.StartInfo.ArgumentList.Add("--environment");
        process.StartInfo.ArgumentList.Add(environmentName);
        if (string.Equals(command, "destroy", StringComparison.Ordinal))
        {
            process.StartInfo.ArgumentList.Add("--yes");
        }

        process.StartInfo.ArgumentList.Add("--non-interactive");

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start 'aspire {command}'.");
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var standardError = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKillProcess(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            var output = await standardOutput.ConfigureAwait(false);
            var error = await standardError.ConfigureAwait(false);
            if (!string.IsNullOrEmpty(output))
            {
                await Console.Out.WriteAsync(output).ConfigureAwait(false);
            }

            if (!string.IsNullOrEmpty(error))
            {
                await Console.Error.WriteAsync(error).ConfigureAwait(false);
            }
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"'aspire {command}' exited with code {process.ExitCode} for AppHost '{appHostPath}'.");
        }
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Cancellation must preserve the original exception even if process termination races with exit.")]
    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Preserve the cancellation exception.
        }
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Best-effort cleanup must preserve the deployment failure.")]
    private static async Task TryDestroyDeploymentAsync(OwnedDeployment deployment)
    {
        try
        {
            using var timeout = new CancellationTokenSource(CleanupTimeout);
            await RunAspireCommandAsync("destroy", deployment, timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            // Preserve the deployment failure.
        }
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Best-effort cleanup must preserve the deployment failure.")]
    private static void TryDeleteOwnedOutputDirectory(OwnedDeployment deployment)
    {
        try
        {
            DeleteOwnedOutputDirectory(deployment);
        }
        catch
        {
            // Preserve the deployment failure.
        }
    }

    private static void DeleteOwnedOutputDirectory(OwnedDeployment deployment)
    {
        if (deployment.DeleteOutputDirectory && Directory.Exists(deployment.OutputPath))
        {
            Directory.Delete(deployment.OutputPath, recursive: true);
        }
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

    [SuppressMessage(
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

    private sealed class ProcessAspireCommandRunner : IAspireCommandRunner
    {
        public static ProcessAspireCommandRunner Instance { get; } = new();

        public Task RunAsync(
            string command,
            string appHostPath,
            string outputPath,
            string environmentName,
            CancellationToken cancellationToken) =>
            RunAspireProcessAsync(
                command,
                appHostPath,
                outputPath,
                environmentName,
                cancellationToken);
    }

    private sealed record OwnedDeployment(
        string AppHostPath,
        string OutputPath,
        string EnvironmentName,
        bool DeleteOutputDirectory,
        IAspireCommandRunner CommandRunner);
}

internal interface IAspireCommandRunner
{
    Task RunAsync(
        string command,
        string appHostPath,
        string outputPath,
        string environmentName,
        CancellationToken cancellationToken);
}
