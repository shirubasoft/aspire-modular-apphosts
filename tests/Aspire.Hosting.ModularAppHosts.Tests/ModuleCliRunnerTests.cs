using Aspire.Hosting;
using CliWrap;
using CliWrap.Buffered;
using Xunit;
using CliCommand = global::CliWrap.Cli;

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
    public async Task Runner_redacts_sensitive_URI_components_from_streamed_and_captured_output()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        const string userName = "octocat";
        const string password = "super-secret-password";
        const string query = "access_token=secret-query-token";
        const string fragment = "secret-fragment";
        var progress = new List<string>();

        var result = await ModuleCliRunner.RunAsync(
            "/bin/sh",
            ["-c", $"printf '%s\\n' 'https://{userName}:{password}@example.test/acme/orders.git?{query}#{fragment}'"],
            Path.GetTempPath(),
            TimeSpan.FromSeconds(5),
            "test redaction",
            TestContext.Current.CancellationToken,
            progress.Add);

        var progressLine = Assert.Single(progress);
        Assert.Equal(
            "https://[REDACTED]@example.test/acme/orders.git?[REDACTED]#[REDACTED]",
            progressLine);
        Assert.Contains(progressLine, result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(userName, result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(password, result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(query, result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(fragment, result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runner_redacts_labeled_credentials_and_authorization_values()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        const string password = "password with spaces";
        const string token = "token-value";
        const string bearer = "header.payload.signature";
        var progress = new List<string>();

        var result = await ModuleCliRunner.RunAsync(
            "/bin/sh",
            [
                "-c",
                $"printf '%s\\n' 'password: \"{password}\"'; " +
                $"printf '%s\\n' 'GITHUB_TOKEN={token}' >&2; " +
                $"printf '%s\\n' 'Authorization: Bearer {bearer}' >&2"
            ],
            Path.GetTempPath(),
            TimeSpan.FromSeconds(5),
            "test credentials",
            TestContext.Current.CancellationToken,
            progress.Add);

        Assert.Contains("password: \"[REDACTED]\"", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("GITHUB_TOKEN=[REDACTED]", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("Authorization: Bearer [REDACTED]", result.StandardError, StringComparison.Ordinal);
        Assert.All(progress, line =>
        {
            Assert.DoesNotContain(password, line, StringComparison.Ordinal);
            Assert.DoesNotContain(token, line, StringComparison.Ordinal);
            Assert.DoesNotContain(bearer, line, StringComparison.Ordinal);
        });
        Assert.DoesNotContain(password, result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(token, result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain(bearer, result.StandardError, StringComparison.Ordinal);
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
    public async Task Repository_commands_use_the_configured_git_executable()
    {
        var path = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}");

        var command = Assert.Single(await RepositorySynchronizer.CreateCommandsAsync(
            path,
            "https://example.test/acme/orders.git",
            updateRepository: true,
            gitExecutablePath: "custom-git",
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("custom-git", command.Executable);
    }

    [Fact]
    public async Task Missing_GitHub_repository_is_cloned_with_the_configured_GitHub_CLI()
    {
        var path = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}");

        var command = Assert.Single(await RepositorySynchronizer.CreateCommandsAsync(
            path,
            "https://github.com/acme/orders.git",
            updateRepository: true,
            githubCliPath: "custom-gh",
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("custom-gh", command.Executable);
        Assert.Equal(
            ["repo", "clone", "https://github.com/acme/orders.git", path, "--", "--recurse-submodules"],
            command.Arguments);
    }

    [Fact]
    public async Task GitHub_CLI_credential_helper_is_process_scoped_without_exposing_its_token()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = TemporaryDirectory.Create();
        var githubCli = Path.Combine(directory.Path, "fake gh");
        var log = Path.Combine(directory.Path, "gh-arguments.txt");
        const string token = "test-token-that-must-not-be-an-argument";
        await File.WriteAllTextAsync(
            githubCli,
            $$"""
            #!/bin/sh
            printf '%s\n' "$*" > '{{log}}'
            cat >/dev/null
            printf 'username=x-access-token\npassword={{token}}\n'
            """,
            TestContext.Current.CancellationToken);
        File.SetUnixFileMode(
            githubCli,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var arguments = GitHubGitAuthentication.ConfigureCredentialHelper(
            ["credential", "fill"],
            "https://github.com/acme/orders.git",
            githubCli);
        var result = await CliCommand.Wrap("git")
            .WithArguments(arguments)
            .WithWorkingDirectory(directory.Path)
            .WithStandardInputPipe(PipeSource.FromString("protocol=https\nhost=github.com\n\n"))
            .ExecuteBufferedAsync(TestContext.Current.CancellationToken);
        var helperArguments = await File.ReadAllTextAsync(log, TestContext.Current.CancellationToken);

        Assert.Contains($"password={token}", result.StandardOutput, StringComparison.Ordinal);
        Assert.Equal("auth git-credential get", helperArguments.Trim());
        Assert.DoesNotContain(token, arguments);
    }
}
