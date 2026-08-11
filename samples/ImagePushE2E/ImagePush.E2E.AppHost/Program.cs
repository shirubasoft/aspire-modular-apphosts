#pragma warning disable ASPIRECOMPUTE003
#pragma warning disable ASPIREPIPELINES003

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting;

const string ImageTag = "push-test";
const string ProjectResourceName = "image-push-project";

var builder = DistributedApplication.CreateBuilder(args);
builder.UseModuleContainers();
builder.AddDockerComposeEnvironment("compose");
var containerRuntime = await ContainerRuntimeResolver.ResolveAsync();

var registryEndpoint = builder.Configuration["ImagePush:RegistryEndpoint"];
if (string.IsNullOrWhiteSpace(registryEndpoint))
{
    throw new InvalidOperationException(
        "ImagePush:RegistryEndpoint must identify the local registry used by the image-push E2E test.");
}

var retagRegistryEndpoint = builder.Configuration["ImagePush:RetagRegistryEndpoint"] ?? registryEndpoint;

var registry = builder.AddContainerRegistry(
    "image-push-registry",
    registryEndpoint,
    "image-push");
var module = await builder.ExportModuleAsync("image-push-e2e", "Sample.ImagePush.Contract", definition =>
{
    definition.WithRepository(builder.AppHostDirectory);

    definition.AddContainer(
            "image-push-declared",
            $"{registryEndpoint}/image-push/declared",
            ImageTag)
        .WithImagePublishCommand(new ModuleContainerExportOptions(
            "image-push/declared",
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

    definition.AddContainer(
            "image-pull-mapped",
            $"{retagRegistryEndpoint}/image-pull/local",
            ImageTag)
        .WithImagePullMapping($"{registryEndpoint}/image-pull/source:{ImageTag}")
        .Configure((_, container) => container.WithExplicitStart());

    definition.AddProject(
            ProjectResourceName,
            Path.Combine(builder.AppHostDirectory, "ExportedProject", "ImagePush.ExportedProject.csproj"))
        .ExportAsContainer(
            new ModuleContainerExportOptions(
                ProjectResourceName,
                containerRuntime,
                "build",
                "--tag",
                ModuleContainerExportOptions.ImageReferencePlaceholder,
                ".")
            {
                ImageTag = ImageTag,
                WorkingDirectory = "ImageFixture"
            },
            (_, container) => container
                .WithContainerRegistry(registry)
                .WithRemoteImageName("project")
                .WithRemoteImageTag(ImageTag)
                .WithExplicitStart());

    definition.AddResource<ContainerResource>(
        "image-push-factory",
        context => context.ApplicationBuilder
            .AddContainer(context.ResourceName, "placeholder")
            .WithExplicitStart(),
        new ModuleContainerExportOptions(
            "image-push/factory",
            containerRuntime,
            "build",
            "--tag",
            ModuleContainerExportOptions.ImageReferencePlaceholder,
            ".")
        {
            ImageRegistry = registryEndpoint,
            ImageTag = ImageTag,
            WorkingDirectory = "ImageFixture"
        });
});

await builder.AddAsync(module);

var extraModule = await builder.ExportModuleAsync("image-push-extra", definition =>
{
    definition.WithRepository(builder.AppHostDirectory);
    definition.AddContainer(
            "image-push-extra",
            $"{registryEndpoint}/image-push/extra",
            ImageTag)
        .WithImagePublishCommand(new ModuleContainerExportOptions(
            "image-push/extra",
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

await builder.AddAsync(extraModule);

var contractOnlyModule = await builder.ExportModuleAsync(
    "contract-only",
    "Sample.ContractOnly",
    definition => definition.AddResource<ParameterResource>(
        "contract-only-message",
        context => context.ApplicationBuilder.AddParameter(
            context.ResourceName,
            "contract metadata without an image publisher",
            publishValueAsDefault: true)));

await builder.AddAsync(contractOnlyModule);
await builder.Build().RunAsync();
