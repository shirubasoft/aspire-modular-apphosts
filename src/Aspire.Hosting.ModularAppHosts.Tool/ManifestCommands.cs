using Aspire.Hosting.ModularAppHosts;
using System.ComponentModel;
using System.Text.Json;

namespace Shirubasoft.Aspire.ModularAppHosts.Tool;

internal sealed class ManifestCommandService(
    IProcessRunner processRunner,
    IEnvironmentAccessor environment,
    TextWriter output,
    TextWriter error)
{
    public async Task<int> ApplyAsync(
        string? file,
        string? json,
        string? tag,
        IReadOnlyList<string> resourceTags,
        string? githubEnvironmentPath,
        CancellationToken cancellationToken)
    {
        try
        {
            if ((file is null) == (json is null))
            {
                throw new ToolUsageException("Specify exactly one of --file or --json.");
            }

            var document = file is not null
                ? await ModuleImageManifestDocument.LoadAsync(
                    Path.GetFullPath(file, environment.CurrentDirectory),
                    cancellationToken).ConfigureAwait(false)
                : ModuleImageManifestDocument.Parse(json!);
            new ManifestTagOverrides(tag, resourceTags).Apply(document);
            var githubEnvironment = githubEnvironmentPath ?? environment.GetEnvironmentVariable("GITHUB_ENV");
            if (string.IsNullOrWhiteSpace(githubEnvironment))
            {
                throw new ToolUsageException(
                    "Set GITHUB_ENV or pass --github-env so the overrides can be applied to subsequent steps.");
            }

            await GitHubFileWriter.AppendAsync(
                githubEnvironment,
                WorkflowImageEnvironment.Create(document),
                cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync(
                $"Applied {document.Images.Count} workflow image override(s) to '{Path.GetFullPath(githubEnvironment)}'.")
                .ConfigureAwait(false);
            return ToolExitCode.Success;
        }
        catch (OperationCanceledException)
        {
            return ToolExitCode.Interrupted;
        }
        catch (Exception exception) when (IsLocalFailure(exception))
        {
            await error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return ToolExitCode.Usage;
        }
    }

    public async Task<int> PublishAsync(
        string appHost,
        string[] selectors,
        bool all,
        string? tag,
        IReadOnlyList<string> resourceTags,
        string? outputPath,
        string? githubOutputName,
        string aspirePath,
        CancellationToken cancellationToken)
    {
        string? temporaryPath = null;
        try
        {
            ValidatePublishArguments(appHost, selectors, all, aspirePath);
            var appHostPath = Path.GetFullPath(appHost, environment.CurrentDirectory);
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
                environment.CurrentDirectory);
            await document.SaveAsync(destination, cancellationToken).ConfigureAwait(false);
            if (githubOutputName is not null)
            {
                var githubOutput = environment.GetEnvironmentVariable("GITHUB_OUTPUT");
                if (string.IsNullOrWhiteSpace(githubOutput))
                {
                    throw new ToolUsageException(
                        "Set GITHUB_OUTPUT when --github-output is requested.");
                }

                await GitHubFileWriter.AppendAsync(
                    githubOutput,
                    [
                        new(githubOutputName, document.ToJson()),
                        new("manifest-path", destination)
                    ],
                    cancellationToken).ConfigureAwait(false);
            }

            await output.WriteLineAsync(destination).ConfigureAwait(false);
            return ToolExitCode.Success;
        }
        catch (OperationCanceledException)
        {
            return ToolExitCode.Interrupted;
        }
        catch (Exception exception) when (IsLocalFailure(exception))
        {
            await error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return ToolExitCode.Usage;
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
                environment.CurrentDirectory,
                environmentVariables,
                CaptureOutput: false),
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
        ArgumentException.ThrowIfNullOrWhiteSpace(appHost);
        ArgumentException.ThrowIfNullOrWhiteSpace(aspirePath);
        if (all == (selectors.Length > 0))
        {
            throw new ToolUsageException("Specify one or more --selector values or --all, but not both.");
        }
    }

    private static bool IsLocalFailure(Exception exception) =>
        exception is ToolUsageException or
            ArgumentException or
            InvalidDataException or
            IOException or
            JsonException or
            Win32Exception or
            UnauthorizedAccessException;
}
