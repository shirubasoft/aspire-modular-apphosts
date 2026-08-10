using Aspire.Hosting.ModularAppHosts;

var builder = DistributedApplication.CreateBuilder(args);
builder.AddDockerComposeEnvironment("compose");

var catalog = await builder.ExportModuleAsync("catalog", definition =>
{
    definition.WithRepository("https://github.com/acme/preview-source.git");
    definition.AddContainer("catalog-api", "nginx", "main");
    definition.AddContainer("catalog-worker", "nginx", "main");
});

builder.AddContainer("shared-cache", "redis", "main");

await builder.ApplyFullControlModulePreviewFromConfigurationAsync();
await builder.AddAsync(catalog);

await builder.Build().RunAsync();
