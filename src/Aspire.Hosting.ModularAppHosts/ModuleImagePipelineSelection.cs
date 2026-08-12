using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

internal enum ModuleImageSelectorKind
{
    Resource,
    Module
}

internal sealed record ModuleImageSelector(
    ModuleImageSelectorKind Kind,
    string Name);

/// <summary>Parses and resolves module image selectors consistently across AppHost pipelines and tools.</summary>
public sealed class ModuleImageSelection
{
    /// <summary>Gets a selection that includes every available image.</summary>
    public static ModuleImageSelection All { get; } = new([], []);

    /// <summary>Creates a selection from explicit module and resource names.</summary>
    public ModuleImageSelection(
        IEnumerable<string> modules,
        IEnumerable<string> resources)
    {
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(resources);
        Selectors = modules
            .Select(name => CreateSelector(ModuleImageSelectorKind.Module, name))
            .Concat(resources.Select(name => CreateSelector(ModuleImageSelectorKind.Resource, name)))
            .Distinct()
            .ToArray();
    }

    internal IReadOnlyList<ModuleImageSelector> Selectors { get; }

    /// <summary>Gets whether the selection contains one or more selectors.</summary>
    public bool IsScoped => Selectors.Count > 0;

    internal bool Includes(string resourceName) =>
        !IsScoped || Selectors.Any(selector =>
            selector.Kind == ModuleImageSelectorKind.Resource &&
            string.Equals(selector.Name, resourceName, StringComparison.OrdinalIgnoreCase));

    internal bool Includes(string moduleName, string declaredResourceName, string effectiveResourceName) =>
        !IsScoped || Selectors.Any(selector => selector.Kind switch
        {
            ModuleImageSelectorKind.Module =>
                string.Equals(selector.Name, moduleName, StringComparison.OrdinalIgnoreCase),
            ModuleImageSelectorKind.Resource =>
                NameMatches(declaredResourceName, effectiveResourceName, selector.Name),
            _ => throw new InvalidOperationException($"Unsupported image selector kind '{selector.Kind}'.")
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
            var matches = selector.Kind switch
            {
                ModuleImageSelectorKind.Module => available.Where(candidate =>
                    string.Equals(candidate.Module, selector.Name, StringComparison.OrdinalIgnoreCase)).ToArray(),
                ModuleImageSelectorKind.Resource => available.Where(candidate =>
                    NameMatches(candidate.DeclaredResource, candidate.EffectiveResource, selector.Name)).ToArray(),
                _ => throw new InvalidOperationException($"Unsupported image selector kind '{selector.Kind}'.")
            };

            if (matches.Length == 0)
            {
                var selectorKind = selector.Kind == ModuleImageSelectorKind.Module ? "module" : "resource";
                unknown.Add($"{selectorKind} '{selector.Name}'");
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

    private static ModuleImageSelector CreateSelector(
        ModuleImageSelectorKind kind,
        string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new ModuleImageSelector(kind, name.Trim());
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

    private sealed record SelectionCandidate<T>(
        T Value,
        string? Module,
        string DeclaredResource,
        string EffectiveResource)
        where T : notnull
    {
        public IEnumerable<string> Names =>
            new[] { DeclaredResource, EffectiveResource }
                .Distinct(StringComparer.OrdinalIgnoreCase);
    }
}
