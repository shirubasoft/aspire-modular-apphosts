#pragma warning disable ASPIREPIPELINES003
#pragma warning disable ASPIRECOMPUTE003

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting;
using Xunit;

namespace Aspire.Hosting.ModularAppHosts.Tests;

public sealed class ModuleImageManifestPipelineTests
{
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

        var document = await ModuleImageManifestPipeline.CreateDocumentAsync(
            builder.Resources,
            new ModuleImageSelection(["api"]),
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
            ModuleImageManifestPipeline.CreateDocumentAsync(
                [resource],
                new ModuleImageSelection(["missing"]),
                TestContext.Current.CancellationToken));

        Assert.Contains("missing", exception.Message, StringComparison.Ordinal);
        Assert.Contains("api", exception.Message, StringComparison.Ordinal);
    }

    private static async Task AddPublisherAsync(
        ContainerResource resource,
        string module,
        string declaredResource)
    {
        var options = new ModuleContainerExportOptions("local/api", "docker", "build")
        {
            ImageRegistry = "registry.example.test",
            ImageTag = "local"
        };
        var plan = await ModuleImagePublishPlan.CreateAsync(
            options,
            repositoryDirty: false,
            (_, _) => Task.FromResult(false),
            TestContext.Current.CancellationToken);
        resource.Annotations.Add(new ModuleImagePublisherAnnotation(
            module,
            declaredResource,
            ModuleResourceKind.Project,
            options,
            plan,
            "/work",
            null,
            null));
        resource.Annotations.Add(new DistributedApplicationModuleResourceAnnotation(
            module,
            declaredResource,
            "/work",
            imported: true));
    }
}
