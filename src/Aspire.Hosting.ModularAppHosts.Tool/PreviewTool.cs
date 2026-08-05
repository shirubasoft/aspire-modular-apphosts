using System.Text;
using System.Text.Json;
using Aspire.Hosting.ModularAppHosts;
using CliWrap;
using CliWrap.Buffered;
using CliCommand = global::CliWrap.Cli;

namespace Shirubasoft.Aspire.ModularAppHosts.Tool;

internal static partial class PreviewTool
{
    private static readonly JsonSerializerOptions CompactJsonOptions = new(ModulePreviewJson.SerializerOptions)
    {
        WriteIndented = false
    };

    private const string Usage = """
        Aspire Modular AppHosts preview tool

        Usage:
          dotnet modular-apphosts preview produce --descriptor <path> --output <path>
              [--image <resource>=<repository>@<sha256-digest>]...
              [--pin <name>=<repository-url>@<full-commit>]...
          dotnet modular-apphosts preview export --module <name> --output <path>
              [--pin <name>=<repository-url>@<full-commit>]...
          dotnet modular-apphosts preview verify --manifest <path> --policy <path>
              [--output <path>]
          dotnet modular-apphosts preview materialize --manifest <path> --policy <path>
              --work-directory <path> --package-feed <path> --resolution <path>
              --consumer-repository <url> --consumer-commit <full-commit>
              [--github-env <path>] [--property <name>=<value>]...
          dotnet modular-apphosts preview trigger --manifest <path> --repo <owner/repo>
              --workflow <file-or-id> --ref <trusted-ref> [--input-name manifest_json]
              [--input <name>=<value>]...

        Produce and export require a clean Git worktree on an attached branch whose HEAD is pushed to origin.
        --dependency is accepted as an alias for --pin.
        """;

    public static async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            if (arguments.Count == 0 || arguments[0] is "--help" or "-h" or "help")
            {
                await Console.Out.WriteLineAsync(Usage).ConfigureAwait(false);
                return 0;
            }

            if (arguments.Count < 2 || !string.Equals(arguments[0], "preview", StringComparison.Ordinal))
            {
                throw new PreviewToolException(
                    "Expected the 'preview produce', 'preview export', 'preview verify', " +
                    "'preview materialize', or 'preview trigger' command.");
            }

            return arguments[1] switch
            {
                "produce" => await ProduceAsync(arguments.Skip(2).ToArray(), cancellationToken).ConfigureAwait(false),
                "export" => await ExportAsync(arguments.Skip(2).ToArray(), cancellationToken).ConfigureAwait(false),
                "verify" => await VerifyAsync(arguments.Skip(2).ToArray(), cancellationToken).ConfigureAwait(false),
                "materialize" => await MaterializeAsync(arguments.Skip(2).ToArray(), cancellationToken).ConfigureAwait(false),
                "trigger" => await TriggerAsync(arguments.Skip(2).ToArray(), cancellationToken).ConfigureAwait(false),
                _ => throw new PreviewToolException(
                    "Expected the 'preview produce', 'preview export', 'preview verify', " +
                    "'preview materialize', or 'preview trigger' command.")
            };
        }
        catch (Exception exception) when (
            exception is PreviewToolException
                or InvalidDataException
                or IOException
                or JsonException
                or ArgumentException
                or UnauthorizedAccessException
                or System.ComponentModel.Win32Exception)
        {
            await Console.Error.WriteLineAsync($"error: {exception.Message}").ConfigureAwait(false);
            return 1;
        }
    }

    private static async Task<int> ExportAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var options = CommandOptions.Parse(arguments, ["pin", "dependency"]);
        var moduleName = options.Required("module");
        var output = options.Required("output");
        var workingDirectory = Path.GetFullPath(options.Optional("working-directory") ?? Environment.CurrentDirectory);
        var gitExecutable = options.Optional("git-executable") ?? "git";
        options.EnsureOnly(
            "module",
            "output",
            "working-directory",
            "git-executable",
            "pin",
            "dependency");

        var git = new GitInspector(gitExecutable, workingDirectory);
        var producer = await git.InspectAsync(output, cancellationToken).ConfigureAwait(false);
        var selections = new List<ModulePreviewSelection>
        {
            new()
            {
                Name = moduleName,
                Repository = producer.Repository,
                Commit = producer.Commit,
                Branch = producer.Branch,
                BaseRef = producer.BaseRef,
                BaseCommit = producer.BaseCommit
            }
        };

        foreach (var pin in options.Many("pin").Concat(options.Many("dependency")))
        {
            selections.Add(ParsePin(pin));
        }

        var manifest = new ModulePreviewManifest
        {
            Producer = producer
        };
        foreach (var selection in selections.OrderBy(selection => selection.Name, StringComparer.Ordinal))
        {
            manifest.Modules.Add(selection);
        }

        await manifest.SaveAsync(output, cancellationToken).ConfigureAwait(false);
        await Console.Out.WriteLineAsync(Path.GetFullPath(output)).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> TriggerAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var options = CommandOptions.Parse(arguments, ["input"]);
        var manifestPath = options.Required("manifest");
        var repository = ValidateTargetRepository(options.Required("repo"));
        var workflow = ValidateWorkflow(options.Required("workflow"));
        var targetRef = ValidateSimpleValue(options.Required("ref"), "ref");
        var inputName = ValidateInputName(options.Optional("input-name") ?? "manifest_json");
        var githubCli = options.Optional("gh-executable") ?? "gh";
        options.EnsureOnly(
            "manifest",
            "repo",
            "workflow",
            "ref",
            "input-name",
            "gh-executable",
            "input");

        var manifest = await ModulePreviewManifest.LoadAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        var manifestJson = JsonSerializer.Serialize(manifest, CompactJsonOptions);
        var inputs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [inputName] = manifestJson
        };
        foreach (var input in options.Many("input"))
        {
            var (name, value) = ParseWorkflowInput(input);
            if (!inputs.TryAdd(name, value))
            {
                throw new PreviewToolException($"Workflow input '{name}' was specified more than once.");
            }
        }

        var body = JsonSerializer.Serialize(new WorkflowDispatchRequest(
            targetRef,
            inputs,
            true), CompactJsonOptions);
        var endpoint = $"repos/{repository}/actions/workflows/{Uri.EscapeDataString(workflow)}/dispatches";
        var result = await RunCommandAsync(
            githubCli,
            [
                "api",
                "--method", "POST",
                "--header", "Accept: application/vnd.github+json",
                "--header", "X-GitHub-Api-Version: 2026-03-10",
                "--input", "-",
                endpoint
            ],
            Environment.CurrentDirectory,
            body,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result, "GitHub workflow dispatch");

        string? runUrl;
        try
        {
            using var response = JsonDocument.Parse(result.StandardOutput);
            runUrl = response.RootElement.TryGetProperty("html_url", out var htmlUrl)
                ? htmlUrl.GetString()
                : null;
        }
        catch (JsonException exception)
        {
            throw new PreviewToolException(
                "GitHub workflow dispatch succeeded but returned an invalid response.",
                exception);
        }

        if (string.IsNullOrWhiteSpace(runUrl))
        {
            throw new PreviewToolException(
                "GitHub workflow dispatch did not return a run URL. Ensure the API supports run details.");
        }

        await Console.Out.WriteLineAsync(runUrl).ConfigureAwait(false);
        return 0;
    }

    private static ModulePreviewSelection ParsePin(string value)
    {
        var equals = value.IndexOf('=', StringComparison.Ordinal);
        var at = value.LastIndexOf('@');
        if (equals <= 0 || at <= equals + 1 || at == value.Length - 1)
        {
            throw new PreviewToolException(
                $"Invalid pin '{value}'. Expected <name>=<repository-url>@<full-commit>.");
        }

        var selection = new ModulePreviewSelection
        {
            Name = value[..equals],
            Repository = CanonicalizeRepository(value[(equals + 1)..at]),
            Commit = value[(at + 1)..]
        };
        var validationManifest = new ModulePreviewManifest
        {
            Producer = new ModulePreviewProducer
            {
                Repository = selection.Repository,
                Commit = selection.Commit
            }
        };
        validationManifest.Modules.Add(selection);
        validationManifest.Validate();
        return selection;
    }

    private static KeyValuePair<string, string> ParseWorkflowInput(string input)
    {
        var equals = input.IndexOf('=', StringComparison.Ordinal);
        if (equals <= 0)
        {
            throw new PreviewToolException(
                $"Invalid workflow input '{input}'. Expected <name>=<value>.");
        }

        var name = ValidateInputName(input[..equals]);
        var value = input[(equals + 1)..];
        if (value.Any(char.IsControl))
        {
            throw new PreviewToolException($"Workflow input '{name}' contains control characters.");
        }

        return KeyValuePair.Create(name, value);
    }

    private static string CanonicalizeRepository(string repository)
    {
        var value = repository.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            if (uri.IsFile || string.IsNullOrWhiteSpace(uri.Host))
            {
                throw new PreviewToolException($"Repository '{repository}' is not a remote repository URL.");
            }

            var path = uri.AbsolutePath.Trim('/');
            if (string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            {
                return $"https://github.com/{EnsureGitSuffix(path)}";
            }

            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                throw new PreviewToolException(
                    $"Repository '{repository}' contains SSH user information. " +
                    "Use a credential-free absolute repository URL for non-GitHub remotes.");
            }

            var builder = new UriBuilder(uri)
            {
                Query = string.Empty,
                Fragment = string.Empty,
                Path = EnsureGitSuffix(path)
            };
            return builder.Uri.AbsoluteUri;
        }

        var colon = value.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0)
        {
            var hostStart = value.LastIndexOf('@', colon);
            var host = value[(hostStart >= 0 ? hostStart + 1 : 0)..colon];
            var path = value[(colon + 1)..].Trim('/');
            if (!string.IsNullOrWhiteSpace(host) && !string.IsNullOrWhiteSpace(path))
            {
                return string.Equals(host, "github.com", StringComparison.OrdinalIgnoreCase)
                    ? $"https://github.com/{EnsureGitSuffix(path)}"
                    : throw new PreviewToolException(
                        $"Repository '{repository}' uses SCP-style SSH syntax. " +
                        "Use a credential-free absolute repository URL for non-GitHub remotes.");
            }
        }

        throw new PreviewToolException($"Repository '{repository}' is not a canonical remote repository URL.");
    }

    private static string EnsureGitSuffix(string path) =>
        path.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? path : $"{path}.git";

    private static string ValidateTargetRepository(string value)
    {
        var segments = value.Split('/');
        if (segments.Length != 2 || segments.Any(segment =>
                string.IsNullOrWhiteSpace(segment) ||
                segment is "." or ".." ||
                segment.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'))))
        {
            throw new PreviewToolException("--repo must use the GitHub owner/repository form.");
        }

        return value;
    }

    private static string ValidateWorkflow(string value) =>
        ValidateSimpleValue(value, "workflow", character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static string ValidateInputName(string value) =>
        ValidateSimpleValue(value, "input-name", character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static string ValidateSimpleValue(
        string value,
        string option,
        Func<char, bool>? allowed = null)
    {
        allowed ??= character => !char.IsControl(character) && !char.IsWhiteSpace(character);
        if (string.IsNullOrWhiteSpace(value) || !value.All(allowed))
        {
            throw new PreviewToolException($"--{option} contains unsupported characters.");
        }

        return value;
    }

    private static async Task<CommandResult> RunCommandAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string? standardInput,
        CancellationToken cancellationToken)
    {
        var command = CliCommand.Wrap(executable)
            .WithArguments(arguments)
            .WithWorkingDirectory(workingDirectory)
            .WithValidation(CommandResultValidation.None);
        if (standardInput is not null)
        {
            command = command.WithStandardInputPipe(PipeSource.FromString(standardInput));
        }

        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);
        try
        {
            var result = await command.ExecuteBufferedAsync(linkedSource.Token).ConfigureAwait(false);
            return new CommandResult(result.ExitCode, result.StandardOutput, result.StandardError);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PreviewToolException(
                $"Command '{Path.GetFileName(executable)}' exceeded the two-minute timeout.",
                exception);
        }
    }

    private static void EnsureSuccess(CommandResult result, string operation)
    {
        if (result.ExitCode == 0)
        {
            return;
        }

        var error = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput
            : result.StandardError;
        throw new PreviewToolException(
            $"{operation} failed with exit code {result.ExitCode}: {error.Trim()}");
    }

    private sealed class GitInspector(string executable, string workingDirectory)
    {
        public async Task<ModulePreviewProducer> InspectAsync(
            string outputPath,
            CancellationToken cancellationToken)
        {
            var repositoryRoot = Path.GetFullPath(
                await GitAsync(cancellationToken, "rev-parse", "--show-toplevel").ConfigureAwait(false));
            var ignoredOutput = GetRepositoryRelativeOutput(repositoryRoot, outputPath);
            if (ignoredOutput is not null &&
                await IsTrackedAsync(ignoredOutput, cancellationToken).ConfigureAwait(false))
            {
                throw new PreviewToolException(
                    $"The preview output '{outputPath}' is tracked by Git. " +
                    "Write manifests to an untracked, ignored, or out-of-repository path.");
            }

            var status = await GitAsync(
                cancellationToken,
                "status",
                "--porcelain=v1",
                "-z",
                "--untracked-files=all")
                .ConfigureAwait(false);
            var dirtyEntries = status
                .Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .Where(entry => !string.Equals(entry, $"?? {ignoredOutput}", StringComparison.Ordinal))
                .ToArray();
            if (dirtyEntries.Length > 0)
            {
                throw new PreviewToolException(
                    "The Git worktree is dirty. Commit all tracked and untracked changes before exporting a preview.");
            }

            var branch = await GitAsync(cancellationToken, "symbolic-ref", "--quiet", "--short", "HEAD")
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(branch))
            {
                throw new PreviewToolException("Preview export requires an attached Git branch.");
            }

            var commit = await GitAsync(cancellationToken, "rev-parse", "HEAD").ConfigureAwait(false);
            var repository = CanonicalizeRepository(
                await GitAsync(cancellationToken, "remote", "get-url", "origin").ConfigureAwait(false));
            var remote = await GitAsync(
                cancellationToken,
                "ls-remote",
                "--symref",
                "origin",
                "HEAD",
                $"refs/heads/{branch}").ConfigureAwait(false);
            var remoteState = ParseRemoteState(remote, branch);
            if (remoteState.BranchCommit is null)
            {
                throw new PreviewToolException(
                    $"Current branch '{branch}' has not been pushed to origin.");
            }

            if (!string.Equals(commit, remoteState.BranchCommit, StringComparison.OrdinalIgnoreCase))
            {
                throw new PreviewToolException(
                    $"Current commit '{commit}' is not the pushed tip of origin branch '{branch}'.");
            }

            return new ModulePreviewProducer
            {
                Repository = repository,
                Commit = commit,
                Dirty = false,
                Branch = branch,
                BaseRef = remoteState.BaseRef,
                BaseCommit = remoteState.BaseCommit
            };
        }

        private async Task<string> GitAsync(
            CancellationToken cancellationToken,
            params string[] arguments)
        {
            var result = await RunCommandAsync(
                executable,
                arguments,
                workingDirectory,
                standardInput: null,
                cancellationToken).ConfigureAwait(false);
            EnsureSuccess(result, $"git {arguments[0]}");
            return result.StandardOutput.Trim();
        }

        private async Task<bool> IsTrackedAsync(
            string repositoryRelativePath,
            CancellationToken cancellationToken)
        {
            var result = await RunCommandAsync(
                executable,
                ["ls-files", "--error-unmatch", "--", repositoryRelativePath],
                workingDirectory,
                standardInput: null,
                cancellationToken).ConfigureAwait(false);
            if (result.ExitCode is 0 or 1)
            {
                return result.ExitCode == 0;
            }

            EnsureSuccess(result, "git ls-files");
            return false;
        }

        private static string? GetRepositoryRelativeOutput(string repositoryRoot, string outputPath)
        {
            var relativePath = Path.GetRelativePath(repositoryRoot, Path.GetFullPath(outputPath));
            if (Path.IsPathRooted(relativePath) ||
                relativePath.Equals("..", StringComparison.Ordinal) ||
                relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
            {
                return null;
            }

            return relativePath.Replace(Path.DirectorySeparatorChar, '/');
        }

        private static RemoteState ParseRemoteState(string output, string branch)
        {
            string? baseRef = null;
            string? baseCommit = null;
            string? branchCommit = null;
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (line.StartsWith("ref: ", StringComparison.Ordinal) && line.EndsWith("\tHEAD", StringComparison.Ordinal))
                {
                    baseRef = line[5..^5];
                    continue;
                }

                var tab = line.IndexOf('\t', StringComparison.Ordinal);
                if (tab <= 0)
                {
                    continue;
                }

                var revision = line[..tab];
                var reference = line[(tab + 1)..];
                if (string.Equals(reference, "HEAD", StringComparison.Ordinal))
                {
                    baseCommit = revision;
                }

                if (string.Equals(reference, $"refs/heads/{branch}", StringComparison.Ordinal))
                {
                    branchCommit = revision;
                }
            }

            if (baseRef is null || baseCommit is null)
            {
                throw new PreviewToolException("Unable to resolve origin's default branch and commit.");
            }

            return new RemoteState(baseRef, baseCommit, branchCommit);
        }
    }

    private sealed class CommandOptions
    {
        private readonly Dictionary<string, List<string>> _values = new(StringComparer.Ordinal);
        private readonly HashSet<string> _repeatable;

        private CommandOptions(IEnumerable<string> repeatable)
        {
            _repeatable = new HashSet<string>(repeatable, StringComparer.Ordinal);
        }

        public static CommandOptions Parse(
            IReadOnlyList<string> arguments,
            IEnumerable<string>? repeatable = null)
        {
            var options = new CommandOptions(repeatable ?? []);
            for (var index = 0; index < arguments.Count; index += 2)
            {
                var option = arguments[index];
                if (!option.StartsWith("--", StringComparison.Ordinal) ||
                    option.Length == 2 ||
                    index + 1 >= arguments.Count)
                {
                    throw new PreviewToolException($"Expected --option value near '{option}'.");
                }

                var name = option[2..];
                if (!options._values.TryGetValue(name, out var values))
                {
                    values = [];
                    options._values.Add(name, values);
                }

                if (values.Count > 0 && !options._repeatable.Contains(name))
                {
                    throw new PreviewToolException($"Option '--{name}' can only be specified once.");
                }

                values.Add(arguments[index + 1]);
            }

            return options;
        }

        public string Required(string name) =>
            Optional(name) ?? throw new PreviewToolException($"Missing required option '--{name}'.");

        public string? Optional(string name) =>
            _values.TryGetValue(name, out var values) ? values[0] : null;

        public List<string> Many(string name) =>
            _values.TryGetValue(name, out var values) ? values : [];

        public void EnsureOnly(params string[] names)
        {
            var accepted = new HashSet<string>(names, StringComparer.Ordinal);
            var unsupported = _values.Keys.FirstOrDefault(name => !accepted.Contains(name));
            if (unsupported is not null)
            {
                throw new PreviewToolException($"Unsupported option '--{unsupported}'.");
            }
        }
    }

    private sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed record RemoteState(string BaseRef, string BaseCommit, string? BranchCommit);

    private sealed record WorkflowDispatchRequest(
        string Ref,
        IReadOnlyDictionary<string, string> Inputs,
        [property: System.Text.Json.Serialization.JsonPropertyName("return_run_details")]
        bool ReturnRunDetails);
}

internal sealed class PreviewToolException : Exception
{
    public PreviewToolException()
    {
    }

    public PreviewToolException(string message)
        : base(message)
    {
    }

    public PreviewToolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
