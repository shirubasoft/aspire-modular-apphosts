using Aspire.Hosting.ModularAppHosts;

namespace Shirubasoft.Aspire.ModularAppHosts.Tool;

internal static partial class PreviewTool
{
    private static async Task<int> DescriptorAsync(
        string[] arguments,
        CancellationToken cancellationToken)
    {
        if (arguments.Length < 2 ||
            !string.Equals(arguments[0], "generate", StringComparison.Ordinal) ||
            !string.Equals(arguments[1], "producer", StringComparison.Ordinal))
        {
            throw new PreviewToolException(
                "Expected the 'preview descriptor generate producer' command.");
        }

        return await GenerateProducerDescriptorAsync(
            arguments.Skip(2).ToArray(),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> GenerateProducerDescriptorAsync(
        string[] arguments,
        CancellationToken cancellationToken)
    {
        var options = CommandOptions.Parse(arguments, ["resource"], ["check", "force"]);
        var workingDirectory = Path.GetFullPath(
            options.Optional("working-directory") ?? Environment.CurrentDirectory);
        var appHost = Path.GetFullPath(options.Required("apphost"), workingDirectory);
        var module = options.Required("module");
        var output = Path.GetFullPath(options.Required("output"), workingDirectory);
        var aspireExecutable = options.Optional("aspire-executable") ?? "aspire";
        var contractVersion = options.Optional("contract-version");
        var configuredArtifactsDirectory = options.Optional("artifacts-directory") is { } artifacts
            ? Path.GetFullPath(artifacts, workingDirectory)
            : null;
        var check = options.Flag("check");
        var force = options.Flag("force");
        options.EnsureOnly(
            "apphost",
            "module",
            "output",
            "working-directory",
            "aspire-executable",
            "artifacts-directory",
            "contract-version",
            "resource",
            "check",
            "force");

        if (check && force)
        {
            throw new PreviewToolException("--check cannot be combined with --force.");
        }

        if (check && !File.Exists(output))
        {
            throw new PreviewToolException(
                $"Producer descriptor '{output}' does not exist and cannot be checked.");
        }

        if (!check && !force && File.Exists(output))
        {
            throw new PreviewToolException(
                $"Producer descriptor '{output}' already exists. Pass --force to replace it.");
        }

        if (contractVersion is not null)
        {
            PreviewPolicyValidation.ValidatePackageVersion(contractVersion, "--contract-version");
        }

        var ownsArtifactsDirectory = configuredArtifactsDirectory is null;
        var artifactsDirectory = configuredArtifactsDirectory ?? Path.Combine(
            Path.GetTempPath(),
            $"modular-apphosts-descriptor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(artifactsDirectory);
        try
        {
            var describeResult = await RunCommandAsync(
                aspireExecutable,
                [
                    "do", "describe-images",
                    "--apphost", appHost,
                    "--output-path", artifactsDirectory,
                    "--non-interactive"
                ],
                workingDirectory,
                standardInput: null,
                cancellationToken,
                applyTimeout: false).ConfigureAwait(false);
            EnsureSuccess(describeResult, "Aspire image description");

            var description = await ModuleImageDescriptionDocument.LoadAsync(
                Path.Combine(artifactsDirectory, "module-images.json"),
                cancellationToken).ConfigureAwait(false);
            var descriptor = CreateProducerDescriptor(
                description,
                module,
                options.Many("resource"),
                contractVersion);

            if (check)
            {
                var existing = await ModulePreviewProducerDescriptor.LoadAsync(
                    output,
                    cancellationToken).ConfigureAwait(false);
                if (!ProducerDescriptorsEqual(existing, descriptor))
                {
                    throw new PreviewToolException(
                        $"Producer descriptor '{output}' does not match module '{module}' in the AppHost. " +
                        "Regenerate it with 'preview descriptor generate producer --force'.");
                }

                await Console.Out.WriteLineAsync("verified").ConfigureAwait(false);
                return 0;
            }

            await descriptor.SaveAsync(output, cancellationToken).ConfigureAwait(false);
            await Console.Out.WriteLineAsync(output).ConfigureAwait(false);
            return 0;
        }
        finally
        {
            if (ownsArtifactsDirectory && Directory.Exists(artifactsDirectory))
            {
                Directory.Delete(artifactsDirectory, recursive: true);
            }
        }
    }

    private static ModulePreviewProducerDescriptor CreateProducerDescriptor(
        ModuleImageDescriptionDocument description,
        string module,
        List<string> resourceSelectors,
        string? contractVersion)
    {
        var moduleDescription = description.Modules.SingleOrDefault(candidate =>
            string.Equals(candidate.Name, module, StringComparison.OrdinalIgnoreCase))
            ?? throw new PreviewToolException(
                $"Module '{module}' is not present in the effective AppHost model.");
        var moduleImages = description.Images
            .Where(image =>
                string.Equals(image.Module, module, StringComparison.OrdinalIgnoreCase) &&
                image.Build is not null &&
                image.PushReference is not null)
            .ToArray();
        if (resourceSelectors.Count > 0)
        {
            var unknown = resourceSelectors
                .Where(selector => !moduleImages.Any(image => ResourceMatches(image, selector)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (unknown.Length > 0)
            {
                var available = moduleImages
                    .SelectMany(image => new[] { image.Resource, image.EffectiveResource })
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase);
                throw new PreviewToolException(
                    $"The following resources are not image publishers in module '{module}': " +
                    $"{string.Join(", ", unknown)}. Available resources: {string.Join(", ", available)}.");
            }

            moduleImages = moduleImages
                .Where(image => resourceSelectors.Any(selector => ResourceMatches(image, selector)))
                .ToArray();
        }

        var duplicate = moduleImages
            .GroupBy(image => image.Resource, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new PreviewToolException(
                $"Module '{module}' describes resource '{duplicate.Key}' more than once. " +
                "Select one effective resource explicitly.");
        }

        if (moduleImages.Length == 0 && moduleDescription.ContractPackageId is null)
        {
            throw new PreviewToolException(
                $"Module '{module}' declares neither a contract package nor an image publisher with a push target.");
        }

        if (contractVersion is not null && moduleDescription.ContractPackageId is null)
        {
            throw new PreviewToolException(
                $"--contract-version cannot be used because module '{module}' does not declare a contract package ID.");
        }

        var descriptor = new ModulePreviewProducerDescriptor
        {
            Schema = ModulePreviewProducerDescriptor.SchemaUri,
            Module = moduleDescription.Name,
            Contract = moduleDescription.ContractPackageId is null
                ? null
                : new ModulePreviewProducerContractDescriptor
                {
                    PackageId = moduleDescription.ContractPackageId,
                    Version = contractVersion
                }
        };
        foreach (var image in moduleImages.OrderBy(image => image.Resource, StringComparer.OrdinalIgnoreCase))
        {
            descriptor.Images.Add(new ModulePreviewProducerImageDescriptor
            {
                Resource = image.Resource,
                ResourceKind = ToWireName(image.ResourceKind),
                Repository = GetImageRepository(image.PushReference!),
                Required = true
            });
        }

        descriptor.Validate();
        return descriptor;
    }

    private static bool ResourceMatches(ModuleImageDescription image, string selector) =>
        string.Equals(image.Resource, selector, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(image.EffectiveResource, selector, StringComparison.OrdinalIgnoreCase);

    private static string ToWireName(ModulePreviewResourceKind kind) => kind switch
    {
        ModulePreviewResourceKind.Project => "project",
        ModulePreviewResourceKind.Container => "container",
        _ => throw new PreviewToolException($"Unsupported module resource kind '{kind}'.")
    };

    private static bool ProducerDescriptorsEqual(
        ModulePreviewProducerDescriptor left,
        ModulePreviewProducerDescriptor right)
    {
        if (!string.Equals(left.Schema, right.Schema, StringComparison.Ordinal) ||
            left.SchemaVersion != right.SchemaVersion ||
            !string.Equals(left.Module, right.Module, StringComparison.Ordinal) ||
            !string.Equals(left.Contract?.PackageId, right.Contract?.PackageId, StringComparison.Ordinal) ||
            !string.Equals(left.Contract?.Version, right.Contract?.Version, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var leftImages = left.Images.OrderBy(image => image.Resource, StringComparer.OrdinalIgnoreCase).ToArray();
        var rightImages = right.Images.OrderBy(image => image.Resource, StringComparer.OrdinalIgnoreCase).ToArray();
        return leftImages.Length == rightImages.Length && leftImages.Zip(rightImages).All(pair =>
            string.Equals(pair.First.Resource, pair.Second.Resource, StringComparison.Ordinal) &&
            string.Equals(pair.First.ResourceKind, pair.Second.ResourceKind, StringComparison.Ordinal) &&
            string.Equals(pair.First.Repository, pair.Second.Repository, StringComparison.Ordinal) &&
            pair.First.Required == pair.Second.Required);
    }
}
