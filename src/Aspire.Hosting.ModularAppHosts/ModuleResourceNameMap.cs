namespace Aspire.Hosting;

internal sealed class ModuleResourceNameMap
{
    private readonly Dictionary<string, string> _resourceNames;

    public ModuleResourceNameMap(
        DistributedApplicationModule module,
        ModuleImportOptions? options)
    {
        var definitions = module.ResourceDefinitions
            .Select(definition => definition.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknownAlias = options?.ResourceAliases.Keys.FirstOrDefault(alias => !definitions.Contains(alias));
        if (unknownAlias is not null)
        {
            throw new InvalidOperationException(
                $"Import options for module '{module.Name}' alias unknown resource '{unknownAlias}'. " +
                $"Available resources: {string.Join(", ", definitions.Order(StringComparer.OrdinalIgnoreCase))}.");
        }

        var prefix = options?.ResourcePrefix ?? string.Empty;
        _resourceNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in module.ResourceDefinitions)
        {
            var resourceName = options?.ResourceAliases.TryGetValue(definition.Name, out var alias) == true
                ? alias
                : $"{prefix}{definition.Name}";
            if (string.IsNullOrWhiteSpace(resourceName))
            {
                throw new InvalidOperationException(
                    $"Import options for module '{module.Name}' map resource '{definition.Name}' to an empty name.");
            }

            var duplicate = _resourceNames.FirstOrDefault(pair =>
                string.Equals(pair.Value, resourceName, StringComparison.OrdinalIgnoreCase));
            if (duplicate.Key is not null)
            {
                throw new InvalidOperationException(
                    $"Import options for module '{module.Name}' map both '{duplicate.Key}' and '{definition.Name}' " +
                    $"to resource name '{resourceName}'.");
            }

            _resourceNames.Add(definition.Name, resourceName);
        }
    }

    public string this[string declaredName] => _resourceNames[declaredName];
}
