using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aspire.Hosting.ModularAppHosts;

/// <summary>
/// Describes tag-only container overrides for a full-control module preview.
/// </summary>
/// <remarks>
/// The manifest deliberately does not carry its source repository identity. The trusted caller
/// supplies that identity separately through <see cref="FullControlModulePreviewSource"/>.
/// </remarks>
public sealed class FullControlModulePreviewManifest
{
    /// <summary>The schema version understood by this release.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>The published JSON Schema for this manifest.</summary>
    public const string SchemaUri =
        "https://raw.githubusercontent.com/shirubasoft/aspire-modular-apphosts/main/schemas/full-control-module-preview.schema.json";

    /// <summary>Gets or sets the JSON Schema URI.</summary>
    [JsonPropertyName("$schema")]
    [JsonPropertyOrder(0)]
    public string Schema { get; set; } = SchemaUri;

    /// <summary>Gets or sets the manifest schema version.</summary>
    [JsonPropertyOrder(1)]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>
    /// Gets effective AppHost resource names that receive the sanitized trusted source ref as their image tag.
    /// </summary>
    [JsonPropertyOrder(2)]
    public IList<string> SourceRefResources { get; } = [];

    /// <summary>Gets explicit image tags keyed by effective AppHost resource name.</summary>
    [JsonPropertyOrder(3)]
    public IDictionary<string, string> ContainerTags { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Reads and validates a full-control preview manifest from a file.</summary>
    public static async Task<FullControlModulePreviewManifest> LoadAsync(
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

    /// <summary>Reads and validates a full-control preview manifest from a stream.</summary>
    public static async Task<FullControlModulePreviewManifest> LoadAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        FullControlModulePreviewManifest manifest;
        try
        {
            manifest = await JsonSerializer.DeserializeAsync<FullControlModulePreviewManifest>(
                stream,
                ModulePreviewJson.SerializerOptions,
                cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The full-control module preview manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The full-control module preview manifest is not valid JSON.",
                exception);
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

    /// <summary>Validates the schema and tag override structure.</summary>
    public void Validate()
    {
        if (!string.Equals(Schema, SchemaUri, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Full-control module preview manifest.$schema must be '{SchemaUri}'.");
        }

        if (SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported full-control module preview manifest schema version '{SchemaVersion}'. " +
                $"Expected '{CurrentSchemaVersion}'.");
        }

        if (SourceRefResources.Count == 0 && ContainerTags.Count == 0)
        {
            throw new InvalidDataException(
                "A full-control module preview manifest must specify at least one container tag override.");
        }

        var resources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var resource in SourceRefResources)
        {
            ModulePreviewValidation.ValidateName(resource, nameof(SourceRefResources));
            if (!resources.Add(resource))
            {
                throw new InvalidDataException(
                    $"The full-control module preview manifest contains duplicate resource '{resource}'.");
            }
        }

        foreach (var (resource, tag) in ContainerTags)
        {
            ModulePreviewValidation.ValidateName(resource, nameof(ContainerTags));
            ValidateContainerTag(tag, $"Container tag for resource '{resource}'");
            if (!resources.Add(resource))
            {
                throw new InvalidDataException(
                    $"Resource '{resource}' cannot use both the trusted source ref and an explicit container tag.");
            }
        }
    }

    /// <summary>Resolves the sparse tag overrides using the trusted source ref.</summary>
    public IReadOnlyDictionary<string, string> ResolveContainerTags(string sourceRef)
    {
        Validate();
        var sourceTag = SanitizeSourceRef(sourceRef);
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var resource in SourceRefResources)
        {
            tags.Add(resource, sourceTag);
        }

        foreach (var (resource, tag) in ContainerTags)
        {
            tags.Add(resource, tag);
        }

        return tags;
    }

    /// <summary>Converts a Git ref name into a valid container tag.</summary>
    public static string SanitizeSourceRef(string sourceRef)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRef);
        if (sourceRef.Any(char.IsControl))
        {
            throw new InvalidDataException("The trusted source ref cannot contain control characters.");
        }

        var builder = new StringBuilder(sourceRef.Length);
        foreach (var character in sourceRef)
        {
            builder.Append(IsContainerTagCharacter(character) ? character : '-');
        }

        var tag = builder.ToString();
        ValidateContainerTag(tag, "The sanitized trusted source ref");
        return tag;
    }

    internal static void ValidateContainerTag(string tag, string location)
    {
        if (string.IsNullOrWhiteSpace(tag) ||
            tag.Length > 128 ||
            !(char.IsAsciiLetterOrDigit(tag[0]) || tag[0] == '_') ||
            tag.Any(character => !IsContainerTagCharacter(character)))
        {
            throw new InvalidDataException(
                $"{location} must be a valid container tag of at most 128 characters.");
        }
    }

    private static bool IsContainerTagCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '-';
}

/// <summary>Identifies the trusted source repository and ref for a full-control preview run.</summary>
public sealed class FullControlModulePreviewSource
{
    /// <summary>Gets or sets the canonical repository URL supplied by the trusted CI caller context.</summary>
    public string Repository { get; set; } = string.Empty;

    /// <summary>Gets or sets the branch or ref name whose sanitized value supplies source-ref image tags.</summary>
    public string Ref { get; set; } = string.Empty;

    /// <summary>Validates the trusted source identity.</summary>
    public void Validate()
    {
        ModulePreviewValidation.ValidateRepository(Repository, nameof(Repository));
        ModulePreviewValidation.ValidateOptionalMetadata(Ref, nameof(Ref));
        _ = FullControlModulePreviewManifest.SanitizeSourceRef(Ref);
    }
}

/// <summary>Configuration used to opt an AppHost into full-control module previews.</summary>
public sealed class FullControlModulePreviewOptions
{
    /// <summary>The conventional configuration section consumed by the AppHost extension.</summary>
    public const string ConfigurationSectionName = "Aspire:ModularAppHosts:FullControlPreview";

    /// <summary>Gets or sets the caller-owned manifest path.</summary>
    public string? ManifestPath { get; set; }

    /// <summary>Gets or sets the source repository URL supplied by trusted CI context.</summary>
    public string? SourceRepository { get; set; }

    /// <summary>Gets or sets the source ref supplied by trusted CI context.</summary>
    public string? SourceRef { get; set; }
}
