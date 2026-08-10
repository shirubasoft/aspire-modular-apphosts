using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting.ModularAppHosts;

/// <summary>Projects a workflow image manifest into standard modular AppHost configuration.</summary>
public static class ModuleImageWorkflowConfiguration
{
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
}
