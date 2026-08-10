using Aspire.Hosting.ModularAppHosts;
using Aspire.Hosting.ApplicationModel;

namespace Shirubasoft.Aspire.ModularAppHosts.Tool;

internal static class WorkflowImageEnvironment
{
    private const string Prefix = "Aspire__ModularAppHosts__Modules";

    public static IReadOnlyList<KeyValuePair<string, string>> Create(ModuleImageManifestDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.Validate();
        var values = new List<KeyValuePair<string, string>>();
        foreach (var image in document.Images)
        {
            var prefix = GetResourcePrefix(image.Module, image.Resource, image.ResourceKind);
            values.Add(new($"{prefix}__ImageRegistry", image.Registry));
            values.Add(new($"{prefix}__ImageName", image.Repository));
            if (image.Tag is not null)
            {
                values.Add(new($"{prefix}__ImageTag", image.Tag));
                values.Add(new($"{prefix}__ImageSHA256", string.Empty));
            }
            else
            {
                values.Add(new($"{prefix}__ImageTag", string.Empty));
                values.Add(new($"{prefix}__ImageSHA256", image.Digest!));
            }

            values.Add(new($"{prefix}__PublishImage", bool.FalseString));
            values.Add(new($"{prefix}__ImagePullPolicy", ImagePullPolicy.Always.ToString()));
            if (image.ResourceKind == ModuleResourceKind.Project)
            {
                values.Add(new($"{prefix}__ProjectMode", ModuleProjectMode.Container.ToString()));
            }
        }

        return values;
    }

    internal static string GetResourcePrefix(
        string module,
        string resource,
        ModuleResourceKind resourceKind)
    {
        var collection = resourceKind switch
        {
            ModuleResourceKind.Project => "Projects",
            ModuleResourceKind.Container => "Containers",
            _ => throw new ToolUsageException(
                $"Unsupported module resource kind '{resourceKind}'.")
        };
        return $"{Prefix}__{module}__{collection}__{resource}";
    }
}
