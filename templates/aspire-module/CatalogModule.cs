using Aspire.Hosting;
using Aspire.Hosting.ModularAppHosts;

[GenerateDistributedApplicationModule(Name, Version = "1")]
public static partial class CatalogModule
{
    public const string Name = "catalog-module-name";
    public const string ApiResourceName = "catalog-api";

    public static void Define(IDistributedApplicationModuleBuilder module)
    {
        module.AddContainer(ApiResourceName, "example/catalog-api", "latest")
            .Configure(container => container
                .WithHttpEndpoint(targetPort: 8080, name: "http"));
    }
}
