using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shirubasoft.Aspire.ModularAppHosts.Tool;

internal sealed class PreviewWorkflowSettings
{
    internal const string SchemaUri =
        "https://raw.githubusercontent.com/shirubasoft/aspire-modular-apphosts/main/schemas/preview-workflow-settings.schema.json";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private static readonly string[] PhaseNames =
    [
        "beforeCheckout",
        "afterCheckout",
        "beforeContract",
        "beforeProduce",
        "beforeTrigger"
    ];

    [JsonPropertyName("$schema")]
    public string? Schema { get; init; }

    public JsonElement RunsOn { get; init; }

    public PreviewWorkflowDotNetSettings? Dotnet { get; init; }

    public PreviewWorkflowCheckoutSettings? Checkout { get; init; }

    public PreviewWorkflowHookSettings? Steps { get; init; }

    [JsonIgnore]
    public PreviewWorkflowRunner Runner { get; private set; } = PreviewWorkflowRunner.Default;

    [JsonIgnore]
    public PreviewWorkflowDotNetSetup DotNetSetup { get; private set; } =
        PreviewWorkflowDotNetSetup.FromGlobalJson("global.json");

    public static PreviewWorkflowSettings Default { get; } = new();

    public static async Task<PreviewWorkflowSettings> LoadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        EnsureNoDuplicateProperties(bytes);

        PreviewWorkflowSettings settings;
        try
        {
            settings = JsonSerializer.Deserialize<PreviewWorkflowSettings>(bytes, SerializerOptions)
                ?? throw new PreviewToolException($"Workflow settings '{path}' contain JSON null.");
        }
        catch (JsonException exception)
        {
            throw new PreviewToolException(
                $"Workflow settings '{path}' are invalid: {exception.Message}",
                exception);
        }

        settings.Validate();
        return settings;
    }

    public IReadOnlyList<PreviewWorkflowStep> GetSteps(PreviewWorkflowHookPhase phase) =>
        phase switch
        {
            PreviewWorkflowHookPhase.BeforeCheckout => Steps?.BeforeCheckout ?? [],
            PreviewWorkflowHookPhase.AfterCheckout => Steps?.AfterCheckout ?? [],
            PreviewWorkflowHookPhase.BeforeContract => Steps?.BeforeContract ?? [],
            PreviewWorkflowHookPhase.BeforeProduce => Steps?.BeforeProduce ?? [],
            PreviewWorkflowHookPhase.BeforeTrigger => Steps?.BeforeTrigger ?? [],
            _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, null)
        };

    private void Validate()
    {
        if (!string.Equals(Schema, SchemaUri, StringComparison.Ordinal))
        {
            throw new PreviewToolException(
                $"Workflow settings $schema must be '{SchemaUri}'.");
        }

        Runner = ParseRunner(RunsOn);
        DotNetSetup = ParseDotNetSetup(Dotnet);
        ValidateCheckout(Checkout);
        ValidateSteps();
    }

    private static PreviewWorkflowRunner ParseRunner(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
        {
            throw new PreviewToolException("Workflow settings must define runsOn.");
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return PreviewWorkflowRunner.FromLabel(
                ValidateScalar(value.GetString(), "runsOn"));
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            var arrayLabels = value.EnumerateArray()
                .Select((label, index) => label.ValueKind == JsonValueKind.String
                    ? ValidateScalar(label.GetString(), $"runsOn[{index}]")
                    : throw new PreviewToolException($"Workflow settings runsOn[{index}] must be a string."))
                .ToArray();
            if (arrayLabels.Length == 0)
            {
                throw new PreviewToolException("Workflow settings runsOn label array cannot be empty.");
            }

            return PreviewWorkflowRunner.FromLabels(arrayLabels);
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new PreviewToolException(
                "Workflow settings runsOn must be a string, a label array, or a group object.");
        }

        string? group = null;
        string[]? labels = null;
        foreach (var property in value.EnumerateObject())
        {
            switch (property.Name)
            {
                case "group":
                    if (property.Value.ValueKind != JsonValueKind.String)
                    {
                        throw new PreviewToolException("Workflow settings runsOn.group must be a string.");
                    }

                    group = ValidateScalar(property.Value.GetString(), "runsOn.group");
                    break;
                case "labels":
                    if (property.Value.ValueKind != JsonValueKind.Array)
                    {
                        throw new PreviewToolException("Workflow settings runsOn.labels must be an array.");
                    }

                    labels = property.Value.EnumerateArray()
                        .Select((label, index) => label.ValueKind == JsonValueKind.String
                            ? ValidateScalar(label.GetString(), $"runsOn.labels[{index}]")
                            : throw new PreviewToolException(
                                $"Workflow settings runsOn.labels[{index}] must be a string."))
                        .ToArray();
                    if (labels.Length == 0)
                    {
                        throw new PreviewToolException("Workflow settings runsOn.labels cannot be empty.");
                    }

                    break;
                default:
                    throw new PreviewToolException(
                        $"Workflow settings runsOn contains unsupported property '{property.Name}'.");
            }
        }

        if (group is null)
        {
            throw new PreviewToolException("Workflow settings runsOn.group is required for a group object.");
        }

        return PreviewWorkflowRunner.FromGroup(group, labels ?? []);
    }

    private static PreviewWorkflowDotNetSetup ParseDotNetSetup(
        PreviewWorkflowDotNetSettings? dotnet)
    {
        if (dotnet is null)
        {
            throw new PreviewToolException("Workflow settings must define dotnet.");
        }

        var selected = (dotnet.GlobalJson is not null ? 1 : 0) +
            (dotnet.Version is not null ? 1 : 0) +
            (dotnet.Skip.HasValue ? 1 : 0);
        if (selected != 1 || dotnet.Skip is false)
        {
            throw new PreviewToolException(
                "Workflow settings dotnet must select exactly one of globalJson, version, or skip: true.");
        }

        if (dotnet.GlobalJson is not null)
        {
            return PreviewWorkflowDotNetSetup.FromGlobalJson(
                ValidateRepositoryPath(dotnet.GlobalJson, "dotnet.globalJson"));
        }

        if (dotnet.Version is not null)
        {
            return PreviewWorkflowDotNetSetup.FromVersion(
                ValidateScalar(dotnet.Version, "dotnet.version"));
        }

        return PreviewWorkflowDotNetSetup.Skip;
    }

    private static void ValidateCheckout(PreviewWorkflowCheckoutSettings? checkout)
    {
        if (checkout?.Token is null)
        {
            return;
        }

        var token = ValidateScalar(checkout.Token, "checkout.token");
        if (!token.StartsWith("${{", StringComparison.Ordinal) ||
            !token.EndsWith("}}", StringComparison.Ordinal))
        {
            throw new PreviewToolException(
                "Workflow settings checkout.token must be a GitHub Actions expression delimited by '${{' and '}}'.");
        }
    }

    private void ValidateSteps()
    {
        var identifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "trigger"
        };

        foreach (var phase in Enum.GetValues<PreviewWorkflowHookPhase>())
        {
            var steps = GetSteps(phase);
            for (var index = 0; index < steps.Count; index++)
            {
                ValidateStep(steps[index], $"steps.{PhaseNames[(int)phase]}[{index}]", identifiers);
            }
        }
    }

    private static void ValidateStep(
        PreviewWorkflowStep step,
        string location,
        HashSet<string> identifiers)
    {
        var uses = step.Uses is not null;
        var run = step.Run is not null;
        if (uses == run)
        {
            throw new PreviewToolException(
                $"Workflow settings {location} must define exactly one of uses or run.");
        }

        if (step.Id is not null)
        {
            var id = ValidateScalar(step.Id, $"{location}.id");
            if (!(char.IsAsciiLetter(id[0]) || id[0] == '_') ||
                id.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-')))
            {
                throw new PreviewToolException(
                    $"Workflow settings {location}.id must contain only ASCII letters, digits, underscores, and hyphens, and cannot start with a digit.");
            }

            if (!identifiers.Add(id))
            {
                throw new PreviewToolException(
                    $"Workflow settings step id '{id}' is duplicate or reserved by the generated workflow.");
            }
        }

        ValidateOptionalScalar(step.Name, $"{location}.name");
        ValidateOptionalScalar(step.Uses, $"{location}.uses");
        ValidateOptionalScalar(step.If, $"{location}.if");
        ValidateOptionalScalar(step.Shell, $"{location}.shell");
        ValidateOptionalScalar(step.WorkingDirectory, $"{location}.working-directory");
        if (run)
        {
            ValidateRun(step.Run!, $"{location}.run");
        }

        if (uses && (step.Shell is not null || step.WorkingDirectory is not null))
        {
            throw new PreviewToolException(
                $"Workflow settings {location} cannot set shell or working-directory for a uses step.");
        }

        if (run && step.With.Count > 0)
        {
            throw new PreviewToolException(
                $"Workflow settings {location} cannot set with for a run step.");
        }

        foreach (var pair in step.With)
        {
            ValidateMappingName(pair.Key, $"{location}.with");
            ValidateScalar(pair.Value, $"{location}.with.{pair.Key}");
        }

        foreach (var pair in step.Env)
        {
            ValidateEnvironmentVariableName(pair.Key, $"{location}.env");
            ValidateScalar(pair.Value, $"{location}.env.{pair.Key}");
        }
    }

    private static void ValidateMappingName(string value, string location)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !(char.IsAsciiLetter(value[0]) || value[0] == '_') ||
            value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-')))
        {
            throw new PreviewToolException(
                $"Workflow settings {location} keys must contain only ASCII letters, digits, underscores, and hyphens, and cannot start with a digit.");
        }
    }

    private static void ValidateEnvironmentVariableName(string value, string location)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !(char.IsAsciiLetter(value[0]) || value[0] == '_') ||
            value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '_')))
        {
            throw new PreviewToolException(
                $"Workflow settings {location} keys must contain only ASCII letters, digits, and underscores, and cannot start with a digit.");
        }
    }

    private static string ValidateRepositoryPath(string value, string location)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            Path.IsPathRooted(value) ||
            value.Any(char.IsControl))
        {
            throw new PreviewToolException(
                $"Workflow settings {location} must be a repository-relative path without control characters.");
        }

        var normalized = value.Replace('\\', '/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
        {
            throw new PreviewToolException(
                $"Workflow settings {location} must be a repository-relative path without '.' or '..' segments.");
        }

        return string.Join('/', segments);
    }

    private static void ValidateOptionalScalar(string? value, string location)
    {
        if (value is not null)
        {
            ValidateScalar(value, location);
        }
    }

    private static string ValidateScalar(string? value, string location)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(character => char.IsControl(character)))
        {
            throw new PreviewToolException(
                $"Workflow settings {location} must be a non-empty single-line string without control characters.");
        }

        return value;
    }

    private static void ValidateRun(string value, string location)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Any(character => char.IsControl(character) && character is not '\r' and not '\n' and not '\t'))
        {
            throw new PreviewToolException(
                $"Workflow settings {location} must be a non-empty command without unsupported control characters.");
        }
    }

    private static void EnsureNoDuplicateProperties(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json, new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Disallow
        });
        var objectProperties = new Stack<HashSet<string>>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                objectProperties.Push(new HashSet<string>(StringComparer.Ordinal));
            }
            else if (reader.TokenType == JsonTokenType.EndObject)
            {
                objectProperties.Pop();
            }
            else if (reader.TokenType == JsonTokenType.PropertyName &&
                !objectProperties.Peek().Add(reader.GetString()!))
            {
                throw new PreviewToolException(
                    $"Workflow settings contain duplicate property '{reader.GetString()}'.");
            }
            else if (reader.TokenType == JsonTokenType.Null)
            {
                throw new PreviewToolException("Workflow settings cannot contain JSON null values.");
            }
        }
    }
}

internal sealed class PreviewWorkflowDotNetSettings
{
    public string? GlobalJson { get; init; }

    public string? Version { get; init; }

    public bool? Skip { get; init; }
}

internal sealed class PreviewWorkflowCheckoutSettings
{
    public string? Token { get; init; }
}

internal sealed class PreviewWorkflowHookSettings
{
    public List<PreviewWorkflowStep> BeforeCheckout { get; init; } = [];

    public List<PreviewWorkflowStep> AfterCheckout { get; init; } = [];

    public List<PreviewWorkflowStep> BeforeContract { get; init; } = [];

    public List<PreviewWorkflowStep> BeforeProduce { get; init; } = [];

    public List<PreviewWorkflowStep> BeforeTrigger { get; init; } = [];
}

internal sealed class PreviewWorkflowStep
{
    public string? Id { get; init; }

    public string? Name { get; init; }

    public string? Uses { get; init; }

    public string? Run { get; init; }

    public SortedDictionary<string, string> With { get; init; } = new(StringComparer.Ordinal);

    public SortedDictionary<string, string> Env { get; init; } = new(StringComparer.Ordinal);

    [JsonPropertyName("if")]
    public string? If { get; init; }

    public string? Shell { get; init; }

    [JsonPropertyName("working-directory")]
    public string? WorkingDirectory { get; init; }
}

internal enum PreviewWorkflowHookPhase
{
    BeforeCheckout,
    AfterCheckout,
    BeforeContract,
    BeforeProduce,
    BeforeTrigger
}

internal sealed record PreviewWorkflowRunner(
    string? Label,
    IReadOnlyList<string> Labels,
    string? Group)
{
    public static PreviewWorkflowRunner Default { get; } = FromLabel("ubuntu-latest");

    public static PreviewWorkflowRunner FromLabel(string label) => new(label, [], null);

    public static PreviewWorkflowRunner FromLabels(IReadOnlyList<string> labels) => new(null, labels, null);

    public static PreviewWorkflowRunner FromGroup(string group, IReadOnlyList<string> labels) =>
        new(null, labels, group);
}

internal sealed record PreviewWorkflowDotNetSetup(
    string? GlobalJson,
    string? Version,
    bool IsSkipped)
{
    public static PreviewWorkflowDotNetSetup Skip { get; } = new(null, null, IsSkipped: true);

    public static PreviewWorkflowDotNetSetup FromGlobalJson(string path) => new(path, null, IsSkipped: false);

    public static PreviewWorkflowDotNetSetup FromVersion(string version) => new(null, version, IsSkipped: false);
}
