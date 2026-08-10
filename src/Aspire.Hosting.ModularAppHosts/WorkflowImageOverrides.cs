using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting.ModularAppHosts;

internal sealed class WorkflowImageOverrideOptions
{
    public string Module { get; set; } = string.Empty;

    public string Resource { get; set; } = string.Empty;

    public ModuleResourceKind ResourceKind { get; set; }

    public string? Registry { get; set; }

    public string? Repository { get; set; }

    public string? Tag { get; set; }

    public string? Digest { get; set; }

    public bool HasFullIdentity =>
        !string.IsNullOrWhiteSpace(Registry) ||
        !string.IsNullOrWhiteSpace(Repository) ||
        !string.IsNullOrWhiteSpace(Digest);

    public void Validate(string configurationPath)
    {
        if (string.IsNullOrWhiteSpace(Module))
        {
            throw new InvalidOperationException($"{configurationPath}:Module must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(Resource))
        {
            throw new InvalidOperationException($"{configurationPath}:Resource must not be empty.");
        }

        if (!Enum.IsDefined(ResourceKind))
        {
            throw new InvalidOperationException(
                $"{configurationPath}:ResourceKind contains unsupported value '{ResourceKind}'.");
        }

        var tag = string.IsNullOrWhiteSpace(Tag) ? null : Tag;
        var digest = string.IsNullOrWhiteSpace(Digest) ? null : Digest;
        if (!HasFullIdentity)
        {
            if (tag is null)
            {
                throw new InvalidOperationException(
                    $"{configurationPath} must specify a tag or a complete remote image identity.");
            }

            ValidateTag(tag, configurationPath);
            return;
        }

        if (string.IsNullOrWhiteSpace(Registry) || string.IsNullOrWhiteSpace(Repository))
        {
            throw new InvalidOperationException(
                $"{configurationPath} must specify Registry and Repository for a complete image identity.");
        }

        if ((tag is null) == (digest is null))
        {
            throw new InvalidOperationException(
                $"{configurationPath} must specify exactly one Tag or Digest for a complete image identity.");
        }

        if (tag is not null)
        {
            ValidateTag(tag, configurationPath);
        }

        if (digest is not null && !ModuleImageIdentityValidation.IsValidDigest(digest))
        {
            throw new InvalidOperationException(
                $"{configurationPath}:Digest must use the form 'sha256:<64 lowercase hexadecimal characters>'.");
        }

        var manifestEntry = new ModuleImageManifestEntry
        {
            Module = Module,
            Resource = Resource,
            ResourceKind = ResourceKind,
            Registry = Registry,
            Repository = Repository,
            Tag = tag,
            Digest = digest
        };
        try
        {
            manifestEntry.Validate();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
        {
            throw new InvalidOperationException($"{configurationPath} is invalid: {exception.Message}", exception);
        }
    }

    private static void ValidateTag(string tag, string configurationPath)
    {
        if (!ModuleImageIdentityValidation.IsValidTag(tag))
        {
            throw new InvalidOperationException(
                $"{configurationPath}:Tag '{tag}' is not a valid OCI distribution tag.");
        }
    }
}

internal static class WorkflowImageOverrideLoader
{
    internal const string SectionName = "WorkflowImageOverrides";

    public static void Apply(IConfiguration configuration, ModularAppHostsOptions options)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(options);

        var section = configuration.GetSection(
            $"{ModularAppHostsOptions.ConfigurationSectionName}:{SectionName}");
        var overrides = new List<WorkflowImageOverrideOptions>();
        var identities = new HashSet<(string Module, string Resource)>(ModuleResourceIdentityComparer.Instance);
        foreach (var child in section.GetChildren())
        {
            var imageOverride = new WorkflowImageOverrideOptions();
            child.Bind(imageOverride);
            imageOverride.Validate(child.Path);
            if (!identities.Add((imageOverride.Module, imageOverride.Resource)))
            {
                throw new InvalidOperationException(
                    $"{section.Path} contains duplicate resource " +
                    $"'{imageOverride.Module}/{imageOverride.Resource}'.");
            }

            overrides.Add(imageOverride);
        }

        foreach (var imageOverride in overrides)
        {
            Apply(options, imageOverride);
        }
    }

    private static void Apply(ModularAppHostsOptions options, WorkflowImageOverrideOptions imageOverride)
    {
        if (!options.Modules.TryGetValue(imageOverride.Module, out var module))
        {
            module = new DistributedApplicationModuleOptions();
            options.Modules.Add(imageOverride.Module, module);
        }

        DistributedApplicationModuleImageOptions resource = imageOverride.ResourceKind switch
        {
            ModuleResourceKind.Project => GetOrAddProject(module, imageOverride.Resource),
            ModuleResourceKind.Container => GetOrAddContainer(module, imageOverride.Resource),
            _ => throw new InvalidOperationException(
                $"Unsupported module resource kind '{imageOverride.ResourceKind}'.")
        };

        resource.ImageTag = imageOverride.Tag;
        resource.ImageSHA256 = imageOverride.Digest;
        resource.HasFullWorkflowImageOverride = imageOverride.HasFullIdentity;
        if (!imageOverride.HasFullIdentity)
        {
            return;
        }

        resource.ImageRegistry = imageOverride.Registry;
        resource.ImageName = imageOverride.Repository;
        resource.PublishImage = false;
        resource.ImagePullPolicy = ImagePullPolicy.Always;
        if (resource is DistributedApplicationModuleProjectOptions project)
        {
            project.ProjectMode = ModuleProjectMode.Container;
        }
    }

    private static DistributedApplicationModuleProjectOptions GetOrAddProject(
        DistributedApplicationModuleOptions module,
        string name)
    {
        if (!module.Projects.TryGetValue(name, out var project))
        {
            project = new DistributedApplicationModuleProjectOptions();
            module.Projects.Add(name, project);
        }

        return project;
    }

    private static DistributedApplicationModuleContainerOptions GetOrAddContainer(
        DistributedApplicationModuleOptions module,
        string name)
    {
        if (!module.Containers.TryGetValue(name, out var container))
        {
            container = new DistributedApplicationModuleContainerOptions();
            module.Containers.Add(name, container);
        }

        return container;
    }
}
