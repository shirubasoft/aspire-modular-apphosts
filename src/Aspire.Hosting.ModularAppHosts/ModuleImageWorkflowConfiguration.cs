using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

namespace Aspire.Hosting;

/// <summary>Projects a workflow image manifest into standard modular AppHost configuration.</summary>
public static class ModuleImageWorkflowConfiguration
{
    /// <summary>
    /// Configuration section containing the effective resource names selected for a workflow image publish.
    /// </summary>
    public const string SelectionConfigurationSectionName =
        "Aspire:ModularAppHosts:Workflow:Resources";

    /// <summary>The configuration section used by the workflow image pipeline.</summary>
    public const string ConfigurationSectionName = "Aspire:ModularAppHosts:Workflow";

    /// <summary>The workflow configuration key for a tag applied to every selected image.</summary>
    public const string TagConfigurationName = "Tag";

    /// <summary>The workflow configuration key for JSON module/resource tag overrides.</summary>
    public const string ResourceTagsConfigurationName = "ResourceTags";

    internal static ModuleImageWorkflowOptions Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var selectors = configuration
            .GetSection(SelectionConfigurationSectionName)
            .Get<string[]>() ?? [];
        var globalTag = GetConfiguredValue(
            configuration[ConfigurationPath.Combine(ConfigurationSectionName, TagConfigurationName)]);
        ValidateTag(globalTag);
        var resourceTagsJson = GetConfiguredValue(
            configuration[ConfigurationPath.Combine(ConfigurationSectionName, ResourceTagsConfigurationName)]);
        Dictionary<string, string> resourceTags;
        try
        {
            resourceTags = resourceTagsJson is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : JsonSerializer.Deserialize<Dictionary<string, string>>(resourceTagsJson)
                    ?? throw new InvalidDataException("Workflow resource tags must be a JSON object.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Workflow resource tags must be a JSON object.", exception);
        }

        var normalizedTags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (identity, tag) in resourceTags)
        {
            var normalizedIdentity = ValidateIdentity(identity);
            ValidateTag(tag);
            normalizedTags.Add(normalizedIdentity, tag);
        }

        return new ModuleImageWorkflowOptions(
            selectors.Length == 0 ? ModuleImageSelection.All : new ModuleImageSelection(selectors),
            globalTag,
            normalizedTags);
    }

    /// <summary>Creates the configuration overrides represented by <paramref name="document"/>.</summary>
    public static IReadOnlyDictionary<string, string> Create(ModuleImageManifestDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.Validate();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var image in document.Images)
        {
            var prefix = GetResourceKey(image.Module, image.Resource, image.ResourceKind);
            values[ConfigurationPath.Combine(prefix, "ImageRegistry")] = image.Registry;
            values[ConfigurationPath.Combine(prefix, "ImageName")] = image.Repository;
            values[ConfigurationPath.Combine(prefix, "ImageTag")] = image.Tag ?? string.Empty;
            values[ConfigurationPath.Combine(prefix, "ImageSHA256")] = image.Digest ?? string.Empty;
            values[ConfigurationPath.Combine(prefix, "PublishImage")] = bool.FalseString;
            values[ConfigurationPath.Combine(prefix, "ImagePullPolicy")] = ImagePullPolicy.Always.ToString();
            if (image.ResourceKind == ModuleResourceKind.Project)
            {
                values[ConfigurationPath.Combine(prefix, "ProjectMode")] = ModuleProjectMode.Container.ToString();
            }
        }

        return values;
    }

    /// <summary>Gets the standard configuration key for a declared module resource.</summary>
    public static string GetResourceKey(
        string module,
        string resource,
        ModuleResourceKind resourceKind)
    {
        ValidateSegment(module, nameof(module));
        ValidateSegment(resource, nameof(resource));
        var collection = resourceKind switch
        {
            ModuleResourceKind.Project => "Projects",
            ModuleResourceKind.Container => "Containers",
            _ => throw new InvalidDataException($"Unsupported module resource kind '{resourceKind}'.")
        };
        return ConfigurationPath.Combine(
            ModularAppHostsOptions.ConfigurationSectionName,
            "Modules",
            module,
            collection,
            resource);
    }

    internal static void ValidateSegment(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        if (value.Contains(ConfigurationPath.KeyDelimiter, StringComparison.Ordinal) ||
            value.Contains("__", StringComparison.Ordinal) ||
            value.Contains('/', StringComparison.Ordinal) ||
            value.Contains('=', StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Module image {name} '{value}' contains a reserved identity or configuration separator.");
        }
    }

    private static string ValidateIdentity(string identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        var slash = identity.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || slash == identity.Length - 1 || identity.IndexOf('/', slash + 1) >= 0)
        {
            throw new InvalidDataException(
                $"Workflow resource tag '{identity}' must use the form <module>/<resource>.");
        }

        var module = identity[..slash];
        var resource = identity[(slash + 1)..];
        ValidateSegment(module, nameof(module));
        ValidateSegment(resource, nameof(resource));
        return $"{module}/{resource}";
    }

    private static void ValidateTag(string? tag)
    {
        if (tag is not null && !ModuleImageIdentityValidation.IsValidTag(tag))
        {
            throw new InvalidDataException($"Workflow image tag '{tag}' is not a valid OCI distribution tag.");
        }
    }

    private static string? GetConfiguredValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}

internal sealed record ModuleImageWorkflowOptions(
    ModuleImageSelection Selection,
    string? GlobalTag,
    IReadOnlyDictionary<string, string> ResourceTags)
{
    public string? ResolveTag(string module, string resource) =>
        ResourceTags.TryGetValue($"{module}/{resource}", out var tag) ? tag : GlobalTag;

    public void ValidateSelectedResources(IReadOnlySet<IResource> selectedResources)
    {
        ArgumentNullException.ThrowIfNull(selectedResources);
        var selectedIdentities = selectedResources
            .Select(resource => resource.Annotations
                .OfType<DistributedApplicationModuleResourceAnnotation>()
                .LastOrDefault())
            .OfType<DistributedApplicationModuleResourceAnnotation>()
            .Select(annotation => $"{annotation.ModuleName}/{annotation.ResourceName}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unmatched = ResourceTags.Keys
            .Where(identity => !selectedIdentities.Contains(identity))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (unmatched.Length > 0)
        {
            throw new InvalidOperationException(
                $"Workflow resource tag overrides do not match selected images: {string.Join(", ", unmatched)}.");
        }
    }
}
