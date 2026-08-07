#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ModularAppHosts;
using Xunit;

namespace Aspire.Hosting.ModularAppHosts.Tests;

public sealed class ModuleImageDescriptionPipelineTests
{
    [Fact]
    public async Task Describes_effective_configured_images_for_all_module_publisher_kinds()
    {
        using var repository = TemporaryDirectory.Create();
        var projectPath = Path.Combine(repository.Path, "ImageProject.csproj");
        File.WriteAllText(
            projectPath,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        var builder = CreatePublishBuilder(repository.Path);
        ConfigureImage(builder, "Projects", "project", "acme/project", "project-ci");
        ConfigureImage(builder, "Containers", "declared", "acme/declared", "declared-ci");
        ConfigureImage(builder, "Containers", "factory", "acme/factory", "factory-ci");
        var module = await builder.ExportModuleAsync("images", definition =>
        {
            definition.WithRepository(repository.Path);
            definition.AddProject("project", projectPath)
                .ExportAsContainer(new ModuleContainerExportOptions("old/project", "build-project", "publish")
                {
                    ImageRegistry = "old.example.test",
                    ImageTag = "old"
                });
            definition.AddContainer("declared", "old.example.test/old/declared", "old")
                .WithImagePublishCommand(new ModuleContainerExportOptions(
                    "old/declared",
                    "build-declared",
                    "publish")
                {
                    ImageRegistry = "old.example.test",
                    ImageTag = "old"
                });
            definition.AddResource<ContainerResource>(
                "factory",
                context => context.ApplicationBuilder.AddContainer(context.ResourceName, "placeholder"),
                new ModuleContainerExportOptions("old/factory", "build-factory", "publish")
                {
                    ImageRegistry = "old.example.test",
                    ImageTag = "old"
                });
            definition.AddContainer("consumed", "registry.example.test/library/redis", "7");
        });

        await builder.AddAsync(module);

        var document = await ModuleImageDescriptionPipeline.CreateDocumentAsync(
            builder.Resources,
            ModuleImageSelection.All,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            ["consumed", "declared", "factory", "project"],
            document.Images.Select(image => image.EffectiveResource));
        Assert.Collection(
            document.Images,
            image =>
            {
                Assert.Equal("consumed", image.Resource);
                Assert.Equal("registry.example.test", image.Registry);
                Assert.Equal("library/redis", image.Repository);
                Assert.Equal("7", image.Tag);
                Assert.Equal("registry.example.test/library/redis:7", image.Reference);
                Assert.Equal(image.Reference, image.PullReference);
                Assert.Null(image.PushReference);
                Assert.Null(image.Build);
            },
            image => AssertImage(image, "declared", "acme/declared", "declared-ci", "build-declared"),
            image => AssertImage(image, "factory", "acme/factory", "factory-ci", "build-factory"),
            image => AssertImage(image, "project", "acme/project", "project-ci", "build-project"));
    }

    [Fact]
    public async Task Description_selection_accepts_the_declared_resource_alias()
    {
        var resource = new ContainerResource("imported-api");
        resource.Annotations.Add(new ContainerImageAnnotation
        {
            Registry = "registry.example.test",
            Image = "acme/api",
            Tag = "preview"
        });
        var options = new ModuleContainerExportOptions("acme/api", "docker", "build")
        {
            ImageRegistry = "registry.example.test",
            ImageTag = "preview"
        };
        var plan = await ModuleImagePublishPlan.CreateAsync(
            options,
            repositoryDirty: false,
            (_, _) => Task.FromResult(false),
            TestContext.Current.CancellationToken);
        resource.Annotations.Add(new ModuleImagePublisherAnnotation(
            "orders",
            "api",
            ModulePreviewResourceKind.Container,
            options,
            plan,
            "/work",
            "https://example.test/orders.git",
            "main"));
        resource.Annotations.Add(new DistributedApplicationModuleResourceAnnotation(
            "orders",
            "api",
            "/work",
            imported: true));

        var document = await ModuleImageDescriptionPipeline.CreateDocumentAsync(
            [resource],
            new ModuleImageSelection(["api"]),
            TestContext.Current.CancellationToken);

        var image = Assert.Single(document.Images);
        Assert.Equal("imported-api", image.EffectiveResource);
        Assert.Equal("api", image.Resource);
    }

    private static void AssertImage(
        ModuleImageDescription image,
        string resource,
        string repository,
        string tag,
        string command)
    {
        Assert.Equal("images", image.Module);
        Assert.Equal(resource, image.Resource);
        Assert.Equal("registry.example.test", image.Registry);
        Assert.Equal(repository, image.Repository);
        Assert.Equal(tag, image.Tag);
        Assert.Null(image.Digest);
        Assert.Equal($"registry.example.test/{repository}:{tag}", image.Reference);
        Assert.Equal(image.Reference, image.PullReference);
        Assert.Equal(image.Reference, image.PushReference);
        Assert.Equal(command, image.Build!.Command);
        Assert.Equal($"build-{resource}", image.Build.Step);
    }

    private static void ConfigureImage(
        IDistributedApplicationBuilder builder,
        string collection,
        string resource,
        string repository,
        string tag)
    {
        var section = $"{ModularAppHostsOptions.ConfigurationSectionName}:Modules:images:{collection}:{resource}";
        builder.Configuration[$"{section}:ImageRegistry"] = "registry.example.test";
        builder.Configuration[$"{section}:ImageName"] = repository;
        builder.Configuration[$"{section}:ImageTag"] = tag;
    }

    private static IDistributedApplicationBuilder CreatePublishBuilder(string projectDirectory)
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = ["--publisher", "manifest"],
            DisableDashboard = true,
            ProjectDirectory = projectDirectory
        });
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:ProjectMode"] =
            nameof(ModuleProjectMode.Container);
        return builder;
    }
}
