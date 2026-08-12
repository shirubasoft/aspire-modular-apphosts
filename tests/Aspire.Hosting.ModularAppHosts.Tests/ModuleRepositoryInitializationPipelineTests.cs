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
            updateOnInitialize: true).Requirement;
        var revision = registry.Register(
            "orders",
            "git@github.com:acme/services.git",
            revision: "release/2026.08",
            updateOnInitialize: true).Requirement;

        Assert.Equal(workspace.Path, Path.GetDirectoryName(branch.RepositoryPath));
        Assert.Equal(workspace.Path, Path.GetDirectoryName(revision.RepositoryPath));
        Assert.NotEqual(branch.RepositoryPath, revision.RepositoryPath);
        Assert.Contains("-rev-", revision.RepositoryPath, StringComparison.Ordinal);
        Assert.False(revision.UpdateOnInitialize);
    }

    [Fact]
    public void Distinct_remotes_with_the_same_repository_name_get_distinct_deterministic_siblings()
    {
        using var workspace = CreateGitWorkspace();
        var registry = new ModuleRepositoryPlanRegistry(workspace.AppHostPath);

        var github = registry.Register(
            "catalog",
            "https://github.com/acme/services.git",
            revision: null,
            updateOnInitialize: true).Requirement;
        var gitlab = registry.Register(
            "orders",
            "https://gitlab.example.test/acme/services.git",
            revision: null,
            updateOnInitialize: true).Requirement;
        var repeatedPlan = new ModuleRepositoryPlanRegistry(workspace.AppHostPath).Register(
            "catalog",
            "https://github.com/acme/services.git",
            revision: null,
            updateOnInitialize: true).Requirement;

        Assert.NotEqual(github.NormalizedRepository, gitlab.NormalizedRepository);
        Assert.NotEqual(github.RepositoryPath, gitlab.RepositoryPath);
        Assert.StartsWith("services-", Path.GetFileName(github.RepositoryPath), StringComparison.Ordinal);
        Assert.StartsWith("services-", Path.GetFileName(gitlab.RepositoryPath), StringComparison.Ordinal);
        Assert.Equal(github.RepositoryPath, repeatedPlan.RepositoryPath);
        Assert.Equal(2, registry.Requirements.Count);
    }

    [Fact]
    public void Local_repository_with_revision_uses_initializer_owned_sibling()
    {
        using var workspace = CreateGitWorkspace();
        var localSource = Path.Combine(workspace.Path, "local-source");
        Directory.CreateDirectory(Path.Combine(localSource, ".git"));
        var registry = new ModuleRepositoryPlanRegistry(workspace.AppHostPath);

        var requirement = registry.Register(
            "orders",
            localSource,
            revision: "v2",
            updateOnInitialize: true).Requirement;

        Assert.NotEqual(localSource, requirement.RepositoryPath);
        Assert.Equal(workspace.Path, Path.GetDirectoryName(requirement.RepositoryPath));
        Assert.StartsWith("file:", requirement.NormalizedRepository, StringComparison.Ordinal);
        Assert.Contains("-rev-", requirement.RepositoryPath, StringComparison.Ordinal);
        Assert.False(requirement.UpdateOnInitialize);
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
            updateOnInitialize: true);
        var second = registry.Register(
            "orders",
            "git@github.com:acme/services.git",
            revision: null,
            updateOnInitialize: true);

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
            updateOnInitialize: true);

        var exception = Assert.Throws<InvalidOperationException>(() => registry.Register(
            "orders",
            "https://github.com/acme/services.git",
            first.Requirement.RepositoryPath,
            revision: null,
            updateOnInitialize: false));

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
            updateOnInitialize: true));

        Assert.Contains("direct sibling", exception.Message, StringComparison.Ordinal);
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
            updateOnInitialize: true).Requirement;

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
    public async Task File_state_store_round_trips_credential_free_initialization_state()
    {
        using var workspace = CreateGitWorkspace();
        var registry = new ModuleRepositoryPlanRegistry(workspace.AppHostPath);
        var requirement = registry.Register(
            "catalog",
            "https://build-user:secret@github.com/acme/services.git?token=secret",
            revision: "v2.0.0",
            updateOnInitialize: true).Requirement;
        var store = new FileModuleRepositoryStateStore();
        var state = new ModuleRepositoryInitializationState(
            ModuleRepositoryInitializationState.CurrentSchemaVersion,
            requirement.NormalizedRepository,
            requirement.RepositoryPath,
            requirement.Revision,
            requirement.ConfigurationFingerprint,
            requirement.NormalizedRepository,
            "0123456789abcdef0123456789abcdef01234567",
            DateTimeOffset.UtcNow);

        await store.WriteAsync(
            requirement,
            state,
            TestContext.Current.CancellationToken);

        var restored = await store.ReadAsync(
            requirement,
            TestContext.Current.CancellationToken);
        var serialized = await File.ReadAllTextAsync(
            requirement.StatePath,
            TestContext.Current.CancellationToken);

        Assert.Equal(state, restored);
        Assert.True(restored!.Matches(requirement));
        Assert.DoesNotContain("secret", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("build-user", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Malformed_file_state_is_treated_as_not_initialized()
    {
        using var workspace = CreateGitWorkspace();
        var registry = new ModuleRepositoryPlanRegistry(workspace.AppHostPath);
        var requirement = registry.Register(
            "catalog",
            "acme/services",
            revision: null,
            updateOnInitialize: true).Requirement;
        Directory.CreateDirectory(Path.GetDirectoryName(requirement.StatePath)!);
        await File.WriteAllTextAsync(
            requirement.StatePath,
            "{not valid JSON",
            TestContext.Current.CancellationToken);

        Assert.Null(await new FileModuleRepositoryStateStore().ReadAsync(
            requirement,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Failed_initialization_does_not_write_repository_state()
    {
        using var workspace = CreateGitWorkspace();
        var registry = new ModuleRepositoryPlanRegistry(workspace.AppHostPath);
        var requirement = registry.Register(
            "catalog",
            "acme/services",
            revision: null,
            updateOnInitialize: true).Requirement;
        var settings = new ModuleRepositoryInitializationSettings(
            "git",
            "gh",
            TimeSpan.FromMinutes(2));
        var stateStore = new InMemoryModuleRepositoryStateStore();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ModuleRepositoryInitializationPipeline.InitializeAsync(
                requirement,
                settings,
                NullLogger.Instance,
                (_, _, _, _, _) => throw new InvalidOperationException("clone failed"),
                TestContext.Current.CancellationToken));

        Assert.Null(await stateStore.ReadAsync(requirement, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Preflight_aggregates_missing_repositories_and_required_paths()
    {
        using var workspace = CreateGitWorkspace();
        var plans = new ModuleRepositoryPlanRegistry(workspace.AppHostPath);
        var requirement = plans.Register(
            "catalog",
            "acme/services",
            revision: null,
            updateOnInitialize: true).Requirement;
        var missingProject = Path.Combine(requirement.RepositoryPath, "src", "Catalog.csproj");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ModuleRepositoryPreflight.ValidateAsync(
                plans.Requirements,
                [new ModuleRequiredPath(
                    "catalog",
                    "project 'catalog-api'",
                    missingProject,
                    ModuleRequiredPathKind.File)],
                new InMemoryModuleRepositoryStateStore(),
                new ModuleRepositoryInitializationSettings("git", "gh", TimeSpan.FromMinutes(2)),
                workspace.AppHostPath,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains(requirement.RepositoryPath, exception.Message, StringComparison.Ordinal);
        Assert.Contains(missingProject, exception.Message, StringComparison.Ordinal);
        Assert.Contains(ModuleRepositoryPreflight.CreateInitializeCommand(workspace.AppHostPath), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Initialization_emits_scoped_structured_repository_operation_events()
    {
        using var workspace = CreateGitWorkspace();
        var plans = new ModuleRepositoryPlanRegistry(workspace.AppHostPath);
        var requirement = plans.Register(
            "catalog",
            "acme/services",
            revision: null,
            updateOnInitialize: true).Requirement;
        var logger = new CapturingLogger();

        await ModuleRepositoryInitializationPipeline.InitializeAsync(
            requirement,
            new ModuleRepositoryInitializationSettings("git", "gh", TimeSpan.FromMinutes(2)),
            logger,
            (_, _, _, lifecycle, _) =>
            {
                lifecycle(new RepositorySyncLifecycleEvent("clone", "started"));
                lifecycle(new RepositorySyncLifecycleEvent(
                    "clone",
                    "completed",
                    ElapsedMilliseconds: 12));
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        var operations = logger.Entries.Where(entry => entry.EventId.Id == 4).ToArray();
        Assert.Equal(2, operations.Length);
        Assert.Equal("clone", operations[0].State["Operation"]);
        Assert.Equal("started", operations[0].State["State"]);
        Assert.Equal(requirement.RepositoryPath, operations[0].State["RepositoryPath"]);
        Assert.Contains(logger.Scopes, scope => scope.ContainsKey("OperationId"));
        Assert.Contains(logger.Scopes, scope => Equals(scope["RepositoryKind"], "branch"));
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

    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<(Microsoft.Extensions.Logging.EventId EventId, Dictionary<string, object?> State)> Entries { get; } = [];

        public List<Dictionary<string, object?>> Scopes { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                Scopes.Add(values.ToDictionary(pair => pair.Key, pair => pair.Value));
            }

            return NullScope.Instance;
        }

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var values = state as IEnumerable<KeyValuePair<string, object?>> ?? [];
            Entries.Add((eventId, values.ToDictionary(pair => pair.Key, pair => pair.Value)));
        }

        private sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();

            public void Dispose()
            {
            }
        }
    }
}
