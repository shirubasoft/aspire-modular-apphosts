using Aspire.Hosting.ModularAppHosts;
using ModularSample.ModuleContract;

var builder = DistributedApplication.CreateBuilder(args);

var exported = AppHostAModule.Register(builder, builder.AppHostDirectory);
var module = AppHostAModule.AddModule(builder, exported);

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

builder.Build().Run();
