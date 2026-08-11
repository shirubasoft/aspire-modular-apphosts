#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES003

using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aspire.Hosting.ModularAppHosts.Tests;

public sealed class ModuleRepositoryInitializationPipelineTests
{
    [Fact]
    public void Planning_finds_git_file_without_invoking_git()
    {
        using var workspace = TemporaryDirectory.Create();
        var gitRoot = Path.Combine(workspace.Path, "consumer");
        var appHost = Path.Combine(gitRoot, "src", "AppHost");
        Directory.CreateDirectory(appHost);
        File.WriteAllText(Path.Combine(gitRoot, ".git"), "gitdir: ../metadata/worktrees/consumer\n");

        var registry = new ModuleRepositoryPlanRegistry(appHost);

        Assert.Equal(gitRoot, registry.AppHostRepositoryRoot);
        Assert.Equal(workspace.Path, registry.SiblingParent);
    }

    [Fact]
    public void Sibling_names_are_remote_specific_and_revision_specific()
    {
        using var workspace = CreateGitWorkspace();
        var registry = new ModuleRepositoryPlanRegistry(workspace.AppHostPath);

        var branch = registry.Register(
            "catalog",
            "https://github.com/Acme/Services.git",
            revision: null,
            updateRepository: true).Requirement;
        var revision = registry.Register(
            "orders",
            "git@github.com:acme/services.git",
            revision: "release/2026.08",
            updateRepository: true).Requirement;

        Assert.Equal(workspace.Path, Path.GetDirectoryName(branch.RepositoryPath));
        Assert.Equal(workspace.Path, Path.GetDirectoryName(revision.RepositoryPath));
        Assert.NotEqual(branch.RepositoryPath, revision.RepositoryPath);
        Assert.Contains("-rev-", revision.RepositoryPath, StringComparison.Ordinal);
        Assert.False(revision.UpdateRepository);
    }

    [Fact]
    public void Equivalent_remotes_share_one_plan_and_collect_module_names()
    {
        using var workspace = CreateGitWorkspace();
        var registry = new ModuleRepositoryPlanRegistry(workspace.AppHostPath);

        var first = registry.Register(
            "catalog",
            "https://build-user:secret@github.com/Acme/Services.git?token=secret",
            revision: null,
            updateRepository: true);
        var second = registry.Register(
            "orders",
            "git@github.com:acme/services.git",
            revision: null,
            updateRepository: true);

        Assert.True(first.IsNew);
        Assert.False(second.IsNew);
        Assert.Same(first.Requirement, second.Requirement);
        Assert.Equal(2, first.Requirement.ModuleNames.Count);
        Assert.Equal("github.com/acme/services", first.Requirement.NormalizedRepository);
        Assert.Single(registry.Requirements);
    }

    [Fact]
    public void Shared_checkout_rejects_conflicting_initialization_policy()
    {
        using var workspace = CreateGitWorkspace();
        var registry = new ModuleRepositoryPlanRegistry(workspace.AppHostPath);
        var first = registry.Register(
            "catalog",
            "acme/services",
            revision: null,
            updateRepository: true);

        var exception = Assert.Throws<InvalidOperationException>(() => registry.Register(
            "orders",
            "https://github.com/acme/services.git",
            first.Requirement.RepositoryPath,
            revision: null,
            updateRepository: false));

        Assert.Contains("conflicting", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Explicit_checkout_must_be_a_direct_sibling()
    {
        using var workspace = CreateGitWorkspace();
        var registry = new ModuleRepositoryPlanRegistry(workspace.AppHostPath);

        var exception = Assert.Throws<InvalidOperationException>(() => registry.Register(
            "catalog",
            "acme/services",
            Path.Combine(workspace.Path, "nested", "services"),
            revision: null,
            updateRepository: true));

        Assert.Contains("direct sibling", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new[] { "--operation", "publish", "--step", "initialize" }, true)]
    [InlineData(new[] { "--operation", "publish", "--step=INITIALIZE" }, true)]
    [InlineData(new[] { "--operation", "run" }, false)]
    [InlineData(new[] { "--operation", "publish", "--step", "build" }, false)]
    public void Initialize_command_detection_matches_the_requested_pipeline_step(
        string[] arguments,
        bool expected)
    {
        Assert.Equal(
            expected,
            ModuleRepositoryInitializationPipeline.IsInitializeCommand(arguments));
    }

    [Fact]
    public void Pipeline_exposes_aggregate_and_per_repository_steps()
    {
        using var workspace = CreateGitWorkspace();
        var registry = new ModuleRepositoryPlanRegistry(workspace.AppHostPath);
        var requirement = registry.Register(
            "catalog",
            "acme/services",
            revision: null,
            updateRepository: true).Requirement;

        var aggregate = ModuleRepositoryInitializationPipeline.CreateAggregateStep();
        var repository = ModuleRepositoryInitializationPipeline.CreateRepositoryStep(
            requirement,
            static () => new ModuleRepositoryInitializationSettings(
                "git",
                "gh",
                TimeSpan.FromMinutes(2)));

        Assert.Equal(ModuleRepositoryInitializationPipeline.StepName, aggregate.Name);
        Assert.Equal(
            ModuleRepositoryInitializationPipeline.GetRepositoryStepName(requirement),
            repository.Name);
        Assert.Contains(ModuleRepositoryInitializationPipeline.StepName, repository.RequiredBySteps);
        Assert.Contains(ModuleRepositoryInitializationPipeline.RepositoryStepTag, repository.Tags);
    }

    [Fact]
    public async Task Successful_initialization_writes_a_credential_free_matching_receipt()
    {
        using var workspace = CreateGitWorkspace();
        var registry = new ModuleRepositoryPlanRegistry(workspace.AppHostPath);
        var requirement = registry.Register(
            "catalog",
            "https://build-user:secret@github.com/acme/services.git?token=secret",
            revision: "v2.0.0",
            updateRepository: true).Requirement;
        var settings = new ModuleRepositoryInitializationSettings(
            "git",
            "gh",
            TimeSpan.FromMinutes(2));
        var synchronizationCount = 0;

        await ModuleRepositoryInitializationPipeline.InitializeAsync(
            requirement,
            settings,
            NullLogger.Instance,
            (_, _, progress, _) =>
            {
                synchronizationCount++;
                progress("fetch complete");
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        var receipt = await ModuleInitializationReceiptStore.ReadAsync(
            requirement.ReceiptPath,
            TestContext.Current.CancellationToken);
        var receiptContents = await File.ReadAllTextAsync(
            requirement.ReceiptPath,
            TestContext.Current.CancellationToken);
        Assert.Equal(1, synchronizationCount);
        Assert.NotNull(receipt);
        Assert.True(receipt.Matches(requirement));
        Assert.True(ModuleInitializationReceiptStore.HasMatchingReceipt(requirement));
        Assert.DoesNotContain("secret", receiptContents, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("build-user", receiptContents, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Malformed_receipt_is_treated_as_not_initialized_by_synchronous_preflight()
    {
        using var workspace = CreateGitWorkspace();
        var registry = new ModuleRepositoryPlanRegistry(workspace.AppHostPath);
        var requirement = registry.Register(
            "catalog",
            "acme/services",
            revision: null,
            updateRepository: true).Requirement;
        Directory.CreateDirectory(Path.GetDirectoryName(requirement.ReceiptPath)!);
        File.WriteAllText(requirement.ReceiptPath, "{not valid JSON");

        Assert.False(ModuleInitializationReceiptStore.HasMatchingReceipt(requirement));
    }

    [Fact]
    public async Task Failed_initialization_does_not_write_a_receipt()
    {
        using var workspace = CreateGitWorkspace();
        var registry = new ModuleRepositoryPlanRegistry(workspace.AppHostPath);
        var requirement = registry.Register(
            "catalog",
            "acme/services",
            revision: null,
            updateRepository: true).Requirement;
        var settings = new ModuleRepositoryInitializationSettings(
            "git",
            "gh",
            TimeSpan.FromMinutes(2));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ModuleRepositoryInitializationPipeline.InitializeAsync(
                requirement,
                settings,
                NullLogger.Instance,
                (_, _, _, _) => throw new InvalidOperationException("clone failed"),
                TestContext.Current.CancellationToken));

        Assert.False(File.Exists(requirement.ReceiptPath));
    }

    private static GitWorkspace CreateGitWorkspace()
    {
        var temporaryDirectory = TemporaryDirectory.Create();
        var gitRoot = Path.Combine(temporaryDirectory.Path, "consumer");
        var appHostPath = Path.Combine(gitRoot, "src", "AppHost");
        Directory.CreateDirectory(Path.Combine(gitRoot, ".git"));
        Directory.CreateDirectory(appHostPath);
        return new GitWorkspace(temporaryDirectory, appHostPath);
    }

    private sealed class GitWorkspace(TemporaryDirectory temporaryDirectory, string appHostPath)
        : IDisposable
    {
        public string Path => temporaryDirectory.Path;

        public string AppHostPath { get; } = appHostPath;

        public void Dispose() => temporaryDirectory.Dispose();
    }
}
