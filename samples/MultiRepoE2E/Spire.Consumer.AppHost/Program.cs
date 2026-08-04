using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ModularAppHosts;

const string moduleName = "spire-sample";
const string apiResourceName = "spire-api";
const string repository = "Shirubasoft/spire";

var builder = DistributedApplication.CreateBuilder(args);
builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:AutoCloneRepositories"] = "true";

builder.ExportModule(moduleName, module =>
{
    module.WithRepository(repository);
    module.AddResource<ProjectResource>(apiResourceName, context =>
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
});

builder.ImportModule(moduleName);
builder.Build().Run();
