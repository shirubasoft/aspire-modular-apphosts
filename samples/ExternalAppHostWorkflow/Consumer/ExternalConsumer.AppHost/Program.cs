#pragma warning disable ASPIRECOMPUTE003
#pragma warning disable ASPIREPIPELINES003

using Aspire.Hosting.ModularAppHosts;

const string ModuleName = "external-build-source";
const string ResourceName = "external-image";
const string ImageTag = "external-test";

var builder = DistributedApplication.CreateBuilder(args);
builder.UseModuleContainers();
var containerRuntime = await ContainerRuntimeResolver.ResolveAsync();
var registryEndpoint = builder.Configuration["ExternalAppHost:RegistryEndpoint"];
if (string.IsNullOrWhiteSpace(registryEndpoint))
{
    throw new InvalidOperationException(
        "ExternalAppHost:RegistryEndpoint must identify the registry used by the sample.");
}

var module = await builder.ExportModuleAsync(ModuleName, definition =>
{
    definition.WithRepository(builder.AppHostDirectory);
    definition.AddContainer(
            ResourceName,
            $"{registryEndpoint}/external/image",
            ImageTag)
        .WithImagePublishCommand(new ModuleContainerExportOptions(
            "external/image",
            containerRuntime,
            "build",
            "--tag",
            ModuleContainerExportOptions.ImageReferencePlaceholder,
            ".")
        {
            ImageRegistry = registryEndpoint,
            ImageTag = ImageTag,
            WorkingDirectory = "ImageFixture"
        })
        .Configure((_, container) => container.WithExplicitStart());
});

await builder.AddAsync(module);
await builder.Build().RunAsync();
