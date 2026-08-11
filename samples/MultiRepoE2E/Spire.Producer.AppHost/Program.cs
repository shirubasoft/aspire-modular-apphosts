using Aspire.Hosting.ModularAppHosts;
using Spire.ModuleContract;

var builder = DistributedApplication.CreateBuilder(args);
builder.BuildModuleImages();

await builder.AddSpireModuleAsync();
await builder.Build().RunAsync();
