using Aspire.Hosting;
using Spire.ModuleContract;

var builder = DistributedApplication.CreateBuilder(args);
builder.BuildModuleImages();

await builder.ImportSpireModuleAsync();
await builder.Build().RunAsync();
