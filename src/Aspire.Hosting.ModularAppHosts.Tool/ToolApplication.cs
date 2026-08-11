using ActionsToolkit.Core.Services;
using Microsoft.Extensions.Configuration;
using System.ComponentModel;
using System.CommandLine;
using System.Text.Json;

namespace Shirubasoft.Aspire.ModularAppHosts.Tool;

internal static class ToolExitCode
{
    public const int Success = 0;
    public const int Failure = 1;
    public const int Usage = 2;
    public const int Interrupted = 130;
}

internal sealed class ToolUsageException : Exception
{
    public ToolUsageException()
        : base("The command input is invalid.")
    {
    }

    public ToolUsageException(string message)
        : base(message)
    {
    }

    public ToolUsageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal static class ToolApplication
{
    public static async Task<int> RunAsync(
        string[] args,
        IProcessRunner processRunner,
        IConfiguration configuration,
        ICoreService githubActions,
        string workingDirectory,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        var service = new ManifestCommandService(
            processRunner,
            configuration,
            githubActions,
            workingDirectory,
            output,
            error);
        var workflowService = new WorkflowCommandService(
            processRunner,
            configuration,
            githubActions,
            workingDirectory,
            output,
            error);
        var root = new RootCommand("Workflow tooling for Aspire modular AppHosts.");
        var manifest = new Command("manifest", "Creates and consumes workflow image manifests.");
        manifest.Subcommands.Add(CreatePublishCommand(service));
        manifest.Subcommands.Add(CreateApplyCommand(service));
        root.Subcommands.Add(manifest);
        var workflow = new Command("workflow", "Orchestrates external GitHub Actions workflows.");
        workflow.Subcommands.Add(CreateDispatchCommand(workflowService));
        root.Subcommands.Add(workflow);

        var parseResult = root.Parse(args);
        try
        {
            var exitCode = await parseResult.InvokeAsync(
                new InvocationConfiguration
                {
                    EnableDefaultExceptionHandler = false,
                    Output = output,
                    Error = error
                },
                cancellationToken).ConfigureAwait(false);
            return parseResult.Errors.Count > 0 ? ToolExitCode.Usage : exitCode;
        }
        catch (OperationCanceledException)
        {
            return ToolExitCode.Interrupted;
        }
        catch (Exception exception) when (IsUsageFailure(exception))
        {
            await error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return ToolExitCode.Usage;
        }
        catch (Exception exception) when (IsRuntimeFailure(exception))
        {
            await error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return ToolExitCode.Failure;
        }
    }

    private static Command CreatePublishCommand(ManifestCommandService service)
    {
        var appHost = new Option<string>("--apphost")
        {
            Description = "AppHost project path or containing directory.",
            Required = true
        };
        var selectors = new Option<string[]>("--selector")
        {
            Description = "Module or resource selector. Repeat for multiple selections.",
            AllowMultipleArgumentsPerToken = true
        };
        var all = new Option<bool>("--all")
        {
            Description = "Publish every module image exposed by the AppHost."
        };
        var tag = new Option<string?>("--tag")
        {
            Description = "Tag applied to every selected image."
        };
        var resourceTags = new Option<string>("--resource-tags")
        {
            Description = "Per-resource tags as a JSON object keyed by <module>/<resource>.",
            DefaultValueFactory = _ => "{}"
        };
        var output = new Option<string?>("--output")
        {
            Description = "Manifest output path."
        };
        var aspirePath = new Option<string>("--aspire-path")
        {
            Description = "Aspire CLI executable path.",
            DefaultValueFactory = _ => "aspire"
        };
        var command = new Command("publish", "Publishes selected images and writes their remote identities.");
        command.Options.Add(appHost);
        command.Options.Add(selectors);
        command.Options.Add(all);
        command.Options.Add(tag);
        command.Options.Add(resourceTags);
        command.Options.Add(output);
        command.Options.Add(aspirePath);
        command.SetAction((parse, cancellationToken) => service.PublishAsync(
            parse.GetRequiredValue(appHost),
            parse.GetValue(selectors) ?? [],
            parse.GetValue(all),
            parse.GetValue(tag),
            parse.GetRequiredValue(resourceTags),
            parse.GetValue(output),
            parse.GetRequiredValue(aspirePath),
            cancellationToken));
        return command;
    }

    private static Command CreateApplyCommand(ManifestCommandService service)
    {
        var file = new Option<string?>("--file")
        {
            Description = "Manifest file path."
        };
        var json = new Option<string?>("--json")
        {
            Description = "Inline compact manifest JSON."
        };
        var tag = new Option<string?>("--tag")
        {
            Description = "Tag applied to every manifest image."
        };
        var resourceTags = new Option<string>("--resource-tags")
        {
            Description = "Per-resource tags as a JSON object keyed by <module>/<resource>.",
            DefaultValueFactory = _ => "{}"
        };
        var childCommand = new Argument<string[]>("command")
        {
            Description = "Command and arguments to run with the manifest configuration; precede them with '--'.",
            Arity = ArgumentArity.OneOrMore
        };
        var command = new Command("apply", "Runs a command with workflow image identities applied.");
        command.Options.Add(file);
        command.Options.Add(json);
        command.Options.Add(tag);
        command.Options.Add(resourceTags);
        command.Arguments.Add(childCommand);
        command.SetAction((parse, cancellationToken) => service.ApplyAsync(
            parse.GetValue(file),
            parse.GetValue(json),
            parse.GetValue(tag),
            parse.GetRequiredValue(resourceTags),
            parse.GetRequiredValue(childCommand),
            parse.Tokens.Any(token => string.Equals(token.Value, "--", StringComparison.Ordinal)),
            cancellationToken));
        return command;
    }

    private static Command CreateDispatchCommand(WorkflowCommandService service)
    {
        var repository = new Option<string>("--repository")
        {
            Description = "Target repository in [HOST/]OWNER/REPO form.",
            Required = true
        };
        var workflow = new Option<string>("--workflow")
        {
            Description = "Target workflow file name, ID, or name.",
            Required = true
        };
        var reference = new Option<string?>("--ref")
        {
            Description = "Target branch or tag containing the workflow. Defaults to the repository default branch."
        };
        var manifest = new Option<string>("--manifest")
        {
            Description = "Module image manifest file passed to the target workflow.",
            Required = true
        };
        var manifestInput = new Option<string>("--manifest-input")
        {
            Description = "Target workflow input that receives the manifest.",
            DefaultValueFactory = _ => "image-manifest"
        };
        var inputs = new Option<string[]>("--input")
        {
            Description = "Additional workflow input in <name>=<value> form. Repeat for multiple inputs.",
            AllowMultipleArgumentsPerToken = true
        };
        var githubCliPath = new Option<string>("--gh-path")
        {
            Description = "GitHub CLI executable path.",
            DefaultValueFactory = _ => service.DefaultGitHubCliPath
        };
        var command = new Command("dispatch", "Dispatches a workflow, waits for it, and returns its result.");
        command.Options.Add(repository);
        command.Options.Add(workflow);
        command.Options.Add(reference);
        command.Options.Add(manifest);
        command.Options.Add(manifestInput);
        command.Options.Add(inputs);
        command.Options.Add(githubCliPath);
        command.SetAction((parse, cancellationToken) => service.DispatchAsync(
            parse.GetRequiredValue(repository),
            parse.GetRequiredValue(workflow),
            parse.GetValue(reference),
            parse.GetRequiredValue(manifest),
            parse.GetRequiredValue(manifestInput),
            parse.GetValue(inputs) ?? [],
            parse.GetRequiredValue(githubCliPath),
            cancellationToken));
        return command;
    }

    private static bool IsUsageFailure(Exception exception) =>
        exception is ToolUsageException or InvalidDataException or JsonException;

    private static bool IsRuntimeFailure(Exception exception) =>
        exception is IOException or Win32Exception or UnauthorizedAccessException;
}
