namespace Aspire.Hosting.ModularAppHosts;

/// <summary>
/// Provides endpoints and values exported by an Aspire deployment for use in external tests.
/// </summary>
public sealed class AspireDeploymentTestConfiguration
{
    /// <summary>The environment variable that identifies the deployment environment file to load.</summary>
    public const string FilePathEnvironmentVariableName = "ASPIRE_TEST_CONFIGURATION_FILE";

    private const string EndpointPrefix = "ASPIRE_TEST_ENDPOINT__";
    private const string ValuePrefix = "ASPIRE_TEST_VALUE__";
    private readonly IReadOnlyDictionary<string, string> _values;

    private AspireDeploymentTestConfiguration(IReadOnlyDictionary<string, string> values)
    {
        _values = values;
    }

    /// <summary>Loads deployment test configuration from an Aspire-generated environment file.</summary>
    public static AspireDeploymentTestConfiguration Load(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var absolutePath = Path.GetFullPath(filePath);
        if (!File.Exists(absolutePath))
        {
            throw new FileNotFoundException(
                $"The Aspire deployment test configuration file '{absolutePath}' does not exist.",
                absolutePath);
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(absolutePath))
        {
            var content = line.TrimStart('\uFEFF').TrimStart();
            if (content.Length == 0 || content[0] == '#')
            {
                continue;
            }

            var separator = content.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var key = content[..separator].Trim();
            values[key] = content[(separator + 1)..];
        }

        return new AspireDeploymentTestConfiguration(values);
    }

    /// <summary>
    /// Loads the file identified by <see cref="FilePathEnvironmentVariableName"/>.
    /// </summary>
    public static AspireDeploymentTestConfiguration LoadFromEnvironment()
    {
        var filePath = Environment.GetEnvironmentVariable(FilePathEnvironmentVariableName);
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new InvalidOperationException(
                $"Set {FilePathEnvironmentVariableName} to the Aspire deployment environment file before running external tests.");
        }

        return Load(filePath);
    }

    /// <summary>Gets a required endpoint by its exported test name.</summary>
    public Uri GetEndpoint(string name)
    {
        var value = GetRequiredValue(GetEndpointVariableName(name), "endpoint", name);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var endpoint))
        {
            throw new InvalidOperationException(
                $"The exported test endpoint '{name}' has invalid absolute URI value '{value}'.");
        }

        return endpoint;
    }

    /// <summary>Gets a required configuration value by its exported test name.</summary>
    public string GetValue(string name) =>
        GetRequiredValue(GetValueVariableName(name), "configuration value", name);

    /// <summary>Creates an HTTP client whose base address is an exported endpoint.</summary>
    public HttpClient CreateHttpClient(string endpointName) =>
        new() { BaseAddress = GetEndpoint(endpointName) };

    internal static string GetEndpointVariableName(string name) =>
        EndpointPrefix + NormalizeName(name);

    internal static string GetValueVariableName(string name) =>
        ValuePrefix + NormalizeName(name);

    private string GetRequiredValue(string variableName, string kind, string name)
    {
        if (!_values.TryGetValue(variableName, out var value) || string.IsNullOrEmpty(value))
        {
            throw new InvalidOperationException(
                $"The Aspire deployment did not export a test {kind} named '{name}'.");
        }

        return value;
    }

    private static string NormalizeName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return string.Create(name.Length, name, static (characters, value) =>
        {
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                characters[index] = char.IsAsciiLetterOrDigit(character)
                    ? char.ToUpperInvariant(character)
                    : '_';
            }
        });
    }
}
