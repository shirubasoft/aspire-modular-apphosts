namespace Aspire.Hosting.ModularAppHosts;

internal sealed class ModuleImageSelection
{
    public static ModuleImageSelection All { get; } = new([]);

    public ModuleImageSelection(IEnumerable<string> resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        Resources = resources.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlySet<string> Resources { get; }

    public bool IsScoped => Resources.Count > 0;

    public bool Includes(string resourceName) =>
        !IsScoped || Resources.Contains(resourceName);
}

internal static class ModuleImagePipelineSelectionParser
{
    private static readonly HashSet<string> OptionsWithValues = new(StringComparer.Ordinal)
    {
        "--operation",
        "--step",
        "--output-path",
        "--log-level",
        "--include-exception-details",
        "--environment",
        "--clear-cache",
        "--yes",
        "--dcp-cli-path",
        "--dcp-container-runtime",
        "--dcp-dependency-check-timeout",
        "--dcp-dashboard-path"
    };

    public static ModuleImageSelection GetSelection(
        IReadOnlyList<string> arguments,
        string stepName)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepName);

        var resourceArgumentsStart = -1;
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (string.Equals(argument, "--step", StringComparison.Ordinal) && index + 1 < arguments.Count)
            {
                if (string.Equals(arguments[index + 1], stepName, StringComparison.OrdinalIgnoreCase))
                {
                    resourceArgumentsStart = index + 2;
                }

                break;
            }

            const string stepPrefix = "--step=";
            if (argument.StartsWith(stepPrefix, StringComparison.Ordinal))
            {
                if (string.Equals(argument[stepPrefix.Length..], stepName, StringComparison.OrdinalIgnoreCase))
                {
                    resourceArgumentsStart = index + 1;
                }

                break;
            }
        }

        if (resourceArgumentsStart < 0)
        {
            return ModuleImageSelection.All;
        }

        var resources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var positionalOnly = false;
        for (var index = resourceArgumentsStart; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (positionalOnly)
            {
                resources.Add(argument);
                continue;
            }

            if (string.Equals(argument, "--", StringComparison.Ordinal))
            {
                positionalOnly = true;
                continue;
            }

            if (OptionsWithValues.Contains(argument))
            {
                index++;
                continue;
            }

            if (OptionsWithValues.Any(option => argument.StartsWith($"{option}=", StringComparison.Ordinal)))
            {
                continue;
            }

            if (argument.StartsWith('-'))
            {
                continue;
            }

            resources.Add(argument);
        }

        return resources.Count == 0
            ? ModuleImageSelection.All
            : new ModuleImageSelection(resources);
    }
}
