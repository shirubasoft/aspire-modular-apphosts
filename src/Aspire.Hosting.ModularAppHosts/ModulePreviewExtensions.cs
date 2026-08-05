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

    /// <summary>Loads and applies a trusted preview resolution before modules are imported.</summary>
    public static async Task<IDistributedApplicationBuilder> ApplyModulePreviewResolutionAsync(
        this IDistributedApplicationBuilder builder,
        string resolutionPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(resolutionPath);

        var resolution = await ModulePreviewResolution.LoadAsync(
            Path.GetFullPath(resolutionPath, builder.AppHostDirectory),
            cancellationToken).ConfigureAwait(false);
        return builder.ApplyModulePreviewResolution(resolution);
    }

    /// <summary>Applies a trusted and verified preview resolution before modules are imported.</summary>
    public static IDistributedApplicationBuilder ApplyModulePreviewResolution(
        this IDistributedApplicationBuilder builder,
        ModulePreviewResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(resolution);
        resolution.Validate();

        var registry = DistributedApplicationModuleExtensions.GetOrCreateRegistryForPreview(builder);
        registry.ApplyPreviewResolution(resolution, builder.AppHostDirectory);
        return builder;
    }
}
