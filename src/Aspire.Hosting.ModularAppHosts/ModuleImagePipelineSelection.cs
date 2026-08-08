using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.ModularAppHosts;

internal enum ModuleImageSelectorKind
{
    Resource,
    Module
}

internal sealed record ModuleImageSelector(ModuleImageSelectorKind Kind, string Name)
{
    public string DisplayName => Kind switch
    {
        ModuleImageSelectorKind.Module => $"module:{Name}",
        ModuleImageSelectorKind.Resource => $"resource:{Name}",
        _ => $"resource:{Name}"
    };
}

internal sealed class ModuleImageSelection
{
    private const string ModulePrefix = "module:";
    private const string ResourcePrefix = "resource:";

    public static ModuleImageSelection All { get; } = new([]);

    public ModuleImageSelection(IEnumerable<string> selectors)
    {
        ArgumentNullException.ThrowIfNull(selectors);
        Selectors = selectors
            .Select(ParseSelector)
            .Distinct()
            .ToArray();
    }

    public IReadOnlyList<ModuleImageSelector> Selectors { get; }

    public bool IsScoped => Selectors.Count > 0;

    public bool Includes(string resourceName) =>
        !IsScoped || Selectors.Any(selector =>
            selector.Kind != ModuleImageSelectorKind.Module &&
            string.Equals(selector.Name, resourceName, StringComparison.OrdinalIgnoreCase));

    public bool Includes(IResource resource) =>
        !IsScoped || Selectors.Any(selector => MatchesResource(selector, resource));

    public bool Includes(string moduleName, string declaredResourceName, string effectiveResourceName) =>
        !IsScoped || Selectors.Any(selector => selector.Kind switch
        {
            ModuleImageSelectorKind.Module =>
                string.Equals(selector.Name, moduleName, StringComparison.OrdinalIgnoreCase),
            ModuleImageSelectorKind.Resource =>
                NameMatches(declaredResourceName, effectiveResourceName, selector.Name),
            _ => NameMatches(declaredResourceName, effectiveResourceName, selector.Name)
        });

    public IReadOnlySet<IResource> ResolveResources(
        IEnumerable<IResource> resources,
        string operation)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        var available = resources.Distinct().ToArray();
        if (!IsScoped)
        {
            return available.ToHashSet();
        }

        var selected = new HashSet<IResource>();
        var unknown = new List<string>();
        foreach (var selector in Selectors)
        {
            var resourceMatches = selector.Kind == ModuleImageSelectorKind.Module
                ? []
                : available.Where(resource => NameMatches(resource, selector.Name)).ToArray();
            if (resourceMatches.Length > 0)
            {
                selected.UnionWith(resourceMatches);
                continue;
            }

            if (selector.Kind != ModuleImageSelectorKind.Module)
            {
                unknown.Add(selector.DisplayName);
                continue;
            }

            var moduleMatches = available
                .Where(resource => string.Equals(
                    GetModuleName(resource),
                    selector.Name,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (moduleMatches.Length > 0)
            {
                selected.UnionWith(moduleMatches);
            }
            else
            {
                unknown.Add(selector.DisplayName);
            }
        }

        if (unknown.Count > 0)
        {
            var availableResources = GetAvailableResourceNames(available);
            var availableModules = available
                .Select(GetModuleName)
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase);
            throw new InvalidOperationException(
                $"The following selectors do not match {operation}: {string.Join(", ", unknown)}. " +
                $"Available image resources: {FormatAvailable(availableResources)}. " +
                $"Available modules: {FormatAvailable(availableModules)}.");
        }

        return selected;
    }

    public static bool NameMatches(IResource resource, string name) =>
        GetNames(resource).Contains(name, StringComparer.OrdinalIgnoreCase);

    public static IEnumerable<string> GetNames(IResource resource)
    {
        yield return resource.Name;
        var moduleResource = resource.Annotations
            .OfType<DistributedApplicationModuleResourceAnnotation>()
            .LastOrDefault();
        if (moduleResource is not null)
        {
            yield return moduleResource.ResourceName;
            yield break;
        }

        var publisher = resource.Annotations.OfType<ModuleImagePublisherAnnotation>().LastOrDefault();
        if (publisher is not null)
        {
            yield return publisher.ResourceName;
        }
    }

    public static string? GetModuleName(IResource resource) =>
        resource.Annotations
            .OfType<DistributedApplicationModuleResourceAnnotation>()
            .LastOrDefault()
            ?.ModuleName ??
        resource.Annotations.OfType<ModuleImagePublisherAnnotation>().LastOrDefault()?.ModuleName;

    private static ModuleImageSelector ParseSelector(string selector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        if (selector.StartsWith(ModulePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return CreateSelector(ModuleImageSelectorKind.Module, selector, ModulePrefix.Length);
        }

        if (selector.StartsWith(ResourcePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return CreateSelector(ModuleImageSelectorKind.Resource, selector, ResourcePrefix.Length);
        }

        return new ModuleImageSelector(ModuleImageSelectorKind.Resource, selector);
    }

    private static ModuleImageSelector CreateSelector(
        ModuleImageSelectorKind kind,
        string selector,
        int prefixLength)
    {
        var name = selector[prefixLength..];
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException(
                $"Image selector '{selector}' must include a name after the prefix.");
        }

        return new ModuleImageSelector(kind, name);
    }

    private static bool MatchesResource(ModuleImageSelector selector, IResource resource) =>
        selector.Kind switch
        {
            ModuleImageSelectorKind.Module => string.Equals(
                selector.Name,
                GetModuleName(resource),
                StringComparison.OrdinalIgnoreCase),
            _ => NameMatches(resource, selector.Name)
        };

    private static bool NameMatches(
        string declaredResourceName,
        string effectiveResourceName,
        string selector) =>
        string.Equals(declaredResourceName, selector, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(effectiveResourceName, selector, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> GetAvailableResourceNames(IEnumerable<IResource> resources) =>
        resources
            .SelectMany(GetNames)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase);

    private static string FormatAvailable(IEnumerable<string> values)
    {
        var materialized = values.ToArray();
        return materialized.Length == 0 ? "none" : string.Join(", ", materialized);
    }
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

        var selectors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var positionalOnly = false;
        for (var index = resourceArgumentsStart; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (positionalOnly)
            {
                selectors.Add(argument);
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

            selectors.Add(argument);
        }

        return selectors.Count == 0
            ? ModuleImageSelection.All
            : new ModuleImageSelection(selectors);
    }

    public static string? GetRequestedStep(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        for (var index = 0; index < arguments.Count; index++)
        {
            if (string.Equals(arguments[index], "--step", StringComparison.Ordinal) && index + 1 < arguments.Count)
            {
                return arguments[index + 1];
            }

            const string prefix = "--step=";
            if (arguments[index].StartsWith(prefix, StringComparison.Ordinal))
            {
                return arguments[index][prefix.Length..];
            }
        }

        return null;
    }
}
