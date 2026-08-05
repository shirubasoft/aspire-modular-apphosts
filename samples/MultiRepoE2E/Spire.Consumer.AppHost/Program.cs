using Aspire.Hosting.ModularAppHosts;
using Spire.ModuleContract;

var builder = DistributedApplication.CreateBuilder(args);
builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:AutoCloneRepositories"] = "true";

SpireModule.ImportModule(builder);
builder.Build().Run();
