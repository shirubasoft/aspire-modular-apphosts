using Aspire.Hosting.ModularAppHosts;

namespace Shirubasoft.Aspire.ModularAppHosts.Tool;

internal static class PreviewPolicyEvaluator
{
    public static ModulePreviewPolicyEvaluation Evaluate(
        ModulePreviewManifest manifest,
        ModulePreviewConsumerPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(policy);

        manifest.Validate();
        policy.Validate();

        var policies = policy.Modules.ToDictionary(module => module.Module, StringComparer.OrdinalIgnoreCase);
        var selections = manifest.Modules.ToDictionary(module => module.Name, StringComparer.OrdinalIgnoreCase);
        var contracts = manifest.Contracts.ToDictionary(contract => contract.Module, StringComparer.OrdinalIgnoreCase);
        var images = manifest.Images.ToLookup(image => image.Module, StringComparer.OrdinalIgnoreCase);
        var evaluations = new List<ModulePreviewPolicyModuleEvaluation>(manifest.Modules.Count);

        foreach (var selection in manifest.Modules)
        {
            if (!policies.TryGetValue(selection.Name, out var modulePolicy))
            {
                throw new InvalidDataException(
                    $"Module '{selection.Name}' is not allowed by the consumer preview policy.");
            }

            if (!string.Equals(selection.Repository, modulePolicy.Repository, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Repository '{selection.Repository}' is not allowed for module '{selection.Name}'.");
            }

            contracts.TryGetValue(selection.Name, out var contract);
            ValidateContractRequest(selection.Name, contract, modulePolicy.Contract);

            var moduleImages = images[selection.Name].ToArray();
            foreach (var image in moduleImages)
            {
                ValidateImageArtifact(image, modulePolicy.Images);
            }

            foreach (var requiredImage in modulePolicy.Images.Where(image => image.Required))
            {
                if (!moduleImages.Any(image =>
                        string.Equals(image.Resource, requiredImage.Resource, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(
                            ToWireName(image.ResourceKind),
                            requiredImage.ResourceKind,
                            StringComparison.Ordinal)))
                {
                    throw new InvalidDataException(
                        $"Module '{selection.Name}' must provide an immutable image for required resource " +
                        $"'{requiredImage.Resource}' ({requiredImage.ResourceKind}).");
                }
            }

            evaluations.Add(new ModulePreviewPolicyModuleEvaluation(
                selection,
                modulePolicy,
                contract,
                moduleImages));
        }

        foreach (var contract in manifest.Contracts)
        {
            EnsureSelectedModule(contract.Module, selections, "contract request");
        }

        foreach (var image in manifest.Images)
        {
            EnsureSelectedModule(image.Module, selections, "image artifact");
        }

        var producerIsSelected = manifest.Modules.Any(module =>
            string.Equals(module.Repository, manifest.Producer.Repository, StringComparison.Ordinal) &&
            string.Equals(module.Commit, manifest.Producer.Commit, StringComparison.OrdinalIgnoreCase));
        if (!producerIsSelected)
        {
            throw new InvalidDataException(
                "The preview producer repository and commit must match one selected module.");
        }

        return new ModulePreviewPolicyEvaluation(manifest, policy, evaluations);
    }

    private static void ValidateContractRequest(
        string module,
        ModulePreviewContractRequest? request,
        ModulePreviewConsumerContractPolicy? policy)
    {
        if (request is null)
        {
            if (policy?.Required == true)
            {
                throw new InvalidDataException(
                    $"Module '{module}' must request its required policy-owned contract package.");
            }

            return;
        }

        if (policy is null || !string.Equals(request.PackageId, policy.PackageId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Contract package '{request.PackageId}' is not allowed for module '{module}'.");
        }

        PreviewPolicyValidation.ValidatePackageVersion(
            request.Version,
            $"Contract request for module '{module}'.Version");
    }

    private static void ValidateImageArtifact(
        ModulePreviewImageArtifact artifact,
        IEnumerable<ModulePreviewConsumerImagePolicy> policies)
    {
        var imagePolicy = policies.SingleOrDefault(policy =>
            string.Equals(policy.Resource, artifact.Resource, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(policy.ResourceKind, ToWireName(artifact.ResourceKind), StringComparison.Ordinal));
        if (imagePolicy is null)
        {
            throw new InvalidDataException(
                $"Image resource '{artifact.Resource}' ({artifact.ResourceKind}) is not allowed " +
                $"for module '{artifact.Module}'.");
        }

        if (!imagePolicy.Repositories.Contains(artifact.Repository, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                $"Image repository '{artifact.Repository}' is not allowed for resource " +
                $"'{artifact.Resource}' in module '{artifact.Module}'.");
        }

        PreviewPolicyValidation.ValidateImageDigest(
            artifact.Sha256,
            $"Image artifact '{artifact.Module}/{artifact.Resource}'.Sha256");
    }

    private static void EnsureSelectedModule(
        string module,
        Dictionary<string, ModulePreviewSelection> selections,
        string item)
    {
        if (!selections.ContainsKey(module))
        {
            throw new InvalidDataException(
                $"The {item} for module '{module}' does not correspond to a selected module.");
        }
    }

    private static string ToWireName(ModulePreviewResourceKind resourceKind) =>
        resourceKind switch
        {
            ModulePreviewResourceKind.Project => "project",
            ModulePreviewResourceKind.Container => "container",
            _ => throw new InvalidDataException($"Unsupported preview resource kind '{resourceKind}'.")
        };
}

internal sealed record ModulePreviewPolicyEvaluation(
    ModulePreviewManifest Manifest,
    ModulePreviewConsumerPolicy Policy,
    IReadOnlyList<ModulePreviewPolicyModuleEvaluation> Modules);

internal sealed record ModulePreviewPolicyModuleEvaluation(
    ModulePreviewSelection Selection,
    ModulePreviewConsumerModulePolicy Policy,
    ModulePreviewContractRequest? Contract,
    IReadOnlyList<ModulePreviewImageArtifact> Images);

internal static class PreviewPolicyValidation
{
    private const int MaximumNameLength = 128;

    public static void Validate(ModulePreviewProducerDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ValidateSchemaVersion(
            descriptor.SchemaVersion,
            ModulePreviewProducerDescriptor.CurrentSchemaVersion,
            "producer descriptor");
        ValidateName(descriptor.Module, "Producer descriptor.Module");

        if (descriptor.Contract is not null)
        {
            ValidatePackageId(descriptor.Contract.PackageId, "Producer descriptor.Contract.PackageId");
            if (!string.IsNullOrWhiteSpace(descriptor.Contract.Version))
            {
                ValidatePackageVersion(descriptor.Contract.Version, "Producer descriptor.Contract.Version");
            }
        }

        var images = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var image in descriptor.Images)
        {
            if (image is null)
            {
                throw new InvalidDataException("The producer descriptor cannot contain a null image.");
            }

            ValidateName(image.Resource, "Producer descriptor image.Resource");
            ValidateResourceKind(image.ResourceKind, "Producer descriptor image.ResourceKind");
            ValidateImageRepository(image.Repository, "Producer descriptor image.Repository");
            if (!images.Add(image.Resource))
            {
                throw new InvalidDataException(
                    $"The producer descriptor contains duplicate image resource '{image.Resource}'.");
            }
        }
    }

    public static void Validate(ModulePreviewConsumerPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ValidateSchemaVersion(
            policy.SchemaVersion,
            ModulePreviewConsumerPolicy.CurrentSchemaVersion,
            "consumer policy");
        if (policy.Modules.Count == 0)
        {
            throw new InvalidDataException("The consumer preview policy must allow at least one module.");
        }

        var modules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var contractVersionEnvironments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var module in policy.Modules)
        {
            if (module is null)
            {
                throw new InvalidDataException("The consumer preview policy cannot contain a null module.");
            }

            ValidateName(module.Module, "Consumer policy module.Module");
            ValidateRepositoryUrl(module.Repository, $"Consumer policy module '{module.Module}'.Repository");
            if (!modules.Add(module.Module))
            {
                throw new InvalidDataException(
                    $"The consumer preview policy contains duplicate module '{module.Module}'.");
            }

            ValidateContractPolicy(module.Module, module.Contract);
            if (module.Contract is not null &&
                !contractVersionEnvironments.Add(module.Contract.VersionEnvironment))
            {
                throw new InvalidDataException(
                    $"The consumer preview policy assigns contract version environment variable " +
                    $"'{module.Contract.VersionEnvironment}' to more than one module.");
            }

            ValidateImagePolicies(module.Module, module.Images);
        }
    }

    public static void ValidatePackageVersion(string value, string location)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 256 ||
            value.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '+')))
        {
            throw new InvalidDataException(
                $"{location} must be an exact package version containing only ASCII letters, digits, '.', '-', or '+'.");
        }
    }

    public static void ValidateImageDigest(string value, string location)
    {
        const string prefix = "sha256:";
        if (!value.StartsWith(prefix, StringComparison.Ordinal) ||
            value.Length != prefix.Length + 64 ||
            value[prefix.Length..].Any(character =>
                !(char.IsAsciiDigit(character) || character is >= 'a' and <= 'f')))
        {
            throw new InvalidDataException(
                $"{location} must be 'sha256:' followed by 64 lowercase hexadecimal characters.");
        }
    }

    private static void ValidateContractPolicy(
        string module,
        ModulePreviewConsumerContractPolicy? contract)
    {
        if (contract is null)
        {
            return;
        }

        ValidatePackageId(contract.PackageId, $"Contract policy for module '{module}'.PackageId");
        ValidateEnvironmentName(
            contract.VersionEnvironment,
            $"Contract policy for module '{module}'.VersionEnvironment");
        if (contract.SourceFallback is null)
        {
            throw new InvalidDataException(
                $"Contract policy for module '{module}' must declare sourceFallback.");
        }

        if (contract.Published is not null)
        {
            ModulePreviewValidation.ValidatePackageSource(
                contract.Published.Source,
                $"Contract policy for module '{module}'.Published.Source");
            if (contract.SourceFallback.Enabled)
            {
                throw new InvalidDataException(
                    $"Contract policy for module '{module}' cannot enable both published resolution " +
                    "and source fallback.");
            }
        }
        else if (contract.SourceFallback.Enabled)
        {
            ValidateRelativeProjectPath(
                contract.SourceFallback.Project,
                $"Contract policy for module '{module}'.SourceFallback.Project");
        }
        else
        {
            throw new InvalidDataException(
                $"Contract policy for module '{module}' must declare a published source or enable source fallback.");
        }

        if (!contract.SourceFallback.Enabled && contract.SourceFallback.Project is not null)
        {
            throw new InvalidDataException(
                $"Contract policy for module '{module}' cannot specify a source fallback project " +
                "when source fallback is disabled.");
        }

        if (contract.Published is not null && contract.AllowedPackProperties.Count > 0)
        {
            throw new InvalidDataException(
                $"Contract policy for module '{module}' cannot allow pack properties in published mode.");
        }

        var properties = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in contract.AllowedPackProperties)
        {
            ValidatePropertyName(property, $"Contract policy for module '{module}'.AllowedPackProperties");
            if (!properties.Add(property))
            {
                throw new InvalidDataException(
                    $"Contract policy for module '{module}' contains duplicate allowed pack property '{property}'.");
            }
        }
    }

    private static void ValidateImagePolicies(
        string module,
        IEnumerable<ModulePreviewConsumerImagePolicy> images)
    {
        var resources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var image in images)
        {
            if (image is null)
            {
                throw new InvalidDataException(
                    $"Consumer policy module '{module}' cannot contain a null image policy.");
            }

            ValidateName(image.Resource, $"Image policy for module '{module}'.Resource");
            ValidateResourceKind(image.ResourceKind, $"Image policy for module '{module}'.ResourceKind");
            if (!resources.Add(image.Resource))
            {
                throw new InvalidDataException(
                    $"Consumer policy module '{module}' contains duplicate image resource " +
                    $"'{image.Resource}'.");
            }

            if (image.Repositories.Count == 0)
            {
                throw new InvalidDataException(
                    $"Image policy for module '{module}', resource '{image.Resource}' must allow a repository.");
            }

            var repositories = new HashSet<string>(StringComparer.Ordinal);
            foreach (var repository in image.Repositories)
            {
                ValidateImageRepository(repository, $"Image policy for module '{module}'.Repositories");
                if (!repositories.Add(repository))
                {
                    throw new InvalidDataException(
                        $"Image policy for module '{module}', resource '{image.Resource}' contains duplicate " +
                        $"repository '{repository}'.");
                }
            }
        }
    }

    private static void ValidateSchemaVersion(int actual, int expected, string document)
    {
        if (actual != expected)
        {
            throw new InvalidDataException(
                $"Unsupported {document} schema version '{actual}'. Expected '{expected}'.");
        }
    }

    private static void ValidateName(string value, string location)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > MaximumNameLength ||
            !char.IsAsciiLetterOrDigit(value[0]) ||
            !char.IsAsciiLetterOrDigit(value[^1]) ||
            value.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
        {
            throw new InvalidDataException(
                $"{location} must contain only ASCII letters, digits, '.', '_', or '-', " +
                "and must start and end with a letter or digit.");
        }
    }

    private static void ValidatePackageId(string value, string location)
    {
        ValidateName(value, location);
        if (value.Length > 100)
        {
            throw new InvalidDataException($"{location} cannot exceed 100 characters.");
        }
    }

    private static void ValidateRepositoryUrl(string value, string location)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            string.IsNullOrWhiteSpace(uri.AbsolutePath.Trim('/')) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            !value.EndsWith(".git", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"{location} must be a credential-free canonical HTTPS repository URL ending in '.git'.");
        }
    }

    private static void ValidateImageRepository(string value, string location)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 255 ||
            value.Contains("://", StringComparison.Ordinal) ||
            value.Contains('@', StringComparison.Ordinal) ||
            value.Contains('#', StringComparison.Ordinal) ||
            value.Contains('?', StringComparison.Ordinal) ||
            value.Any(character => char.IsWhiteSpace(character) || char.IsUpper(character)))
        {
            throw new InvalidDataException(
                $"{location} must be a lowercase OCI repository without a scheme, tag, or digest.");
        }

        var slash = value.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || slash == value.Length - 1)
        {
            throw new InvalidDataException(
                $"{location} must include a registry host and repository path.");
        }

        var registry = value[..slash];
        var path = value[(slash + 1)..];
        if (!IsRegistry(registry) || path.Split('/').Any(segment => !IsImagePathSegment(segment)))
        {
            throw new InvalidDataException($"{location} is not a valid OCI repository.");
        }
    }

    private static bool IsRegistry(string value)
    {
        var colon = value.LastIndexOf(':');
        var host = colon < 0 ? value : value[..colon];
        var port = colon < 0 ? null : value[(colon + 1)..];
        return host.Length > 0 &&
            host.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-') &&
            host.Split('.').All(segment =>
                segment.Length > 0 && segment[0] != '-' && segment[^1] != '-') &&
            (port is null || (port.Length > 0 && port.All(char.IsAsciiDigit)));
    }

    private static bool IsImagePathSegment(string segment) =>
        segment.Length > 0 &&
        char.IsAsciiLetterOrDigit(segment[0]) &&
        char.IsAsciiLetterOrDigit(segment[^1]) &&
        segment.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    private static void ValidateResourceKind(string value, string location)
    {
        if (value is not ("project" or "container"))
        {
            throw new InvalidDataException($"{location} must be 'project' or 'container'.");
        }
    }

    private static void ValidateEnvironmentName(string value, string location)
    {
        if (string.IsNullOrEmpty(value) ||
            !(char.IsAsciiLetter(value[0]) || value[0] == '_') ||
            value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '_')))
        {
            throw new InvalidDataException($"{location} must be a valid environment variable name.");
        }

        if (value.Equals("ModulePreview__Resolution", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("ModulePreview__PackageFeed", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("GITHUB_", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("RUNNER_", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"{location} uses reserved environment variable name '{value}'.");
        }
    }

    private static void ValidatePropertyName(string value, string location)
    {
        if (string.IsNullOrEmpty(value) ||
            !(char.IsAsciiLetter(value[0]) || value[0] == '_') ||
            value.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '-')))
        {
            throw new InvalidDataException($"{location} contains an invalid MSBuild property name '{value}'.");
        }
    }

    private static void ValidateRelativeProjectPath(string? value, string location)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            Path.IsPathRooted(value) ||
            value.Contains('\\', StringComparison.Ordinal) ||
            value.Any(char.IsControl) ||
            !value.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"{location} must be a repository-relative '.csproj' path using '/' separators.");
        }

        var segments = value.Split('/');
        if (segments.Any(segment =>
                segment.Length == 0 ||
                segment is "." or ".." ||
                segment.Any(character =>
                    !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'))))
        {
            throw new InvalidDataException(
                $"{location} must not escape the repository or contain unsupported path characters.");
        }
    }
}
