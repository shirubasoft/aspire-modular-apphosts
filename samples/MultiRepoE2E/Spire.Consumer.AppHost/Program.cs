using Aspire.Hosting.ModularAppHosts;
using Spire.ModuleContract;

var builder = DistributedApplication.CreateBuilder(args);
builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:AutoCloneRepositories"] = "true";

await SpireModule.ImportModuleAsync(builder);
await builder.Build().RunAsync();
