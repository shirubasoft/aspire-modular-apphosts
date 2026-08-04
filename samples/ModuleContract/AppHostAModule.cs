using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ModularAppHosts;

namespace ModularSample.ModuleContract;

public static class AppHostAModule
{
    public const string Name = "AppHostA";
    public const string ApiResourceName = "sample-api";
    public const string StaticResourceName = "sample-static";
    public const string MessageResourceName = "sample-message";

    public static IDistributedApplicationModule Register(
        IDistributedApplicationBuilder builder,
        string sourceRoot)
    {
        var absoluteSourceRoot = Path.GetFullPath(sourceRoot, builder.AppHostDirectory);

        return builder.ExportModule(Name, module =>
        {
            module.WithRepository(absoluteSourceRoot);

            module.AddProject(
                    ApiResourceName,
                    Path.Combine(absoluteSourceRoot, "Api", "ModularSample.Api.csproj"))
                .ExportAsContainer(
                    new ModuleContainerExportOptions(
                        imageName: "modular-sample-api",
                        publishCommand: "podman",
                        publishArguments: ["build", "--tag", "modular-sample-api:dev", "."])
                    {
                        ImageTag = "dev"
                    },
                    container => container
                        .WithHttpEndpoint(targetPort: 8080, name: "http")
                        .WithHttpHealthCheck("/health"));

            module.AddContainer(StaticResourceName, "nginx", "alpine")
                .Configure(container => container
                    .WithHttpEndpoint(targetPort: 80, name: "http")
                    .WithHttpHealthCheck("/"));

            module.AddResource<ParameterResource>(MessageResourceName, context =>
                context.ApplicationBuilder.AddParameter(
                    context.ResourceName,
                    "Hello from an arbitrary exported Aspire resource.",
                    publishValueAsDefault: true));
        });
    }
}
