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

    public static async Task<IDistributedApplicationModule> RegisterAsync(
        IDistributedApplicationBuilder builder,
        string sourceRoot,
        CancellationToken cancellationToken = default)
    {
        var absoluteSourceRoot = Path.GetFullPath(sourceRoot, builder.AppHostDirectory);
        var containerRuntime = await ContainerRuntimeResolver.ResolveAsync(cancellationToken).ConfigureAwait(false);

        return await builder.ExportModuleAsync(Name, module =>
        {
            module.WithRepository(absoluteSourceRoot);

            module.AddResource<ParameterResource>(MessageResourceName, context =>
                context.ApplicationBuilder.AddParameter(
                    context.ResourceName,
                    "Hello from an arbitrary exported Aspire resource.",
                    publishValueAsDefault: true));

            module.AddProject(
                    ApiResourceName,
                    Path.Combine(absoluteSourceRoot, "Api", "ModularSample.Api.csproj"))
                .ConfigureProject((context, project) =>
                {
                    var message = context.GetResource<ParameterResource>(MessageResourceName);
                    project
                        .WithEnvironment("MODULE_MESSAGE", message)
                        .WithHttpEndpoint(name: "http")
                        .WithHttpHealthCheck("/health");
                })
                .ExportAsContainer(
                    new ModuleContainerExportOptions(
                        imageName: "modular-sample-api",
                        publishCommand: containerRuntime,
                        publishArguments:
                        [
                            "build",
                            "--tag",
                            ModuleContainerExportOptions.ImageReferencePlaceholder,
                            "."
                        ]),
                    (context, container) =>
                    {
                        var message = context.GetResource<ParameterResource>(MessageResourceName);
                        container
                            .WithEnvironment("MODULE_MESSAGE", message)
                            .WithHttpEndpoint(targetPort: 8080, name: "http")
                            .WithHttpHealthCheck("/health");
                    });

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

            module.AddContainer(StaticResourceName, "busybox", "1.37")
                .Configure((context, container) =>
                {
                    var message = context.GetResource<ParameterResource>(MessageResourceName);
                    container
                        .WithEnvironment("MODULE_MESSAGE", message)
                        .WithEntrypoint("/bin/sh")
                        .WithArgs(
                            "-c",
                            "printf '%s' \"$MODULE_MESSAGE\" > /tmp/index.html && " +
                            "exec httpd -f -p 8080 -h /tmp")
                        .WithHttpEndpoint(targetPort: 8080, name: "http")
                        .WithHttpHealthCheck("/");
                });

            module.AddContainer(GeneratedStaticResourceName, "modular-sample-static")
                .WithImagePublishCommand(new ModuleContainerExportOptions(
                    imageName: "modular-sample-static",
                    publishCommand: containerRuntime,
                    publishArguments:
                    [
                        "build",
                        "--tag",
                        ModuleContainerExportOptions.ImageReferencePlaceholder,
                        "."
                    ]))
                .Configure((_, container) => container
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
        }, cancellationToken).ConfigureAwait(false);
    }

}
