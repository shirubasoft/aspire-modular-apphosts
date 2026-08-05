using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ModularAppHosts;

namespace Spire.ModuleContract;

[GenerateDistributedApplicationModule(Name, Version = "1")]
public static partial class SpireModule
{
    public const string Name = "spire-sample";
    public const string ApiResourceName = "spire-api";
    public const string Repository = "Shirubasoft/spire";

    public static void Define(IDistributedApplicationModuleBuilder module)
    {
        module.WithRepository(Repository);
        module.RequiresRepository();
        module.AddResource<ProjectResource>(ApiResourceName, context =>
            context.ApplicationBuilder
                .AddProject(
                    context.ResourceName,
                    Path.Combine(
                        context.RepositoryPath,
                        "sample",
                        "Sample.ApiService",
                        "Sample.ApiService.csproj"))
                .WithHttpEndpoint(port: 55201, name: "http")
                .WithExternalHttpEndpoints()
                .WithHttpHealthCheck("/health"));
    }
}
