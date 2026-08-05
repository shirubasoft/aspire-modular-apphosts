using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aspire.Hosting.ModularAppHosts;

/// <summary>
/// Describes preview inputs that a trusted consumer has resolved and verified for an AppHost run.
/// </summary>
public sealed class ModulePreviewResolution
{
    /// <summary>The schema version understood by this release.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Gets or sets the resolution schema version.</summary>
    [JsonPropertyOrder(0)]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Gets or sets the lowercase SHA-256 of the canonical producer request JSON.</summary>
    [JsonPropertyOrder(1)]
    public string RequestSha256 { get; set; } = string.Empty;

    /// <summary>Gets or sets the trusted consumer repository identity.</summary>
    [JsonPropertyOrder(2)]
    public ModulePreviewConsumerIdentity Consumer { get; set; } = new();

    /// <summary>Gets the immutable module source selections verified by the consumer.</summary>
    [JsonPropertyOrder(3)]
    public IList<ModulePreviewSelection> Modules { get; } = [];

    /// <summary>Gets the immutable contract packages verified by the consumer.</summary>
    [JsonPropertyOrder(4)]
    public IList<ModulePreviewResolvedContract> Contracts { get; } = [];

    /// <summary>Gets the immutable OCI images verified by the consumer.</summary>
    [JsonPropertyOrder(5)]
    public IList<ModulePreviewImageArtifact> Images { get; } = [];

    /// <summary>Reads and validates a preview resolution from a file.</summary>
    public static async Task<ModulePreviewResolution> LoadAsync(
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

    /// <summary>Reads and validates a preview resolution from a stream.</summary>
    public static async Task<ModulePreviewResolution> LoadAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        ModulePreviewResolution resolution;
        try
        {
            resolution = await JsonSerializer.DeserializeAsync<ModulePreviewResolution>(
                stream,
                ModulePreviewJson.SerializerOptions,
                cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The module preview resolution is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The module preview resolution is not valid JSON.", exception);
        }

        resolution.Validate();
        return resolution;
    }

    /// <summary>Writes the resolution as deterministic, indented JSON.</summary>
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

    /// <summary>Validates module selections and verified artifacts.</summary>
    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported module preview resolution schema version '{SchemaVersion}'. " +
                $"Expected '{CurrentSchemaVersion}'.");
        }

        ModulePreviewValidation.ValidateHexSha256(RequestSha256, nameof(RequestSha256));
        if (Consumer is null)
        {
            throw new InvalidDataException("The module preview resolution must specify a consumer identity.");
        }

        Consumer.Validate();
        if (Modules.Count == 0)
        {
            throw new InvalidDataException("The module preview resolution must select at least one module.");
        }

        var moduleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var module in Modules)
        {
            if (module is null)
            {
                throw new InvalidDataException("The module preview resolution cannot contain a null module selection.");
            }

            module.Validate();
            if (!moduleNames.Add(module.Name))
            {
                throw new InvalidDataException(
                    $"The module preview resolution contains duplicate module name '{module.Name}'.");
            }
        }

        var contractModules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var contract in Contracts)
        {
            if (contract is null)
            {
                throw new InvalidDataException("The module preview resolution cannot contain a null contract.");
            }

            contract.Validate(moduleNames);
            if (!contractModules.Add(contract.Module))
            {
                throw new InvalidDataException(
                    $"The module preview resolution contains duplicate contracts for module '{contract.Module}'.");
            }
        }

        var imageResources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var image in Images)
        {
            if (image is null)
            {
                throw new InvalidDataException("The module preview resolution cannot contain a null image artifact.");
            }

            image.Validate(moduleNames);
            if (!imageResources.Add($"{image.Module}\0{image.Resource}"))
            {
                throw new InvalidDataException(
                    $"The module preview resolution contains duplicate image artifacts for resource " +
                    $"'{image.Resource}' in module '{image.Module}'.");
            }
        }
    }
}

/// <summary>Identifies the trusted repository and revision that consumed a preview request.</summary>
public sealed class ModulePreviewConsumerIdentity
{
    /// <summary>Gets or sets the canonical remote repository URL.</summary>
    [JsonPropertyOrder(0)]
    public string Repository { get; set; } = string.Empty;

    /// <summary>Gets or sets the full immutable consumer commit ID.</summary>
    [JsonPropertyOrder(1)]
    public string Commit { get; set; } = string.Empty;

    internal void Validate()
    {
        ModulePreviewValidation.ValidateRepository(Repository, $"{nameof(ModulePreviewResolution.Consumer)}.{nameof(Repository)}");
        ModulePreviewValidation.ValidateCommit(Commit, $"{nameof(ModulePreviewResolution.Consumer)}.{nameof(Commit)}");
    }
}

/// <summary>Records a contract package verified by a trusted preview consumer.</summary>
public sealed class ModulePreviewResolvedContract
{
    /// <summary>Gets or sets the selected module that owns the contract.</summary>
    [JsonPropertyOrder(0)]
    public string Module { get; set; } = string.Empty;

    /// <summary>Gets or sets the verified NuGet package identifier.</summary>
    [JsonPropertyOrder(1)]
    public string PackageId { get; set; } = string.Empty;

    /// <summary>Gets or sets the verified exact NuGet package version.</summary>
    [JsonPropertyOrder(2)]
    public string Version { get; set; } = string.Empty;

    /// <summary>Gets or sets the lowercase hexadecimal SHA-256 of the verified package bytes.</summary>
    [JsonPropertyOrder(3)]
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional trusted credential-free HTTPS NuGet source.</summary>
    [JsonPropertyOrder(4)]
    public string? Source { get; set; }

    /// <summary>Gets or sets the optional consumer-local path to the verified package.</summary>
    [JsonPropertyOrder(5)]
    public string? PackagePath { get; set; }

    internal void Validate(IReadOnlySet<string> selectedModules)
    {
        ModulePreviewValidation.ValidateSelectedModule(Module, selectedModules, "Resolved contract");
        ModulePreviewValidation.ValidatePackageId(PackageId, $"Contract for module '{Module}'.{nameof(PackageId)}");
        ModulePreviewValidation.ValidatePackageVersion(Version, $"Contract for module '{Module}'.{nameof(Version)}");
        ModulePreviewValidation.ValidateHexSha256(Sha256, $"Contract for module '{Module}'.{nameof(Sha256)}");
        if (Source is not null)
        {
            ModulePreviewValidation.ValidatePackageSource(Source, $"Contract for module '{Module}'.{nameof(Source)}");
        }
        ModulePreviewValidation.ValidateOptionalMetadata(PackagePath, $"Contract for module '{Module}'.{nameof(PackagePath)}");
        if (Source is null && PackagePath is null)
        {
            throw new InvalidDataException(
                $"Contract for module '{Module}' must record a trusted package source or consumer-local package path.");
        }
    }
}
