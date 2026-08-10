using Aspire.Hosting.ModularAppHosts;

namespace Shirubasoft.Aspire.ModularAppHosts.Tool;

internal sealed record ResourceTagOverride(string Module, string Resource, string Tag)
{
    public string Identity => $"{Module}/{Resource}";
}

internal sealed class ManifestTagOverrides
{
    private readonly Dictionary<string, ResourceTagOverride> _resources =
        new(StringComparer.OrdinalIgnoreCase);

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
            if (!_resources.TryAdd(imageOverride.Identity, imageOverride))
            {
                throw new ToolUsageException(
                    $"Resource tag override '{imageOverride.Identity}' is specified more than once.");
            }
        }
    }

    public string? GlobalTag { get; }

    public bool HasOverrides => GlobalTag is not null || _resources.Count > 0;

    public IReadOnlyDictionary<string, string?>? CreateProducerEnvironment(
        IReadOnlyList<ModuleImageDescription> images)
    {
        ArgumentNullException.ThrowIfNull(images);
        if (!HasOverrides)
        {
            return null;
        }

        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var image in images
                     .OrderBy(image => image.Module, StringComparer.Ordinal)
                     .ThenBy(image => image.Resource, StringComparer.Ordinal))
        {
            var tag = GlobalTag;
            var identity = GetIdentity(image.Module, image.Resource);
            if (_resources.TryGetValue(identity, out var resourceTag))
            {
                tag = resourceTag.Tag;
                matched.Add(identity);
            }

            if (tag is null)
            {
                continue;
            }

            var prefix = ModuleImageWorkflowConfiguration.GetResourceKey(
                image.Module,
                image.Resource,
                image.ResourceKind).Replace(":", "__", StringComparison.Ordinal);
            values[$"{prefix}__ImageTag"] = tag;
            values[$"{prefix}__ImageSHA256"] = string.Empty;
        }

        ThrowForUnmatched(matched);
        return values;
    }

    public void Apply(ModuleImageManifestDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.Validate();
        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var image in document.Images)
        {
            var tag = GlobalTag;
            var identity = GetIdentity(image.Module, image.Resource);
            if (_resources.TryGetValue(identity, out var resourceTag))
            {
                tag = resourceTag.Tag;
                matched.Add(identity);
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
        if (!ModuleImageIdentityValidation.IsValidTag(tag))
        {
            throw new ToolUsageException($"Image tag '{tag}' is not a valid OCI distribution tag.");
        }
    }

    private static string GetIdentity(string module, string resource) => $"{module}/{resource}";

    private void ThrowForUnmatched(HashSet<string> matched)
    {
        var unmatched = _resources.Keys
            .Where(key => !matched.Contains(key))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (unmatched.Length > 0)
        {
            throw new ToolUsageException(
                $"Resource tag overrides do not match selected images: {string.Join(", ", unmatched)}.");
        }
    }
}
