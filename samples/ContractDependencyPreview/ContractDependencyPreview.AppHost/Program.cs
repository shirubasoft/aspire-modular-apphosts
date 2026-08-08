using Aspire.Hosting.ModularAppHosts;

var builder = DistributedApplication.CreateBuilder(args);
var module = await builder.ExportModuleAsync(
    "contract-dependency-preview",
    "Example.Preview.Producer.Contract",
    definition => definition
        .AddContainer("preview-api", "registry.example.test/preview/producer", "sample")
        .WithImagePullMapping("docker.io/library/alpine:3.20")
        .WithImagePublishCommand(new ModuleContainerExportOptions(
            "preview/producer",
            "docker",
            "build",
            "--tag",
            ModuleContainerExportOptions.ImageReferencePlaceholder,
            ".")
        {
            ImageRegistry = "registry.example.test",
            ImageTag = "sample",
            WorkingDirectory = "."
        }));

await builder.AddAsync(module);
await builder.Build().RunAsync();
