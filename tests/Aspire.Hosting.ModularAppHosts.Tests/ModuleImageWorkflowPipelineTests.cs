#pragma warning disable ASPIREPIPELINES003
#pragma warning disable ASPIRECOMPUTE003

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting;
using Xunit;

namespace Aspire.Hosting.ModularAppHosts.Tests;

public sealed class ModuleImageWorkflowPipelineTests
{
    [Fact]
    public async Task Includes_Aspire_native_project_publishers()
    {
        using var repository = TemporaryDirectory.Create();
        var projectPath = Path.Combine(repository.Path, "Orders.Api.csproj");
        File.WriteAllText(
            projectPath,
            "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = ["--publisher", "manifest"],
            DisableDashboard = true,
            ProjectDirectory = repository.Path
        });
        var registry = builder.AddContainerRegistry("registry", "registry.example.test", "team");
        var module = builder.ExportModule("orders", definition =>
        {
            definition.WithRepository(repository.Path);
            definition.AddProject("api", projectPath)
                .ExportAsContainer(
                    "services/orders",
                    (_, container) => container.WithContainerRegistry(registry));
        });
        builder.AddModule(module);

        var document = await ModuleImageWorkflowPipeline.CreateDocumentAsync(
            builder.Resources,
            new ModuleImageSelection(["orders"], []),
            TestContext.Current.CancellationToken);

        var image = Assert.Single(document.Images);
        Assert.Equal(ModuleResourceKind.Project, image.ResourceKind);
        Assert.Equal("registry.example.test/team/services/orders:latest", image.Reference);
    }

    [Fact]
    public async Task Uses_structured_remote_registry_identity_and_declared_resource_alias()
    {
        var builder = DistributedApplication.CreateBuilder();
        var registry = builder.AddContainerRegistry("registry", "registry.example.test", "acme");
        var container = builder
            .AddContainer("imported-api", "local/api", "local")
            .WithContainerRegistry(registry)
            .WithRemoteImageName("services/orders")
            .WithRemoteImageTag("candidate")
            .Resource;
        await AddPublisherAsync(container, "orders", "api");

        var document = await ModuleImageWorkflowPipeline.CreateDocumentAsync(
            builder.Resources,
            new ModuleImageSelection([], ["api"]),
            TestContext.Current.CancellationToken);

        var image = Assert.Single(document.Images);
        Assert.Equal("orders", image.Module);
        Assert.Equal("api", image.Resource);
        Assert.Equal("registry.example.test", image.Registry);
        Assert.Equal("acme/services/orders", image.Repository);
        Assert.Equal("candidate", image.Tag);
        Assert.Equal("registry.example.test/acme/services/orders:candidate", image.Reference);
    }

    [Fact]
    public async Task Rejects_unknown_or_non_publishable_selectors()
    {
        var resource = new ContainerResource("imported-api");
        resource.Annotations.Add(new ContainerImageAnnotation
        {
            Registry = "registry.example.test",
            Image = "acme/api",
            Tag = "candidate"
        });
        await AddPublisherAsync(resource, "orders", "api");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ModuleImageWorkflowPipeline.CreateDocumentAsync(
                [resource],
                new ModuleImageSelection([], ["missing"]),
                TestContext.Current.CancellationToken));

        Assert.Contains("missing", exception.Message, StringComparison.Ordinal);
        Assert.Contains("api", exception.Message, StringComparison.Ordinal);
    }

    private static Task AddPublisherAsync(
        ContainerResource resource,
        string module,
        string declaredResource)
    {
        var options = new ModuleImageCommandOptions("local/api", "docker", "build")
        {
            ImageRegistry = "registry.example.test",
            ImageTag = "local"
        };
        var recipe = new ModuleImageBuildRecipe(
            new ModuleImageRecipeIdentity(module, declaredResource),
            new ModuleImageRepositorySettings(
                "/work",
                "/work",
                Repository: null,
                Revision: null,
                RefreshCleanCheckout: false,
                "git",
                "gh",
                TimeSpan.FromMinutes(2)),
            new ModuleImageCommandSettings(
                options,
                TimeSpan.FromMinutes(15),
                TimeSpan.FromMinutes(10)));
        resource.Annotations.Add(new ModuleImagePublisherAnnotation(
            ModuleResourceKind.Project,
            recipe));
        resource.Annotations.Add(new DistributedApplicationModuleResourceAnnotation(
            module,
            declaredResource,
            "/work",
            imported: true));
        return Task.CompletedTask;
    }
}
