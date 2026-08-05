using Aspire.Hosting.ModularAppHosts;
using Xunit;

namespace Aspire.Hosting.ModularAppHosts.Tests;

public sealed class ModuleCliRunnerTests
{
    [Fact]
    public async Task Runner_streams_and_captures_standard_output_and_error()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var progress = new List<string>();

        var result = await ModuleCliRunner.RunAsync(
            "/bin/sh",
            ["-c", "echo cloning; echo warning >&2"],
            Path.GetTempPath(),
            TimeSpan.FromSeconds(5),
            "test clone",
            TestContext.Current.CancellationToken,
            progress.Add);

        Assert.True(result.IsSuccess);
        Assert.Contains("cloning", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("warning", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("cloning", progress);
        Assert.Contains("warning", progress);
    }

    [Fact]
    public async Task Runner_stops_a_command_at_the_configured_timeout()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var exception = await Assert.ThrowsAsync<TimeoutException>(() =>
            ModuleCliRunner.RunAsync(
                "/bin/sh",
                ["-c", "sleep 10"],
                Path.GetTempPath(),
                TimeSpan.FromMilliseconds(100),
                "slow clone",
                TestContext.Current.CancellationToken));

        Assert.Contains("slow clone", exception.Message, StringComparison.Ordinal);
        Assert.Contains("00:00:00.1000000", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Repository_commands_use_the_configured_git_executable()
    {
        var path = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}");

        var command = Assert.Single(RepositorySynchronizer.CreateCommands(
            path,
            "https://github.com/acme/orders.git",
            updateRepository: true,
            gitExecutablePath: "custom-git"));

        Assert.Equal("custom-git", command.Executable);
    }
}
