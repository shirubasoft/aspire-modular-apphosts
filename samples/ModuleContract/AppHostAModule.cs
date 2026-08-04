using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ModularAppHosts;

namespace ModularSample.ModuleContract;

[GenerateDistributedApplicationModule(Name)]
public static partial class AppHostAModule
{
    public const string Name = "AppHostA";
    public const string ApiResourceName = "sample-api";
    public const string ProjectResourceName = "sample-project";
    public const string CSharpAppResourceName = "sample-csharp-app";
    public const string StaticResourceName = "sample-static";
    public const string GeneratedStaticResourceName = "sample-generated-static";
    public const string ExecutableResourceName = "sample-executable";
    public const string DotnetToolResourceName = "sample-dotnet-tool";
    public const string MessageResourceName = "sample-message";
    public const string ConnectionStringResourceName = "sample-connection-string";
    public const string ExternalServiceResourceName = "sample-external-service";
    public const string ContainerRegistryResourceName = "sample-container-registry";
    public const string CustomResourceName = "sample-custom";

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
                .ConfigureProject(project => project
                    .WithHttpEndpoint(name: "http")
                    .WithHttpHealthCheck("/health"))
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

            module.AddResource<ProjectResource>(ProjectResourceName, context =>
                context.ApplicationBuilder
                    .AddProject(
                        context.ResourceName,
                        Path.Combine(context.RepositoryPath, "Api", "ModularSample.Api.csproj"))
                    .WithExplicitStart());

#pragma warning disable ASPIRECSHARPAPPS001
            module.AddResource<ProjectResource>(CSharpAppResourceName, context =>
                context.ApplicationBuilder
                    .AddCSharpApp(
                        context.ResourceName,
                        Path.Combine(context.RepositoryPath, "Api", "ModularSample.Api.csproj"))
                    .WithExplicitStart());
#pragma warning restore ASPIRECSHARPAPPS001

            module.AddContainer(StaticResourceName, "nginx", "alpine")
                .Configure(container => container
                    .WithHttpEndpoint(targetPort: 80, name: "http")
                    .WithHttpHealthCheck("/"));

            module.AddContainer(GeneratedStaticResourceName, "modular-sample-static", "dev")
                .WithImagePublishCommand(new ModuleContainerExportOptions(
                    imageName: "modular-sample-static",
                    publishCommand: "podman",
                    publishArguments: ["build", "--tag", "modular-sample-static:dev", "."])
                {
                    ImageTag = "dev"
                })
                .Configure(container => container
                    .WithHttpEndpoint(targetPort: 80, name: "http")
                    .WithHttpHealthCheck("/"));

            module.AddResource<ExecutableResource>(ExecutableResourceName, context =>
                context.ApplicationBuilder
                    .AddExecutable(context.ResourceName, "dotnet", context.RepositoryPath, "--info")
                    .WithExplicitStart());

#pragma warning disable ASPIREDOTNETTOOL
            module.AddResource<DotnetToolResource>(DotnetToolResourceName, context =>
                context.ApplicationBuilder
                    .AddDotnetTool(context.ResourceName, "dotnetsay")
                    .WithExplicitStart());
#pragma warning restore ASPIREDOTNETTOOL

            module.AddResource<ParameterResource>(MessageResourceName, context =>
                context.ApplicationBuilder.AddParameter(
                    context.ResourceName,
                    "Hello from an arbitrary exported Aspire resource.",
                    publishValueAsDefault: true));

            module.AddResource<ConnectionStringResource>(ConnectionStringResourceName, context =>
            {
                var message = context.GetResource<ParameterResource>(MessageResourceName);
                return context.ApplicationBuilder.AddConnectionString(
                    context.ResourceName,
                    ReferenceExpression.Create($"Endpoint=https://example.com/;Message={message}"));
            });

            module.AddResource<ExternalServiceResource>(ExternalServiceResourceName, context =>
                context.ApplicationBuilder.AddExternalService(
                    context.ResourceName,
                    new Uri("https://example.com/")));

#pragma warning disable ASPIRECOMPUTE003
            module.AddResource<ContainerRegistryResource>(ContainerRegistryResourceName, context =>
                context.ApplicationBuilder.AddContainerRegistry(
                    context.ResourceName,
                    "docker.io",
                    "library"));
#pragma warning restore ASPIRECOMPUTE003

            module.AddResource<SampleCustomResource>(CustomResourceName, context =>
                context.ApplicationBuilder.AddResource(new SampleCustomResource(context.ResourceName)));
        });
    }
}
