using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aspire.Hosting;

internal sealed record ModuleInitializationReceipt(
    int SchemaVersion,
    string Repository,
    string Destination,
    string? Revision,
    string ConfigurationFingerprint,
    DateTimeOffset InitializedAtUtc)
{
    public const int CurrentSchemaVersion = 1;

    public static ModuleInitializationReceipt Create(
        ModuleRepositoryRequirement requirement,
        DateTimeOffset initializedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        return new ModuleInitializationReceipt(
            CurrentSchemaVersion,
            requirement.NormalizedRepository,
            requirement.RepositoryPath,
            requirement.Revision,
            requirement.ConfigurationFingerprint,
            initializedAtUtc);
    }

    public bool Matches(ModuleRepositoryRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        return SchemaVersion == CurrentSchemaVersion &&
            string.Equals(Repository, requirement.NormalizedRepository, StringComparison.Ordinal) &&
            PathSafety.AreEqual(Destination, requirement.RepositoryPath) &&
            string.Equals(Revision, requirement.Revision, StringComparison.Ordinal) &&
            string.Equals(
                ConfigurationFingerprint,
                requirement.ConfigurationFingerprint,
                StringComparison.Ordinal);
    }
}

internal static class ModuleInitializationReceiptStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    public static async Task WriteAsync(
        ModuleRepositoryRequirement requirement,
        DateTimeOffset initializedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        var receipt = ModuleInitializationReceipt.Create(requirement, initializedAtUtc);
        var receiptDirectory = Path.GetDirectoryName(requirement.ReceiptPath)
            ?? throw new InvalidOperationException(
                $"Unable to determine the receipt directory for '{requirement.ReceiptPath}'.");
        Directory.CreateDirectory(receiptDirectory);
        var temporaryPath = Path.Combine(
            receiptDirectory,
            $".{Path.GetFileName(requirement.ReceiptPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await using (stream.ConfigureAwait(false))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    receipt,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, requirement.ReceiptPath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    public static async Task<ModuleInitializationReceipt?> ReadAsync(
        string receiptPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(receiptPath);
        if (!File.Exists(receiptPath))
        {
            return null;
        }

        var stream = new FileStream(
            receiptPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (stream.ConfigureAwait(false))
        {
            return await JsonSerializer.DeserializeAsync<ModuleInitializationReceipt>(
                stream,
                SerializerOptions,
                cancellationToken).ConfigureAwait(false);
        }
    }

    public static ModuleInitializationReceipt? Read(string receiptPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(receiptPath);
        if (!File.Exists(receiptPath))
        {
            return null;
        }

        using var stream = new FileStream(
            receiptPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        return JsonSerializer.Deserialize<ModuleInitializationReceipt>(stream, SerializerOptions);
    }

    public static bool HasMatchingReceipt(ModuleRepositoryRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        try
        {
            return Read(requirement.ReceiptPath)?.Matches(requirement) == true;
        }
        catch (Exception exception) when (
            exception is JsonException
                or IOException
                or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
