#pragma warning disable ASPIRECOMPUTE003
#pragma warning disable ASPIREPIPELINES003

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

internal sealed class ModuleImagePublisherAnnotation(
    string moduleName,
    string resourceName,
    ModuleResourceKind resourceKind,
    ModuleContainerExportOptions options,
    ModuleImagePublishPlan plan,
    string workingDirectory,
    string? repository,
    string? revision,
    string? branchImageTag = null) : IResourceAnnotation
{
    public string ModuleName { get; } = moduleName;

    public string ResourceName { get; } = resourceName;

    public ModuleResourceKind ResourceKind { get; } = resourceKind;

    public ModuleContainerExportOptions Options { get; } = options;

    public ModuleImagePublishPlan Plan { get; } = plan;

    public string WorkingDirectory { get; } = workingDirectory;

    public string? Repository { get; } = repository;

    public string? Revision { get; } = revision;

    public string? BranchImageTag { get; } = branchImageTag;
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
        bool allowUnqualifiedPullReference = false)
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
                cancellationToken).ConfigureAwait(false);
            pushTargetKind = ModuleImagePushTargetKind.AspireRegistry;
        }
        else if (moduleDeclaresRegistry && image.SHA256 is not { Length: > 0 })
        {
            var localIdentity = ParseReference(localImage, image);
            pushedImage = new ModuleRemoteImage(
                localIdentity.Registry!,
                localIdentity.Repository,
                localIdentity.Tag!,
                localImage);
            pushTargetKind = ModuleImagePushTargetKind.ContainerRuntime;
        }

        var pullImage = mapping?.RemoteImageReference ??
            (explicitRegistry is null && image.Registry is { Length: > 0 }
                ? localImage
                : pushedImage?.Reference) ??
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
        CancellationToken cancellationToken)
    {
        var publisher = resource.Annotations.OfType<ModuleImagePublisherAnnotation>().LastOrDefault();
        var options = new ContainerImagePushOptions
        {
#pragma warning disable CA1308
            RemoteImageName = publisher?.Plan.ImageName ?? resource.Name.ToLowerInvariant(),
#pragma warning restore CA1308
            RemoteImageTag = publisher?.Plan.ImageTag ?? "latest"
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
