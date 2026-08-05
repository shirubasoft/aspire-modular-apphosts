using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aspire.Hosting.ModularAppHosts;

/// <summary>Describes an immutable, cross-repository module composition for an AppHost run.</summary>
public sealed class ModulePreviewManifest
{
    /// <summary>The schema version understood by this release.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Gets or sets the manifest schema version.</summary>
    [JsonPropertyOrder(0)]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Gets or sets metadata for the repository that produced the preview request.</summary>
    [JsonPropertyOrder(1)]
    public ModulePreviewProducer Producer { get; set; } = new();

    /// <summary>Gets or sets the immutable module selections applied by the receiving AppHost.</summary>
    [JsonPropertyOrder(2)]
    public IList<ModulePreviewSelection> Modules { get; } = [];

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

internal static class ModulePreviewJson
{
    public static JsonSerializerOptions SerializerOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };
}

internal static class ModulePreviewValidation
{
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
