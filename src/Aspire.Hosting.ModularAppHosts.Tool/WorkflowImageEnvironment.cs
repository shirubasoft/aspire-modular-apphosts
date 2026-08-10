using Aspire.Hosting.ModularAppHosts;

namespace Shirubasoft.Aspire.ModularAppHosts.Tool;

internal static class WorkflowImageEnvironment
{
    private const string Prefix = "Aspire__ModularAppHosts__WorkflowImageOverrides";

    public static IReadOnlyList<KeyValuePair<string, string>> Create(ModuleImageManifestDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.Validate();
        var values = new List<KeyValuePair<string, string>>();
        for (var index = 0; index < document.Images.Count; index++)
        {
            var image = document.Images[index];
            var prefix = GetPrefix(index);
            values.Add(new($"{prefix}__Module", image.Module));
            values.Add(new($"{prefix}__Resource", image.Resource));
            values.Add(new($"{prefix}__ResourceKind", image.ResourceKind.ToString()));
            values.Add(new($"{prefix}__Registry", image.Registry));
            values.Add(new($"{prefix}__Repository", image.Repository));
            if (image.Tag is not null)
            {
                values.Add(new($"{prefix}__Tag", image.Tag));
            }
            else
            {
                values.Add(new($"{prefix}__Digest", image.Digest!));
            }
        }

        return values;
    }

    public static string GetPrefix(int index) => $"{Prefix}__{index}";
}
