using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

internal enum ModuleImageSelectorKind
{
    Auto,
    Resource,
    Module,
    Identity
}

internal sealed record ModuleImageSelector(
    ModuleImageSelectorKind Kind,
    string Name,
    string? Module = null,
    string? Resource = null)
{
    public string DisplayName => Kind switch
    {
        ModuleImageSelectorKind.Module => $"module:{Name}",
        ModuleImageSelectorKind.Resource => $"resource:{Name}",
        _ => Name
    };
}

/// <summary>Parses and resolves module image selectors consistently across AppHost pipelines and tools.</summary>
public sealed class ModuleImageSelection
{
    private const string ModulePrefix = "module:";
    private const string ResourcePrefix = "resource:";

    /// <summary>Gets a selection that includes every available image.</summary>
    public static ModuleImageSelection All { get; } = new([]);

    /// <summary>Creates a selection from module, resource, or <c>module/resource</c> selectors.</summary>
    public ModuleImageSelection(IEnumerable<string> selectors)
    {
        ArgumentNullException.ThrowIfNull(selectors);
        Selectors = selectors
            .Select(ParseSelector)
            .Distinct()
            .ToArray();
    }

    internal IReadOnlyList<ModuleImageSelector> Selectors { get; }

    /// <summary>Gets whether the selection contains one or more selectors.</summary>
    public bool IsScoped => Selectors.Count > 0;

    internal bool Includes(string resourceName) =>
        !IsScoped || Selectors.Any(selector =>
            selector.Kind is ModuleImageSelectorKind.Auto or ModuleImageSelectorKind.Resource &&
            string.Equals(selector.Name, resourceName, StringComparison.OrdinalIgnoreCase));

    internal bool Includes(string moduleName, string declaredResourceName, string effectiveResourceName) =>
        !IsScoped || Selectors.Any(selector => selector.Kind switch
        {
            ModuleImageSelectorKind.Module =>
                string.Equals(selector.Name, moduleName, StringComparison.OrdinalIgnoreCase),
            ModuleImageSelectorKind.Identity =>
                string.Equals(selector.Module, moduleName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(selector.Resource, declaredResourceName, StringComparison.OrdinalIgnoreCase),
            ModuleImageSelectorKind.Resource =>
                NameMatches(declaredResourceName, effectiveResourceName, selector.Name),
            _ =>
                string.Equals(selector.Name, moduleName, StringComparison.OrdinalIgnoreCase) ||
                NameMatches(declaredResourceName, effectiveResourceName, selector.Name)
        });

    /// <summary>Resolves selectors against structured image descriptions.</summary>
    public IReadOnlyList<ModuleImageDescription> ResolveDescriptions(
        IEnumerable<ModuleImageDescription> descriptions,
        string operation)
    {
        ArgumentNullException.ThrowIfNull(descriptions);
        var candidates = descriptions
            .Select(description => new SelectionCandidate<ModuleImageDescription>(
                description,
                description.Module,
                description.Resource,
                description.EffectiveResource))
            .ToArray();
        return Resolve(candidates, operation)
            .OrderBy(description => description.Module, StringComparer.Ordinal)
            .ThenBy(description => description.Resource, StringComparer.Ordinal)
            .ToArray();
    }

    internal IReadOnlySet<IResource> ResolveResources(
        IEnumerable<IResource> resources,
        string operation)
    {
        ArgumentNullException.ThrowIfNull(resources);
        var candidates = resources
            .Distinct()
            .Select(resource => new SelectionCandidate<IResource>(
                resource,
                GetModuleName(resource),
                GetDeclaredResourceName(resource),
                resource.Name))
            .ToArray();
        return Resolve(candidates, operation).ToHashSet();
    }

    private HashSet<T> Resolve<T>(
        IReadOnlyList<SelectionCandidate<T>> available,
        string operation)
        where T : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        if (!IsScoped)
        {
            return available.Select(candidate => candidate.Value).ToHashSet();
        }

        var selected = new HashSet<T>();
        var unknown = new List<string>();
        foreach (var selector in Selectors)
        {
            var moduleMatches = available.Where(candidate =>
                string.Equals(candidate.Module, selector.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
            var resourceMatches = available.Where(candidate =>
                NameMatches(candidate.DeclaredResource, candidate.EffectiveResource, selector.Name)).ToArray();
            SelectionCandidate<T>[] matches;
            switch (selector.Kind)
            {
                case ModuleImageSelectorKind.Module:
                    matches = moduleMatches;
                    break;
                case ModuleImageSelectorKind.Resource:
                    matches = resourceMatches;
                    break;
                case ModuleImageSelectorKind.Identity:
                    matches = available.Where(candidate =>
                        string.Equals(candidate.Module, selector.Module, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(candidate.DeclaredResource, selector.Resource, StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                    break;
                default:
                    if (moduleMatches.Length > 0 && resourceMatches.Length > 0)
                    {
                        throw CreateAmbiguousException(selector.Name, moduleMatches, resourceMatches);
                    }

                    if (resourceMatches.Select(candidate => candidate.Identity)
                        .Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any())
                    {
                        throw CreateAmbiguousException(selector.Name, [], resourceMatches);
                    }

                    matches = moduleMatches.Length > 0 ? moduleMatches : resourceMatches;
                    break;
            }

            if (matches.Length == 0)
            {
                unknown.Add(selector.DisplayName);
                continue;
            }

            selected.UnionWith(matches.Select(candidate => candidate.Value));
        }

        if (unknown.Count > 0)
        {
            var availableModules = available
                .Select(candidate => candidate.Module)
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase);
            throw new InvalidOperationException(
                $"The following selectors do not match {operation}: {string.Join(", ", unknown)}. " +
                $"Available image resources: {FormatAvailable(available.SelectMany(candidate => candidate.Names))}. " +
                $"Available modules: {FormatAvailable(availableModules)}.");
        }

        return selected;
    }

    internal static bool NameMatches(IResource resource, string name) =>
        GetNames(resource).Contains(name, StringComparer.OrdinalIgnoreCase);

    internal static IEnumerable<string> GetNames(IResource resource)
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

    internal static string? GetModuleName(IResource resource) =>
        resource.Annotations
            .OfType<DistributedApplicationModuleResourceAnnotation>()
            .LastOrDefault()
            ?.ModuleName ??
        resource.Annotations.OfType<ModuleImagePublisherAnnotation>().LastOrDefault()?.ModuleName;

    private static string GetDeclaredResourceName(IResource resource) =>
        resource.Annotations
            .OfType<DistributedApplicationModuleResourceAnnotation>()
            .LastOrDefault()
            ?.ResourceName ??
        resource.Annotations.OfType<ModuleImagePublisherAnnotation>().LastOrDefault()?.ResourceName ??
        resource.Name;

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

        var separator = selector.IndexOf('/', StringComparison.Ordinal);
        if (separator > 0 && separator < selector.Length - 1 &&
            selector.IndexOf('/', separator + 1) < 0)
        {
            return new ModuleImageSelector(
                ModuleImageSelectorKind.Identity,
                selector,
                selector[..separator],
                selector[(separator + 1)..]);
        }

        return new ModuleImageSelector(ModuleImageSelectorKind.Auto, selector);
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

    private static bool NameMatches(
        string declaredResourceName,
        string effectiveResourceName,
        string selector) =>
        string.Equals(declaredResourceName, selector, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(effectiveResourceName, selector, StringComparison.OrdinalIgnoreCase);

    private static string FormatAvailable(IEnumerable<string> values)
    {
        var materialized = values
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return materialized.Length == 0 ? "none" : string.Join(", ", materialized);
    }

    private static InvalidOperationException CreateAmbiguousException<T>(
        string selector,
        IEnumerable<SelectionCandidate<T>> moduleMatches,
        IEnumerable<SelectionCandidate<T>> resourceMatches)
        where T : notnull
    {
        var identities = moduleMatches.Concat(resourceMatches)
            .Select(candidate => candidate.Identity)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase);
        return new InvalidOperationException(
            $"Image selector '{selector}' is ambiguous. Use module:{selector}, resource:{selector}, " +
            $"or one of: {string.Join(", ", identities)}.");
    }

    private sealed record SelectionCandidate<T>(
        T Value,
        string? Module,
        string DeclaredResource,
        string EffectiveResource)
        where T : notnull
    {
        public string Identity => Module is null ? DeclaredResource : $"{Module}/{DeclaredResource}";

        public IEnumerable<string> Names =>
            new[] { DeclaredResource, EffectiveResource }
                .Distinct(StringComparer.OrdinalIgnoreCase);
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
