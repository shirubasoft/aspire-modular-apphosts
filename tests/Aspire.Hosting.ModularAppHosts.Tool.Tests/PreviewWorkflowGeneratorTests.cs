using YamlDotNet.Serialization;
using Xunit;

namespace Shirubasoft.Aspire.ModularAppHosts.Tool.Tests;

public sealed class PreviewWorkflowGeneratorTests
{
    [Fact]
    public async Task Producer_generation_matches_golden_file_and_emits_valid_yaml()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var outputDirectory = Path.Combine(
            Path.GetTempPath(),
            $"modular-apphosts-workflow-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);
        try
        {
            var outputPath = Path.Combine(outputDirectory, "producer.yml");
            string[] arguments =
                [
                    "preview", "workflow", "generate", "producer",
                    "--descriptor", "module-preview.producer.json",
                    "--working-directory", Path.Combine(AppContext.BaseDirectory, "TestData"),
                    "--apphost", "src/Sample.AppHost/Sample.AppHost.csproj",
                    "--output", outputPath,
                    "--repo", "example/consumer-tests",
                    "--workflow", "e2e.yml",
                    "--ref", "main",
                    "--aspire-version", "13.4.6",
                    "--tool-version", "4.4.0",
                    "--github-token-secret", "PREVIEW_GITHUB_TOKEN",
                    "--registry-auth-script", ".github/scripts/registry-login.sh",
                    "--package-auth-script", ".github/scripts/package-login.sh",
                    "--contract-publish-script", ".github/scripts/publish-contract.sh",
                    "--secret", "REGISTRY_TOKEN=CONTAINER_REGISTRY_TOKEN",
                    "--secret", "PACKAGE_TOKEN=PACKAGE_FEED_TOKEN"
                ];
            var exitCode = await PreviewTool.RunAsync(
                arguments,
                cancellationToken);

            Assert.Equal(0, exitCode);
            var actual = await File.ReadAllTextAsync(outputPath, cancellationToken);
            var expected = await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, "TestData", "producer-workflow.yml"),
                cancellationToken);
            Assert.Equal(NormalizeLineEndings(expected), NormalizeLineEndings(actual));
            Assert.Contains(
                "branch=$(git symbolic-ref --quiet --short HEAD)",
                actual,
                StringComparison.Ordinal);
            Assert.Contains(
                "pushed_repository=${push_reference%:*}",
                actual,
                StringComparison.Ordinal);
            Assert.Contains(
                "select(.build != null and .pushReference != null)",
                actual,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                ".registry + \"/\" + .repository",
                actual,
                StringComparison.Ordinal);

            var parsed = new DeserializerBuilder().Build()
                .Deserialize<Dictionary<object, object>>(actual);
            Assert.Contains("on", parsed.Keys.Cast<string>());
            Assert.Contains("jobs", parsed.Keys.Cast<string>());

            Assert.Equal(1, await PreviewTool.RunAsync(arguments, cancellationToken));
            Assert.Equal(actual, await File.ReadAllTextAsync(outputPath, cancellationToken));
            Assert.Equal(0, await PreviewTool.RunAsync(
                [.. arguments, "--force"],
                cancellationToken));
            Assert.Equal(actual, await File.ReadAllTextAsync(outputPath, cancellationToken));
        }
        finally
        {
            Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData("preview", "workflow")]
    [InlineData("preview", "workflow", "generate")]
    [InlineData("preview", "workflow", "generate", "consumer")]
    public async Task Workflow_command_rejects_incomplete_or_unknown_shapes(params string[] arguments)
    {
        var exitCode = await PreviewTool.RunAsync(
            arguments,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task Producer_generation_requires_an_explicit_registry_authentication_choice()
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"workflow-{Guid.NewGuid():N}.yml");
        try
        {
            var exitCode = await PreviewTool.RunAsync(
                [
                    "preview", "workflow", "generate", "producer",
                    "--descriptor", "module-preview.producer.json",
                    "--working-directory", Path.Combine(AppContext.BaseDirectory, "TestData"),
                    "--apphost", "src/Sample.AppHost/Sample.AppHost.csproj",
                    "--output", outputPath,
                    "--repo", "example/consumer-tests",
                    "--workflow", "e2e.yml",
                    "--ref", "main",
                    "--aspire-version", "13.4.6",
                    "--tool-version", "4.4.0",
                    "--github-token-secret", "PREVIEW_GITHUB_TOKEN"
                ],
                TestContext.Current.CancellationToken);

            Assert.Equal(1, exitCode);
            Assert.False(File.Exists(outputPath));
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    private static string NormalizeLineEndings(string value) =>
        $"{value.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n')}\n";
}
