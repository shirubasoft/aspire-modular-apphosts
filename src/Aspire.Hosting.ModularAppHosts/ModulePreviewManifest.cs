using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aspire.Hosting.ModularAppHosts;

/// <summary>Describes an immutable, cross-repository module composition for an AppHost run.</summary>
public sealed class ModulePreviewManifest
{
    /// <summary>The schema version understood by this release.</summary>
    public const int CurrentSchemaVersion = 2;

    /// <summary>Gets or sets the manifest schema version.</summary>
    [JsonPropertyOrder(0)]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Gets or sets metadata for the repository that produced the preview request.</summary>
    [JsonPropertyOrder(1)]
    public ModulePreviewProducer Producer { get; set; } = new();

    /// <summary>Gets or sets the immutable module selections applied by the receiving AppHost.</summary>
    [JsonPropertyOrder(2)]
    public IList<ModulePreviewSelection> Modules { get; } = [];

    /// <summary>Gets the immutable contract packages offered by the producer.</summary>
    [JsonPropertyOrder(3)]
    public IList<ModulePreviewContractRequest> Contracts { get; } = [];

    /// <summary>Gets the immutable container images offered by the producer.</summary>
    [JsonPropertyOrder(4)]
    public IList<ModulePreviewImageArtifact> Images { get; } = [];

    /// <summary>Reads and validates a preview manifest from a file.</summary>
    public static async Task<ModulePreviewManifest> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            return await LoadAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Reads and validates a preview manifest from a stream.</summary>
    public static async Task<ModulePreviewManifest> LoadAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        ModulePreviewManifest manifest;
        try
        {
            manifest = await JsonSerializer.DeserializeAsync<ModulePreviewManifest>(
                stream,
                ModulePreviewJson.SerializerOptions,
                cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The module preview manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The module preview manifest is not valid JSON.", exception);
        }

        manifest.Validate();
        return manifest;
    }

    /// <summary>Writes the manifest as deterministic, indented JSON.</summary>
    public async Task SaveAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Validate();

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var stream = new FileStream(
            fullPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            await JsonSerializer.SerializeAsync(
                stream,
                this,
                ModulePreviewJson.SerializerOptions,
                cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Validates the schema, repository identities, and immutable revisions.</summary>
    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported module preview manifest schema version '{SchemaVersion}'. " +
                $"Expected '{CurrentSchemaVersion}'.");
        }

        if (Producer is null)
        {
            throw new InvalidDataException("The module preview manifest must specify producer metadata.");
        }

        Producer.Validate(nameof(Producer));
        if (Modules.Count == 0)
        {
            throw new InvalidDataException("The module preview manifest must select at least one module.");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var module in Modules)
        {
            if (module is null)
            {
                throw new InvalidDataException("The module preview manifest cannot contain a null module selection.");
            }

            module.Validate();
            if (!names.Add(module.Name))
            {
                throw new InvalidDataException(
                    $"The module preview manifest contains duplicate module name '{module.Name}'.");
            }
        }

        var contractModules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var contract in Contracts)
        {
            if (contract is null)
            {
                throw new InvalidDataException("The module preview manifest cannot contain a null contract artifact.");
            }

            contract.Validate(names);
            if (!contractModules.Add(contract.Module))
            {
                throw new InvalidDataException(
                    $"The module preview manifest contains duplicate contract artifacts for module '{contract.Module}'.");
            }
        }

        var imageResources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var image in Images)
        {
            if (image is null)
            {
                throw new InvalidDataException("The module preview manifest cannot contain a null image artifact.");
            }

            image.Validate(names);
            if (!imageResources.Add($"{image.Module}\0{image.Resource}"))
            {
                throw new InvalidDataException(
                    $"The module preview manifest contains duplicate image artifacts for resource " +
                    $"'{image.Resource}' in module '{image.Module}'.");
            }
        }
    }
}

/// <summary>Identifies the repository and revision that produced a preview request.</summary>
public sealed class ModulePreviewProducer
{
    /// <summary>Gets or sets the canonical remote repository URL.</summary>
    [JsonPropertyOrder(0)]
    public string Repository { get; set; } = string.Empty;

    /// <summary>Gets or sets the full immutable commit ID.</summary>
    [JsonPropertyOrder(1)]
    public string Commit { get; set; } = string.Empty;

    /// <summary>Gets or sets whether the producer contained uncommitted changes.</summary>
    [JsonPropertyOrder(2)]
    public bool Dirty { get; set; }

    /// <summary>Gets or sets the optional human-readable source branch.</summary>
    [JsonPropertyOrder(3)]
    public string? Branch { get; set; }

    /// <summary>Gets or sets the optional fully qualified default branch reference.</summary>
    [JsonPropertyOrder(4)]
    public string? BaseRef { get; set; }

    /// <summary>Gets or sets the optional immutable default branch commit ID.</summary>
    [JsonPropertyOrder(5)]
    public string? BaseCommit { get; set; }

    internal void Validate(string location)
    {
        ModulePreviewValidation.ValidateRepository(Repository, $"{location}.{nameof(Repository)}");
        ModulePreviewValidation.ValidateCommit(Commit, $"{location}.{nameof(Commit)}");
        if (Dirty)
        {
            throw new InvalidDataException(
                $"{location}.{nameof(Dirty)} must be false because a preview manifest can only select committed content.");
        }

        ModulePreviewValidation.ValidateOptionalMetadata(Branch, $"{location}.{nameof(Branch)}");
        ModulePreviewValidation.ValidateOptionalMetadata(BaseRef, $"{location}.{nameof(BaseRef)}");
        ModulePreviewValidation.ValidateBaseMetadata(BaseRef, BaseCommit, location);
        if (BaseCommit is not null)
        {
            ModulePreviewValidation.ValidateCommit(BaseCommit, $"{location}.{nameof(BaseCommit)}");
        }
    }
}

/// <summary>Selects the immutable Git source used to materialize one distributed application module.</summary>
public sealed class ModulePreviewSelection
{
    /// <summary>Gets or sets the exported module name.</summary>
    [JsonPropertyOrder(0)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the canonical remote repository URL.</summary>
    [JsonPropertyOrder(1)]
    public string Repository { get; set; } = string.Empty;

    /// <summary>Gets or sets the full immutable commit ID.</summary>
    [JsonPropertyOrder(2)]
    public string Commit { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional human-readable source branch.</summary>
    [JsonPropertyOrder(3)]
    public string? Branch { get; set; }

    /// <summary>Gets or sets the optional fully qualified default branch reference.</summary>
    [JsonPropertyOrder(4)]
    public string? BaseRef { get; set; }

    /// <summary>Gets or sets the optional immutable default branch commit ID.</summary>
    [JsonPropertyOrder(5)]
    public string? BaseCommit { get; set; }

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new InvalidDataException($"A module preview selection must specify {nameof(Name)}.");
        }

        ModulePreviewValidation.ValidateOptionalMetadata(Name, nameof(Name));
        ModulePreviewValidation.ValidateRepository(Repository, $"Module '{Name}'.{nameof(Repository)}");
        ModulePreviewValidation.ValidateCommit(Commit, $"Module '{Name}'.{nameof(Commit)}");
        ModulePreviewValidation.ValidateOptionalMetadata(Branch, $"Module '{Name}'.{nameof(Branch)}");
        ModulePreviewValidation.ValidateOptionalMetadata(BaseRef, $"Module '{Name}'.{nameof(BaseRef)}");
        ModulePreviewValidation.ValidateBaseMetadata(BaseRef, BaseCommit, $"Module '{Name}'");
        if (BaseCommit is not null)
        {
            ModulePreviewValidation.ValidateCommit(BaseCommit, $"Module '{Name}'.{nameof(BaseCommit)}");
        }
    }
}

/// <summary>Requests an exact module contract package without selecting its trusted source.</summary>
public sealed class ModulePreviewContractRequest
{
    /// <summary>Gets or sets the selected module that owns the contract.</summary>
    [JsonPropertyOrder(0)]
    public string Module { get; set; } = string.Empty;

    /// <summary>Gets or sets the NuGet package identifier.</summary>
    [JsonPropertyOrder(1)]
    public string PackageId { get; set; } = string.Empty;

    /// <summary>Gets or sets the exact NuGet package version.</summary>
    [JsonPropertyOrder(2)]
    public string Version { get; set; } = string.Empty;

    /// <summary>Gets the exact direct package dependencies attested by the producer.</summary>
    [JsonPropertyOrder(3)]
    public IList<ModulePreviewContractDependency> Dependencies { get; } = [];

    internal void Validate(IReadOnlySet<string> selectedModules)
    {
        ModulePreviewValidation.ValidateSelectedModule(Module, selectedModules, "Contract request");
        ModulePreviewValidation.ValidatePackageId(PackageId, $"Contract for module '{Module}'.{nameof(PackageId)}");
        ModulePreviewValidation.ValidatePackageVersion(Version, $"Contract for module '{Module}'.{nameof(Version)}");
        ModulePreviewValidation.ValidateContractDependencies(
            Dependencies,
            $"Contract for module '{Module}'.{nameof(Dependencies)}");
    }
}

/// <summary>Attests the exact version of one direct contract package dependency.</summary>
public sealed class ModulePreviewContractDependency
{
    /// <summary>Gets or sets the dependency's NuGet package identifier.</summary>
    [JsonPropertyOrder(0)]
    public string PackageId { get; set; } = string.Empty;

    /// <summary>Gets or sets the exact resolved NuGet package version.</summary>
    [JsonPropertyOrder(1)]
    public string Version { get; set; } = string.Empty;
}

/// <summary>Identifies the kind of exported module resource replaced by a preview image.</summary>
public enum ModulePreviewResourceKind
{
    /// <summary>An exported project represented by its container image.</summary>
    Project,

    /// <summary>An exported container resource.</summary>
    Container
}

/// <summary>Identifies an immutable OCI image offered by a preview producer or verified by a consumer.</summary>
public sealed class ModulePreviewImageArtifact
{
    /// <summary>Gets or sets the selected module that owns the resource.</summary>
    [JsonPropertyOrder(0)]
    public string Module { get; set; } = string.Empty;

    /// <summary>Gets or sets the exported resource name before consumer aliases are applied.</summary>
    [JsonPropertyOrder(1)]
    public string Resource { get; set; } = string.Empty;

    /// <summary>Gets or sets the exported resource kind.</summary>
    [JsonPropertyOrder(2)]
    public ModulePreviewResourceKind ResourceKind { get; set; }

    /// <summary>Gets or sets the OCI image repository without a tag or digest.</summary>
    [JsonPropertyOrder(3)]
    public string Repository { get; set; } = string.Empty;

    /// <summary>Gets or sets the immutable OCI digest in <c>sha256:&lt;64 lowercase hex&gt;</c> form.</summary>
    [JsonPropertyOrder(4)]
    public string Sha256 { get; set; } = string.Empty;

    internal void Validate(IReadOnlySet<string> selectedModules)
    {
        ModulePreviewValidation.ValidateSelectedModule(Module, selectedModules, "Image artifact");
        ModulePreviewValidation.ValidateName(Resource, $"Image for module '{Module}'.{nameof(Resource)}");
        if (!Enum.IsDefined(ResourceKind))
        {
            throw new InvalidDataException(
                $"Image for resource '{Resource}' in module '{Module}' has unsupported resource kind '{ResourceKind}'.");
        }

        ModulePreviewValidation.ValidateImageRepository(
            Repository,
            $"Image for resource '{Resource}' in module '{Module}'.{nameof(Repository)}");
        ModulePreviewValidation.ValidateImageSha256(
            Sha256,
            $"Image for resource '{Resource}' in module '{Module}'.{nameof(Sha256)}");
    }
}

internal static class ModulePreviewJson
{
    public static JsonSerializerOptions SerializerOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter<ModulePreviewResourceKind>(JsonNamingPolicy.CamelCase, false) }
    };
}

internal static class ModulePreviewValidation
{
    public static void ValidateContractDependencies(
        IEnumerable<ModulePreviewContractDependency> dependencies,
        string location)
    {
        ArgumentNullException.ThrowIfNull(dependencies);

        var packageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dependency in dependencies)
        {
            if (dependency is null)
            {
                throw new InvalidDataException($"{location} cannot contain a null dependency.");
            }

            ValidatePackageId(dependency.PackageId, $"{location}.{nameof(dependency.PackageId)}");
            ValidatePackageVersion(dependency.Version, $"{location}.{nameof(dependency.Version)}");
            if (!packageIds.Add(dependency.PackageId))
            {
                throw new InvalidDataException(
                    $"{location} contains duplicate package ID '{dependency.PackageId}'.");
            }
        }
    }

    public static void ValidateRepository(string repository, string location)
    {
        if (string.IsNullOrWhiteSpace(repository) ||
            !Uri.TryCreate(repository, UriKind.Absolute, out var uri) ||
            uri.IsFile ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            string.IsNullOrWhiteSpace(uri.AbsolutePath.Trim('/')))
        {
            throw new InvalidDataException(
                $"{location} must be a canonical absolute remote repository URL.");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidDataException(
                $"{location} cannot contain credentials, a query, or a fragment.");
        }

        if (uri.Scheme is not ("https" or "http" or "ssh" or "git"))
        {
            throw new InvalidDataException(
                $"{location} uses unsupported repository URL scheme '{uri.Scheme}'.");
        }
    }

    public static void ValidateCommit(string commit, string location)
    {
        if (string.IsNullOrEmpty(commit) ||
            (commit.Length is not (40 or 64)) ||
            commit.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException(
                $"{location} must be a full 40- or 64-character hexadecimal commit ID.");
        }
    }

    public static void ValidateOptionalMetadata(string? value, string location)
    {
        if (value is not null &&
            (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl)))
        {
            throw new InvalidDataException($"{location} cannot be empty or contain control characters.");
        }
    }

    public static void ValidateName(string value, string location)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 200 ||
            value.Any(char.IsControl))
        {
            throw new InvalidDataException(
                $"{location} must be a non-empty value of at most 200 characters without control characters.");
        }
    }

    public static void ValidateSelectedModule(
        string module,
        IReadOnlySet<string> selectedModules,
        string artifactKind)
    {
        ValidateName(module, $"{artifactKind}.{nameof(ModulePreviewImageArtifact.Module)}");
        if (!selectedModules.Contains(module))
        {
            throw new InvalidDataException(
                $"{artifactKind} references module '{module}', which is not selected by the preview manifest.");
        }
    }

    public static void ValidatePackageId(string packageId, string location)
    {
        if (string.IsNullOrWhiteSpace(packageId) ||
            packageId.Length > 100 ||
            packageId.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_')))
        {
            throw new InvalidDataException(
                $"{location} must contain only ASCII letters, digits, '.', '-', or '_' and be at most 100 characters.");
        }
    }

    public static void ValidatePackageVersion(string version, string location)
    {
        if (string.IsNullOrWhiteSpace(version) ||
            version.Length > 256 ||
            version.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '+')))
        {
            throw new InvalidDataException(
                $"{location} must be an exact package version containing only ASCII letters, digits, '.', '-', or '+'.");
        }
    }

    public static void ValidatePackageSource(string source, string location)
    {
        if (string.IsNullOrWhiteSpace(source) ||
            !Uri.TryCreate(source, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidDataException(
                $"{location} must be a credential-free HTTPS package source without a query or fragment.");
        }
    }

    public static void ValidateHexSha256(string sha256, string location)
    {
        if (sha256.Length != 64 ||
            sha256.Any(character =>
                !(character is >= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new InvalidDataException($"{location} must be exactly 64 lowercase hexadecimal characters.");
        }
    }

    public static void ValidateImageSha256(string sha256, string location)
    {
        const string prefix = "sha256:";
        if (!sha256.StartsWith(prefix, StringComparison.Ordinal) || sha256.Length != prefix.Length + 64)
        {
            throw new InvalidDataException(
                $"{location} must use the form 'sha256:<64 lowercase hexadecimal characters>'.");
        }

        ValidateHexSha256(sha256[prefix.Length..], location);
    }

    public static void ValidateImageRepository(string repository, string location)
    {
        if (string.IsNullOrWhiteSpace(repository) ||
            repository.Length > 255 ||
            repository.Contains("://", StringComparison.Ordinal) ||
            repository.Contains('@', StringComparison.Ordinal) ||
            repository[0] == '/' ||
            repository[^1] == '/' ||
            repository.Contains("//", StringComparison.Ordinal) ||
            !repository.Contains('/', StringComparison.Ordinal) ||
            repository.Any(character =>
                !(character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-' or '_' or '/' or ':')))
        {
            throw new InvalidDataException(
                $"{location} must be a lowercase explicit OCI registry/repository without a tag or digest.");
        }

        var firstSlash = repository.IndexOf('/', StringComparison.Ordinal);
        if (repository[(firstSlash + 1)..].Contains(':', StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{location} cannot contain an image tag.");
        }
    }

    public static void ValidateBaseMetadata(string? baseRef, string? baseCommit, string location)
    {
        if ((baseRef is null) != (baseCommit is null))
        {
            throw new InvalidDataException(
                $"{location} must specify both {nameof(ModulePreviewProducer.BaseRef)} and " +
                $"{nameof(ModulePreviewProducer.BaseCommit)}, or omit both.");
        }
    }
}
