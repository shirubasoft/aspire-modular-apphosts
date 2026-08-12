using Aspire.Hosting.ApplicationModel;

var builder = DistributedApplication.CreateBuilder(args);

builder.ExportModule("remote-notifications", module =>
{
    module.WithRepository("https://github.com/shirubasoft/spire-external-repo-sample.git");
    module.RequiresRepository();
    module.AddResource<ProjectResource>("notification-service", context =>
        context.ApplicationBuilder
            .AddProject(
                context.ResourceName,
                Path.Combine(
                    context.RepositoryPath,
                    "NotificationService",
                    "NotificationService.csproj"))
            .WithHttpEndpoint(name: "http")
            .WithExternalHttpEndpoints()
            .WithHttpHealthCheck("/health"));
});

builder.ImportModule("remote-notifications");

await builder.Build().RunAsync();
