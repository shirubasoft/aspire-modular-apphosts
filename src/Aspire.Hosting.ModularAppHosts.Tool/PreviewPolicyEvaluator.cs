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

            if (!RepositoryIdentityComparer.Instance.Equals(selection.Repository, modulePolicy.Repository))
            {
                throw new InvalidDataException(
                    $"Repository '{selection.Repository}' is not allowed for module '{selection.Name}'. " +
                    $"Expected repository '{modulePolicy.Repository}'.");
            }

            var producerOwnsModule = ProducerMatchesSelection(manifest.Producer, selection);
            contracts.TryGetValue(selection.Name, out var contract);
            ValidateContractRequest(
                selection,
                manifest.Producer,
                producerOwnsModule,
                contract,
                modulePolicy.Contract);

            var moduleImages = images[selection.Name].ToArray();
            foreach (var image in moduleImages)
            {
                ValidateImageArtifact(image, modulePolicy.Images);
                ValidateImageProducer(
                    image,
                    manifest.Producer,
                    selection,
                    producerOwnsModule,
                    modulePolicy.Images);
            }

            foreach (var requiredImage in modulePolicy.Images.Where(image =>
                IsRequiredFromProducer(image, manifest.Producer, producerOwnsModule)))
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

        if (manifest.Contracts.Count == 0 && manifest.Images.Count == 0)
        {
            throw new InvalidDataException(
                "A policy-verified preview must offer at least one contract package or immutable image.");
        }

        return new ModulePreviewPolicyEvaluation(manifest, policy, evaluations);
    }

    private static void ValidateContractRequest(
        ModulePreviewSelection selection,
        ModulePreviewProducer producer,
        bool producerOwnsModule,
        ModulePreviewContractRequest? request,
        ModulePreviewConsumerContractPolicy? policy)
    {
        if (request is null)
        {
            if (producerOwnsModule && policy?.Required == true)
            {
                throw new InvalidDataException(
                    $"Module '{selection.Name}' must request its required policy-owned contract package.");
            }

            return;
        }

        if (!producerOwnsModule)
        {
            throw new InvalidDataException(
                $"Preview producer repository and commit " +
                $"('{producer.Repository}' at '{producer.Commit}') do not match module '{selection.Name}' " +
                $"('{selection.Repository}' at '{selection.Commit}'), " +
                "so the producer cannot request its contract package.");
        }

        if (policy is null || !string.Equals(request.PackageId, policy.PackageId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Contract package '{request.PackageId}' is not allowed for module '{selection.Name}'.");
        }

        PreviewPolicyValidation.ValidatePackageVersion(
            request.Version,
            $"Contract request for module '{selection.Name}'.Version");
        ValidateContractDependencies(selection.Name, request.Dependencies, policy.Dependencies);
    }

    private static void ValidateContractDependencies(
        string module,
        IEnumerable<ModulePreviewContractDependency> requestedDependencies,
        IEnumerable<ModulePreviewContractDependency> policyDependencies)
    {
        var requested = requestedDependencies.ToDictionary(
            dependency => dependency.PackageId,
            StringComparer.OrdinalIgnoreCase);
        var expected = policyDependencies.ToDictionary(
            dependency => dependency.PackageId,
            StringComparer.OrdinalIgnoreCase);

        foreach (var dependency in expected.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!requested.TryGetValue(dependency.Key, out var actual))
            {
                throw new InvalidDataException(
                    $"Contract for module '{module}' must attest direct dependency " +
                    $"'{dependency.Value.PackageId}' at exact version '{dependency.Value.Version}', but it is missing.");
            }

            if (!string.Equals(actual.Version, dependency.Value.Version, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Contract dependency '{actual.PackageId}' for module '{module}' resolved exact version " +
                    $"'{actual.Version}', but consumer policy requires '{dependency.Value.Version}'.");
            }
        }

        var undeclared = requested
            .Where(item => !expected.ContainsKey(item.Key))
            .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(item => $"'{item.Value.PackageId}' at '{item.Value.Version}'")
            .ToArray();
        if (undeclared.Length > 0)
        {
            throw new InvalidDataException(
                $"Contract for module '{module}' attests dependencies not allowed by consumer policy: " +
                $"{string.Join(", ", undeclared)}.");
        }
    }

    private static void ValidateImageProducer(
        ModulePreviewImageArtifact artifact,
        ModulePreviewProducer producer,
        ModulePreviewSelection selection,
        bool producerOwnsModule,
        IEnumerable<ModulePreviewConsumerImagePolicy> policies)
    {
        if (producerOwnsModule)
        {
            return;
        }

        var imagePolicy = FindImagePolicy(artifact, policies);
        if (ProducerIsAuthorized(imagePolicy, producer))
        {
            return;
        }

        var expectedRepositories = new[] { selection.Repository }
            .Concat(imagePolicy.ProducerRepositories)
            .Select(repository => $"'{repository}'");
        throw new InvalidDataException(
            $"Preview producer repository '{producer.Repository}' is not allowed for image " +
            $"resource '{artifact.Resource}' in module '{artifact.Module}'. Expected one of: " +
            $"{string.Join(", ", expectedRepositories)}.");
    }

    private static bool IsRequiredFromProducer(
        ModulePreviewConsumerImagePolicy imagePolicy,
        ModulePreviewProducer producer,
        bool producerOwnsModule) =>
        imagePolicy.Required && (producerOwnsModule || ProducerIsAuthorized(imagePolicy, producer));

    private static bool ProducerIsAuthorized(
        ModulePreviewConsumerImagePolicy imagePolicy,
        ModulePreviewProducer producer) =>
        imagePolicy.ProducerRepositories.Contains(
            producer.Repository,
            RepositoryIdentityComparer.Instance);

    private static void ValidateImageArtifact(
        ModulePreviewImageArtifact artifact,
        IEnumerable<ModulePreviewConsumerImagePolicy> policies)
    {
        var imagePolicy = FindImagePolicy(artifact, policies);

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

    private static ModulePreviewConsumerImagePolicy FindImagePolicy(
        ModulePreviewImageArtifact artifact,
        IEnumerable<ModulePreviewConsumerImagePolicy> policies) =>
        policies.SingleOrDefault(policy =>
            string.Equals(policy.Resource, artifact.Resource, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(policy.ResourceKind, ToWireName(artifact.ResourceKind), StringComparison.Ordinal))
        ?? throw new InvalidDataException(
            $"Image resource '{artifact.Resource}' ({artifact.ResourceKind}) is not allowed " +
            $"for module '{artifact.Module}'.");

    private static bool ProducerMatchesSelection(
        ModulePreviewProducer producer,
        ModulePreviewSelection selection) =>
        RepositoryIdentityComparer.Instance.Equals(selection.Repository, producer.Repository) &&
        string.Equals(selection.Commit, producer.Commit, StringComparison.OrdinalIgnoreCase);

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
        if (descriptor.Schema is not null &&
            !string.Equals(descriptor.Schema, ModulePreviewProducerDescriptor.SchemaUri, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Producer descriptor.$schema must be '{ModulePreviewProducerDescriptor.SchemaUri}'.");
        }

        if (descriptor.Contract is not null)
        {
            ValidatePackageId(descriptor.Contract.PackageId, "Producer descriptor.Contract.PackageId");
            if (descriptor.Contract.Version is not null)
            {
                ValidatePackageVersion(descriptor.Contract.Version, "Producer descriptor.Contract.Version");
            }
            ModulePreviewValidation.ValidateContractDependencies(
                descriptor.Contract.Dependencies,
                "Producer descriptor.Contract.Dependencies");
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
        ModulePreviewValidation.ValidateContractDependencies(
            contract.Dependencies,
            $"Contract policy for module '{module}'.Dependencies");
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

            var producerRepositories = new HashSet<string>(RepositoryIdentityComparer.Instance);
            foreach (var repository in image.ProducerRepositories)
            {
                ValidateRepositoryUrl(
                    repository,
                    $"Image policy for module '{module}'.ProducerRepositories");
                if (!producerRepositories.Add(repository))
                {
                    throw new InvalidDataException(
                        $"Image policy for module '{module}', resource '{image.Resource}' contains duplicate " +
                        $"producer repository '{repository}'.");
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
