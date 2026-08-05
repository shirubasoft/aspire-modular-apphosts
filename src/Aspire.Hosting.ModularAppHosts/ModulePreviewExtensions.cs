namespace Aspire.Hosting.ModularAppHosts;

/// <summary>Extensions for applying immutable module preview compositions.</summary>
public static class ModulePreviewExtensions
{
    /// <summary>Loads and applies a preview manifest before modules are imported.</summary>
    public static async Task<IDistributedApplicationBuilder> ApplyModulePreviewManifestAsync(
        this IDistributedApplicationBuilder builder,
        string manifestPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);

        var manifest = await ModulePreviewManifest.LoadAsync(
            Path.GetFullPath(manifestPath, builder.AppHostDirectory),
            cancellationToken).ConfigureAwait(false);
        return builder.ApplyModulePreviewManifest(manifest);
    }

    /// <summary>Applies a validated preview manifest before modules are imported.</summary>
    public static IDistributedApplicationBuilder ApplyModulePreviewManifest(
        this IDistributedApplicationBuilder builder,
        ModulePreviewManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(manifest);
        manifest.Validate();

        var registry = DistributedApplicationModuleExtensions.GetOrCreateRegistryForPreview(builder);
        registry.ApplyPreviewManifest(manifest, builder.AppHostDirectory);
        return builder;
    }
}
