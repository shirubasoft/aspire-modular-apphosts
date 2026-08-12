#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES003

using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Logging;
using Xunit;
using CliCommand = global::CliWrap.Cli;

namespace Aspire.Hosting.ModularAppHosts.Tests;

public sealed class RepositoryInitializationContractTests
{
    [Fact]
    public async Task Initialization_clones_the_planned_revision_and_emits_structured_events()
    {
        using var workspace = TemporaryDirectory.Create();
        var sourcePath = Path.Combine(workspace.Path, "orders-source");
        var firstCommit = await InitializeRepositoryAsync(sourcePath, "first");
        var secondCommit = await CommitAsync(sourcePath, "second");
        var appHostPath = CreateAppHostRepository(workspace.Path);
        var plans = new ModuleRepositoryPlanRegistry(appHostPath);
        var requirement = plans.Register(
            "orders",
            sourcePath,
            revision: firstCommit,
            updateOnInitialize: true).Requirement;
        var logger = new CapturingLogger();
        var stateStore = new InMemoryModuleRepositoryStateStore();

        await ModuleRepositoryInitializationPipeline.InitializeAndRecordAsync(
            requirement,
            new ModuleRepositoryInitializationSettings("git", "gh", TimeSpan.FromMinutes(1)),
            logger,
            logger,
            stateStore,
            reportingStep: null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEqual(sourcePath, requirement.RepositoryPath);
        Assert.Equal(workspace.Path, Path.GetDirectoryName(requirement.RepositoryPath));
        Assert.False(requirement.UpdateOnInitialize);
        Assert.Equal(firstCommit, await ReadGitAsync(requirement.RepositoryPath, "rev-parse", "HEAD"));
        Assert.Equal(string.Empty, await ReadGitAsync(requirement.RepositoryPath, "branch", "--show-current"));
        var state = await stateStore.ReadAsync(requirement, TestContext.Current.CancellationToken);
        Assert.NotNull(state);
        Assert.True(state.Matches(requirement));
        Assert.Equal(firstCommit, state.ResolvedCommit, ignoreCase: true);

        await ModuleRepositoryPreflight.ValidateAsync(
            [requirement],
            [],
            stateStore,
            new ModuleRepositoryInitializationSettings("git", "gh", TimeSpan.FromMinutes(1)),
            appHostPath,
            cancellationToken: TestContext.Current.CancellationToken);

        await RunGitAsync(requirement.RepositoryPath, "checkout", "--detach", secondCommit);
        var staleCommit = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ModuleRepositoryPreflight.ValidateAsync(
                [requirement],
                [],
                stateStore,
                new ModuleRepositoryInitializationSettings("git", "gh", TimeSpan.FromMinutes(1)),
                appHostPath,
                cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("HEAD does not match", staleCommit.Message, StringComparison.Ordinal);

        await RunGitAsync(requirement.RepositoryPath, "checkout", "--detach", firstCommit);
        await RunGitAsync(requirement.RepositoryPath, "remote", "set-url", "origin", "https://example.test/other.git");
        var wrongOrigin = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ModuleRepositoryPreflight.ValidateAsync(
                [requirement],
                [],
                stateStore,
                new ModuleRepositoryInitializationSettings("git", "gh", TimeSpan.FromMinutes(1)),
                appHostPath,
                cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("checkout origin differs", wrongOrigin.Message, StringComparison.Ordinal);

        Assert.Contains(logger.Entries, entry => entry.EventId.Id == 1);
        Assert.Contains(logger.Entries, entry => entry.EventId.Id == 3);
        var operations = logger.Entries
            .Where(entry => entry.EventId.Id == 4)
            .Select(entry => (entry.State["Operation"], entry.State["State"]))
            .ToArray();
        Assert.Equal(
            [
                ("clone", "started"),
                ("clone", "completed"),
                ("fetch", "started"),
                ("fetch", "completed"),
                ("checkout", "started"),
                ("checkout", "completed"),
                ("submodule-update", "started"),
                ("submodule-update", "completed")
            ],
            operations);
        Assert.Contains(
            logger.Scopes,
            scope => Equals(scope["RepositoryKind"], "revision") &&
                Equals(scope["RepositoryPath"], requirement.RepositoryPath));
    }

    [Fact]
    public async Task Synchronization_fast_forwards_a_clean_checkout_with_an_upstream()
    {
        using var workspace = TemporaryDirectory.Create();
        var sourcePath = Path.Combine(workspace.Path, "catalog-source");
        await InitializeRepositoryAsync(sourcePath, "first");
        var checkoutPath = Path.Combine(workspace.Path, "catalog-checkout");
        await SynchronizeAsync(checkoutPath, sourcePath, updateRepository: true);
        var oldCommit = await ReadGitAsync(checkoutPath, "rev-parse", "HEAD");
        var newCommit = await CommitAsync(sourcePath, "second");
        var lifecycle = new List<RepositorySyncLifecycleEvent>();

        await SynchronizeAsync(
            checkoutPath,
            sourcePath,
            updateRepository: true,
            lifecycle.Add);

        Assert.NotEqual(oldCommit, newCommit);
        Assert.Equal(newCommit, await ReadGitAsync(checkoutPath, "rev-parse", "HEAD"));
        Assert.Equal("second", await File.ReadAllTextAsync(
            Path.Combine(checkoutPath, "content.txt"),
            TestContext.Current.CancellationToken));
        Assert.Equal(
            [
                ("fast-forward", "started", (string?)null),
                ("fast-forward", "completed", (string?)null),
                ("submodule-update", "started", (string?)null),
                ("submodule-update", "completed", (string?)null)
            ],
            lifecycle.Select(entry => (entry.Operation, entry.State, entry.Reason)).ToArray());
    }

    [Theory]
    [InlineData(true, true, "dirty")]
    [InlineData(false, false, "disabled")]
    public async Task Synchronization_preserves_a_checkout_when_updating_is_not_allowed(
        bool makeDirty,
        bool updateRepository,
        string expectedReason)
    {
        using var workspace = TemporaryDirectory.Create();
        var sourcePath = Path.Combine(workspace.Path, "inventory-source");
        await InitializeRepositoryAsync(sourcePath, "first");
        var checkoutPath = Path.Combine(workspace.Path, "inventory-checkout");
        await SynchronizeAsync(checkoutPath, sourcePath, updateRepository: true);
        var checkoutCommit = await ReadGitAsync(checkoutPath, "rev-parse", "HEAD");
        await CommitAsync(sourcePath, "upstream-change");
        if (makeDirty)
        {
            await File.WriteAllTextAsync(
                Path.Combine(checkoutPath, "content.txt"),
                "local-change",
                TestContext.Current.CancellationToken);
        }

        var lifecycle = new List<RepositorySyncLifecycleEvent>();
        await SynchronizeAsync(checkoutPath, sourcePath, updateRepository, lifecycle.Add);

        Assert.Equal(checkoutCommit, await ReadGitAsync(checkoutPath, "rev-parse", "HEAD"));
        var expectedLifecycle = makeDirty
            ? [("update", "skipped", expectedReason)]
            : new[]
            {
                ("update", "skipped", expectedReason),
                ("submodule-update", "started", (string?)null),
                ("submodule-update", "completed", (string?)null)
            };
        Assert.Equal(
            expectedLifecycle,
            lifecycle.Select(entry => (entry.Operation, entry.State, entry.Reason)).ToArray());
        Assert.Equal(
            makeDirty ? "local-change" : "first",
            await File.ReadAllTextAsync(
                Path.Combine(checkoutPath, "content.txt"),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Synchronization_reports_a_clean_checkout_without_an_upstream()
    {
        using var workspace = TemporaryDirectory.Create();
        var repositoryPath = Path.Combine(workspace.Path, "standalone");
        var commit = await InitializeRepositoryAsync(repositoryPath, "first");
        var lifecycle = new List<RepositorySyncLifecycleEvent>();

        await SynchronizeAsync(
            repositoryPath,
            repository: null,
            updateRepository: true,
            lifecycle.Add);

        Assert.Equal(commit, await ReadGitAsync(repositoryPath, "rev-parse", "HEAD"));
        Assert.Equal(
            [
                ("update", "skipped", "no-upstream"),
                ("submodule-update", "started", (string?)null),
                ("submodule-update", "completed", (string?)null)
            ],
            lifecycle.Select(entry => (entry.Operation, entry.State, entry.Reason)).ToArray());
    }

    [Fact]
    public async Task Repository_inspection_wrappers_report_roots_and_read_only_git_state()
    {
        using var workspace = TemporaryDirectory.Create();
        var repositoryPath = Path.Combine(workspace.Path, "inspected");
        var commit = await InitializeRepositoryAsync(repositoryPath, "first");
        var nestedPath = Path.Combine(repositoryPath, "src", "AppHost");
        Directory.CreateDirectory(nestedPath);
        var projectPath = Path.Combine(nestedPath, "AppHost.csproj");
        var branch = await ReadGitAsync(repositoryPath, "branch", "--show-current");

        Assert.Equal(repositoryPath, RepositoryIdentity.FindRepositoryRoot(projectPath));
        Assert.Equal(repositoryPath, RepositoryIdentity.TryFindRepositoryRoot(projectPath));
        Assert.Equal(repositoryPath, await RepositoryInspector.FindRepositoryRootAsync(
            projectPath,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(repositoryPath, await RepositoryInspector.TryFindRepositoryRootAsync(
            projectPath,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(branch, await RepositoryInspector.TryGetBranchAsync(
            repositoryPath,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(commit[..12], await RepositoryInspector.TryGetCommitAsync(
            repositoryPath,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(commit, await RepositoryInspector.TryResolveCommitAsync(
            repositoryPath,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Null(await RepositoryInspector.TryResolveCommitAsync(
            repositoryPath,
            "missing-revision",
            cancellationToken: TestContext.Current.CancellationToken));

        await RunGitAsync(repositoryPath, "checkout", "--detach", "HEAD");
        Assert.Null(await RepositoryInspector.TryGetBranchAsync(
            repositoryPath,
            cancellationToken: TestContext.Current.CancellationToken));

        var nonRepositoryPath = Path.Combine(workspace.Path, "not-a-repository");
        Directory.CreateDirectory(nonRepositoryPath);
        var missingFile = Path.Combine(nonRepositoryPath, "Missing.csproj");
        Assert.Equal(nonRepositoryPath, RepositoryIdentity.FindRepositoryRoot(missingFile));
        Assert.Null(RepositoryIdentity.TryFindRepositoryRoot(missingFile));
        Assert.Equal(nonRepositoryPath, await RepositoryInspector.FindRepositoryRootAsync(
            missingFile,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Null(await RepositoryInspector.TryFindRepositoryRootAsync(
            missingFile,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Null(await RepositoryInspector.TryGetCommitAsync(
            nonRepositoryPath,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await RepositoryInspector.IsDirtyAsync(
            nonRepositoryPath,
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Required_inspection_explains_an_unusable_git_executable()
    {
        using var workspace = TemporaryDirectory.Create();
        var repositoryPath = Path.Combine(workspace.Path, "checkout");
        Directory.CreateDirectory(Path.Combine(repositoryPath, ".git"));
        var missingGit = $"missing-git-{Guid.NewGuid():N}";

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RepositoryInspector.IsGitRepositoryAsync(
                repositoryPath,
                gitExecutablePath: missingGit,
                requireSuccessfulInspection: true,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains(repositoryPath, exception.Message, StringComparison.Ordinal);
        Assert.Contains(missingGit, exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(ModularAppHostsOptions.GitExecutablePath), exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(ModularAppHostsOptions.RepositoryCommandTimeout), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Single_command_wrapper_returns_the_first_command_or_null()
    {
        using var workspace = TemporaryDirectory.Create();
        var sourcePath = Path.Combine(workspace.Path, "wrapper-source");
        await InitializeRepositoryAsync(sourcePath, "first");
        var checkoutPath = Path.Combine(workspace.Path, "wrapper-checkout");

        var clone = await RepositorySynchronizer.CreateCommandAsync(
            checkoutPath,
            sourcePath,
            updateRepository: true,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(clone);
        Assert.Equal("clone", clone.Operation);
        Assert.Equal("git", clone.Executable);
        Assert.Equal(
            ["clone", "--recurse-submodules", "--", sourcePath, checkoutPath],
            clone.Arguments);

        await SynchronizeAsync(checkoutPath, sourcePath, updateRepository: true);
        var submoduleUpdate = await RepositorySynchronizer.CreateCommandAsync(
            checkoutPath,
            sourcePath,
            updateRepository: false,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(submoduleUpdate);
        Assert.Equal("submodule-update", submoduleUpdate.Operation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Synchronization_rejects_a_missing_or_mismatched_origin(bool addMismatchedOrigin)
    {
        using var workspace = TemporaryDirectory.Create();
        var expectedSource = Path.Combine(workspace.Path, "expected-source");
        await InitializeRepositoryAsync(expectedSource, "expected");
        var checkoutPath = Path.Combine(workspace.Path, "checkout");
        await InitializeRepositoryAsync(checkoutPath, "checkout");
        string? mismatchedSource = null;
        if (addMismatchedOrigin)
        {
            mismatchedSource = Path.Combine(workspace.Path, "other-source");
            await InitializeRepositoryAsync(mismatchedSource, "other");
            await RunGitAsync(checkoutPath, "remote", "add", "origin", mismatchedSource);
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RepositorySynchronizer.CreateCommandsAsync(
                checkoutPath,
                expectedSource,
                updateRepository: false,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains(expectedSource, exception.Message, StringComparison.Ordinal);
        Assert.Contains(
            mismatchedSource ?? "(missing)",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Repository_mismatch_errors_redact_remote_credentials_and_queries()
    {
        using var workspace = TemporaryDirectory.Create();
        var checkoutPath = Path.Combine(workspace.Path, "checkout");
        await InitializeRepositoryAsync(checkoutPath, "checkout");
        const string actualRepository =
            "https://actual-user:actual-password@example.test/acme/actual.git?auth=actual-query";
        const string expectedRepository =
            "https://expected-user:expected-password@example.test/acme/expected.git?auth=expected-query";
        await RunGitAsync(checkoutPath, "remote", "add", "origin", actualRepository);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RepositorySynchronizer.CreateCommandsAsync(
                checkoutPath,
                expectedRepository,
                updateRepository: false,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("[REDACTED]", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("actual-user", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("actual-password", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("actual-query", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("expected-user", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("expected-password", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("expected-query", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dirty_checkout_at_the_pinned_revision_is_preserved()
    {
        using var workspace = TemporaryDirectory.Create();
        var sourcePath = Path.Combine(workspace.Path, "pinned-source");
        var revision = await InitializeRepositoryAsync(sourcePath, "first");
        var checkoutPath = Path.Combine(workspace.Path, "pinned-checkout");
        await SynchronizeAsync(
            checkoutPath,
            sourcePath,
            updateRepository: true,
            revision: revision);
        await File.WriteAllTextAsync(
            Path.Combine(checkoutPath, "content.txt"),
            "local-change",
            TestContext.Current.CancellationToken);
        var lifecycle = new List<RepositorySyncLifecycleEvent>();

        var commands = await RepositorySynchronizer.CreateCommandsAsync(
            checkoutPath,
            sourcePath,
            updateRepository: true,
            revision: revision,
            lifecycle: lifecycle.Add,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(commands);
        Assert.Equal(revision, await ReadGitAsync(checkoutPath, "rev-parse", "HEAD"));
        Assert.Equal(
            [("update", "skipped", "dirty")],
            lifecycle.Select(entry => (entry.Operation, entry.State, entry.Reason)).ToArray());
    }

    [Fact]
    public async Task Dirty_checkout_at_a_different_revision_fails_before_checkout()
    {
        using var workspace = TemporaryDirectory.Create();
        var sourcePath = Path.Combine(workspace.Path, "changed-source");
        var pinnedRevision = await InitializeRepositoryAsync(sourcePath, "first");
        var currentRevision = await CommitAsync(sourcePath, "second");
        var checkoutPath = Path.Combine(workspace.Path, "changed-checkout");
        await SynchronizeAsync(checkoutPath, sourcePath, updateRepository: true);
        await File.WriteAllTextAsync(
            Path.Combine(checkoutPath, "content.txt"),
            "local-change",
            TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RepositorySynchronizer.CreateCommandsAsync(
                checkoutPath,
                sourcePath,
                updateRepository: true,
                revision: pinnedRevision,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(currentRevision, await ReadGitAsync(checkoutPath, "rev-parse", "HEAD"));
        Assert.Contains(pinnedRevision, exception.Message, StringComparison.Ordinal);
        Assert.Contains("local changes", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Failed_clone_reports_the_origin_error_without_a_completed_event()
    {
        using var workspace = TemporaryDirectory.Create();
        var missingSource = Path.Combine(workspace.Path, "missing-source");
        var checkoutPath = Path.Combine(workspace.Path, "failed-checkout");
        var lifecycle = new List<RepositorySyncLifecycleEvent>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RepositorySynchronizer.SynchronizeAsync(
                checkoutPath,
                missingSource,
                updateRepository: true,
                TestContext.Current.CancellationToken,
                commandTimeout: TimeSpan.FromMinutes(1),
                lifecycle: lifecycle.Add));

        Assert.Contains(checkoutPath, exception.Message, StringComparison.Ordinal);
        Assert.Contains("exit code", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            [("clone", "started")],
            lifecycle.Select(entry => (entry.Operation, entry.State)).ToArray());
    }

    [Fact]
    public async Task Preflight_logs_aggregate_failure_details_and_the_exact_initialize_command()
    {
        using var workspace = TemporaryDirectory.Create();
        var appHostPath = CreateAppHostRepository(workspace.Path);
        var plans = new ModuleRepositoryPlanRegistry(appHostPath);
        var requirement = plans.Register(
            "payments",
            "https://example.test/acme/payments.git",
            revision: null,
            updateOnInitialize: true).Requirement;
        var missingProject = Path.Combine(requirement.RepositoryPath, "src", "Payments.csproj");
        var logger = new CapturingLogger();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ModuleRepositoryPreflight.ValidateAsync(
                plans.Requirements,
                [new ModuleRequiredPath(
                    "payments",
                    "project 'payments-api'",
                    missingProject,
                    ModuleRequiredPathKind.File)],
                new InMemoryModuleRepositoryStateStore(),
                new ModuleRepositoryInitializationSettings("git", "gh", TimeSpan.FromMinutes(1)),
                appHostPath,
                logger,
                TestContext.Current.CancellationToken));

        var failure = Assert.Single(logger.Entries, entry => entry.EventId.Id == 10);
        Assert.Equal(2, failure.State["FailureCount"]);
        var initializeCommand = ModuleRepositoryPreflight.CreateInitializeCommand(appHostPath);
        Assert.Equal(initializeCommand, failure.State["InitializeCommand"]);
        Assert.Contains(initializeCommand, exception.Message, StringComparison.Ordinal);
        Assert.Contains(requirement.RepositoryPath, exception.Message, StringComparison.Ordinal);
        Assert.Contains(missingProject, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Initialization_routes_lifecycle_to_pipeline_logs_and_raw_output_only_to_the_resource()
    {
        using var workspace = TemporaryDirectory.Create();
        var plans = new ModuleRepositoryPlanRegistry(CreateAppHostRepository(workspace.Path));
        var requirement = plans.Register(
            "payments",
            "https://example.test/acme/payments.git",
            revision: null,
            updateOnInitialize: true).Requirement;
        var lifecycleLogger = new CapturingLogger();
        var resourceLogger = new CapturingLogger();

        await ModuleRepositoryInitializationPipeline.InitializeAsync(
            requirement,
            new ModuleRepositoryInitializationSettings("git", "gh", TimeSpan.FromMinutes(1)),
            lifecycleLogger,
            resourceLogger,
            (_, _, progress, lifecycle, _) =>
            {
                progress("clone output");
                lifecycle(new RepositorySyncLifecycleEvent("clone", "started"));
                lifecycle(new RepositorySyncLifecycleEvent(
                    "clone",
                    "completed",
                    ElapsedMilliseconds: 12));
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal([1, 4, 4, 3], lifecycleLogger.Entries.Select(entry => entry.EventId.Id));
        Assert.Equal([2], resourceLogger.Entries.Select(entry => entry.EventId.Id));
        Assert.DoesNotContain(lifecycleLogger.Entries, entry => entry.EventId.Id == 2);
        Assert.Contains(resourceLogger.Entries, entry =>
            entry.EventId.Id == 2 && Equals(entry.State["Output"], "clone output"));
        Assert.Contains(lifecycleLogger.Scopes, scope => scope.ContainsKey("OperationId"));
        Assert.Contains(resourceLogger.Scopes, scope => scope.ContainsKey("OperationId"));
    }

    private static string CreateAppHostRepository(string workspacePath)
    {
        var repositoryPath = Path.Combine(workspacePath, "consumer");
        var appHostPath = Path.Combine(repositoryPath, "src", "AppHost");
        Directory.CreateDirectory(Path.Combine(repositoryPath, ".git"));
        Directory.CreateDirectory(appHostPath);
        return appHostPath;
    }

    private static async Task<string> InitializeRepositoryAsync(string repositoryPath, string contents)
    {
        Directory.CreateDirectory(repositoryPath);
        await RunGitAsync(repositoryPath, "init");
        await RunGitAsync(repositoryPath, "config", "user.name", "Modular AppHosts Tests");
        await RunGitAsync(repositoryPath, "config", "user.email", "tests@example.test");
        return await CommitAsync(repositoryPath, contents);
    }

    private static async Task<string> CommitAsync(string repositoryPath, string contents)
    {
        await File.WriteAllTextAsync(
            Path.Combine(repositoryPath, "content.txt"),
            contents,
            TestContext.Current.CancellationToken);
        await RunGitAsync(repositoryPath, "add", "--", "content.txt");
        await RunGitAsync(repositoryPath, "commit", "-m", contents);
        return await ReadGitAsync(repositoryPath, "rev-parse", "HEAD");
    }

    private static async Task SynchronizeAsync(
        string repositoryPath,
        string? repository,
        bool updateRepository,
        Action<RepositorySyncLifecycleEvent>? lifecycle = null,
        string? revision = null) =>
        await RepositorySynchronizer.SynchronizeAsync(
            repositoryPath,
            repository,
            updateRepository,
            TestContext.Current.CancellationToken,
            revision: revision,
            commandTimeout: TimeSpan.FromMinutes(1),
            lifecycle: lifecycle);

    private static async Task RunGitAsync(string repositoryPath, params string[] arguments)
    {
        await CliCommand.Wrap("git")
            .WithArguments(arguments)
            .WithWorkingDirectory(repositoryPath)
            .ExecuteBufferedAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<string> ReadGitAsync(string repositoryPath, params string[] arguments)
    {
        var result = await CliCommand.Wrap("git")
            .WithArguments(arguments)
            .WithWorkingDirectory(repositoryPath)
            .ExecuteBufferedAsync(TestContext.Current.CancellationToken);
        return result.StandardOutput.Trim();
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<(EventId EventId, Dictionary<string, object?> State)> Entries { get; } = [];

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

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
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
