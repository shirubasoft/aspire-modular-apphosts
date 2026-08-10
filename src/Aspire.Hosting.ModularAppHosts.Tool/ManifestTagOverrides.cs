using Aspire.Hosting.ModularAppHosts;

namespace Shirubasoft.Aspire.ModularAppHosts.Tool;

internal sealed record ResourceTagOverride(string Module, string Resource, string Tag)
{
    public string Identity => $"{Module}/{Resource}";
}

internal sealed class ManifestTagOverrides
{
    private readonly Dictionary<(string Module, string Resource), ResourceTagOverride> _resources =
        new(ModuleResourceKeyComparer.Instance);

    public ManifestTagOverrides(string? globalTag, IEnumerable<string> resourceTags)
    {
        ArgumentNullException.ThrowIfNull(resourceTags);
        GlobalTag = string.IsNullOrWhiteSpace(globalTag) ? null : globalTag;
        if (GlobalTag is not null)
        {
            ValidateTag(GlobalTag);
        }

        foreach (var value in resourceTags)
        {
            var imageOverride = Parse(value);
            if (!_resources.TryAdd((imageOverride.Module, imageOverride.Resource), imageOverride))
            {
                throw new ToolUsageException(
                    $"Resource tag override '{imageOverride.Identity}' is specified more than once.");
            }
        }
    }

    public string? GlobalTag { get; }

    public bool HasOverrides => GlobalTag is not null || _resources.Count > 0;

    public void Apply(ModuleImageManifestDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.Validate();
        var matched = new HashSet<(string Module, string Resource)>(ModuleResourceKeyComparer.Instance);
        foreach (var image in document.Images)
        {
            var tag = GlobalTag;
            if (_resources.TryGetValue((image.Module, image.Resource), out var resourceTag))
            {
                tag = resourceTag.Tag;
                matched.Add((image.Module, image.Resource));
            }

            if (tag is not null)
            {
                image.Tag = tag;
                image.Digest = null;
            }
        }

        ThrowForUnmatched(matched);
        document.Validate();
    }

    public IReadOnlyDictionary<string, string?> CreateProducerEnvironment(
        IReadOnlyList<ModuleImageDescription> images)
    {
        ArgumentNullException.ThrowIfNull(images);
        var matched = new HashSet<(string Module, string Resource)>(ModuleResourceKeyComparer.Instance);
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        var index = 0;
        foreach (var image in images
                     .OrderBy(image => image.Module, StringComparer.Ordinal)
                     .ThenBy(image => image.Resource, StringComparer.Ordinal))
        {
            var tag = GlobalTag;
            if (_resources.TryGetValue((image.Module, image.Resource), out var resourceTag))
            {
                tag = resourceTag.Tag;
                matched.Add((image.Module, image.Resource));
            }

            if (tag is null)
            {
                continue;
            }

            var prefix = WorkflowImageEnvironment.GetPrefix(index++);
            values[$"{prefix}__Module"] = image.Module;
            values[$"{prefix}__Resource"] = image.Resource;
            values[$"{prefix}__ResourceKind"] = image.ResourceKind.ToString();
            values[$"{prefix}__Tag"] = tag;
        }

        ThrowForUnmatched(matched);
        return values;
    }

    private static ResourceTagOverride Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var equals = value.IndexOf('=', StringComparison.Ordinal);
        var slash = value.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || equals <= slash + 1 || equals == value.Length - 1)
        {
            throw new ToolUsageException(
                $"Resource tag override '{value}' must use the form <module>/<resource>=<tag>.");
        }

        var module = value[..slash];
        var resource = value[(slash + 1)..equals];
        var tag = value[(equals + 1)..];
        ValidateTag(tag);
        return new ResourceTagOverride(module, resource, tag);
    }

    private static void ValidateTag(string tag)
    {
        var document = new ModuleImageManifestDocument();
        document.Images.Add(new ModuleImageManifestEntry
        {
            Module = "validation",
            Resource = "validation",
            ResourceKind = ModuleResourceKind.Container,
            Registry = "registry.invalid",
            Repository = "validation",
            Tag = tag
        });
        try
        {
            document.Validate();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
        {
            throw new ToolUsageException(exception.Message, exception);
        }
    }

    private void ThrowForUnmatched(HashSet<(string Module, string Resource)> matched)
    {
        var unmatched = _resources.Keys
            .Where(key => !matched.Contains(key))
            .Select(key => $"{key.Module}/{key.Resource}")
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (unmatched.Length > 0)
        {
            throw new ToolUsageException(
                $"Resource tag overrides do not match selected images: {string.Join(", ", unmatched)}.");
        }
    }
}

internal sealed class ModuleResourceKeyComparer : IEqualityComparer<(string Module, string Resource)>
{
    public static ModuleResourceKeyComparer Instance { get; } = new();

    public bool Equals(
        (string Module, string Resource) x,
        (string Module, string Resource) y) =>
        string.Equals(x.Module, y.Module, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(x.Resource, y.Resource, StringComparison.OrdinalIgnoreCase);

    public int GetHashCode((string Module, string Resource) obj) =>
        HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Module),
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Resource));
}
