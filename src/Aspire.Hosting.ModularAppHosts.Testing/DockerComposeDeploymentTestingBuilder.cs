#pragma warning disable ASPIREFILESYSTEM001
#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREUSERSECRETS001

using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Testing;
using CliWrap;
using HealthChecks.Uris;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using CliCommand = global::CliWrap.Cli;

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

    /// <summary>The prefix used for generated Aspire test deployment environment names.</summary>
    public const string DefaultDeploymentEnvironmentName = "Tests";

    private const string EndpointPrefix = "ASPIRE_TEST_ENDPOINT__";
    private const string EndpointHealthPathPrefix = "ASPIRE_TEST_ENDPOINT_HEALTH_PATH__";
    private const string ValuePrefix = "ASPIRE_TEST_VALUE__";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly IDistributedApplicationBuilder _innerBuilder;
    private readonly OwnedDeployment? _ownedDeployment;
    private readonly object _lifecycleLock = new();

    [SuppressMessage(
        "Usage",
        "CA2213:Disposable fields should be disposed",
        Justification = "The shared disposal task disposes the application asynchronously for both disposal APIs.")]
    private DistributedApplication? _application;
    private Task? _disposeTask;

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
    /// The deployment environment defaults to a unique name prefixed by <see cref="DefaultDeploymentEnvironmentName"/> and
    /// can be overridden with <see cref="DeploymentEnvironmentVariableName"/>. The output path defaults to a temporary
    /// directory and can be overridden with <see cref="DeploymentOutputPathEnvironmentVariableName"/>. Asynchronously
    /// disposing the builder destroys the deployment.
    /// </remarks>
    public static Task<DockerComposeDeploymentTestingBuilder> DeployAsync<TEntryPoint>(
        CancellationToken cancellationToken = default)
        where TEntryPoint : class
    {
        var environmentName = System.Environment.GetEnvironmentVariable(DeploymentEnvironmentVariableName);
        var outputPath = System.Environment.GetEnvironmentVariable(DeploymentOutputPathEnvironmentVariableName);
        var options = new DockerComposeDeploymentOptions { OutputPath = outputPath };
        if (!string.IsNullOrWhiteSpace(environmentName))
        {
            options.EnvironmentName = environmentName;
        }

        return DeployAsync<TEntryPoint>(options, cancellationToken);
    }

    /// <summary>
    /// Deploys the AppHost to Docker Compose with explicit deployment options and creates a testing builder that owns it.
    /// </summary>
    /// <typeparam name="TEntryPoint">A type in the AppHost assembly to deploy.</typeparam>
    /// <param name="options">The deployment options.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <remarks>Asynchronously disposing the returned builder runs <c>aspire destroy</c>.</remarks>
    public static Task<DockerComposeDeploymentTestingBuilder> DeployAsync<TEntryPoint>(
        DockerComposeDeploymentOptions options,
        CancellationToken cancellationToken = default)
        where TEntryPoint : class
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);
        var appHostPath = ResolveAppHostPath(typeof(TEntryPoint).Assembly);
        return DeployCoreAsync<TEntryPoint>(
            Snapshot(options),
            appHostPath,
            CliWrapAspireCommandRunner.Instance,
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
    /// <remarks>Asynchronously disposing the returned builder runs <c>aspire destroy</c>.</remarks>
    public static Task<DockerComposeDeploymentTestingBuilder> DeployAsync<TEntryPoint>(
        string environmentName,
        string? outputPath = null,
        CancellationToken cancellationToken = default)
        where TEntryPoint : class
    {
        return DeployAsync<TEntryPoint>(new DockerComposeDeploymentOptions
        {
            EnvironmentName = environmentName,
            OutputPath = outputPath
        }, cancellationToken);
    }

    internal static Task<DockerComposeDeploymentTestingBuilder> DeployAsync<TEntryPoint>(
        DockerComposeDeploymentOptions options,
        string appHostPath,
        IAspireCommandRunner commandRunner,
        CancellationToken cancellationToken = default)
        where TEntryPoint : class
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(appHostPath);
        ArgumentNullException.ThrowIfNull(commandRunner);
        return DeployCoreAsync<TEntryPoint>(
            Snapshot(options),
            Path.GetFullPath(appHostPath),
            commandRunner,
            cancellationToken);
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "All deployment failures require best-effort cleanup before the original exception is rethrown.")]
    private static async Task<DockerComposeDeploymentTestingBuilder> DeployCoreAsync<TEntryPoint>(
        DockerComposeDeploymentOptions options,
        string appHostPath,
        IAspireCommandRunner commandRunner,
        CancellationToken cancellationToken)
        where TEntryPoint : class
    {
        var deleteOutputDirectory = string.IsNullOrWhiteSpace(options.OutputPath);
        var absoluteOutputPath = deleteOutputDirectory
            ? CreateTemporaryOutputPath(appHostPath)
            : Path.GetFullPath(options.OutputPath!);
        Directory.CreateDirectory(absoluteOutputPath);

        var deployment = new OwnedDeployment(
            appHostPath,
            absoluteOutputPath,
            options,
            deleteOutputDirectory,
            commandRunner);

        Exception? deploymentFailure = null;
        for (var attempt = 0; attempt <= options.PortConflictRetryCount; attempt++)
        {
            try
            {
                await RunAspireCommandAsync("deploy", deployment, cancellationToken).ConfigureAwait(false);
                var configurationFilePath = Path.Combine(absoluteOutputPath, $".env.{options.EnvironmentName}");
                return Create<TEntryPoint>(configurationFilePath, deployment);
            }
            catch (Exception exception) when (
                attempt < options.PortConflictRetryCount && IsPortConflict(exception))
            {
                Console.WriteLine(
                    $"[aspire deploy] Host-port conflict detected; cleaning the partial deployment before retry " +
                    $"{attempt + 1} of {options.PortConflictRetryCount}.");
                var retryCleanupFailure = await DestroyFailedDeploymentAsync(deployment).ConfigureAwait(false);
                if (retryCleanupFailure is not null)
                {
                    throw new AggregateException(
                        $"The Compose deployment hit a host-port conflict and cleanup before retry also failed. " +
                        $"Deployment state was retained at '{deployment.OutputPath}' for recovery.",
                        exception,
                        retryCleanupFailure);
                }
            }
            catch (Exception exception)
            {
                deploymentFailure = exception;
                break;
            }
        }

        deploymentFailure ??= new InvalidOperationException("The Compose deployment failed without an exception.");
        var cleanupFailure = await CleanupFailedDeploymentAsync(deployment).ConfigureAwait(false);
        if (cleanupFailure is not null)
        {
            throw new AggregateException(
                $"The Compose deployment failed and cleanup also failed. Deployment state was retained at " +
                $"'{deployment.OutputPath}' for recovery.",
                deploymentFailure,
                cleanupFailure);
        }

        ExceptionDispatchInfo.Capture(deploymentFailure).Throw();
        throw new InvalidOperationException("Unreachable code.");
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

        var values = DotEnvFile.Load(absolutePath);
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
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            if (_application is not null)
            {
                throw new InvalidOperationException("The distributed application has already been built.");
            }

            return _application = _innerBuilder.Build();
        }
    }

    /// <inheritdoc />
    public Task<DistributedApplication> BuildAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Build());
    }

    /// <inheritdoc />
    [SuppressMessage(
        "Design",
        "CA1065:Do not raise exceptions in unexpected locations",
        Justification = "Synchronous disposal cannot preserve the builder's required asynchronous deployment cleanup.")]
    [SuppressMessage(
        "Design",
        "CA1063:Implement IDisposable correctly",
        Justification = "The synchronous interface member is explicit so callers use the public asynchronous disposal contract.")]
    void IDisposable.Dispose() => throw new InvalidOperationException(
        $"{nameof(DockerComposeDeploymentTestingBuilder)} performs asynchronous cleanup. Use await using or await DisposeAsync().");

    /// <inheritdoc />
    public ValueTask DisposeAsync() => new(GetOrCreateDisposalTask());

    private Task GetOrCreateDisposalTask()
    {
        TaskCompletionSource? completion = null;
        lock (_lifecycleLock)
        {
            if (_disposeTask is not null)
            {
                return _disposeTask;
            }

            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeTask = completion.Task;
        }

        _ = CompleteDisposalAsync(completion);
        return completion.Task;
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Every disposal failure must be transferred to all callers of the shared disposal task.")]
    private async Task CompleteDisposalAsync(TaskCompletionSource completion)
    {
        try
        {
            await DisposeCoreAsync().ConfigureAwait(false);
            completion.SetResult();
        }
        catch (Exception exception)
        {
            completion.SetException(exception);
        }
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "All cleanup paths must run before disposal propagates failures.")]
    private async Task DisposeCoreAsync()
    {
        Exception? failure = null;
        try
        {
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
            var deploymentDestroyed = false;
            try
            {
                await RunAspireCommandAsync("destroy", _ownedDeployment, CancellationToken.None).ConfigureAwait(false);
                deploymentDestroyed = true;
            }
            catch (Exception exception)
            {
                failure = failure is null
                    ? exception
                    : new AggregateException(failure, exception);
            }

            try
            {
                if (deploymentDestroyed)
                {
                    DeleteOwnedOutputDirectory(_ownedDeployment);
                }
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

    internal static string GetEndpointVariableName(string resourceName) => EndpointPrefix + EncodeName(resourceName);

    internal static string GetEndpointVariableName(string resourceName, string endpointName) =>
        EndpointPrefix + EncodeName(resourceName) + "__" + EncodeName(endpointName);

    internal static string GetEndpointHealthPathVariableName(string resourceName) =>
        EndpointHealthPathPrefix + EncodeName(resourceName);

    internal static string GetEndpointHealthPathVariableName(string resourceName, string endpointName) =>
        EndpointHealthPathPrefix + EncodeName(resourceName) + "__" + EncodeName(endpointName);

    internal static string GetValueVariableName(string configurationKey) =>
        ValuePrefix + EncodeName(configurationKey);

    internal static string CreateDefaultDeploymentEnvironmentName() =>
        $"{DefaultDeploymentEnvironmentName}-{System.Environment.ProcessId}-{Guid.NewGuid():N}";

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

    private static void ValidateOptions(DockerComposeDeploymentOptions options)
    {
        ValidateEnvironmentName(options.EnvironmentName);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.AspireCliPath);
        if (options.DeploymentTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.DeploymentTimeout,
                "The deployment timeout must be positive.");
        }

        if (options.CleanupTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.CleanupTimeout,
                "The cleanup timeout must be positive.");
        }

        if (options.PortConflictRetryCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.PortConflictRetryCount,
                "The port-conflict retry count cannot be negative.");
        }
    }

    private static DockerComposeDeploymentOptions Snapshot(DockerComposeDeploymentOptions options) => new()
    {
        EnvironmentName = options.EnvironmentName,
        OutputPath = options.OutputPath,
        AspireCliPath = options.AspireCliPath,
        PortConflictRetryCount = options.PortConflictRetryCount,
        DeploymentTimeout = options.DeploymentTimeout,
        CleanupTimeout = options.CleanupTimeout
    };

    private static async Task RunAspireCommandAsync(
        string command,
        OwnedDeployment deployment,
        CancellationToken cancellationToken)
    {
        var timeout = string.Equals(command, "deploy", StringComparison.Ordinal)
            ? deployment.Options.DeploymentTimeout
            : deployment.Options.CleanupTimeout;
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);
        try
        {
            await deployment.CommandRunner.RunAsync(
                deployment.Options.AspireCliPath,
                command,
                deployment.AppHostPath,
                deployment.OutputPath,
                deployment.Options.EnvironmentName,
                linkedSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"'aspire {command}' exceeded the configured timeout of {timeout}.",
                exception);
        }
    }

    private static async Task RunAspireCliAsync(
        string aspireCliPath,
        string command,
        string appHostPath,
        string outputPath,
        string environmentName,
        CancellationToken cancellationToken)
    {
        var invocation = ResolveAspireCliInvocation(aspireCliPath, appHostPath);
        var output = new StringBuilder();
        var outputLock = new object();
        void ReportOutput(string line)
        {
            lock (outputLock)
            {
                output.AppendLine(line);
                Console.WriteLine($"[aspire {command}] {line}");
            }
        }

        async Task<CommandResult> ExecuteAsync(AspireCliInvocation candidate)
        {
            var arguments = new List<string>(candidate.PrefixArguments)
            {
                command,
                "--apphost",
                appHostPath,
                "--output-path",
                outputPath,
                "--environment",
                environmentName
            };
            if (string.Equals(command, "destroy", StringComparison.Ordinal))
            {
                arguments.Add("--yes");
            }

            arguments.Add("--non-interactive");
            return await CliCommand.Wrap(candidate.Executable)
                .WithArguments(arguments)
                .WithWorkingDirectory(Path.GetDirectoryName(appHostPath)!)
                .WithValidation(CommandResultValidation.None)
                .WithStandardOutputPipe(PipeTarget.ToDelegate(ReportOutput))
                .WithStandardErrorPipe(PipeTarget.ToDelegate(ReportOutput))
                .ExecuteAsync(cancellationToken);
        }

        var result = await ExecuteAsync(invocation).ConfigureAwait(false);
        if (!result.IsSuccess && ShouldFallBackToAspireOnPath(invocation, output.ToString()))
        {
            ReportOutput("The manifest tool is not restored; falling back to 'aspire' on PATH.");
            result = await ExecuteAsync(new AspireCliInvocation(aspireCliPath, [])).ConfigureAwait(false);
        }

        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(
                CreateAspireCommandFailureMessage(command, result.ExitCode, appHostPath, output.ToString()));
        }
    }

    private static string CreateAspireCommandFailureMessage(
        string command,
        int exitCode,
        string appHostPath,
        string output)
    {
        var diagnostic = output.Trim();
        if (diagnostic.Length > 4000)
        {
            diagnostic = diagnostic[^4000..];
        }

        return $"'aspire {command}' exited with code {exitCode} for AppHost '{appHostPath}'." +
            (diagnostic.Length == 0 ? string.Empty : $"{System.Environment.NewLine}{diagnostic}");
    }

    internal static AspireCliInvocation ResolveAspireCliInvocation(string aspireCliPath, string appHostPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(aspireCliPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(appHostPath);
        if (!string.Equals(aspireCliPath, "aspire", StringComparison.Ordinal))
        {
            return new AspireCliInvocation(aspireCliPath, []);
        }

        var current = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(appHostPath))!);
        while (current is not null)
        {
            var manifestPath = Path.Combine(current.FullName, ".config", "dotnet-tools.json");
            if (File.Exists(manifestPath) && ManifestProvidesAspireCli(manifestPath))
            {
                return new AspireCliInvocation("dotnet", ["tool", "run", "aspire", "--"]);
            }

            current = current.Parent;
        }

        return new AspireCliInvocation(aspireCliPath, []);
    }

    internal static bool ShouldFallBackToAspireOnPath(AspireCliInvocation invocation, string output)
        => string.Equals(invocation.Executable, "dotnet", StringComparison.Ordinal)
            && invocation.PrefixArguments.SequenceEqual(["tool", "run", "aspire", "--"])
            && output.Contains("dotnet tool restore", StringComparison.OrdinalIgnoreCase);

    private static bool ManifestProvidesAspireCli(string manifestPath)
    {
        try
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!manifest.RootElement.TryGetProperty("tools", out var tools))
            {
                return false;
            }

            return tools.EnumerateObject().Any(tool =>
                tool.Value.TryGetProperty("commands", out var commands) &&
                commands.EnumerateArray().Any(command =>
                    string.Equals(command.GetString(), "aspire", StringComparison.Ordinal)));
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"Unable to read local .NET tool manifest '{manifestPath}'.",
                exception);
        }
    }

    private static bool IsPortConflict(Exception exception)
    {
        var message = exception.Message;
        if (message.Contains("address already in use", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("port is already allocated", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("port is already in use", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return exception.InnerException is not null && IsPortConflict(exception.InnerException);
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Cleanup failures are returned so they can be reported with the deployment failure.")]
    private static async Task<Exception?> CleanupFailedDeploymentAsync(OwnedDeployment deployment)
    {
        var destroyFailure = await DestroyFailedDeploymentAsync(deployment).ConfigureAwait(false);
        if (destroyFailure is not null)
        {
            return destroyFailure;
        }

        try
        {
            DeleteOwnedOutputDirectory(deployment);
        }
        catch (Exception exception)
        {
            return exception;
        }

        return null;
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Cleanup failures are returned so callers can preserve deployment diagnostics and recovery state.")]
    private static async Task<Exception?> DestroyFailedDeploymentAsync(OwnedDeployment deployment)
    {
        try
        {
            await RunAspireCommandAsync("destroy", deployment, CancellationToken.None).ConfigureAwait(false);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static void DeleteOwnedOutputDirectory(OwnedDeployment deployment)
    {
        if (deployment.DeleteOutputDirectory && Directory.Exists(deployment.OutputPath))
        {
            Directory.Delete(deployment.OutputPath, recursive: true);
        }
    }

    private static void ImportConfiguration(
        IDistributedApplicationBuilder builder,
        Dictionary<string, string> values)
    {
        var configuration = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in values.Where(pair => pair.Key.StartsWith(ValuePrefix, StringComparison.Ordinal)))
        {
            var configurationKey = DecodeName(pair.Key[ValuePrefix.Length..]);
            if (!configuration.TryAdd(configurationKey, pair.Value))
            {
                throw new InvalidOperationException(
                    $"The deployment test configuration exports configuration key '{configurationKey}' more than once.");
            }
        }

        builder.Configuration.AddInMemoryCollection(configuration);
    }

    private static void ImportEndpoints(
        IDistributedApplicationBuilder builder,
        Dictionary<string, string> values)
    {
        var resources = new Dictionary<string, (DeployedEndpointResource Resource, IResourceBuilder<DeployedEndpointResource> Builder)>(
            StringComparer.OrdinalIgnoreCase);
        var endpointVariableNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in values.Where(pair => pair.Key.StartsWith(EndpointPrefix, StringComparison.Ordinal)))
        {
            endpointVariableNames.Add(pair.Key);
            var encodedEndpoint = pair.Key[EndpointPrefix.Length..];
            var separator = encodedEndpoint.IndexOf("__", StringComparison.Ordinal);
            var encodedResourceName = separator < 0 ? encodedEndpoint : encodedEndpoint[..separator];
            var resourceName = DecodeName(encodedResourceName);
            var endpointName = separator < 0
                ? null
                : DecodeName(encodedEndpoint[(separator + 2)..]);
            if (!Uri.TryCreate(pair.Value, UriKind.Absolute, out var endpoint)
                || !(string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                || endpoint.AbsolutePath != "/"
                || endpoint.Query.Length > 0
                || endpoint.Fragment.Length > 0
                || endpoint.UserInfo.Length > 0)
            {
                throw new InvalidOperationException(
                    $"The exported test endpoint '{resourceName}' must be an absolute HTTP(S) origin without " +
                    $"credentials, path, query, or fragment; found '{pair.Value}'.");
            }

            endpointName ??= endpoint.Scheme;
            if (!resources.TryGetValue(resourceName, out var imported))
            {
                var resource = new DeployedEndpointResource(resourceName);
                var resourceBuilder = builder.AddResource(resource)
                    .WithInitialState(new CustomResourceSnapshot
                    {
                        ResourceType = "ExternalService",
                        State = KnownResourceStates.Running,
                        Properties = []
                    })
                    .ExcludeFromManifest();
                imported = (resource, resourceBuilder);
                resources.Add(resourceName, imported);
            }

            if (imported.Resource.Annotations.OfType<EndpointAnnotation>().Any(annotation =>
                string.Equals(annotation.Name, endpointName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    $"The exported test endpoint '{resourceName}/{endpointName}' is defined more than once.");
            }

            var annotation = new EndpointAnnotation(
                ProtocolType.Tcp,
                uriScheme: endpoint.Scheme,
                transport: endpoint.Scheme,
                name: endpointName,
                port: endpoint.Port,
                targetPort: endpoint.Port,
                isExternal: true,
                isProxied: false)
            {
                TargetHost = endpoint.Host
            };
            annotation.AllocatedEndpoint = new AllocatedEndpoint(annotation, endpoint.Host, endpoint.Port);
            imported.Resource.Annotations.Add(annotation);
            imported.Builder.WithUrl(endpoint.AbsoluteUri);

            var healthKey = EndpointHealthPathPrefix + encodedEndpoint;
            if (values.TryGetValue(healthKey, out var healthPath))
            {
                if (!TestEndpointHealthPath.IsRootRelative(healthPath))
                {
                    throw new InvalidOperationException(
                        $"The exported health check for '{resourceName}/{endpointName}' must be a root-relative URI path; " +
                        $"found '{healthPath}'.");
                }

                var healthCheckKey = $"{resourceName}_{endpointName}_deployment_check";
                var healthCheckUri = new Uri(endpoint, healthPath);
                builder.Services.AddHealthChecks().AddUrlGroup(
                    options => options.AddUri(healthCheckUri),
                    healthCheckKey);
                imported.Builder.WithHealthCheck(healthCheckKey);
            }
        }

        foreach (var healthPair in values.Where(pair =>
            pair.Key.StartsWith(EndpointHealthPathPrefix, StringComparison.Ordinal)))
        {
            var endpointVariableName = EndpointPrefix + healthPair.Key[EndpointHealthPathPrefix.Length..];
            if (!endpointVariableNames.Contains(endpointVariableName))
            {
                throw new InvalidOperationException(
                    $"The deployment test configuration exports health check '{healthPair.Key}' without its endpoint.");
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
            var decodedName = StrictUtf8.GetString(Convert.FromHexString(encodedName));
            if (string.IsNullOrWhiteSpace(decodedName))
            {
                throw new FormatException("The decoded name is empty.");
            }

            return decodedName;
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException)
        {
            throw new InvalidOperationException(
                $"The Aspire deployment test configuration contains invalid encoded name '{encodedName}'.",
                exception);
        }
    }

    private sealed class DeployedEndpointResource(string name) : Resource(name), IResourceWithEndpoints;

    private sealed class CliWrapAspireCommandRunner : IAspireCommandRunner
    {
        public static CliWrapAspireCommandRunner Instance { get; } = new();

        public Task RunAsync(
            string aspireCliPath,
            string command,
            string appHostPath,
            string outputPath,
            string environmentName,
            CancellationToken cancellationToken) =>
            RunAspireCliAsync(
                aspireCliPath,
                command,
                appHostPath,
                outputPath,
                environmentName,
                cancellationToken);
    }

    private sealed record OwnedDeployment(
        string AppHostPath,
        string OutputPath,
        DockerComposeDeploymentOptions Options,
        bool DeleteOutputDirectory,
        IAspireCommandRunner CommandRunner);
}

internal sealed record AspireCliInvocation(string Executable, IReadOnlyList<string> PrefixArguments);

internal interface IAspireCommandRunner
{
    Task RunAsync(
        string aspireCliPath,
        string command,
        string appHostPath,
        string outputPath,
        string environmentName,
        CancellationToken cancellationToken);
}
