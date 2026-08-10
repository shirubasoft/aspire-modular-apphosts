using Aspire.Hosting.Testing;
using Spire.ModuleContract;
using Xunit;

namespace Spire.Consumer.Tests;

public sealed class WorkflowImageOverrideTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task Consumer_runs_the_producer_image_without_its_build_repository()
    {
        Assert.SkipUnless(
            string.Equals(
                Environment.GetEnvironmentVariable("WORKFLOW_IMAGE_E2E"),
                "1",
                StringComparison.Ordinal),
            "Run through the dedicated local-registry workflow image E2E job.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TestTimeout);
        var unavailableRepository = Path.Combine(
            Path.GetTempPath(),
            $"missing-workflow-image-source-{Guid.NewGuid():N}");
        await using var builder = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.Spire_Consumer_AppHost>(
                [
                    $"Aspire:ModularAppHosts:Modules:{SpireModule.Name}:BuildRepository=" +
                    unavailableRepository
                ],
                timeout.Token);
        await using var application = await builder.BuildAsync(timeout.Token)
            .WaitAsync(TestTimeout, timeout.Token);

        await application.StartAsync(timeout.Token).WaitAsync(TestTimeout, timeout.Token);
        await application.ResourceNotifications.WaitForResourceHealthyAsync(
            SpireModule.ApiResourceName,
            timeout.Token);
        using var client = application.CreateHttpClient(SpireModule.ApiResourceName, "http");

        var marker = await client.GetStringAsync("/marker.txt", timeout.Token);

        Assert.Equal("multi-repo-resource-pinned-revision", marker.Trim());
        Assert.False(Directory.Exists(unavailableRepository));
    }
}
