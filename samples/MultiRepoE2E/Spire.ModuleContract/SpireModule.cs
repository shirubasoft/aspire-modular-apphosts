using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace Spire.ModuleContract;

[GenerateDistributedApplicationModule(Name, Version = "1")]
public static partial class SpireModule
{
    public const string Name = "multi-repo-resource-build";
    public const string ApiResourceName = "multi-repo-api";
    public const string ImageName = "multi-repo-e2e-resource";

    public static void Define(IDistributedApplicationModuleBuilder module)
    {
        var options = module.GetOptions<SpireModuleOptions>().Value;
        if (string.IsNullOrWhiteSpace(options.BuildRepository))
        {
            throw new InvalidOperationException(
                $"Configure '{module.ConfigurationSection.Path}:BuildRepository'.");
        }
        if (string.IsNullOrWhiteSpace(options.DefinitionRepository))
        {
            throw new InvalidOperationException(
                $"Configure '{module.ConfigurationSection.Path}:DefinitionRepository'.");
        }

        var image = string.IsNullOrWhiteSpace(options.ImageRegistry)
            ? ImageName
            : $"{options.ImageRegistry}/{ImageName}";
        module.WithRepository(options.DefinitionRepository);
        module.AddContainer(ApiResourceName, image)
            .WithImagePublishCommand(new ModuleContainerExportOptions(
                imageName: ImageName,
                publishCommand: "bash",
                publishArguments:
                [
                    "build-image.sh",
                    ModuleContainerExportOptions.ImageReferencePlaceholder
                ])
            {
                ImageRegistry = options.ImageRegistry,
                BuildRepository = options.BuildRepository,
                BuildRepositoryRevision = options.BuildRepositoryRevision,
                WorkingDirectory = "."
            })
            .Configure((_, container) => container
                .WithHttpEndpoint(targetPort: 80, name: "http")
                .WithExternalHttpEndpoints()
                .WithHttpHealthCheck("/health.txt"));
    }
}

public sealed class SpireModuleOptions
{
    public string DefinitionRepository { get; set; } = string.Empty;

    public string BuildRepository { get; set; } = string.Empty;

    public string? BuildRepositoryRevision { get; set; }

    public string? ImageRegistry { get; set; }
}
