using Aspire.Hosting.ModularAppHosts;
using Spire.ModuleContract;

var builder = DistributedApplication.CreateBuilder(args);
builder.BuildModuleImages();

await SpireModule.ImportModuleAsync(builder);
await builder.Build().RunAsync();
