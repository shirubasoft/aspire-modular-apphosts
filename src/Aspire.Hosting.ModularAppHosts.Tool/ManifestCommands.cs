using ActionsToolkit.Core.Services;
using Aspire.Hosting;
using Microsoft.Extensions.Configuration;

namespace Shirubasoft.Aspire.ModularAppHosts.Tool;

internal sealed class ManifestCommandService(
    IProcessRunner processRunner,
    IConfiguration configuration,
    ICoreService githubActions,
    string workingDirectory,
    TextWriter output,
    TextWriter error)
{
    public async Task<int> ApplyAsync(
        string? file,
        string? json,
        string? tag,
        string resourceTags,
        string[] command,
        bool hasCommandSeparator,
        CancellationToken cancellationToken)
    {
        if ((file is null) == (json is null))
        {
            throw new ToolUsageException("Specify exactly one of --file or --json.");
        }

        if (!hasCommandSeparator || command.Length == 0 || string.IsNullOrWhiteSpace(command[0]))
        {
            throw new ToolUsageException("Specify a command to run after '--'.");
        }

        var document = file is not null
            ? await ModuleImageManifestDocument.LoadAsync(
                Path.GetFullPath(file, workingDirectory),
                cancellationToken).ConfigureAwait(false)
            : ModuleImageManifestDocument.Parse(json!);
        new ManifestTagOverrides(tag, resourceTags).Apply(document);
        var environment = ModuleImageWorkflowConfiguration.Create(document)
            .ToDictionary(
                pair => pair.Key.Replace(":", "__", StringComparison.Ordinal),
                pair => (string?)pair.Value,
                StringComparer.Ordinal);
        var result = await processRunner.RunAsync(
            new ProcessInvocation(
                command[0],
                command[1..],
                workingDirectory,
                environment,
                ProcessOutputMode.Stream),
            cancellationToken).ConfigureAwait(false);
        return result.ExitCode;
    }

    public async Task<int> PublishAsync(
        string appHost,
        string[] selectors,
        bool all,
        string? tag,
        string resourceTags,
        string? outputPath,
        string aspirePath,
        CancellationToken cancellationToken)
    {
        string? temporaryPath = null;
        try
        {
            ValidatePublishArguments(appHost, selectors, all, aspirePath);
            var appHostPath = Path.GetFullPath(appHost, workingDirectory);
            if (!File.Exists(appHostPath) && !Directory.Exists(appHostPath))
            {
                throw new ToolUsageException($"AppHost path '{appHostPath}' does not exist.");
            }

            temporaryPath = Path.Combine(Path.GetTempPath(), $"modular-apphosts-{Guid.NewGuid():N}");
            var descriptionPath = Path.Combine(temporaryPath, "description");
            var manifestPath = Path.Combine(temporaryPath, "manifest");
            Directory.CreateDirectory(descriptionPath);

            var describe = await RunAspireAsync(
                aspirePath,
                appHostPath,
                "describe-images",
                descriptionPath,
                null,
                cancellationToken).ConfigureAwait(false);
            if (!describe.IsSuccess)
            {
                return await WriteAspireFailureAsync(describe, "Aspire image discovery").ConfigureAwait(false);
            }

            var descriptions = await ModuleImageDescriptionDocument.LoadAsync(
                Path.Combine(descriptionPath, "module-images.json"),
                cancellationToken).ConfigureAwait(false);
            var publishable = descriptions.Images
                .Where(image => image.Build is not null && image.Push is not null)
                .ToArray();
            var selected = SelectImages(publishable, selectors, all);
            var overrides = new ManifestTagOverrides(tag, resourceTags);
            var producerEnvironment = new Dictionary<string, string?>(
                overrides.CreateProducerEnvironment(selected) ??
                    new Dictionary<string, string?>(),
                StringComparer.Ordinal);
            var effectiveSelectors = selected
                .Select(image => image.EffectiveResource)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var selectionPrefix = ModuleImageWorkflowConfiguration
                .SelectionConfigurationSectionName
                .Replace(":", "__", StringComparison.Ordinal);
            for (var index = 0; index < effectiveSelectors.Length; index++)
            {
                producerEnvironment[$"{selectionPrefix}__{index}"] = effectiveSelectors[index];
            }

            var publish = await RunAspireAsync(
                aspirePath,
                appHostPath,
                "workflow-images",
                manifestPath,
                producerEnvironment,
                cancellationToken).ConfigureAwait(false);
            if (!publish.IsSuccess)
            {
                return await WriteAspireFailureAsync(publish, "Aspire workflow image publish")
                    .ConfigureAwait(false);
            }

            var document = await ModuleImageManifestDocument.LoadAsync(
                Path.Combine(manifestPath, ModuleImageManifestDocument.DefaultFileName),
                cancellationToken).ConfigureAwait(false);
            var destination = Path.GetFullPath(
                outputPath ?? "module-image-manifest.json",
                workingDirectory);
            await document.SaveAsync(destination, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(configuration["GITHUB_OUTPUT"]))
            {
                await githubActions.SetOutputAsync("manifest", document.ToJson()).ConfigureAwait(false);
                await githubActions.SetOutputAsync("manifest-path", destination).ConfigureAwait(false);
            }

            await output.WriteLineAsync(destination).ConfigureAwait(false);
            return ToolExitCode.Success;
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    Directory.Delete(temporaryPath, recursive: true);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private async Task<ProcessExecutionResult> RunAspireAsync(
        string aspirePath,
        string appHost,
        string step,
        string? outputPath,
        IReadOnlyDictionary<string, string?>? environmentVariables,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "do",
            step,
            "--apphost",
            appHost
        };
        if (outputPath is not null)
        {
            arguments.Add("--output-path");
            arguments.Add(outputPath);
        }

        arguments.Add("--non-interactive");
        return await processRunner.RunAsync(
            new ProcessInvocation(
                aspirePath,
                arguments,
                workingDirectory,
                environmentVariables,
                ProcessOutputMode.Stream),
            cancellationToken).ConfigureAwait(false);
    }

    private static ModuleImageDescription[] SelectImages(
        ModuleImageDescription[] publishable,
        string[] selectors,
        bool all)
    {
        try
        {
            if (all && publishable.Length == 0)
            {
                throw new ToolUsageException("The AppHost does not expose any publishable module images.");
            }

            var selection = all ? ModuleImageSelection.All : new ModuleImageSelection(selectors);
            return selection.ResolveDescriptions(publishable, "publishable module images").ToArray();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw new ToolUsageException(exception.Message, exception);
        }
    }

    private async Task<int> WriteAspireFailureAsync(
        ProcessExecutionResult result,
        string operation)
    {
        await error.WriteLineAsync($"{operation} failed with exit code {result.ExitCode}.")
            .ConfigureAwait(false);
        return ToolExitCode.Failure;
    }

    private static void ValidatePublishArguments(
        string appHost,
        string[] selectors,
        bool all,
        string aspirePath)
    {
        if (string.IsNullOrWhiteSpace(appHost))
        {
            throw new ToolUsageException("--apphost cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(aspirePath))
        {
            throw new ToolUsageException("--aspire-path cannot be empty.");
        }

        if (all == (selectors.Length > 0))
        {
            throw new ToolUsageException("Specify one or more --selector values or --all, but not both.");
        }
    }
}
