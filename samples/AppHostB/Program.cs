using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ModularAppHosts;
using ModularSample.ModuleContract;

var builder = DistributedApplication.CreateBuilder(args);

var samplesRoot = Path.GetFullPath("..", builder.AppHostDirectory);
var appHostASource = Path.Combine(samplesRoot, "AppHostA");
builder.Configuration[$"Parameters:{DistributedApplicationModuleExtensions.RepositoryBaseLocationParameterName}"] = samplesRoot;

AppHostAModule.Register(builder, appHostASource);
var imported = builder.ImportModule(AppHostAModule.Name);

var api = imported.GetResource<ContainerResource>(AppHostAModule.ApiResourceName);
var staticSite = imported.GetResource<ContainerResource>(AppHostAModule.StaticResourceName);

builder.AddDockerfile("dependency-gateway", "Gateway")
    .WithHttpEndpoint(targetPort: 8080, name: "http")
    .WithReference(api.GetEndpoint("http"))
    .WithReference(staticSite.GetEndpoint("http"))
    .WithEnvironment("UPSTREAM_API", api.GetEndpoint("http"))
    .WithEnvironment("UPSTREAM_STATIC", staticSite.GetEndpoint("http"))
    .WaitFor(api)
    .WaitFor(staticSite)
    .WithHttpHealthCheck("/health");

builder.Build().Run();
