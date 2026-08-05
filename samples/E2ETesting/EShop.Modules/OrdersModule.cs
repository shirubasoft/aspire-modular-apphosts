using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ModularAppHosts;

namespace EShop.Modules;

[GenerateDistributedApplicationModule(Name)]
public static partial class OrdersModule
{
    public const string Name = "orders";
    public const string ApiResourceName = "orders-api";
    public const int ExternalHttpPort = 55102;

    public static void Define(IDistributedApplicationModuleBuilder module)
    {
        module.AddResource<ProjectResource>(ApiResourceName, context =>
            context.ApplicationBuilder
                .AddProject(
                    context.ResourceName,
                    Path.Combine(context.RepositoryPath, "EShop.Orders.Api", "EShop.Orders.Api.csproj"))
                .WithHttpEndpoint(port: ExternalHttpPort, name: "http")
                .WithExternalHttpEndpoints()
                .WithHttpHealthCheck("/health"));
    }
}
