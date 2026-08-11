using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aspire.Hosting.ModularAppHosts;

/// <summary>Describes the remotely pullable module images shared between repository workflows.</summary>
public sealed class ModuleImageManifestDocument
{
    /// <summary>The largest manifest accepted as a GitHub workflow input.</summary>
    public const int MaximumJsonLength = 65_535;

    /// <summary>The default file name written by the workflow image pipeline.</summary>
    public const string DefaultFileName = "module-image-manifest.json";

    /// <summary>The schema version understood by this release.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Gets or sets the document schema version.</summary>
    [JsonPropertyOrder(0)]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Gets the image overrides in deterministic module and resource order.</summary>
    [JsonPropertyOrder(1)]
    public IList<ModuleImageManifestEntry> Images { get; } = [];

    /// <summary>Reads and validates an image manifest.</summary>
    public static async Task<ModuleImageManifestDocument> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return Parse(json);
    }

    /// <summary>Reads and validates an image manifest from JSON.</summary>
    public static ModuleImageManifestDocument Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        if (json.Length > MaximumJsonLength)
        {
            throw new InvalidDataException(
                $"The module image manifest exceeds the {MaximumJsonLength}-character workflow input limit.");
        }

        var document = JsonSerializer.Deserialize<ModuleImageManifestDocument>(json, SerializerOptions)
            ?? throw new InvalidDataException("The module image manifest is empty.");
        document.Validate();
        return document;
    }

    /// <summary>Writes deterministic JSON to <paramref name="path"/>.</summary>
    public async Task SaveAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Validate();
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var stream = new FileStream(
            fullPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (stream.ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(stream, this, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
            await stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Serializes the manifest as compact JSON suitable for a workflow input.</summary>
    public string ToJson()
    {
        Validate();
        var json = JsonSerializer.Serialize(this, CompactSerializerOptions);
        if (json.Length > MaximumJsonLength)
        {
            throw new InvalidDataException(
                $"The module image manifest exceeds the {MaximumJsonLength}-character workflow input limit.");
        }

        return json;
    }

    /// <summary>Validates the schema and every module resource image identity.</summary>
    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported module image manifest schema version '{SchemaVersion}'. " +
                $"Expected '{CurrentSchemaVersion}'.");
        }

        if (Images.Count == 0)
        {
            throw new InvalidDataException("The module image manifest must contain at least one image.");
        }

        var identities = new HashSet<(string Module, string Resource)>(ModuleResourceIdentityComparer.Instance);
        foreach (var image in Images)
        {
            ArgumentNullException.ThrowIfNull(image);
            image.Validate();
            if (!identities.Add((image.Module, image.Resource)))
            {
                throw new InvalidDataException(
                    $"The module image manifest contains duplicate resource '{image.Module}/{image.Resource}'.");
            }
        }

        var ordered = Images
            .OrderBy(image => image.Module, StringComparer.Ordinal)
            .ThenBy(image => image.Resource, StringComparer.Ordinal)
            .ToArray();
        Images.Clear();
        foreach (var image in ordered)
        {
            Images.Add(image);
        }
    }

    internal static JsonSerializerOptions SerializerOptions { get; } = CreateSerializerOptions(writeIndented: true);

    private static JsonSerializerOptions CompactSerializerOptions { get; } = CreateSerializerOptions(writeIndented: false);

    private static JsonSerializerOptions CreateSerializerOptions(bool writeIndented) =>
        new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = writeIndented,
            Converters = { new JsonStringEnumConverter<ModuleResourceKind>(JsonNamingPolicy.CamelCase, false) }
        };
}

/// <summary>Maps one declared module resource to a complete remotely pullable image identity.</summary>
public sealed class ModuleImageManifestEntry
{
    /// <summary>Gets or sets the module name.</summary>
    [JsonPropertyOrder(0)]
    public string Module { get; set; } = string.Empty;

    /// <summary>Gets or sets the resource name declared by the module contract.</summary>
    [JsonPropertyOrder(1)]
    public string Resource { get; set; } = string.Empty;

    /// <summary>Gets or sets the kind of module resource receiving the image.</summary>
    [JsonPropertyOrder(2)]
    public ModuleResourceKind ResourceKind { get; set; }

    /// <summary>Gets or sets the registry host.</summary>
    [JsonPropertyOrder(3)]
    public string Registry { get; set; } = string.Empty;

    /// <summary>Gets or sets the repository path within the registry.</summary>
    [JsonPropertyOrder(4)]
    public string Repository { get; set; } = string.Empty;

    /// <summary>Gets or sets the image tag when the image is not digest-pinned.</summary>
    [JsonPropertyOrder(5)]
    public string? Tag { get; set; }

    /// <summary>Gets or sets the canonical digest when the image is digest-pinned.</summary>
    [JsonPropertyOrder(6)]
    public string? Digest { get; set; }

    /// <summary>Gets the complete image reference represented by this entry.</summary>
    [JsonIgnore]
    public string Reference => Digest is { Length: > 0 }
        ? $"{Registry}/{Repository}@{Digest}"
        : $"{Registry}/{Repository}:{Tag}";

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Module);
        ArgumentException.ThrowIfNullOrWhiteSpace(Resource);
        ModuleImageWorkflowConfiguration.ValidateSegment(Module, nameof(Module));
        ModuleImageWorkflowConfiguration.ValidateSegment(Resource, nameof(Resource));
        ArgumentException.ThrowIfNullOrWhiteSpace(Registry);
        ArgumentException.ThrowIfNullOrWhiteSpace(Repository);
        if (!Enum.IsDefined(ResourceKind))
        {
            throw new InvalidDataException($"Unsupported module resource kind '{ResourceKind}'.");
        }

        if (Registry.Contains("://", StringComparison.Ordinal) || Registry.Contains('/', StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Image registry '{Registry}' must be a host name with an optional port, not a URL or path.");
        }

        if (Repository.StartsWith('/') ||
            Repository.EndsWith('/') ||
            Repository.Contains('@', StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Image repository '{Repository}' is not a valid repository path.");
        }

        var tag = string.IsNullOrWhiteSpace(Tag) ? null : Tag;
        var digest = string.IsNullOrWhiteSpace(Digest) ? null : Digest;
        if ((tag is null) == (digest is null))
        {
            throw new InvalidDataException(
                $"Module image '{Module}/{Resource}' must specify exactly one tag or digest.");
        }

        if (tag is not null && !ModuleImageIdentityValidation.IsValidTag(tag))
        {
            throw new InvalidDataException($"Image tag '{tag}' is not a valid OCI distribution tag.");
        }

        if (digest is not null && !ModuleImageIdentityValidation.IsValidDigest(digest))
        {
            throw new InvalidDataException(
                $"Image digest '{digest}' must use the form 'sha256:<64 lowercase hexadecimal characters>'.");
        }
    }
}

/// <summary>Validates OCI image tags and supported image digests.</summary>
public static class ModuleImageIdentityValidation
{
    /// <summary>Returns whether <paramref name="value"/> is a valid OCI distribution tag.</summary>
    public static bool IsValidTag(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length is 0 or > 128 || !IsTagFirstCharacter(value[0]))
        {
            return false;
        }

        return value.Skip(1).All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '-');
    }

    /// <summary>Returns whether <paramref name="value"/> is a supported canonical image digest.</summary>
    public static bool IsValidDigest(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        const string prefix = "sha256:";
        return value.StartsWith(prefix, StringComparison.Ordinal) &&
            value.Length == prefix.Length + 64 &&
            value[prefix.Length..].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private static bool IsTagFirstCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character) || character == '_';
}

internal sealed class ModuleResourceIdentityComparer : IEqualityComparer<(string Module, string Resource)>
{
    public static ModuleResourceIdentityComparer Instance { get; } = new();

    public bool Equals(
        (string Module, string Resource) x,
        (string Module, string Resource) y) =>
        string.Equals(x.Module, y.Module, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(x.Resource, y.Resource, StringComparison.OrdinalIgnoreCase);

    public int GetHashCode((string Module, string Resource) obj) =>
        HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Module),
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Resource));
}
