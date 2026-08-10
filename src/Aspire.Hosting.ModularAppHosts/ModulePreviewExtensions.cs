using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting.ModularAppHosts;

/// <summary>Extensions for applying immutable module preview compositions.</summary>
public static class ModulePreviewExtensions
{
    /// <summary>
    /// Applies a configured full-control preview when its manifest path is present; otherwise leaves the AppHost unchanged.
    /// </summary>
    public static async Task<IDistributedApplicationBuilder> ApplyFullControlModulePreviewFromConfigurationAsync(
        this IDistributedApplicationBuilder builder,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new FullControlModulePreviewOptions();
        builder.Configuration
            .GetSection(FullControlModulePreviewOptions.ConfigurationSectionName)
            .Bind(options);
        if (string.IsNullOrWhiteSpace(options.ManifestPath))
        {
            if (!string.IsNullOrWhiteSpace(options.SourceRepository) ||
                !string.IsNullOrWhiteSpace(options.SourceRef))
            {
                throw new InvalidOperationException(
                    $"{FullControlModulePreviewOptions.ConfigurationSectionName}:ManifestPath must be configured " +
                    "when a full-control preview source is configured.");
            }

            return builder;
        }

        if (string.IsNullOrWhiteSpace(options.SourceRepository) ||
            string.IsNullOrWhiteSpace(options.SourceRef))
        {
            throw new InvalidOperationException(
                $"{FullControlModulePreviewOptions.ConfigurationSectionName}:SourceRepository and SourceRef " +
                "must be supplied by trusted CI context when ManifestPath is configured.");
        }

        return await builder.ApplyFullControlModulePreviewAsync(
            options.ManifestPath,
            new FullControlModulePreviewSource
            {
                Repository = options.SourceRepository,
                Ref = options.SourceRef
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Loads and applies tag-only full-control preview overrides before modules are imported.</summary>
    public static async Task<IDistributedApplicationBuilder> ApplyFullControlModulePreviewAsync(
        this IDistributedApplicationBuilder builder,
        string manifestPath,
        FullControlModulePreviewSource source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        ArgumentNullException.ThrowIfNull(source);

        var manifest = await FullControlModulePreviewManifest.LoadAsync(
            Path.GetFullPath(manifestPath, builder.AppHostDirectory),
            cancellationToken).ConfigureAwait(false);
        return builder.ApplyFullControlModulePreview(manifest, source);
    }

    /// <summary>Applies validated tag-only overrides with a separately trusted source identity.</summary>
    public static IDistributedApplicationBuilder ApplyFullControlModulePreview(
        this IDistributedApplicationBuilder builder,
        FullControlModulePreviewManifest manifest,
        FullControlModulePreviewSource source)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(source);
        manifest.Validate();
        source.Validate();

        var registry = DistributedApplicationModuleExtensions.GetOrCreateRegistryForPreview(builder);
        registry.ApplyFullControlPreview(manifest, source, builder.AppHostDirectory);
        registry.ApplyFullControlPreviewTags(builder.Resources);
        return builder;
    }

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
