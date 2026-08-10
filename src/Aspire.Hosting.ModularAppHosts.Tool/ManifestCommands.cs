using ActionsToolkit.Core.Services;
using Aspire.Hosting.ModularAppHosts;
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
        IReadOnlyList<string> resourceTags,
        CancellationToken cancellationToken)
    {
        if ((file is null) == (json is null))
        {
            throw new ToolUsageException("Specify exactly one of --file or --json.");
        }

        var document = file is not null
            ? await ModuleImageManifestDocument.LoadAsync(
                Path.GetFullPath(file, workingDirectory),
                cancellationToken).ConfigureAwait(false)
            : ModuleImageManifestDocument.Parse(json!);
        new ManifestTagOverrides(tag, resourceTags).Apply(document);
        if (string.IsNullOrWhiteSpace(configuration["GITHUB_ENV"]))
        {
            throw new ToolUsageException(
                "GITHUB_ENV is not configured. Run this command from a GitHub Actions step.");
        }

        foreach (var (name, value) in WorkflowImageEnvironment.Create(document))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await githubActions.ExportVariableAsync(name, value).ConfigureAwait(false);
        }

        await output.WriteLineAsync(
            $"Applied {document.Images.Count} workflow image override(s) to subsequent steps.")
            .ConfigureAwait(false);
        return ToolExitCode.Success;
    }

    public async Task<int> PublishAsync(
        string appHost,
        string[] selectors,
        bool all,
        string? tag,
        IReadOnlyList<string> resourceTags,
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
                [],
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
            var producerEnvironment = overrides.CreateProducerEnvironment(selected);
            var effectiveSelectors = selected
                .Select(image => image.EffectiveResource)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var publish = await RunAspireAsync(
                aspirePath,
                appHostPath,
                "workflow-images",
                manifestPath,
                effectiveSelectors,
                producerEnvironment,
                cancellationToken).ConfigureAwait(false);
            if (!publish.IsSuccess)
            {
                return await WriteAspireFailureAsync(publish, "Aspire workflow image publish")
                    .ConfigureAwait(false);
            }

            var document = await ModuleImageManifestDocument.LoadAsync(
                Path.Combine(manifestPath, ModuleImageManifestPipeline.FileName),
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
        string[] selectors,
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
        if (selectors.Length > 0)
        {
            arguments.Add("--");
            arguments.AddRange(selectors);
        }

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
        if (all)
        {
            if (publishable.Length == 0)
            {
                throw new ToolUsageException("The AppHost does not expose any publishable module images.");
            }

            return publishable;
        }

        var selected = new HashSet<ModuleImageDescription>();
        var unknown = new List<string>();
        foreach (var selector in selectors.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var normalized = selector;
            var moduleOnly = selector.StartsWith("module:", StringComparison.OrdinalIgnoreCase);
            var resourceOnly = selector.StartsWith("resource:", StringComparison.OrdinalIgnoreCase);
            if (moduleOnly || resourceOnly)
            {
                normalized = selector[(selector.IndexOf(':', StringComparison.Ordinal) + 1)..];
            }

            var moduleMatches = resourceOnly
                ? []
                : publishable.Where(image =>
                    string.Equals(image.Module, normalized, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (moduleMatches.Length > 0)
            {
                selected.UnionWith(moduleMatches);
                continue;
            }

            var identitySeparator = normalized.IndexOf('/', StringComparison.Ordinal);
            if (!moduleOnly && identitySeparator > 0 && identitySeparator < normalized.Length - 1)
            {
                var moduleName = normalized[..identitySeparator];
                var resourceName = normalized[(identitySeparator + 1)..];
                var identityMatches = publishable.Where(image =>
                    string.Equals(image.Module, moduleName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(image.Resource, resourceName, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (identityMatches.Length > 0)
                {
                    selected.UnionWith(identityMatches);
                }
                else
                {
                    unknown.Add(selector);
                }

                continue;
            }

            var resourceMatches = moduleOnly
                ? []
                : publishable.Where(image =>
                    string.Equals(image.Resource, normalized, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(image.EffectiveResource, normalized, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (resourceMatches.Length > 1)
            {
                var identities = resourceMatches
                    .Select(image => $"{image.Module}/{image.Resource}")
                    .Order(StringComparer.OrdinalIgnoreCase);
                throw new ToolUsageException(
                    $"Image selector '{selector}' is ambiguous. Use one of: {string.Join(", ", identities)}.");
            }

            if (resourceMatches.Length > 0)
            {
                selected.UnionWith(resourceMatches);
            }
            else
            {
                unknown.Add(selector);
            }
        }

        if (unknown.Count > 0)
        {
            var availableModules = publishable
                .Select(image => image.Module)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase);
            var availableResources = publishable
                .SelectMany(image => new[] { image.Resource, image.EffectiveResource })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase);
            throw new ToolUsageException(
                $"Unknown image selectors: {string.Join(", ", unknown)}. " +
                $"Available modules: {string.Join(", ", availableModules)}. " +
                $"Available resources: {string.Join(", ", availableResources)}.");
        }

        return selected
            .OrderBy(image => image.Module, StringComparer.Ordinal)
            .ThenBy(image => image.Resource, StringComparer.Ordinal)
            .ToArray();
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
