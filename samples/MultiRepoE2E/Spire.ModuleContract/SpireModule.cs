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
    public const string BuildRepositoryEnvironmentName = "MULTI_REPO_RESOURCE_BUILD_REPOSITORY";
    public const string BuildRevisionEnvironmentName = "MULTI_REPO_RESOURCE_BUILD_REVISION";

    public static void Define(IDistributedApplicationModuleBuilder module)
    {
        var buildRepository = GetRequiredEnvironmentVariable(BuildRepositoryEnvironmentName);
        var buildRevision = GetRequiredEnvironmentVariable(BuildRevisionEnvironmentName);

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
                BuildRepository = buildRepository,
                BuildRepositoryRevision = buildRevision,
                WorkingDirectory = "."
            })
            .Configure(container => container
                .WithHttpEndpoint(targetPort: 80, name: "http")
                .WithExternalHttpEndpoints()
                .WithHttpHealthCheck("/health.txt"));
    }

    private static string GetRequiredEnvironmentVariable(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Environment variable '{name}' is required.");
}
