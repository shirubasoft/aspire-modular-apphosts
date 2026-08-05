using CliWrap;
using CliCommand = global::CliWrap.Cli;

namespace Aspire.Hosting.ModularAppHosts;

/// <summary>Resolves the container runtime used by Aspire module image commands.</summary>
public static class ContainerRuntimeResolver
{
    private const string Docker = "docker";
    private const string Podman = "podman";
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(10);
    private static readonly RuntimeDefinition[] KnownRuntimes =
    [
        new(Docker, IsDefault: true),
        new(Podman, IsDefault: false)
    ];

    /// <summary>
    /// Resolves Docker or Podman from Aspire's container-runtime environment variables and local availability.
    /// </summary>
    /// <remarks>
    /// <c>ASPIRE_CONTAINER_RUNTIME</c> takes precedence over the legacy
    /// <c>DOTNET_ASPIRE_CONTAINER_RUNTIME</c> variable. Without an explicit value, Docker and Podman are
    /// probed in parallel. A running runtime is preferred over one that is merely installed, and Docker is
    /// used as the tie-breaker and final fallback.
    /// </remarks>
    /// <param name="cancellationToken">A token that cancels container-runtime probes.</param>
    /// <returns>The <c>docker</c> or <c>podman</c> executable name.</returns>
    public static Task<string> ResolveAsync(CancellationToken cancellationToken = default)
    {
        var configuredRuntime = Environment.GetEnvironmentVariable("ASPIRE_CONTAINER_RUNTIME") ??
            Environment.GetEnvironmentVariable("DOTNET_ASPIRE_CONTAINER_RUNTIME");
        return ResolveAsync(configuredRuntime, RunAsync, cancellationToken);
    }

    internal static async Task<string> ResolveAsync(
        string? configuredRuntime,
        Func<string, IReadOnlyList<string>, CancellationToken, Task<int?>> runCommandAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runCommandAsync);

        if (!string.IsNullOrWhiteSpace(configuredRuntime))
        {
            return NormalizeConfiguredRuntime(configuredRuntime);
        }

        var checks = KnownRuntimes
            .Select(runtime => CheckRuntimeAsync(runtime, runCommandAsync, cancellationToken))
            .ToArray();
        var runtimes = await Task.WhenAll(checks).ConfigureAwait(false);

        return FindBestRuntime(runtimes).Executable;
    }

    private static string NormalizeConfiguredRuntime(string configuredRuntime)
    {
        var runtime = configuredRuntime.Trim();
        if (string.Equals(runtime, Docker, StringComparison.OrdinalIgnoreCase))
        {
            return Docker;
        }

        if (string.Equals(runtime, Podman, StringComparison.OrdinalIgnoreCase))
        {
            return Podman;
        }

        throw new InvalidOperationException(
            "ASPIRE_CONTAINER_RUNTIME must be either 'docker' or 'podman'.");
    }

    private static async Task<RuntimeAvailability> CheckRuntimeAsync(
        RuntimeDefinition runtime,
        Func<string, IReadOnlyList<string>, CancellationToken, Task<int?>> runCommandAsync,
        CancellationToken cancellationToken)
    {
        var statusExitCode = await runCommandAsync(
                runtime.Executable,
                ["container", "ls", "-n", "1"],
                cancellationToken)
            .ConfigureAwait(false);
        if (statusExitCode == 0)
        {
            return new RuntimeAvailability(runtime.Executable, IsInstalled: true, IsRunning: true, runtime.IsDefault);
        }

        var versionExitCode = await runCommandAsync(runtime.Executable, ["--version"], cancellationToken)
            .ConfigureAwait(false);
        return new RuntimeAvailability(
            runtime.Executable,
            IsInstalled: versionExitCode == 0,
            IsRunning: false,
            runtime.IsDefault);
    }

    private static RuntimeAvailability FindBestRuntime(IEnumerable<RuntimeAvailability> runtimes)
    {
        RuntimeAvailability? best = null;
        foreach (var candidate in runtimes)
        {
            if (best is null)
            {
                best = candidate;
                continue;
            }

            if (!best.IsInstalled && candidate.IsInstalled)
            {
                best = candidate;
            }
            else if (!best.IsRunning && candidate.IsRunning)
            {
                best = candidate;
            }
            else if (candidate.IsDefault &&
                candidate.IsInstalled == best.IsInstalled &&
                candidate.IsRunning == best.IsRunning)
            {
                best = candidate;
            }
        }

        return best!;
    }

    private static async Task<int?> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = new CancellationTokenSource(CommandTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeout.Token);
            var result = await CliCommand.Wrap(executable)
                .WithArguments(arguments)
                .WithValidation(CommandResultValidation.None)
                .WithStandardOutputPipe(PipeTarget.ToStream(Stream.Null))
                .WithStandardErrorPipe(PipeTarget.ToStream(Stream.Null))
                .ExecuteAsync(linked.Token)
                .ConfigureAwait(false);
            return result.ExitCode;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or System.ComponentModel.Win32Exception
                or IOException
                || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return null;
        }
    }

    private sealed record RuntimeDefinition(string Executable, bool IsDefault);

    private sealed record RuntimeAvailability(
        string Executable,
        bool IsInstalled,
        bool IsRunning,
        bool IsDefault);
}
