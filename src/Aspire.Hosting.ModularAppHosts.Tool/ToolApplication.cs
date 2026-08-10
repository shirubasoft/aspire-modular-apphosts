using System.CommandLine;

namespace Shirubasoft.Aspire.ModularAppHosts.Tool;

internal static class ToolExitCode
{
    public const int Success = 0;
    public const int TargetFailure = 1;
    public const int Usage = 2;
    public const int GitHubFailure = 3;
    public const int AuthenticationFailure = 4;
    public const int Timeout = 124;
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
        IEnvironmentAccessor environment,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        var service = new ManifestCommandService(processRunner, environment, output, error);
        var root = new RootCommand("Workflow tooling for Aspire modular AppHosts.");
        var manifest = new Command("manifest", "Creates and consumes workflow image manifests.");
        manifest.Subcommands.Add(CreatePublishCommand(service));
        manifest.Subcommands.Add(CreateApplyCommand(service));
        root.Subcommands.Add(manifest);

        var parseResult = root.Parse(args);
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
        var resourceTags = new Option<string[]>("--resource-tag")
        {
            Description = "Per-resource tag in <module>/<resource>=<tag> form.",
            AllowMultipleArgumentsPerToken = true
        };
        var output = new Option<string?>("--output")
        {
            Description = "Manifest output path."
        };
        var githubOutput = new Option<string?>("--github-output")
        {
            Description = "GitHub step output name that receives compact manifest JSON."
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
        command.Options.Add(githubOutput);
        command.Options.Add(aspirePath);
        command.SetAction((parse, cancellationToken) => service.PublishAsync(
            parse.GetRequiredValue(appHost),
            parse.GetValue(selectors) ?? [],
            parse.GetValue(all),
            parse.GetValue(tag),
            parse.GetValue(resourceTags) ?? [],
            parse.GetValue(output),
            parse.GetValue(githubOutput),
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
        var resourceTags = new Option<string[]>("--resource-tag")
        {
            Description = "Per-resource tag in <module>/<resource>=<tag> form.",
            AllowMultipleArgumentsPerToken = true
        };
        var githubEnvironment = new Option<string?>("--github-env")
        {
            Description = "GitHub environment file path. Defaults to GITHUB_ENV."
        };
        var command = new Command("apply", "Applies image identities to subsequent GitHub Actions steps.");
        command.Options.Add(file);
        command.Options.Add(json);
        command.Options.Add(tag);
        command.Options.Add(resourceTags);
        command.Options.Add(githubEnvironment);
        command.SetAction((parse, cancellationToken) => service.ApplyAsync(
            parse.GetValue(file),
            parse.GetValue(json),
            parse.GetValue(tag),
            parse.GetValue(resourceTags) ?? [],
            parse.GetValue(githubEnvironment),
            cancellationToken));
        return command;
    }
}
