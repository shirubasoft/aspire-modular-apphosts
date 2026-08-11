#pragma warning disable ASPIRECOMPUTE003
#pragma warning disable ASPIREPIPELINES003

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

internal sealed class ModuleImagePublisherAnnotation(
    ModuleResourceKind resourceKind,
    ModuleImageBuildRecipe recipe,
    Func<
        ModuleImageBuildRecipe,
        ILogger,
        ILogger,
        CancellationToken,
        Task<ModulePreparedImage>>? prepareAsync = null) : IResourceAnnotation
{
    private readonly object _preparationLock = new();
    private readonly Func<
        ModuleImageBuildRecipe,
        ILogger,
        ILogger,
        CancellationToken,
        Task<ModulePreparedImage>> _prepareAsync = prepareAsync ?? ModuleImageRecipeEvaluator.PrepareAsync;
    private Task<ModulePreparedImage>? _preparationTask;
    private ModulePreparedImage? _preparedImage;

    public string ModuleName => Recipe.ModuleName;

    public string ResourceName => Recipe.ResourceName;

    public ModuleResourceKind ResourceKind { get; } = resourceKind;

    public ModuleImageBuildRecipe Recipe { get; } = recipe;

    public ModuleContainerExportOptions Options => Recipe.Options;

    public string WorkingDirectory => Recipe.WorkingDirectory;

    public string? Repository => Recipe.Repository;

    public string? Revision => Recipe.Revision;

    public Task<ModulePreparedImage> PrepareAsync(
        ILogger lifecycleLogger,
        ILogger resourceLogger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lifecycleLogger);
        ArgumentNullException.ThrowIfNull(resourceLogger);

        lock (_preparationLock)
        {
            _preparationTask ??= PrepareCoreAsync(
                lifecycleLogger,
                resourceLogger,
                cancellationToken);
            return _preparationTask;
        }
    }

    public bool TryGetPreparedImage(out ModulePreparedImage preparedImage)
    {
        lock (_preparationLock)
        {
            if (_preparedImage is not null)
            {
                preparedImage = _preparedImage;
                return true;
            }

            preparedImage = null!;
            return false;
        }
    }

    private async Task<ModulePreparedImage> PrepareCoreAsync(
        ILogger lifecycleLogger,
        ILogger resourceLogger,
        CancellationToken cancellationToken)
    {
        var preparedImage = await _prepareAsync(
            Recipe,
            lifecycleLogger,
            resourceLogger,
            cancellationToken).ConfigureAwait(false);
        lock (_preparationLock)
        {
            _preparedImage = preparedImage;
        }

        return preparedImage;
    }
}

internal sealed record ModuleEffectiveImage(
    string Reference,
    string PullReference,
    string? PushReference,
    ModuleImagePushTargetKind PushTargetKind,
    string? Registry,
    string Repository,
    string? Tag,
    string? Digest,
    ModuleRemoteImage? PushImage);

internal sealed record ModuleRemoteImage(
    string Registry,
    string Repository,
    string Tag,
    string Reference);

internal enum ModuleImagePushTargetKind
{
    None,
    ContainerRuntime,
    AspireRegistry
}

internal static class ModuleEffectiveImageResolver
{
    public static bool HasPullSource(IResource resource)
    {
        var image = resource.Annotations.OfType<ContainerImageAnnotation>().LastOrDefault();
        if (image is null)
        {
            return false;
        }

        if (GetPullMapping(resource) is not null)
        {
            EnsureMappingIsTaggable(resource, image);
            return true;
        }

        var explicitRegistry = GetResourceRegistry(resource);
        var mayHaveExplicitRegistry = explicitRegistry is not null && MayHaveRemoteEndpoint(explicitRegistry);
        if (image.SHA256 is { Length: > 0 })
        {
            return image.Registry is { Length: > 0 } && !mayHaveExplicitRegistry;
        }

        return image.Registry is { Length: > 0 } ||
            mayHaveExplicitRegistry ||
            HasFallbackRegistry(resource);
    }

    public static bool HasPushTarget(IResource resource)
    {
        var image = resource.Annotations.OfType<ContainerImageAnnotation>().LastOrDefault();
        if (image is null || image.SHA256 is { Length: > 0 })
        {
            return false;
        }

        var registry = GetResourceRegistry(resource);
        return image.Registry is { Length: > 0 } ||
            registry is not null && MayHaveRemoteEndpoint(registry) ||
            HasFallbackRegistry(resource);
    }

    public static async Task<ModuleEffectiveImage> ResolveAsync(
        IResource resource,
        CancellationToken cancellationToken,
        bool allowUnqualifiedPullReference = false,
        bool usePreparedPublisherImage = false)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (!resource.TryGetContainerImageName(out var localImage))
        {
            throw new InvalidOperationException(
                $"Resource '{resource.Name}' does not have a container image reference.");
        }

        var image = resource.Annotations.OfType<ContainerImageAnnotation>().LastOrDefault()
            ?? throw new InvalidOperationException(
                $"Resource '{resource.Name}' does not have a container image annotation.");
        var publisher = resource.Annotations.OfType<ModuleImagePublisherAnnotation>().LastOrDefault();
        ModulePreparedImage? preparedImage = null;
        if (usePreparedPublisherImage && publisher is not null &&
            !publisher.TryGetPreparedImage(out preparedImage))
        {
            throw new InvalidOperationException(
                $"Resource '{resource.Name}' has not prepared its module image. Run its image preparation step first.");
        }

        if (preparedImage is not null)
        {
            localImage = preparedImage.CanonicalImageReference;
        }

        var mapping = GetPullMapping(resource);
        if (mapping is not null)
        {
            EnsureMappingIsTaggable(resource, image);
        }

        var explicitRegistry = await GetRemoteRegistryAsync(
            GetResourceRegistry(resource),
            cancellationToken).ConfigureAwait(false);
        var moduleDeclaresRegistry = image.Registry is { Length: > 0 };
        var registry = explicitRegistry ??
            (moduleDeclaresRegistry
                ? null
                : await GetFallbackRegistryAsync(resource, cancellationToken).ConfigureAwait(false));
        ModuleRemoteImage? pushedImage = null;
        var pushTargetKind = ModuleImagePushTargetKind.None;
        if (registry is not null && image.SHA256 is not { Length: > 0 })
        {
            pushedImage = await ResolveRegistryImageAsync(
                resource,
                image,
                registry,
                publisher,
                preparedImage,
                localImage,
                cancellationToken).ConfigureAwait(false);
            pushTargetKind = ModuleImagePushTargetKind.AspireRegistry;
        }
        else if (moduleDeclaresRegistry && image.SHA256 is not { Length: > 0 })
        {
            var remoteImage = publisher is null
                ? localImage
                : preparedImage?.CanonicalImageReference ??
                    $"{ModuleImageReference.GetRepository(publisher.Options)}:{publisher.Options.ImageTag ?? "latest"}";
            var remoteIdentity = ParseReference(remoteImage, image);
            pushedImage = new ModuleRemoteImage(
                remoteIdentity.Registry!,
                remoteIdentity.Repository,
                remoteIdentity.Tag!,
                remoteImage);
            pushTargetKind = ModuleImagePushTargetKind.ContainerRuntime;
        }

        var pullImage = mapping?.RemoteImageReference ??
            pushedImage?.Reference ??
            (explicitRegistry is null && image.Registry is { Length: > 0 } ? localImage : null) ??
            (allowUnqualifiedPullReference
                ? localImage
                : throw new InvalidOperationException(
                    $"Resource '{resource.Name}' does not have a container registry to pull from."));

        var parsed = ParseReference(localImage, image);
        return new ModuleEffectiveImage(
            localImage,
            pullImage,
            pushedImage?.Reference,
            pushTargetKind,
            parsed.Registry,
            parsed.Repository,
            parsed.Tag,
            parsed.Digest,
            pushedImage);
    }

    private static async Task<ModuleRemoteImage> ResolveRegistryImageAsync(
        IResource resource,
        ContainerImageAnnotation image,
        IContainerRegistry registry,
        ModuleImagePublisherAnnotation? publisher,
        ModulePreparedImage? preparedImage,
        string localImageReference,
        CancellationToken cancellationToken)
    {
        var effectiveIdentity = ParseReference(
            preparedImage?.CanonicalImageReference ?? localImageReference,
            image);
        var options = new ContainerImagePushOptions
        {
#pragma warning disable CA1308
            RemoteImageName = publisher?.Options.ImageName ?? resource.Name.ToLowerInvariant(),
#pragma warning restore CA1308
            RemoteImageTag = preparedImage is not null
                ? effectiveIdentity.Tag ?? "latest"
                : publisher?.Options.ImageTag ?? "latest"
        };
        var context = new ContainerImagePushOptionsCallbackContext
        {
            Resource = resource,
            Options = options,
            CancellationToken = cancellationToken
        };
        foreach (var annotation in resource.Annotations.OfType<ContainerImagePushOptionsCallbackAnnotation>())
        {
            await annotation.Callback(context).ConfigureAwait(false);
        }

        var reference = await options.GetFullRemoteImageNameAsync(registry, cancellationToken).ConfigureAwait(false);
        var remoteName = options.RemoteImageName!;
        var parsedName = ModuleImageReference.ParseRepository(remoteName);
        var registryHost = parsedName.Registry ??
            await registry.Endpoint.GetValueAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(registryHost))
        {
            throw new InvalidOperationException(
                $"Resource '{resource.Name}' does not have a remote container registry endpoint.");
        }

        var registryRepository = parsedName.Registry is null && registry.Repository is not null
            ? await registry.Repository.GetValueAsync(cancellationToken).ConfigureAwait(false)
            : null;
        var repository = string.IsNullOrWhiteSpace(registryRepository)
            ? parsedName.Name
            : $"{registryRepository}/{parsedName.Name}";
        return new ModuleRemoteImage(
            registryHost,
            repository,
            options.RemoteImageTag ?? "latest",
            reference);
    }

    private static (string? Registry, string Repository, string? Tag, string? Digest) ParseReference(
        string reference,
        ContainerImageAnnotation image)
    {
        var digest = image.SHA256 is { Length: > 0 }
            ? $"sha256:{image.SHA256}"
            : null;
        var withoutDigest = reference.Split('@', 2)[0];
        var lastSlash = withoutDigest.LastIndexOf('/');
        var lastColon = withoutDigest.LastIndexOf(':');
        var repositoryWithRegistry = lastColon > lastSlash
            ? withoutDigest[..lastColon]
            : withoutDigest;
        var tag = digest is null && lastColon > lastSlash
            ? withoutDigest[(lastColon + 1)..]
            : image.Tag;
        var parsed = ModuleImageReference.ParseRepository(repositoryWithRegistry);
        return (parsed.Registry, parsed.Name, tag, digest);
    }

    private static void EnsureMappingIsTaggable(IResource resource, ContainerImageAnnotation image)
    {
        if (image.SHA256 is { Length: > 0 })
        {
            throw new InvalidOperationException(
                $"Resource '{resource.Name}' cannot map a pulled image to its digest-pinned local reference. " +
                "Configure a tagged local image when using WithImagePullMapping.");
        }
    }

    private static ModuleImagePullMappingAnnotation? GetPullMapping(IResource resource) =>
        resource.Annotations.OfType<ModuleImagePullMappingAnnotation>().LastOrDefault();

    private static IContainerRegistry? GetResourceRegistry(IResource resource) =>
        resource.Annotations.OfType<ContainerRegistryReferenceAnnotation>().LastOrDefault()?.Registry;

    private static IEnumerable<IContainerRegistry> GetFallbackRegistries(IResource resource)
    {
        foreach (var annotation in resource.Annotations.OfType<DeploymentTargetAnnotation>().Reverse())
        {
            if (annotation.ContainerRegistry is not null)
            {
                yield return annotation.ContainerRegistry;
            }
        }

        foreach (var registry in resource.Annotations
            .OfType<RegistryTargetAnnotation>()
            .Select(annotation => annotation.Registry))
        {
            yield return registry;
        }
    }

    private static async Task<IContainerRegistry?> GetFallbackRegistryAsync(
        IResource resource,
        CancellationToken cancellationToken)
    {
        var registries = new List<IContainerRegistry>();
        foreach (var registry in GetFallbackRegistries(resource))
        {
            if (await GetRemoteRegistryAsync(registry, cancellationToken).ConfigureAwait(false) is not null &&
                !registries.Contains(registry))
            {
                registries.Add(registry);
            }
        }

        return registries.Count switch
        {
            0 => null,
            1 => registries[0],
            _ => throw new InvalidOperationException(
                $"Resource '{resource.Name}' has multiple container registries available. " +
                "Specify one with WithContainerRegistry.")
        };
    }

    private static bool HasFallbackRegistry(IResource resource)
    {
        var registries = GetFallbackRegistries(resource)
            .Where(MayHaveRemoteEndpoint)
            .Distinct()
            .ToArray();
        return registries.Length switch
        {
            0 => false,
            1 => true,
            _ => throw new InvalidOperationException(
                $"Resource '{resource.Name}' has multiple container registries available. " +
                "Specify one with WithContainerRegistry.")
        };
    }

    private static async Task<IContainerRegistry?> GetRemoteRegistryAsync(
        IContainerRegistry? registry,
        CancellationToken cancellationToken)
    {
        if (registry is null)
        {
            return null;
        }

        var endpoint = await registry.Endpoint.GetValueAsync(cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(endpoint) ? null : registry;
    }

    private static bool MayHaveRemoteEndpoint(IContainerRegistry registry) =>
        registry.Endpoint.IsConditional ||
        registry.Endpoint.ValueProviders.Count > 0 ||
        !string.IsNullOrWhiteSpace(registry.Endpoint.Format);
}
