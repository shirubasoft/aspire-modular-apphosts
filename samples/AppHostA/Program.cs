using Aspire.Hosting.ModularAppHosts;
using ModularSample.ModuleContract;

var builder = DistributedApplication.CreateBuilder(args);

var exported = AppHostAModule.Register(builder, builder.AppHostDirectory);
builder.Add(exported);

builder.Build().Run();
