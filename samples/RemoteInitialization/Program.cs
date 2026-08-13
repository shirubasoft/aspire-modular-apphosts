var builder = DistributedApplication.CreateBuilder(args);

builder.ExportModule("remote-notifications", module =>
{
    module.WithRepository("https://github.com/shirubasoft/spire-external-repo-sample.git");
    module
        .AddProject(
            "notification-service",
            Path.Combine("NotificationService", "NotificationService.csproj"),
            ModuleProjectPathBase.Repository)
        .ConfigureProject((_, project) =>
            project
                .WithHttpEndpoint(name: "http")
                .WithExternalHttpEndpoints()
                .WithHttpHealthCheck("/health"))
        .ExportAsContainerWithCommand(
            new ModuleImageCommandOptions(
                "notification-service",
                "dotnet",
                "publish",
                "NotificationService.csproj",
                "/t:PublishContainer",
                $"-p:ContainerRepository={ModuleImageCommandOptions.ImageNamePlaceholder}",
                $"-p:ContainerImageTag={ModuleImageCommandOptions.ImageTagPlaceholder}"),
            (_, container) =>
                container
                    .WithHttpEndpoint(targetPort: 8080, name: "http")
                    .WithExternalHttpEndpoints());
});

builder.ImportModule("remote-notifications");

await builder.Build().RunAsync();
