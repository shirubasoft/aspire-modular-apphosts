using Aspire.Hosting.ModularAppHosts;
using ModularSample.ModuleContract;

var builder = DistributedApplication.CreateBuilder(args);
builder.BuildModuleImages();

var exported = await AppHostAModule.RegisterAsync(builder, builder.AppHostDirectory);
var module = await builder.AddAppHostAModuleAsync(exported);

_ = module.Api;
_ = module.Project;
_ = module.CSharpApp;
_ = module.Static;
_ = module.GeneratedStatic;
_ = module.Executable;
_ = module.DotnetTool;
_ = module.Message;
_ = module.ConnectionString;
_ = module.ExternalService;
_ = module.ContainerRegistry;
_ = module.Custom;

await builder.Build().RunAsync();
