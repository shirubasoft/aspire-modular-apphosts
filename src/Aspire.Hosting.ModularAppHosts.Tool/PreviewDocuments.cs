using System.Text.Json;
using System.Text.Json.Serialization;
using Aspire.Hosting.ModularAppHosts;

namespace Shirubasoft.Aspire.ModularAppHosts.Tool;

internal sealed class ModulePreviewProducerDescriptor : IPreviewDocument
{
    public const int CurrentSchemaVersion = 2;
    public const string SchemaUri =
        "https://raw.githubusercontent.com/shirubasoft/aspire-modular-apphosts/main/schemas/module-preview-producer.schema.json";

    [JsonPropertyOrder(0)]
    [JsonPropertyName("$schema")]
    public string? Schema { get; set; }

    [JsonPropertyOrder(1)]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyOrder(2)]
    public string Module { get; set; } = string.Empty;

    [JsonPropertyOrder(3)]
    public ModulePreviewProducerContractDescriptor? Contract { get; set; }

    [JsonPropertyOrder(4)]
    public IList<ModulePreviewProducerImageDescriptor> Images { get; } = [];

    public static Task<ModulePreviewProducerDescriptor> LoadAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        PreviewDocumentJson.LoadAsync<ModulePreviewProducerDescriptor>(path, cancellationToken);

    public static Task<ModulePreviewProducerDescriptor> LoadAsync(
        Stream stream,
        CancellationToken cancellationToken = default) =>
        PreviewDocumentJson.LoadAsync<ModulePreviewProducerDescriptor>(stream, cancellationToken);

    public Task SaveAsync(string path, CancellationToken cancellationToken = default) =>
        PreviewDocumentJson.SaveAsync(path, this, cancellationToken);

    public void Validate() => PreviewPolicyValidation.Validate(this);
}

internal sealed class ModulePreviewProducerContractDescriptor
{
    [JsonPropertyOrder(0)]
    public string PackageId { get; set; } = string.Empty;

    [JsonPropertyOrder(1)]
    public string? Version { get; set; }

    [JsonPropertyOrder(2)]
    public IList<ModulePreviewContractDependency> Dependencies { get; } = [];
}

internal sealed class ModulePreviewProducerImageDescriptor
{
    [JsonPropertyOrder(0)]
    public string Resource { get; set; } = string.Empty;

    [JsonPropertyOrder(1)]
    public string ResourceKind { get; set; } = string.Empty;

    [JsonPropertyOrder(2)]
    public string Repository { get; set; } = string.Empty;

    [JsonPropertyOrder(3)]
    public bool Required { get; set; }
}

internal sealed class ModulePreviewConsumerPolicy : IPreviewDocument
{
    public const int CurrentSchemaVersion = 3;

    [JsonPropertyOrder(0)]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyOrder(1)]
    public IList<ModulePreviewConsumerModulePolicy> Modules { get; } = [];

    public static Task<ModulePreviewConsumerPolicy> LoadAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        PreviewDocumentJson.LoadAsync<ModulePreviewConsumerPolicy>(path, cancellationToken);

    public static Task<ModulePreviewConsumerPolicy> LoadAsync(
        Stream stream,
        CancellationToken cancellationToken = default) =>
        PreviewDocumentJson.LoadAsync<ModulePreviewConsumerPolicy>(stream, cancellationToken);

    public void Validate() => PreviewPolicyValidation.Validate(this);
}

internal sealed class ModulePreviewConsumerModulePolicy
{
    [JsonPropertyOrder(0)]
    public string Module { get; set; } = string.Empty;

    [JsonPropertyOrder(1)]
    public string Repository { get; set; } = string.Empty;

    [JsonPropertyOrder(2)]
    public ModulePreviewConsumerContractPolicy? Contract { get; set; }

    [JsonPropertyOrder(3)]
    public IList<ModulePreviewConsumerImagePolicy> Images { get; } = [];
}

internal sealed class ModulePreviewConsumerContractPolicy
{
    [JsonPropertyOrder(0)]
    public string PackageId { get; set; } = string.Empty;

    [JsonPropertyOrder(1)]
    public string VersionEnvironment { get; set; } = string.Empty;

    [JsonPropertyOrder(2)]
    public bool Required { get; set; } = true;

    [JsonPropertyOrder(3)]
    public ModulePreviewPublishedContractPolicy? Published { get; set; }

    [JsonPropertyOrder(4)]
    public ModulePreviewSourceFallbackPolicy SourceFallback { get; set; } = new();

    [JsonPropertyOrder(5)]
    public IList<string> AllowedPackProperties { get; } = [];

    [JsonPropertyOrder(6)]
    public IList<ModulePreviewContractDependency> Dependencies { get; } = [];
}

internal sealed class ModulePreviewPublishedContractPolicy
{
    [JsonPropertyOrder(0)]
    public string Source { get; set; } = string.Empty;
}

internal sealed class ModulePreviewSourceFallbackPolicy
{
    [JsonPropertyOrder(0)]
    public bool Enabled { get; set; }

    [JsonPropertyOrder(1)]
    public string? Project { get; set; }
}

internal sealed class ModulePreviewConsumerImagePolicy
{
    [JsonPropertyOrder(0)]
    public string Resource { get; set; } = string.Empty;

    [JsonPropertyOrder(1)]
    public string ResourceKind { get; set; } = string.Empty;

    [JsonPropertyOrder(2)]
    public IList<string> Repositories { get; } = [];

    [JsonPropertyOrder(3)]
    public IList<string> ProducerRepositories { get; } = [];

    [JsonPropertyOrder(4)]
    public bool Required { get; set; }
}

internal interface IPreviewDocument
{
    void Validate();
}

internal static class PreviewDocumentJson
{
    private static JsonSerializerOptions SerializerOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    public static async Task<T> LoadAsync<T>(
        string path,
        CancellationToken cancellationToken)
        where T : class, IPreviewDocument
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
            return await LoadAsync<T>(stream, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    public static async Task<T> LoadAsync<T>(
        Stream stream,
        CancellationToken cancellationToken)
        where T : class, IPreviewDocument
    {
        ArgumentNullException.ThrowIfNull(stream);

        T document;
        try
        {
            document = await JsonSerializer.DeserializeAsync<T>(
                stream,
                SerializerOptions,
                cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The preview document is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The preview document is not valid JSON.", exception);
        }

        document.Validate();
        return document;
    }

    public static async Task SaveAsync<T>(
        string path,
        T document,
        CancellationToken cancellationToken)
        where T : class, IPreviewDocument
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(document);
        document.Validate();

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
                document,
                SerializerOptions,
                cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }
}
