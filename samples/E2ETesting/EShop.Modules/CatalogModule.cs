using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ModularAppHosts;

namespace EShop.Modules;

[GenerateDistributedApplicationModule(Name)]
public static partial class CatalogModule
{
    public const string Name = "catalog";
    public const string ApiResourceName = "catalog-api";
    public const int ExternalHttpPort = 55101;

    public static IDistributedApplicationModule Register(
        IDistributedApplicationBuilder builder,
        string sourceRoot)
    {
        var absoluteSourceRoot = Path.GetFullPath(sourceRoot, builder.AppHostDirectory);

        return builder.ExportModule(Name, module =>
        {
            module.WithRepository(absoluteSourceRoot);
            module.AddResource<ProjectResource>(ApiResourceName, context =>
                context.ApplicationBuilder
                    .AddProject(
                        context.ResourceName,
                        Path.Combine(context.RepositoryPath, "EShop.Catalog.Api", "EShop.Catalog.Api.csproj"))
                    .WithHttpEndpoint(port: ExternalHttpPort, name: "http")
                    .WithExternalHttpEndpoints()
                    .WithHttpHealthCheck("/health"));
        });
    }
}
