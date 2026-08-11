using ActionsToolkit.Core.Services;
using Aspire.Hosting;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

namespace Shirubasoft.Aspire.ModularAppHosts.Tool;

internal sealed class WorkflowCommandService(
    IProcessRunner processRunner,
    IConfiguration configuration,
    ICoreService githubActions,
    string workingDirectory,
    TextWriter output,
    TextWriter error)
{
    public string DefaultGitHubCliPath =>
        configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:GitHubCliPath"] ?? "gh";

    public async Task<int> DispatchAsync(
        string repository,
        string workflow,
        string? reference,
        string manifestPath,
        string manifestInput,
        IReadOnlyList<string> rawInputs,
        string githubCliPath,
        CancellationToken cancellationToken)
    {
        ValidateRequiredValue(repository, "--repository");
        ValidateRequiredValue(workflow, "--workflow");
        ValidateRequiredValue(manifestPath, "--manifest");
        ValidateRequiredValue(manifestInput, "--manifest-input");
        ValidateRequiredValue(githubCliPath, "--gh-path");

        var document = await ModuleImageManifestDocument.LoadAsync(
            Path.GetFullPath(manifestPath, workingDirectory),
            cancellationToken).ConfigureAwait(false);
        var inputs = ParseInputs(rawInputs);
        if (!inputs.TryAdd(manifestInput, document.ToJson()))
        {
            throw new ToolUsageException(
                $"Workflow input '{manifestInput}' is reserved for the image manifest.");
        }

        if (inputs.Count > 10)
        {
            throw new ToolUsageException("GitHub workflow dispatch supports at most 10 inputs.");
        }

        var payload = JsonSerializer.Serialize(inputs);
        if (payload.Length > ModuleImageManifestDocument.MaximumJsonLength)
        {
            throw new ToolUsageException(
                $"The complete workflow input payload exceeds {ModuleImageManifestDocument.MaximumJsonLength} characters.");
        }

        var dispatchArguments = new List<string>
        {
            "workflow",
            "run",
            workflow,
            "--repo",
            repository,
            "--json"
        };
        if (!string.IsNullOrWhiteSpace(reference))
        {
            dispatchArguments.Add("--ref");
            dispatchArguments.Add(reference);
        }

        var dispatch = await processRunner.RunAsync(
            new ProcessInvocation(
                githubCliPath,
                dispatchArguments,
                workingDirectory,
                OutputMode: ProcessOutputMode.Capture,
                StandardInput: payload),
            cancellationToken).ConfigureAwait(false);
        if (!dispatch.IsSuccess)
        {
            await WriteProcessFailureAsync("GitHub workflow dispatch", dispatch).ConfigureAwait(false);
            return ToolExitCode.Failure;
        }

        if (!TryGetRun(dispatch.StandardOutput, out var runId, out var runUrl))
        {
            await error.WriteLineAsync(
                "GitHub CLI did not return the created workflow run URL. Version 2.87.0 or newer is required.")
                .ConfigureAwait(false);
            return ToolExitCode.Failure;
        }

        await output.WriteLineAsync($"Dispatched {runUrl}").ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(configuration["GITHUB_OUTPUT"]))
        {
            await githubActions.SetOutputAsync("run-id", runId).ConfigureAwait(false);
            await githubActions.SetOutputAsync("run-url", runUrl).ConfigureAwait(false);
        }

        var watch = await processRunner.RunAsync(
            new ProcessInvocation(
                githubCliPath,
                ["run", "watch", runId, "--repo", repository, "--compact", "--exit-status"],
                workingDirectory,
                OutputMode: ProcessOutputMode.Stream),
            cancellationToken).ConfigureAwait(false);
        return watch.ExitCode;
    }

    private static Dictionary<string, string> ParseInputs(IReadOnlyList<string> rawInputs)
    {
        var inputs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawInput in rawInputs)
        {
            var separator = rawInput.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                throw new ToolUsageException(
                    $"Workflow input '{rawInput}' must use the form <name>=<value>.");
            }

            var name = rawInput[..separator];
            if (string.IsNullOrWhiteSpace(name) ||
                name.Contains('\r', StringComparison.Ordinal) ||
                name.Contains('\n', StringComparison.Ordinal))
            {
                throw new ToolUsageException($"Workflow input name '{name}' is invalid.");
            }

            if (!inputs.TryAdd(name, rawInput[(separator + 1)..]))
            {
                throw new ToolUsageException($"Workflow input '{name}' is specified more than once.");
            }
        }

        return inputs;
    }

    private static bool TryGetRun(string standardOutput, out string runId, out string runUrl)
    {
        runId = string.Empty;
        runUrl = string.Empty;
        var candidate = standardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var id = uri.Segments.LastOrDefault()?.Trim('/');
        if (!long.TryParse(id, out _))
        {
            return false;
        }

        runId = id;
        runUrl = uri.AbsoluteUri;
        return true;
    }

    private async Task WriteProcessFailureAsync(
        string operation,
        ProcessExecutionResult result)
    {
        await error.WriteLineAsync($"{operation} failed with exit code {result.ExitCode}.")
            .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            await error.WriteLineAsync(result.StandardError.Trim()).ConfigureAwait(false);
        }
    }

    private static void ValidateRequiredValue(string value, string option)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ToolUsageException($"{option} cannot be empty.");
        }
    }
}
