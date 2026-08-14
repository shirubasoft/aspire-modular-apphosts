#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES003
#pragma warning disable ASPIREPIPELINES004

using System.Diagnostics;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

/// <summary>Represents a host tool that must be available before dependent resources can start.</summary>
public sealed class RequiredToolResource : Resource
{
    /// <summary>Initializes a required host tool resource.</summary>
    /// <param name="name">The Aspire resource name.</param>
    /// <param name="command">The executable name or path that must be available.</param>
    public RequiredToolResource(string name, string command)
        : base(name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        Command = command;
    }

    /// <summary>Gets the executable name or path that must be available.</summary>
    public string Command { get; }
}

/// <summary>Controls the command used to install a required host tool.</summary>
public sealed class RequiredToolInstallOptions
{
    /// <summary>Initializes a required tool installation command.</summary>
    /// <param name="command">The installer executable name or path.</param>
    /// <param name="arguments">The arguments passed to the installer.</param>
    public RequiredToolInstallOptions(string command, params string[] arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Any(argument => argument is null))
        {
            throw new ArgumentException("Installer arguments cannot contain null values.", nameof(arguments));
        }

        Command = command;
        Arguments = [.. arguments];
    }

    /// <summary>Gets the installer executable name or path.</summary>
    public string Command { get; }

    /// <summary>Gets the arguments passed to the installer.</summary>
    public IReadOnlyList<string> Arguments { get; }

    /// <summary>
    /// Gets or sets the installer working directory. Relative paths are resolved from the AppHost directory.
    /// </summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>Gets or sets the maximum time allowed for installation.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(10);
}

/// <summary>Extensions for declaring host tools required by Aspire resources.</summary>
public static class RequiredToolResourceExtensions
{
    private const string WebsiteCommandName = "website";
    private const string InstallCommandName = "install";
    private const string ResourceType = "Required Tool";

    /// <summary>
    /// Adds a host tool resource whose health reflects whether <paramref name="command"/> is available.
    /// </summary>
    /// <remarks>
    /// The resource is local-only and excluded from deployment manifests. Resources that call <c>WaitFor(tool)</c>
    /// remain blocked until the command becomes available on the AppHost machine.
    /// </remarks>
    public static IResourceBuilder<RequiredToolResource> AddRequiredTool(
        this IDistributedApplicationBuilder builder,
        string name,
        string command)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        var resource = new RequiredToolResource(name, command);
        var healthCheckKey = RequiredToolHealthCheck.GetKey(resource);
        builder.Services.AddHealthChecks().Add(new HealthCheckRegistration(
            healthCheckKey,
            _ => new RequiredToolHealthCheck(resource),
            failureStatus: HealthStatus.Unhealthy,
            tags: null));
        var resourceBuilder = builder.AddResource(resource)
            .WithIconName("Toolbox")
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = ResourceType,
                State = KnownResourceStates.Waiting,
                Properties =
                [
                    new ResourcePropertySnapshot("tool.command", resource.Command)
                ]
            })
            .WithHealthCheck(healthCheckKey)
            .ExcludeFromManifest();

        resourceBuilder.WithPipelineStepFactory(context =>
        {
            var installer = context.Resource.Annotations
                .OfType<RequiredToolInstallerAnnotation>()
                .LastOrDefault();
            return installer is null
                ? []
                : [RequiredToolInstallationPipeline.CreateStep(resource, installer)];
        });
        builder.Eventing.Subscribe<InitializeResourceEvent>(resource, async (@event, cancellationToken) =>
        {
            await @event.Eventing.PublishAsync(
                new BeforeResourceStartedEvent(resource, @event.Services),
                cancellationToken).ConfigureAwait(false);
            await @event.Notifications.PublishUpdateAsync(
                resource,
                snapshot => snapshot with
                {
                    StartTimeStamp = DateTime.UtcNow,
                    State = KnownResourceStates.Running
                }).ConfigureAwait(false);
        });

        return resourceBuilder;
    }

    /// <summary>Adds a dashboard link and command that open the tool's website.</summary>
    public static IResourceBuilder<RequiredToolResource> WithWebsite(
        this IResourceBuilder<RequiredToolResource> builder,
        string website)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(website);
        if (!Uri.TryCreate(website, UriKind.Absolute, out var uri) ||
            !IsHttpWebsite(uri))
        {
            throw new ArgumentException(
                "The required tool website must be an absolute HTTP or HTTPS URL.",
                nameof(website));
        }

        return builder.WithWebsite(uri);
    }

    /// <summary>Adds a dashboard link and command that open the tool's website.</summary>
    public static IResourceBuilder<RequiredToolResource> WithWebsite(
        this IResourceBuilder<RequiredToolResource> builder,
        Uri website)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(website);
        if (!website.IsAbsoluteUri || !IsHttpWebsite(website))
        {
            throw new ArgumentException(
                "The required tool website must be an absolute HTTP or HTTPS URL.",
                nameof(website));
        }

        builder.Resource.Annotations.Add(new RequiredToolWebsiteAnnotation(website));
        return builder
            .WithUrl(website.AbsoluteUri, "Website")
            .WithCommand(
                WebsiteCommandName,
                "Open website",
                _ => RequiredToolOperations.OpenWebsiteAsync(website),
                new CommandOptions
                {
                    Description = $"Opens installation guidance for {builder.Resource.Name}.",
                    IconName = "Open"
                });
    }

    /// <summary>Adds the command used by the dashboard and <c>aspire do initialize</c> to install the tool.</summary>
    public static IResourceBuilder<RequiredToolResource> WithInstallCommand(
        this IResourceBuilder<RequiredToolResource> builder,
        string command,
        params string[] arguments) =>
        builder.WithInstallCommand(new RequiredToolInstallOptions(command, arguments));

    /// <summary>Adds the command used by the dashboard and <c>aspire do initialize</c> to install the tool.</summary>
    public static IResourceBuilder<RequiredToolResource> WithInstallCommand(
        this IResourceBuilder<RequiredToolResource> builder,
        RequiredToolInstallOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.Timeout,
                "The required tool installation timeout must be positive.");
        }

        var workingDirectory = ResolveWorkingDirectory(builder.ApplicationBuilder, options.WorkingDirectory);
        var installer = new RequiredToolInstallerAnnotation(
            options.Command,
            options.Arguments,
            workingDirectory,
            options.Timeout);
        var previousInstaller = builder.Resource.Annotations
            .OfType<RequiredToolInstallerAnnotation>()
            .LastOrDefault();
        if (previousInstaller is not null)
        {
            builder.Resource.Annotations.Remove(previousInstaller);
        }

        builder.Resource.Annotations.Add(installer);
        RequiredToolInstallationPipeline.Configure(builder.ApplicationBuilder);
        return builder.WithCommand(
            InstallCommandName,
            "Install",
            async context =>
            {
                try
                {
                    var resolvedPath = await RequiredToolOperations.EnsureInstalledAsync(
                        builder.Resource,
                        installer,
                        context.Logger,
                        context.CancellationToken).ConfigureAwait(false);
                    return CommandResults.Success(
                        $"{builder.Resource.Name} is available at {resolvedPath}.");
                }
                catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
                {
                    return CommandResults.Canceled();
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException
                        or TimeoutException
                        or IOException
                        or System.ComponentModel.Win32Exception)
                {
                    return CommandResults.Failure(exception);
                }
            },
            new CommandOptions
            {
                Description = $"Installs {builder.Resource.Name} on the AppHost machine.",
                IconName = "ArrowDownload",
                IsHighlighted = true,
                UpdateState = context => context.ResourceSnapshot.HealthStatus is HealthStatus.Healthy
                    ? ResourceCommandState.Disabled
                    : ResourceCommandState.Enabled
            });
    }

    private static string ResolveWorkingDirectory(
        IDistributedApplicationBuilder builder,
        string? configuredWorkingDirectory)
    {
        if (string.IsNullOrWhiteSpace(configuredWorkingDirectory))
        {
            return builder.AppHostDirectory;
        }

        return Path.GetFullPath(configuredWorkingDirectory, builder.AppHostDirectory);
    }

    private static bool IsHttpWebsite(Uri website) =>
        string.Equals(website.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(website.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
}

internal sealed record RequiredToolWebsiteAnnotation(Uri Website) : IResourceAnnotation;

internal sealed record RequiredToolInstallerAnnotation(
    string Command,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    TimeSpan Timeout) : IResourceAnnotation;

internal sealed class RequiredToolHealthCheck(RequiredToolResource resource) : IHealthCheck
{
    internal static string GetKey(RequiredToolResource tool) => $"required-tool-{tool.Name}";

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolvedPath = RequiredToolPathResolver.Resolve(resource.Command);
        return Task.FromResult(resolvedPath is null
            ? HealthCheckResult.Unhealthy(
                $"Required tool command '{resource.Command}' was not found on PATH.")
            : HealthCheckResult.Healthy(
                $"Required tool command '{resource.Command}' resolved to '{resolvedPath}'."));
    }
}

internal static class RequiredToolPathResolver
{
    public static string? Resolve(string command) =>
        Resolve(command, Environment.GetEnvironmentVariable("PATH"));

    internal static string? Resolve(string command, string? searchPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        if (command.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            var path = Path.GetFullPath(command);
            return File.Exists(path) ? path : null;
        }

        if (string.IsNullOrWhiteSpace(searchPath))
        {
            return null;
        }

        var extensions = GetExecutableExtensions(command);
        foreach (var directory in searchPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var normalizedDirectory = directory.Trim().Trim('"');
            if (normalizedDirectory.Length == 0)
            {
                continue;
            }

            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(normalizedDirectory, command + extension);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        return null;
    }

    private static string[] GetExecutableExtensions(string command)
    {
        if (!OperatingSystem.IsWindows() || Path.HasExtension(command))
        {
            return [string.Empty];
        }

        var pathExtensions = Environment.GetEnvironmentVariable("PATHEXT");
        return string.IsNullOrWhiteSpace(pathExtensions)
            ? [string.Empty, ".exe", ".cmd", ".bat", ".com"]
            : pathExtensions.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(extension => extension.StartsWith('.') ? extension : $".{extension}")
                .Prepend(string.Empty)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
    }
}

internal static class RequiredToolOperations
{
    private static readonly Action<ILogger, string, Exception?> LogInstallerOutput =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(1, nameof(LogInstallerOutput)),
            "{Output}");

    public static Task<ExecuteCommandResult> OpenWebsiteAsync(Uri website)
    {
        ArgumentNullException.ThrowIfNull(website);
        try
        {
            Process.Start(new ProcessStartInfo(website.AbsoluteUri)
            {
                UseShellExecute = true
            });
            return Task.FromResult(CommandResults.Success($"Opened {website}."));
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return Task.FromResult(CommandResults.Failure(
                $"Could not open {website}. Open the URL manually. {exception.Message}"));
        }
    }

    public static async Task<string> EnsureInstalledAsync(
        RequiredToolResource resource,
        RequiredToolInstallerAnnotation installer,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(installer);
        ArgumentNullException.ThrowIfNull(logger);
        cancellationToken.ThrowIfCancellationRequested();

        var resolvedPath = RequiredToolPathResolver.Resolve(resource.Command);
        if (resolvedPath is not null)
        {
            return resolvedPath;
        }

        var result = await ModuleCliRunner.RunAsync(
            installer.Command,
            installer.Arguments,
            installer.WorkingDirectory,
            installer.Timeout,
            $"Install required tool {resource.Name}",
            cancellationToken,
            line => LogInstallerOutput(logger, line, null)).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(
                $"Installation command for required tool '{resource.Name}' exited with code {result.ExitCode}. " +
                GetCommandError(result));
        }

        resolvedPath = RequiredToolPathResolver.Resolve(resource.Command);
        if (resolvedPath is null)
        {
            throw new InvalidOperationException(
                $"Installation command for required tool '{resource.Name}' succeeded, but command " +
                $"'{resource.Command}' is still unavailable on PATH.");
        }

        return resolvedPath;
    }

    private static string GetCommandError(ModuleCliResult result)
    {
        var error = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput
            : result.StandardError;
        return string.IsNullOrWhiteSpace(error)
            ? "The installer produced no diagnostic output."
            : error.Trim();
    }
}

internal static class RequiredToolInstallationPipeline
{
    internal const string StepTag = "install-required-tool";

    private sealed class PipelineRegistration;

    public static void Configure(IDistributedApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ModuleRepositoryInitializationPipeline.Configure(builder);
        if (builder.Services.Any(descriptor =>
                descriptor.ServiceType == typeof(PipelineRegistration)))
        {
            return;
        }

        builder.Services.AddSingleton<PipelineRegistration>();
        builder.Pipeline.AddPipelineConfiguration(context =>
        {
            ConfigureInitializationDependencies(context.Steps);
            return Task.CompletedTask;
        });
    }

    public static PipelineStep CreateStep(
        RequiredToolResource resource,
        RequiredToolInstallerAnnotation installer)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(installer);
        return new PipelineStep
        {
            Name = GetStepName(resource),
            Description = $"Installs required host tool {resource.Name} when it is unavailable.",
            Action = async context =>
            {
                var task = await context.ReportingStep.CreateTaskAsync(
                    $"Install {resource.Name}",
                    context.CancellationToken).ConfigureAwait(false);
                await using var configuredTask = task.ConfigureAwait(false);
                try
                {
                    var resourceLogger = context.Services
                        .GetRequiredService<ResourceLoggerService>()
                        .GetLogger(resource);
                    var resolvedPath = await RequiredToolOperations.EnsureInstalledAsync(
                        resource,
                        installer,
                        resourceLogger,
                        context.CancellationToken).ConfigureAwait(false);
                    await task.SucceedAsync(
                        $"Available at {resolvedPath}",
                        context.CancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    await task.FailAsync(exception.Message, CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
            },
            RequiredBySteps = [ModuleRepositoryInitializationPipeline.StepName],
            Tags = [StepTag],
            Resource = resource
        };
    }

    internal static void ConfigureInitializationDependencies(IReadOnlyList<PipelineStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var toolInstallSteps = steps
            .Where(step => step.Tags.Contains(StepTag, StringComparer.Ordinal))
            .ToArray();
        if (toolInstallSteps.Length == 0)
        {
            return;
        }

        var otherInitializationSteps = steps.Where(step =>
            !step.Tags.Contains(StepTag, StringComparer.Ordinal) &&
            step.RequiredBySteps.Contains(
                ModuleRepositoryInitializationPipeline.StepName,
                StringComparer.Ordinal));
        foreach (var initializationStep in otherInitializationSteps)
        {
            foreach (var toolInstallStep in toolInstallSteps)
            {
                if (!initializationStep.DependsOnSteps.Contains(
                        toolInstallStep.Name,
                        StringComparer.Ordinal))
                {
                    initializationStep.DependsOnSteps.Add(toolInstallStep.Name);
                }
            }
        }
    }

    internal static string GetStepName(RequiredToolResource resource) => $"install-{resource.Name}";
}
