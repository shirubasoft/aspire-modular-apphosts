using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ModularAppHosts;

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

        module.AddContainer(ApiResourceName, ImageName)
            .WithImagePublishCommand(new ModuleContainerExportOptions(
                imageName: ImageName,
                publishCommand: "bash",
                publishArguments:
                [
                    "build-image.sh",
                    ModuleContainerExportOptions.ImageReferencePlaceholder
                ])
            {
                BuildRepository = options.BuildRepository,
                BuildRepositoryRevision = options.BuildRepositoryRevision,
                WorkingDirectory = "."
            })
            .Configure(container => container
                .WithHttpEndpoint(targetPort: 80, name: "http")
                .WithExternalHttpEndpoints()
                .WithHttpHealthCheck("/health.txt"));
    }
}

public sealed class SpireModuleOptions
{
    public string BuildRepository { get; set; } = string.Empty;

    public string? BuildRepositoryRevision { get; set; }
}
