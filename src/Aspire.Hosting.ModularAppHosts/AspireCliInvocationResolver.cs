using System.Text.Json;

namespace Aspire.Hosting;

internal sealed record AspireCliInvocation(
    string Executable,
    IReadOnlyList<string> PrefixArguments);

internal static class AspireCliInvocationResolver
{
    internal static AspireCliInvocation Resolve(
        string aspireCliPath,
        string appHostPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(aspireCliPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(appHostPath);
        if (!string.Equals(aspireCliPath, "aspire", StringComparison.Ordinal))
        {
            return new AspireCliInvocation(aspireCliPath, []);
        }

        var absoluteAppHostPath = Path.GetFullPath(appHostPath);
        var appHostDirectory = Directory.Exists(absoluteAppHostPath)
            ? absoluteAppHostPath
            : Path.GetDirectoryName(absoluteAppHostPath)!;
        var current = new DirectoryInfo(appHostDirectory);
        while (current is not null)
        {
            var manifestPath = Path.Combine(current.FullName, ".config", "dotnet-tools.json");
            if (File.Exists(manifestPath) && ManifestProvidesAspireCli(manifestPath))
            {
                return new AspireCliInvocation(
                    "dotnet",
                    ["tool", "run", "aspire", "--"]);
            }

            current = current.Parent;
        }

        return new AspireCliInvocation(aspireCliPath, []);
    }

    internal static bool ShouldFallBackToAspireOnPath(
        AspireCliInvocation invocation,
        string output)
        => string.Equals(invocation.Executable, "dotnet", StringComparison.Ordinal)
            && invocation.PrefixArguments.SequenceEqual(["tool", "run", "aspire", "--"])
            && output.Contains("dotnet tool restore", StringComparison.OrdinalIgnoreCase);

    private static bool ManifestProvidesAspireCli(string manifestPath)
    {
        try
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!manifest.RootElement.TryGetProperty("tools", out var tools))
            {
                return false;
            }

            return tools.EnumerateObject().Any(tool =>
                tool.Value.TryGetProperty("commands", out var commands)
                && commands.EnumerateArray().Any(command =>
                    string.Equals(command.GetString(), "aspire", StringComparison.Ordinal)));
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"Unable to read local .NET tool manifest '{manifestPath}'.",
                exception);
        }
    }
}
