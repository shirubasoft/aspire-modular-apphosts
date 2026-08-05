using ModularSample.ModuleContract;
using Xunit;

namespace Aspire.Hosting.ModularAppHosts.Tests;

public sealed class SampleContainerRuntimeTests
{
    [Theory]
    [InlineData("docker", "docker")]
    [InlineData(" DOCKER ", "docker")]
    [InlineData("podman", "podman")]
    [InlineData(" PODMAN ", "podman")]
    public async Task Explicit_runtime_bypasses_detection(string configuredRuntime, string expected)
    {
        var result = await SampleContainerRuntime.ResolveAsync(
            configuredRuntime,
            (_, _) => throw new InvalidOperationException("An explicit runtime must not be probed."));

        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task Unsupported_explicit_runtime_fails_before_detection()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SampleContainerRuntime.ResolveAsync(
                "containerd",
                (_, _) => throw new InvalidOperationException("An explicit runtime must not be probed.")));

        Assert.Contains("docker", exception.Message, StringComparison.Ordinal);
        Assert.Contains("podman", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Docker_is_the_tiebreaker_when_both_runtimes_are_running()
    {
        var result = await SampleContainerRuntime.ResolveAsync(
            configuredRuntime: null,
            (_, arguments) => Task.FromResult<int?>(IsStatusCommand(arguments) ? 0 : null));

        Assert.Equal("docker", result);
    }

    [Fact]
    public async Task Running_podman_is_preferred_over_installed_but_stopped_docker()
    {
        var result = await SampleContainerRuntime.ResolveAsync(
            configuredRuntime: null,
            (executable, arguments) => Task.FromResult<int?>(
                (executable, GetCommand(arguments)) switch
                {
                    ("docker", "container ls -n 1") => 1,
                    ("docker", "--version") => 0,
                    ("podman", "container ls -n 1") => 0,
                    _ => null
                }));

        Assert.Equal("podman", result);
    }

    [Fact]
    public async Task Installed_podman_is_preferred_when_docker_is_not_installed()
    {
        var result = await SampleContainerRuntime.ResolveAsync(
            configuredRuntime: null,
            (executable, arguments) => Task.FromResult<int?>(
                (executable, GetCommand(arguments)) switch
                {
                    ("podman", "container ls -n 1") => 1,
                    ("podman", "--version") => 0,
                    _ => null
                }));

        Assert.Equal("podman", result);
    }

    [Fact]
    public async Task Docker_is_the_fallback_when_no_runtime_is_available()
    {
        var result = await SampleContainerRuntime.ResolveAsync(
            configuredRuntime: null,
            (_, _) => Task.FromResult<int?>(null));

        Assert.Equal("docker", result);
    }

    [Fact]
    public async Task Runtime_status_probes_start_in_parallel()
    {
        var probesStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProbes = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedCount = 0;

        Task<int?> Run(string _, IReadOnlyList<string> arguments)
        {
            if (!IsStatusCommand(arguments))
            {
                return Task.FromResult<int?>(null);
            }

            if (Interlocked.Increment(ref startedCount) == 2)
            {
                probesStarted.SetResult();
            }

            return releaseProbes.Task;
        }

        var resolution = SampleContainerRuntime.ResolveAsync(configuredRuntime: null, Run);
        await probesStarted.Task.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        releaseProbes.SetResult(0);

        Assert.Equal("docker", await resolution);
    }

    private static bool IsStatusCommand(IReadOnlyList<string> arguments) =>
        GetCommand(arguments) == "container ls -n 1";

    private static string GetCommand(IReadOnlyList<string> arguments) => string.Join(' ', arguments);
}
