using System.Text;

namespace Shirubasoft.Aspire.ModularAppHosts.Tool;

internal interface IEnvironmentAccessor
{
    string CurrentDirectory { get; }

    string? GetEnvironmentVariable(string name);
}

internal sealed class SystemEnvironmentAccessor : IEnvironmentAccessor
{
    public string CurrentDirectory => Environment.CurrentDirectory;

    public string? GetEnvironmentVariable(string name) => Environment.GetEnvironmentVariable(name);
}

internal static class GitHubFileWriter
{
    public static async Task AppendAsync(
        string path,
        IEnumerable<KeyValuePair<string, string>> values,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(values);
        var content = new StringBuilder();
        foreach (var (name, value) in values)
        {
            ValidateName(name);
            if (value.Contains('\r', StringComparison.Ordinal) ||
                value.Contains('\n', StringComparison.Ordinal))
            {
                var delimiter = $"modular_apphosts_{Guid.NewGuid():N}";
                content.Append(name).Append("<<").AppendLine(delimiter);
                content.AppendLine(value);
                content.AppendLine(delimiter);
            }
            else
            {
                content.Append(name).Append('=').AppendLine(value);
            }
        }

        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.AppendAllTextAsync(
            fullPath,
            content.ToString(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            name.Contains('=', StringComparison.Ordinal) ||
            name.Contains('\r', StringComparison.Ordinal) ||
            name.Contains('\n', StringComparison.Ordinal))
        {
            throw new ToolUsageException($"GitHub file entry name '{name}' is invalid.");
        }
    }
}
