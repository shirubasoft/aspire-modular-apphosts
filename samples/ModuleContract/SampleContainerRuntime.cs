using CliWrap;
using CliCommand = global::CliWrap.Cli;

namespace ModularSample.ModuleContract;

internal static class SampleContainerRuntime
{
    private const string Docker = "docker";
    private const string Podman = "podman";
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(10);
    private static readonly RuntimeDefinition[] KnownRuntimes =
    [
        new(Docker, IsDefault: true),
        new(Podman, IsDefault: false)
    ];

    public static string Resolve()
    {
        var configuredRuntime = Environment.GetEnvironmentVariable("ASPIRE_CONTAINER_RUNTIME") ??
            Environment.GetEnvironmentVariable("DOTNET_ASPIRE_CONTAINER_RUNTIME");
        return ResolveAsync(configuredRuntime, RunAsync).GetAwaiter().GetResult();
    }

    internal static async Task<string> ResolveAsync(
        string? configuredRuntime,
        Func<string, IReadOnlyList<string>, Task<int?>> run)
    {
        ArgumentNullException.ThrowIfNull(run);

        if (!string.IsNullOrWhiteSpace(configuredRuntime))
        {
            return NormalizeConfiguredRuntime(configuredRuntime);
        }

        var checks = KnownRuntimes
            .Select(runtime => CheckRuntimeAsync(runtime, run))
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
            "ASPIRE_CONTAINER_RUNTIME must be either 'docker' or 'podman' for the modular AppHost sample.");
    }

    private static async Task<RuntimeAvailability> CheckRuntimeAsync(
        RuntimeDefinition runtime,
        Func<string, IReadOnlyList<string>, Task<int?>> run)
    {
        var statusExitCode = await run(runtime.Executable, ["container", "ls", "-n", "1"])
            .ConfigureAwait(false);
        if (statusExitCode == 0)
        {
            return new RuntimeAvailability(runtime.Executable, IsInstalled: true, IsRunning: true, runtime.IsDefault);
        }

        var versionExitCode = await run(runtime.Executable, ["--version"]).ConfigureAwait(false);
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

    private static async Task<int?> RunAsync(string executable, IReadOnlyList<string> arguments)
    {
        try
        {
            using var timeout = new CancellationTokenSource(CommandTimeout);
            var result = await CliCommand.Wrap(executable)
                .WithArguments(arguments)
                .WithValidation(CommandResultValidation.None)
                .WithStandardOutputPipe(PipeTarget.ToStream(Stream.Null))
                .WithStandardErrorPipe(PipeTarget.ToStream(Stream.Null))
                .ExecuteAsync(timeout.Token)
                .ConfigureAwait(false);
            return result.ExitCode;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or System.ComponentModel.Win32Exception
                or IOException
                or OperationCanceledException)
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
