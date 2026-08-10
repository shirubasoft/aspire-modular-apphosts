using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Aspire.Hosting.ModularAppHosts;

namespace Shirubasoft.Aspire.ModularAppHosts.Tool;

internal sealed class WorkflowDispatchService(
    IProcessRunner processRunner,
    IEnvironmentAccessor environment,
    TextWriter output,
    TextWriter error)
{
    private const int MaximumWorkflowInputPayloadLength = 65_535;
    private static readonly Version MinimumGitHubCliVersion = new(2, 97, 0);

    public async Task<int> DispatchAsync(
        string repository,
        string workflow,
        string reference,
        string manifestPath,
        string manifestInput,
        string[] inputs,
        TimeSpan timeout,
        string githubPath,
        CancellationToken cancellationToken)
    {
        WorkflowRunIdentity? run = null;
        ModuleImageManifestDocument? manifest = null;
        string? fullManifestPath = null;
        try
        {
            ValidateArguments(repository, workflow, reference, manifestInput, timeout, githubPath);
            fullManifestPath = Path.GetFullPath(manifestPath, environment.CurrentDirectory);
            manifest = await ModuleImageManifestDocument.LoadAsync(
                fullManifestPath,
                cancellationToken).ConfigureAwait(false);
            var compactManifest = manifest.ToJson();
            var workflowInputs = ParseInputs(inputs, manifestInput, compactManifest);
            var dispatchPayload = JsonSerializer.Serialize(workflowInputs);
            if (dispatchPayload.Length > MaximumWorkflowInputPayloadLength)
            {
                throw new ToolUsageException(
                    $"Workflow inputs exceed GitHub's {MaximumWorkflowInputPayloadLength}-character payload limit.");
            }

            var version = await RunGitHubAsync(
                githubPath,
                ["--version"],
                standardInput: null,
                cancellationToken).ConfigureAwait(false);
            if (!version.IsSuccess)
            {
                return await WriteGitHubFailureAsync(version, "GitHub CLI version check").ConfigureAwait(false);
            }

            ValidateGitHubCliVersion(version.StandardOutput);
            var authentication = await RunGitHubAsync(
                githubPath,
                ["auth", "status", "--active"],
                standardInput: null,
                cancellationToken).ConfigureAwait(false);
            if (!authentication.IsSuccess)
            {
                await error.WriteLineAsync("GitHub CLI authentication is unavailable or invalid.")
                    .ConfigureAwait(false);
                return ToolExitCode.AuthenticationFailure;
            }

            var dispatch = await RunGitHubAsync(
                githubPath,
                [
                    "workflow", "run", workflow,
                    "--repo", repository,
                    "--ref", reference,
                    "--json"
                ],
                dispatchPayload,
                cancellationToken).ConfigureAwait(false);
            if (!dispatch.IsSuccess)
            {
                return await WriteGitHubFailureAsync(dispatch, "Workflow dispatch").ConfigureAwait(false);
            }

            run = ParseRunIdentity(dispatch.StandardOutput);
            await output.WriteLineAsync($"Dispatched {run.Url}").ConfigureAwait(false);
            using var timeoutSource = new CancellationTokenSource(timeout);
            using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutSource.Token);
            try
            {
                var watch = await RunGitHubAsync(
                    githubPath,
                    ["run", "watch", run.Id, "--repo", repository, "--compact"],
                    standardInput: null,
                    linkedSource.Token).ConfigureAwait(false);
                if (!watch.IsSuccess)
                {
                    await WriteOutputsAsync(manifest, fullManifestPath, run, "unknown", CancellationToken.None)
                        .ConfigureAwait(false);
                    return await WriteGitHubFailureAsync(watch, "Workflow watch").ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
            {
                await CancelAsync(githubPath, repository, run.Id).ConfigureAwait(false);
                await WriteOutputsAsync(manifest, fullManifestPath, run, "timed_out", CancellationToken.None)
                    .ConfigureAwait(false);
                return ToolExitCode.Timeout;
            }
            catch (OperationCanceledException)
            {
                await CancelAsync(githubPath, repository, run.Id).ConfigureAwait(false);
                await WriteOutputsAsync(manifest, fullManifestPath, run, "cancelled", CancellationToken.None)
                    .ConfigureAwait(false);
                return ToolExitCode.Interrupted;
            }

            var view = await RunGitHubAsync(
                githubPath,
                ["run", "view", run.Id, "--repo", repository, "--json", "conclusion,databaseId,url"],
                standardInput: null,
                cancellationToken).ConfigureAwait(false);
            if (!view.IsSuccess)
            {
                await WriteOutputsAsync(manifest, fullManifestPath, run, "unknown", CancellationToken.None)
                    .ConfigureAwait(false);
                return await WriteGitHubFailureAsync(view, "Workflow conclusion query").ConfigureAwait(false);
            }

            WorkflowRunConclusion final;
            try
            {
                final = ParseConclusion(view.StandardOutput, run);
            }
            catch (GitHubOperationException)
            {
                await WriteOutputsAsync(manifest, fullManifestPath, run, "unknown", CancellationToken.None)
                    .ConfigureAwait(false);
                throw;
            }

            await WriteOutputsAsync(
                manifest,
                fullManifestPath,
                final.Run,
                final.Conclusion,
                cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync(
                $"External workflow {final.Run.Id} concluded with '{final.Conclusion}'.")
                .ConfigureAwait(false);
            return string.Equals(final.Conclusion, "success", StringComparison.OrdinalIgnoreCase)
                ? ToolExitCode.Success
                : ToolExitCode.TargetFailure;
        }
        catch (OperationCanceledException)
        {
            if (run is not null)
            {
                await CancelAsync(githubPath, repository, run.Id).ConfigureAwait(false);
                if (manifest is not null && fullManifestPath is not null)
                {
                    await WriteOutputsAsync(manifest, fullManifestPath, run, "cancelled", CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }

            return ToolExitCode.Interrupted;
        }
        catch (ToolUsageException exception)
        {
            await error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return ToolExitCode.Usage;
        }
        catch (GitHubOperationException exception)
        {
            await error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return ToolExitCode.GitHubFailure;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or IOException or
                                          JsonException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return ToolExitCode.Usage;
        }
    }

    private async Task<ProcessExecutionResult> RunGitHubAsync(
        string githubPath,
        IReadOnlyList<string> arguments,
        string? standardInput,
        CancellationToken cancellationToken)
    {
        try
        {
            return await processRunner.RunAsync(
                new ProcessInvocation(
                    githubPath,
                    arguments,
                    environment.CurrentDirectory,
                    StandardInput: standardInput),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Win32Exception exception)
        {
            throw new GitHubOperationException(
                $"Unable to execute GitHub CLI at '{githubPath}': {exception.Message}",
                exception);
        }
    }

    private async Task CancelAsync(string githubPath, string repository, string runId)
    {
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await RunGitHubAsync(
                githubPath,
                ["run", "cancel", runId, "--repo", repository],
                standardInput: null,
                cancellationSource.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or GitHubOperationException)
        {
        }
    }

    private async Task WriteOutputsAsync(
        ModuleImageManifestDocument manifest,
        string manifestPath,
        WorkflowRunIdentity run,
        string conclusion,
        CancellationToken cancellationToken)
    {
        var githubOutput = environment.GetEnvironmentVariable("GITHUB_OUTPUT");
        if (string.IsNullOrWhiteSpace(githubOutput))
        {
            return;
        }

        await GitHubFileWriter.AppendAsync(
            githubOutput,
            [
                new("manifest", manifest.ToJson()),
                new("manifest-path", manifestPath),
                new("run-id", run.Id),
                new("run-url", run.Url),
                new("conclusion", conclusion)
            ],
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> WriteGitHubFailureAsync(
        ProcessExecutionResult result,
        string operation)
    {
        await error.WriteLineAsync($"{operation} failed with GitHub CLI exit code {result.ExitCode}.")
            .ConfigureAwait(false);
        return result.ExitCode == ToolExitCode.AuthenticationFailure
            ? ToolExitCode.AuthenticationFailure
            : ToolExitCode.GitHubFailure;
    }

    private static Dictionary<string, string> ParseInputs(
        IEnumerable<string> inputs,
        string manifestInput,
        string manifest)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in inputs)
        {
            var separator = input.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                throw new ToolUsageException($"Workflow input '{input}' must use the form <name>=<value>.");
            }

            var name = input[..separator];
            if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsWhiteSpace))
            {
                throw new ToolUsageException($"Workflow input name '{name}' is invalid.");
            }
            if (!values.TryAdd(name, input[(separator + 1)..]))
            {
                throw new ToolUsageException($"Workflow input '{name}' is specified more than once.");
            }
        }

        if (!values.TryAdd(manifestInput, manifest))
        {
            throw new ToolUsageException(
                $"Workflow input '{manifestInput}' is reserved for the image manifest.");
        }

        return values;
    }

    private static WorkflowRunIdentity ParseRunIdentity(string standardOutput)
    {
        foreach (var line in standardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Reverse())
        {
            if (!Uri.TryCreate(line.Trim(), UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https"))
            {
                continue;
            }

            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var runs = Array.FindLastIndex(
                segments,
                segment => string.Equals(segment, "runs", StringComparison.OrdinalIgnoreCase));
            if (runs >= 0 && runs + 1 < segments.Length &&
                long.TryParse(segments[runs + 1], out var runId) && runId > 0)
            {
                return new WorkflowRunIdentity(runId.ToString(CultureInfo.InvariantCulture), uri.ToString());
            }
        }

        try
        {
            using var document = JsonDocument.Parse(standardOutput);
            var root = document.RootElement;
            var id = root.GetProperty("workflow_run_id").GetInt64();
            var url = root.TryGetProperty("html_url", out var htmlUrl)
                ? htmlUrl.GetString()
                : root.GetProperty("run_url").GetString();
            if (id > 0 && !string.IsNullOrWhiteSpace(url))
            {
                return new WorkflowRunIdentity(id.ToString(CultureInfo.InvariantCulture), url);
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
        }

        throw new GitHubOperationException(
            "GitHub CLI did not return the created workflow run URL and ID. Ensure gh 2.97 or newer is installed.");
    }

    private static WorkflowRunConclusion ParseConclusion(
        string json,
        WorkflowRunIdentity dispatchedRun)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var conclusion = root.GetProperty("conclusion").GetString();
            if (string.IsNullOrWhiteSpace(conclusion))
            {
                throw new GitHubOperationException("The external workflow completed without a conclusion.");
            }

            var id = root.TryGetProperty("databaseId", out var databaseId) && databaseId.TryGetInt64(out var value)
                ? value.ToString(CultureInfo.InvariantCulture)
                : dispatchedRun.Id;
            var url = root.TryGetProperty("url", out var runUrl) && !string.IsNullOrWhiteSpace(runUrl.GetString())
                ? runUrl.GetString()!
                : dispatchedRun.Url;
            return new WorkflowRunConclusion(new WorkflowRunIdentity(id, url), conclusion);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new GitHubOperationException(
                "GitHub CLI returned an invalid workflow conclusion response.",
                exception);
        }
    }

    private static void ValidateGitHubCliVersion(string standardOutput)
    {
        var version = standardOutput
            .Split([' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(value => Version.TryParse(value, out var parsed) ? parsed : null)
            .OfType<Version>()
            .FirstOrDefault();
        if (version is null || version < MinimumGitHubCliVersion)
        {
            throw new ToolUsageException(
                $"GitHub CLI {MinimumGitHubCliVersion} or newer is required to obtain the dispatched run ID safely.");
        }
    }

    private static void ValidateArguments(
        string repository,
        string workflow,
        string reference,
        string manifestInput,
        TimeSpan timeout,
        string githubPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(workflow);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestInput);
        ArgumentException.ThrowIfNullOrWhiteSpace(githubPath);
        var repositoryParts = repository.Split('/');
        if (repositoryParts.Length != 2 || repositoryParts.Any(string.IsNullOrWhiteSpace))
        {
            throw new ToolUsageException("--repository must use the form <owner>/<repository>.");
        }

        if (timeout <= TimeSpan.Zero)
        {
            throw new ToolUsageException("--timeout must be positive.");
        }
    }
}

internal sealed record WorkflowRunIdentity(string Id, string Url);

internal sealed record WorkflowRunConclusion(WorkflowRunIdentity Run, string Conclusion);

internal sealed class GitHubOperationException : Exception
{
    public GitHubOperationException()
        : base("A GitHub operation failed.")
    {
    }

    public GitHubOperationException(string message)
        : base(message)
    {
    }

    public GitHubOperationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
