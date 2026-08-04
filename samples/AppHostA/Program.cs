using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ModularAppHosts;
using ModularSample.ModuleContract;

var builder = DistributedApplication.CreateBuilder(args);

var exported = AppHostAModule.Register(builder, builder.AppHostDirectory);
builder.Add(exported);

_ = exported.GetResource<ContainerResource>(AppHostAModule.ApiResourceName);
_ = exported.GetResource<ProjectResource>(AppHostAModule.ProjectResourceName);
_ = exported.GetResource<CSharpAppResource>(AppHostAModule.CSharpAppResourceName);
_ = exported.GetResource<ContainerResource>(AppHostAModule.StaticResourceName);
_ = exported.GetResource<ExecutableResource>(AppHostAModule.ExecutableResourceName);
#pragma warning disable ASPIREDOTNETTOOL
_ = exported.GetResource<DotnetToolResource>(AppHostAModule.DotnetToolResourceName);
#pragma warning restore ASPIREDOTNETTOOL
_ = exported.GetResource<ParameterResource>(AppHostAModule.MessageResourceName);
_ = exported.GetResource<ConnectionStringResource>(AppHostAModule.ConnectionStringResourceName);
_ = exported.GetResource<ExternalServiceResource>(AppHostAModule.ExternalServiceResourceName);
#pragma warning disable ASPIRECOMPUTE003
_ = exported.GetResource<ContainerRegistryResource>(AppHostAModule.ContainerRegistryResourceName);
#pragma warning restore ASPIRECOMPUTE003
_ = exported.GetResource<SampleCustomResource>(AppHostAModule.CustomResourceName);

builder.Build().Run();
