using Aspire.Hosting.ModularAppHosts;
using System.Text.Json;
using Xunit;

namespace Aspire.Hosting.ModularAppHosts.Tool.Tests;

public sealed class WorkflowDocumentationTests
{
    [Fact]
    public async Task Checked_in_manifest_example_matches_the_current_contract()
    {
        var repositoryRoot = FindRepositoryRoot();
        var examplePath = Path.Combine(
            repositoryRoot,
            "docs",
            "examples",
            ModuleImageManifestDocument.DefaultFileName);

        var manifest = await ModuleImageManifestDocument.LoadAsync(
            examplePath,
            TestContext.Current.CancellationToken);

        Assert.Equal(ModuleImageManifestDocument.CurrentSchemaVersion, manifest.SchemaVersion);
        Assert.Equal(2, manifest.Images.Count);
    }

    [Fact]
    public async Task Checked_in_schema_tracks_the_current_manifest_version()
    {
        var schemaPath = Path.Combine(
            FindRepositoryRoot(),
            "docs",
            "module-image-manifest.schema.json");
        await using var stream = File.OpenRead(schemaPath);
        using var schema = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: TestContext.Current.CancellationToken);

        var schemaVersion = schema.RootElement
            .GetProperty("properties")
            .GetProperty("schemaVersion")
            .GetProperty("const")
            .GetInt32();
        Assert.Equal(ModuleImageManifestDocument.CurrentSchemaVersion, schemaVersion);
    }

    [Theory]
    [InlineData("repo-a-e2e.yml")]
    [InlineData("repo-b-dispatch.yml")]
    [InlineData("repo-b-workflow-call.yml")]
    public async Task Adoption_workflows_do_not_reimplement_tool_orchestration(string fileName)
    {
        var workflowPath = Path.Combine(FindRepositoryRoot(), "docs", "workflows", fileName);
        var workflow = await File.ReadAllTextAsync(
            workflowPath,
            TestContext.Current.CancellationToken);

        Assert.DoesNotContain("run: |", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("jq ", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("gh workflow run", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("gh run watch", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("GITHUB_ENV", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("GITHUB_OUTPUT", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("export Aspire__", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("--github-env", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("--github-output", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Repo_A_workflow_runs_the_E2E_command_through_manifest_apply()
    {
        var workflowPath = Path.Combine(
            FindRepositoryRoot(),
            "docs",
            "workflows",
            "repo-a-e2e.yml");
        var workflow = await File.ReadAllTextAsync(
            workflowPath,
            TestContext.Current.CancellationToken);
        workflow = workflow.ReplaceLineEndings("\n");

        var applyIndex = workflow.IndexOf("manifest apply", StringComparison.Ordinal);
        var commandBoundaryIndex = workflow.IndexOf("\n          --\n", StringComparison.Ordinal);
        var testIndex = workflow.IndexOf("dotnet test", StringComparison.Ordinal);
        Assert.True(applyIndex >= 0);
        Assert.True(commandBoundaryIndex > applyIndex);
        Assert.True(testIndex > commandBoundaryIndex);
    }

    [Fact]
    public async Task Multi_repo_sample_uses_the_same_portable_apply_command()
    {
        var readmePath = Path.Combine(
            FindRepositoryRoot(),
            "samples",
            "MultiRepoE2E",
            "README.md");
        var readme = await File.ReadAllTextAsync(
            readmePath,
            TestContext.Current.CancellationToken);

        Assert.Contains("manifest apply", readme, StringComparison.Ordinal);
        Assert.Contains("--file artifacts/manual-workflow-image-manifest.json", readme, StringComparison.Ordinal);
        Assert.Contains("dotnet test", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("GITHUB_ENV", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("export Aspire__", readme, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Aspire.ModularAppHosts.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Unable to locate the repository root.");
    }
}
