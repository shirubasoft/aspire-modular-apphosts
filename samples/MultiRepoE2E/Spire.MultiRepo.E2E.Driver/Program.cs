using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;

namespace Spire.MultiRepo.E2E.Driver;

internal static class Program
{
    private const string ModuleName = "multi-repo-resource-build";
    private const string ResourceName = "multi-repo-api";
    private const string PinnedMarker = "multi-repo-resource-pinned-revision";
    private const string LatestMarker = "multi-repo-resource-unpinned-latest";
    private const string InitializedMarker = "multi-repo-resource-initialized-update";
    private const string RefreshMarker = "multi-repo-resource-runtime-refresh";
    private const string DirtyMarker = "multi-repo-resource-dirty-rebuild";
    private const string DirtyUpstreamMarker = "multi-repo-resource-dirty-upstream";
    private const string PackageVersion = "0.0.0-multi-repo-e2e";
    private const string DummyUserName = "e2e-user";
    private const string DummyPassword = "e2e-password";
    private const string DummyQueryToken = "e2e-query-token";
    private const string DummyFragment = "e2e-fragment";

    public static async Task<int> Main(string[] args)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(GitProxy.LogEnvironmentVariable)))
        {
            return await GitProxy.RunAsync(args, CancellationToken.None).ConfigureAwait(false);
        }

        using var cancellationSource = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellationSource.Cancel();
        };

        E2EOptions options;
        try
        {
            options = E2EOptions.Parse(args);
        }
        catch (ArgumentException exception)
        {
            await Console.Error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 2;
        }

        var repositoryRoot = options.RepositoryRoot ?? FindRepositoryRoot(Directory.GetCurrentDirectory());
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"modular-apphosts-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryRoot);
        var succeeded = false;
        try
        {
            var scenario = new MultiRepositoryScenario(repositoryRoot, temporaryRoot, options);
            await scenario.RunAsync(cancellationSource.Token).ConfigureAwait(false);
            succeeded = true;
            await Console.Out.WriteLineAsync("Multi-repository initialization E2E passed.").ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (cancellationSource.IsCancellationRequested)
        {
            await Console.Error.WriteLineAsync("Multi-repository initialization E2E was cancelled.").ConfigureAwait(false);
            return 130;
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync(exception.ToString()).ConfigureAwait(false);
            return 1;
        }
        finally
        {
            if (succeeded && !options.KeepTemporary)
            {
                TryDeleteDirectory(temporaryRoot);
            }
            else
            {
                await Console.Error.WriteLineAsync($"E2E workspace: {temporaryRoot}").ConfigureAwait(false);
            }
        }
    }

    private static string FindRepositoryRoot(string startDirectory)
    {
        var current = new DirectoryInfo(Path.GetFullPath(startDirectory));
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Aspire.ModularAppHosts.slnx")) &&
                (Directory.Exists(Path.Combine(current.FullName, ".git")) ||
                 File.Exists(Path.Combine(current.FullName, ".git"))))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException(
            $"Unable to find the aspire-modular-apphosts repository above '{startDirectory}'. " +
            "Pass --repository-root explicitly.");
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class MultiRepositoryScenario(
        string repositoryRoot,
        string temporaryRoot,
        E2EOptions options)
    {
        private readonly ProcessExecutor _process = new();
        private readonly AspireCommand _aspire = AspireCommand.Create(options.AspirePath);
        private readonly string _consumerRepository = Path.Combine(temporaryRoot, "consumer");
        private readonly string _contractRepository = Path.Combine(temporaryRoot, "producer-contract-source");
        private readonly string _resourceRepository = Path.Combine(temporaryRoot, "resource-build-source");
        private readonly string _packageFeed = Path.Combine(temporaryRoot, "packages");
        private readonly string _nugetPackages = Path.Combine(temporaryRoot, "nuget-packages");
        private readonly string _gitProxyLog = Path.Combine(temporaryRoot, "git-proxy.jsonl");
        private readonly string _driverExecutable = GetDriverExecutable();
        private string _appHost = string.Empty;
        private string _pinnedRevision = string.Empty;
        private string _latestRevision = string.Empty;
        private string _remoteRepository = string.Empty;

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            await WritePhaseAsync("Create isolated package consumer and producer repositories").ConfigureAwait(false);
            await CreateFixtureAsync(cancellationToken).ConfigureAwait(false);

            await WritePhaseAsync("Fail fast before pinned initialization").ConfigureAwait(false);
            var pinnedEnvironment = CreateAppHostEnvironment(
                _resourceRepository,
                _pinnedRevision,
                GitProxyPolicy.Initialize);
            await AssertStartRequiresInitializationAsync(pinnedEnvironment, cancellationToken).ConfigureAwait(false);

            await WritePhaseAsync("Initialize a local pinned source without moving its developer checkout")
                .ConfigureAwait(false);
            var pinnedDirectoriesBefore = GetSiblingGitDirectories();
            var pinnedInitialization = await RunInitializeAsync(
                pinnedEnvironment,
                cancellationToken).ConfigureAwait(false);
            var pinnedCheckout = GetNewSiblingGitDirectory(pinnedDirectoriesBefore);
            AssertEqual(
                _pinnedRevision,
                await GetRevisionAsync(pinnedCheckout, cancellationToken).ConfigureAwait(false),
                "The initializer-owned pinned checkout did not resolve the requested revision.");
            AssertEqual(
                _latestRevision,
                await GetRevisionAsync(_resourceRepository, cancellationToken).ConfigureAwait(false),
                "Pinned initialization moved the developer checkout.");
            AssertEqual(
                string.Empty,
                await RunGitOutputAsync(pinnedCheckout, ["branch", "--show-current"], cancellationToken)
                    .ConfigureAwait(false),
                "The pinned checkout is not detached.");
            AssertEqual(
                Path.GetFullPath(_resourceRepository),
                Path.GetFullPath(await RunGitOutputAsync(
                    pinnedCheckout,
                    ["remote", "get-url", "origin"],
                    cancellationToken).ConfigureAwait(false)),
                "The pinned checkout has the wrong producer origin.");
            AssertInitializationLifecycle(pinnedInitialization.CombinedOutput);

            var siblingCount = GetSiblingGitDirectories().Count;
            var pinnedRevisionBeforeRepeat = await GetRevisionAsync(pinnedCheckout, cancellationToken)
                .ConfigureAwait(false);
            await RunInitializeAsync(pinnedEnvironment, cancellationToken).ConfigureAwait(false);
            AssertEqual(
                siblingCount,
                GetSiblingGitDirectories().Count,
                "Repeated initialization created another sibling checkout.");
            AssertEqual(
                pinnedRevisionBeforeRepeat,
                await GetRevisionAsync(pinnedCheckout, cancellationToken).ConfigureAwait(false),
                "Repeated initialization moved the pinned checkout.");

            await WritePhaseAsync("Run the pinned image with read-only Git inspection").ConfigureAwait(false);
            DeleteGitProxyLog();
            var pinnedRunEnvironment = CreateAppHostEnvironment(
                _resourceRepository,
                _pinnedRevision,
                GitProxyPolicy.ReadOnly);
            await AssertResourceMarkerAsync(pinnedRunEnvironment, PinnedMarker, cancellationToken)
                .ConfigureAwait(false);
            AssertNoRepositoryMutation(ReadGitProxyOperations());

            await WritePhaseAsync("Fail fast, initialize, and redact a credential-bearing remote")
                .ConfigureAwait(false);
            DeleteGitProxyLog();
            var remoteEnvironment = CreateAppHostEnvironment(
                _remoteRepository,
                revision: null,
                GitProxyPolicy.Initialize);
            await AssertStartRequiresInitializationAsync(remoteEnvironment, cancellationToken).ConfigureAwait(false);
            var remoteDirectoriesBefore = GetSiblingGitDirectories();
            var remoteInitialization = await RunInitializeAsync(remoteEnvironment, cancellationToken)
                .ConfigureAwait(false);
            var remoteCheckout = GetNewSiblingGitDirectory(remoteDirectoriesBefore);
            AssertRedacted(remoteInitialization.CombinedOutput);
            AssertReceiptsAreCredentialFree();
            AssertEqual(
                _latestRevision,
                await GetRevisionAsync(remoteCheckout, cancellationToken).ConfigureAwait(false),
                "The unpinned checkout did not start at the producer's latest revision.");
            AssertEqual(
                _remoteRepository,
                await RunGitOutputAsync(
                    remoteCheckout,
                    ["remote", "get-url", "origin"],
                    cancellationToken).ConfigureAwait(false),
                "The unpinned checkout has the wrong configured origin.");

            await WritePhaseAsync("Pick up a clean upstream change through initialize").ConfigureAwait(false);
            var initializedRevision = await CommitMarkerAsync(
                InitializedMarker,
                "Change marker for another initialization",
                cancellationToken).ConfigureAwait(false);
            await RunInitializeAsync(remoteEnvironment, cancellationToken).ConfigureAwait(false);
            AssertEqual(
                initializedRevision,
                await GetRevisionAsync(remoteCheckout, cancellationToken).ConfigureAwait(false),
                "Another initialization did not fast-forward the clean checkout.");

            await WritePhaseAsync("Build the initialized image and keep normal run read-only").ConfigureAwait(false);
            DeleteGitProxyLog();
            var readOnlyEnvironment = CreateAppHostEnvironment(
                _remoteRepository,
                revision: null,
                GitProxyPolicy.ReadOnly);
            await AssertResourceMarkerAsync(readOnlyEnvironment, InitializedMarker, cancellationToken)
                .ConfigureAwait(false);
            AssertNoRepositoryMutation(ReadGitProxyOperations());

            await WritePhaseAsync("Leave a clean checkout unchanged until runtime refresh is enabled")
                .ConfigureAwait(false);
            var refreshRevision = await CommitMarkerAsync(
                RefreshMarker,
                "Change marker for runtime refresh",
                cancellationToken).ConfigureAwait(false);
            DeleteGitProxyLog();
            await AssertResourceMarkerAsync(readOnlyEnvironment, InitializedMarker, cancellationToken)
                .ConfigureAwait(false);
            AssertEqual(
                initializedRevision,
                await GetRevisionAsync(remoteCheckout, cancellationToken).ConfigureAwait(false),
                "Default run moved a clean checkout without refresh being enabled.");
            AssertNoRepositoryMutation(ReadGitProxyOperations());

            await WritePhaseAsync("Fast-forward a clean checkout with opt-in runtime refresh")
                .ConfigureAwait(false);
            DeleteGitProxyLog();
            var refreshEnvironment = CreateAppHostEnvironment(
                _remoteRepository,
                revision: null,
                GitProxyPolicy.Refresh,
                refreshBuildRepositories: true);
            await AssertResourceMarkerAsync(refreshEnvironment, RefreshMarker, cancellationToken)
                .ConfigureAwait(false);
            AssertEqual(
                refreshRevision,
                await GetRevisionAsync(remoteCheckout, cancellationToken).ConfigureAwait(false),
                "Opt-in runtime refresh did not fast-forward the clean checkout.");
            AssertContainsNetworkUpdate(ReadGitProxyOperations());

            await WritePhaseAsync("Preserve a dirty checkout and rebuild it even with refresh enabled")
                .ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Combine(remoteCheckout, "marker.txt"),
                $"{DirtyMarker}{Environment.NewLine}",
                cancellationToken).ConfigureAwait(false);
            var dirtyBaseRevision = await GetRevisionAsync(remoteCheckout, cancellationToken).ConfigureAwait(false);
            var dirtyUpstreamRevision = await CommitMarkerAsync(
                DirtyUpstreamMarker,
                "Change marker behind a dirty checkout",
                cancellationToken).ConfigureAwait(false);
            DeleteGitProxyLog();
            await AssertResourceMarkerAsync(refreshEnvironment, DirtyMarker, cancellationToken)
                .ConfigureAwait(false);
            AssertEqual(
                dirtyBaseRevision,
                await GetRevisionAsync(remoteCheckout, cancellationToken).ConfigureAwait(false),
                "Runtime refresh moved a dirty checkout.");
            AssertEqual(
                dirtyUpstreamRevision,
                await GetRevisionAsync(_resourceRepository, cancellationToken).ConfigureAwait(false),
                "The producer repository did not retain its new upstream revision.");
            AssertNotEmpty(
                await RunGitOutputAsync(
                    remoteCheckout,
                    ["status", "--porcelain", "--untracked-files=normal"],
                    cancellationToken).ConfigureAwait(false),
                "The dirty checkout was unexpectedly cleaned.");
            AssertNoNetworkUpdate(ReadGitProxyOperations());
        }

        private async Task CreateFixtureAsync(CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(_packageFeed);
            await RunRequiredAsync(
                new ProcessInvocation(
                    "dotnet",
                    [
                        "pack",
                        Path.Combine(repositoryRoot, "src", "Aspire.Hosting.ModularAppHosts", "Aspire.Hosting.ModularAppHosts.csproj"),
                        "--configuration", "Release",
                        "--output", _packageFeed,
                        $"-p:PackageVersion={PackageVersion}"
                    ],
                    repositoryRoot),
                "Pack the modular AppHosts library",
                cancellationToken).ConfigureAwait(false);
            await RunRequiredAsync(
                new ProcessInvocation(
                    "dotnet",
                    [
                        "pack",
                        Path.Combine(repositoryRoot, "samples", "MultiRepoE2E", "Spire.ModuleContract", "Spire.ModuleContract.csproj"),
                        "--configuration", "Release",
                        "--output", _packageFeed,
                        $"-p:PackageVersion={PackageVersion}"
                    ],
                    repositoryRoot),
                "Pack the module contract",
                cancellationToken).ConfigureAwait(false);

            CopyRepository(repositoryRoot, _consumerRepository);
            Directory.Move(
                Path.Combine(_consumerRepository, "samples", "MultiRepoE2E", "Spire.ModuleContract"),
                _contractRepository);
            Directory.Move(
                Path.Combine(_consumerRepository, "samples", "MultiRepoE2E", "ResourceBuildRepository"),
                _resourceRepository);

            await InitializeRepositoryAsync(_consumerRepository, cancellationToken).ConfigureAwait(false);
            await InitializeRepositoryAsync(_contractRepository, cancellationToken).ConfigureAwait(false);
            await InitializeRepositoryAsync(_resourceRepository, cancellationToken).ConfigureAwait(false);
            _pinnedRevision = await GetRevisionAsync(_resourceRepository, cancellationToken).ConfigureAwait(false);
            _latestRevision = await CommitMarkerAsync(
                LatestMarker,
                "Change marker after pinned revision",
                cancellationToken).ConfigureAwait(false);
            _remoteRepository =
                $"https://{DummyUserName}:{DummyPassword}@example.invalid/modular/resource-build.git" +
                $"?access_token={DummyQueryToken}#{DummyFragment}";
            _appHost = Path.Combine(
                _consumerRepository,
                "samples",
                "MultiRepoE2E",
                "Spire.Consumer.AppHost",
                "Spire.Consumer.AppHost.csproj");

            await RunRequiredAsync(
                new ProcessInvocation(
                    "dotnet",
                    ["restore", _appHost],
                    _consumerRepository,
                    CreatePackageEnvironment()),
                "Restore the isolated package consumer",
                cancellationToken).ConfigureAwait(false);
        }

        private async Task InitializeRepositoryAsync(string path, CancellationToken cancellationToken)
        {
            await RunGitAsync(path, ["init", "--initial-branch", "main"], cancellationToken)
                .ConfigureAwait(false);
            await RunGitAsync(path, ["config", "user.name", "MultiRepo E2E"], cancellationToken)
                .ConfigureAwait(false);
            await RunGitAsync(path, ["config", "user.email", "multi-repo@example.invalid"], cancellationToken)
                .ConfigureAwait(false);
            await RunGitAsync(path, ["add", "."], cancellationToken).ConfigureAwait(false);
            await RunGitAsync(path, ["commit", "-m", "Create isolated E2E repository"], cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task<string> CommitMarkerAsync(
            string marker,
            string message,
            CancellationToken cancellationToken)
        {
            await File.WriteAllTextAsync(
                Path.Combine(_resourceRepository, "marker.txt"),
                $"{marker}{Environment.NewLine}",
                cancellationToken).ConfigureAwait(false);
            await RunGitAsync(_resourceRepository, ["add", "marker.txt"], cancellationToken)
                .ConfigureAwait(false);
            await RunGitAsync(_resourceRepository, ["commit", "-m", message], cancellationToken)
                .ConfigureAwait(false);
            return await GetRevisionAsync(_resourceRepository, cancellationToken).ConfigureAwait(false);
        }

        private async Task AssertStartRequiresInitializationAsync(
            IReadOnlyDictionary<string, string?> environment,
            CancellationToken cancellationToken)
        {
            var result = await RunAspireAsync(
                [
                    "start",
                    "--apphost", _appHost,
                    "--isolated",
                    "--format", "Json",
                    "--non-interactive"
                ],
                environment,
                cancellationToken).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                await StopAppHostAsync(cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException("Aspire start succeeded before repository initialization.");
            }

            AssertContains(
                result.CombinedOutput,
                "Run 'aspire do initialize --non-interactive'.",
                "The preflight failure did not contain the exact initialization command.");
        }

        private async Task<ProcessResult> RunInitializeAsync(
            IReadOnlyDictionary<string, string?> environment,
            CancellationToken cancellationToken)
        {
            var result = await RunAspireAsync(
                [
                    "do", "initialize",
                    "--apphost", _appHost,
                    "--pipeline-log-level", "trace",
                    "--non-interactive"
                ],
                environment,
                cancellationToken).ConfigureAwait(false);
            EnsureSuccess(result, "aspire do initialize");
            await Console.Out.WriteLineAsync(Redact(result.CombinedOutput).Trim()).ConfigureAwait(false);
            return result;
        }

        private async Task AssertResourceMarkerAsync(
            IReadOnlyDictionary<string, string?> environment,
            string expectedMarker,
            CancellationToken cancellationToken)
        {
            var started = false;
            try
            {
                var start = await RunAspireAsync(
                    [
                        "start",
                        "--apphost", _appHost,
                        "--isolated",
                        "--format", "Json",
                        "--non-interactive"
                    ],
                    environment,
                    cancellationToken).ConfigureAwait(false);
                EnsureSuccess(start, "aspire start");
                started = true;

                var wait = await RunAspireAsync(
                    [
                        "wait", ResourceName,
                        "--apphost", _appHost,
                        "--timeout", "180",
                        "--non-interactive"
                    ],
                    environment,
                    cancellationToken).ConfigureAwait(false);
                EnsureSuccess(wait, $"aspire wait {ResourceName}");

                var describe = await RunAspireAsync(
                    [
                        "describe", ResourceName,
                        "--apphost", _appHost,
                        "--format", "Json",
                        "--non-interactive"
                    ],
                    environment,
                    cancellationToken).ConfigureAwait(false);
                EnsureSuccess(describe, $"aspire describe {ResourceName}");
                var resourceUrl = FindHttpResourceUrl(describe.StandardOutput);
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                var health = await client.GetStringAsync(
                    new Uri(new Uri(resourceUrl.TrimEnd('/') + "/"), "health.txt"),
                    cancellationToken).ConfigureAwait(false);
                AssertEqual(
                    "healthy-from-separate-build-repository",
                    health.Trim(),
                    "The running image does not contain the producer-owned health marker.");
                var marker = await client.GetStringAsync(
                    new Uri(new Uri(resourceUrl.TrimEnd('/') + "/"), "marker.txt"),
                    cancellationToken).ConfigureAwait(false);
                AssertEqual(expectedMarker, marker.Trim(), "The running image contains the wrong marker.");
            }
            finally
            {
                if (started)
                {
                    await StopAppHostAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private async Task StopAppHostAsync(CancellationToken cancellationToken)
        {
            var result = await RunAspireAsync(
                ["stop", "--apphost", _appHost, "--non-interactive"],
                environment: null,
                cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                await Console.Error.WriteLineAsync(
                    $"Emergency Aspire stop failed:{Environment.NewLine}{Redact(result.CombinedOutput)}")
                    .ConfigureAwait(false);
            }
        }

        private Task<ProcessResult> RunAspireAsync(
            IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string?>? environment,
            CancellationToken cancellationToken)
        {
            return _process.RunAsync(
                new ProcessInvocation(
                    _aspire.FileName,
                    [.. _aspire.PrefixArguments, .. arguments],
                    _consumerRepository,
                    environment),
                cancellationToken);
        }

        private async Task RunRequiredAsync(
            ProcessInvocation invocation,
            string operation,
            CancellationToken cancellationToken)
        {
            var result = await _process.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(result, operation);
        }

        private async Task RunGitAsync(
            string workingDirectory,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            var result = await _process.RunAsync(
                new ProcessInvocation("git", arguments, workingDirectory),
                cancellationToken).ConfigureAwait(false);
            EnsureSuccess(result, $"git {string.Join(' ', arguments)}");
        }

        private async Task<string> RunGitOutputAsync(
            string workingDirectory,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            var result = await _process.RunAsync(
                new ProcessInvocation("git", arguments, workingDirectory),
                cancellationToken).ConfigureAwait(false);
            EnsureSuccess(result, $"git {string.Join(' ', arguments)}");
            return result.StandardOutput.Trim();
        }

        private Task<string> GetRevisionAsync(string repository, CancellationToken cancellationToken) =>
            RunGitOutputAsync(repository, ["rev-parse", "HEAD"], cancellationToken);

        private Dictionary<string, string?> CreatePackageEnvironment() => new(StringComparer.Ordinal)
        {
            ["SpireContractPackageVersion"] = PackageVersion,
            ["RestoreAdditionalProjectSources"] = _packageFeed,
            ["NUGET_PACKAGES"] = _nugetPackages
        };

        private Dictionary<string, string?> CreateAppHostEnvironment(
            string buildRepository,
            string? revision,
            GitProxyPolicy proxyPolicy,
            bool refreshBuildRepositories = false)
        {
            var environment = CreatePackageEnvironment();
            var section = "Aspire__ModularAppHosts";
            var module = $"{section}__Modules__{ModuleName}";
            environment[$"{module}__DefinitionRepository"] = _contractRepository;
            environment[$"{module}__BuildRepository"] = buildRepository;
            environment[$"{module}__BuildRepositoryRevision"] = revision;
            environment[$"{section}__GitExecutablePath"] = _driverExecutable;
            environment[$"{section}__GitHubCliPath"] = _driverExecutable;
            environment[$"{section}__RefreshBuildRepositoriesOnRun"] = refreshBuildRepositories.ToString();
            environment[GitProxy.LogEnvironmentVariable] = _gitProxyLog;
            environment[GitProxy.PolicyEnvironmentVariable] = proxyPolicy.ToString();
            environment[GitProxy.RealGitEnvironmentVariable] = "git";
            environment[GitProxy.RemoteRepositoryEnvironmentVariable] = _remoteRepository;
            environment[GitProxy.SourceRepositoryEnvironmentVariable] = _resourceRepository;
            if (!string.IsNullOrWhiteSpace(options.ContainerRuntime))
            {
                environment["ASPIRE_CONTAINER_RUNTIME"] = options.ContainerRuntime;
            }

            return environment;
        }

        private HashSet<string> GetSiblingGitDirectories()
        {
            return Directory.EnumerateDirectories(temporaryRoot)
                .Where(path => Directory.Exists(Path.Combine(path, ".git")) ||
                    File.Exists(Path.Combine(path, ".git")))
                .Select(Path.GetFullPath)
                .ToHashSet(PathComparer);
        }

        private string GetNewSiblingGitDirectory(IReadOnlySet<string> before)
        {
            var added = GetSiblingGitDirectories()
                .Where(path => !before.Contains(path))
                .ToArray();
            if (added.Length != 1)
            {
                throw new InvalidOperationException(
                    $"Expected exactly one initializer-owned direct sibling, but found {added.Length}: " +
                    string.Join(", ", added));
            }

            return added[0];
        }

        private void AssertReceiptsAreCredentialFree()
        {
            var receiptDirectory = Path.Combine(
                _consumerRepository,
                ".aspire",
                "modular-apphosts",
                "repositories");
            var receipts = Directory.Exists(receiptDirectory)
                ? Directory.EnumerateFiles(receiptDirectory, "*.json").ToArray()
                : [];
            if (receipts.Length == 0)
            {
                throw new InvalidOperationException("Initialization did not write a repository receipt.");
            }

            foreach (var receipt in receipts)
            {
                AssertRedacted(File.ReadAllText(receipt));
            }
        }

        private IReadOnlyList<GitProxyOperation> ReadGitProxyOperations()
        {
            if (!File.Exists(_gitProxyLog))
            {
                return [];
            }

            return File.ReadLines(_gitProxyLog)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => JsonSerializer.Deserialize<GitProxyOperation>(line)
                    ?? throw new InvalidDataException("The Git proxy wrote an empty operation."))
                .ToArray();
        }

        private void DeleteGitProxyLog()
        {
            File.Delete(_gitProxyLog);
        }

        private static void AssertInitializationLifecycle(string output)
        {
            AssertContains(output, "Initializing repository", "Initialization did not log its start.");
            AssertContains(output, "Initialized repository", "Initialization did not log its completion.");
        }

        private static void AssertNoRepositoryMutation(IEnumerable<GitProxyOperation> operations)
        {
            var mutation = operations.FirstOrDefault(operation => GitProxy.IsNetworkOrMutation(operation.Operation));
            if (mutation is not null)
            {
                throw new InvalidOperationException(
                    $"Default run invoked prohibited Git operation '{mutation.Operation}'.");
            }
        }

        private static void AssertContainsNetworkUpdate(IEnumerable<GitProxyOperation> operations)
        {
            if (!operations.Any(operation => operation.Operation is "fetch" or "pull"))
            {
                throw new InvalidOperationException(
                    "Opt-in runtime refresh did not invoke a Git fetch or pull operation.");
            }
        }

        private static void AssertNoNetworkUpdate(IEnumerable<GitProxyOperation> operations)
        {
            if (operations.Any(operation => operation.Operation is "fetch" or "pull"))
            {
                throw new InvalidOperationException("Runtime refresh fetched or pulled a dirty checkout.");
            }
        }

        private static void AssertRedacted(string value)
        {
            foreach (var sensitiveValue in new[] { DummyUserName, DummyPassword, DummyQueryToken, DummyFragment })
            {
                if (value.Contains(sensitiveValue, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Initialization output or receipt exposed E2E credential marker '{sensitiveValue}'.");
                }
            }
        }

        private static string FindHttpResourceUrl(string json)
        {
            using var document = JsonDocument.Parse(json);
            if (TryFindHttpUrl(document.RootElement, out var url))
            {
                return url;
            }

            throw new InvalidOperationException("Aspire describe did not return an HTTP URL for the resource.");
        }

        private static bool TryFindHttpUrl(JsonElement element, out string url)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var isHttpEndpoint = element.TryGetProperty("name", out var name) &&
                    string.Equals(name.GetString(), "http", StringComparison.OrdinalIgnoreCase);
                if (isHttpEndpoint && element.TryGetProperty("url", out var urlElement) &&
                    urlElement.GetString() is { Length: > 0 } value)
                {
                    url = value;
                    return true;
                }

                foreach (var property in element.EnumerateObject())
                {
                    if (TryFindHttpUrl(property.Value, out url))
                    {
                        return true;
                    }
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    if (TryFindHttpUrl(item, out url))
                    {
                        return true;
                    }
                }
            }

            url = string.Empty;
            return false;
        }

        private static void EnsureSuccess(ProcessResult result, string operation)
        {
            if (!result.IsSuccess)
            {
                throw new InvalidOperationException(
                    $"{operation} failed with exit code {result.ExitCode}:{Environment.NewLine}" +
                    Redact(result.CombinedOutput));
            }
        }

        private static async Task WritePhaseAsync(string phase)
        {
            await Console.Out.WriteLineAsync($"[multi-repo-e2e] {phase}").ConfigureAwait(false);
        }

        private static string GetDriverExecutable()
        {
            var executable = Path.Combine(
                AppContext.BaseDirectory,
                OperatingSystem.IsWindows()
                    ? "Spire.MultiRepo.E2E.Driver.exe"
                    : "Spire.MultiRepo.E2E.Driver");
            if (!File.Exists(executable))
            {
                throw new InvalidOperationException($"The Git proxy executable '{executable}' does not exist.");
            }

            return executable;
        }

        private static void CopyRepository(string source, string destination)
        {
            var excludedDirectoryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".git",
                ".aspire",
                ".vs",
                "artifacts",
                "bin",
                "obj"
            };
            CopyDirectory(source, destination, excludedDirectoryNames);
        }

        private static void CopyDirectory(
            string source,
            string destination,
            IReadOnlySet<string> excludedDirectoryNames)
        {
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.EnumerateFiles(source))
            {
                var destinationFile = Path.Combine(destination, Path.GetFileName(file));
                File.Copy(file, destinationFile);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(destinationFile, File.GetUnixFileMode(file));
                }
            }

            foreach (var directory in Directory.EnumerateDirectories(source))
            {
                if (excludedDirectoryNames.Contains(Path.GetFileName(directory)))
                {
                    continue;
                }

                CopyDirectory(
                    directory,
                    Path.Combine(destination, Path.GetFileName(directory)),
                    excludedDirectoryNames);
            }
        }
    }

    private sealed record E2EOptions(
        string? RepositoryRoot,
        string? AspirePath,
        string? ContainerRuntime,
        bool KeepTemporary)
    {
        public static E2EOptions Parse(IReadOnlyList<string> args)
        {
            string? repositoryRoot = null;
            string? aspirePath = null;
            string? containerRuntime = null;
            var keepTemporary = false;
            for (var index = 0; index < args.Count; index++)
            {
                switch (args[index])
                {
                    case "--repository-root":
                        repositoryRoot = ReadValue(args, ref index, "--repository-root");
                        break;
                    case "--aspire-path":
                        aspirePath = ReadValue(args, ref index, "--aspire-path");
                        break;
                    case "--container-runtime":
                        containerRuntime = ReadValue(args, ref index, "--container-runtime");
                        if (containerRuntime is not ("docker" or "podman"))
                        {
                            throw new ArgumentException("--container-runtime must be 'docker' or 'podman'.");
                        }
                        break;
                    case "--keep-temporary":
                        keepTemporary = true;
                        break;
                    default:
                        throw new ArgumentException($"Unknown option '{args[index]}'.");
                }
            }

            return new E2EOptions(
                repositoryRoot is null ? null : Path.GetFullPath(repositoryRoot),
                aspirePath,
                containerRuntime,
                keepTemporary);
        }

        private static string ReadValue(IReadOnlyList<string> args, ref int index, string option)
        {
            index++;
            if (index >= args.Count || string.IsNullOrWhiteSpace(args[index]))
            {
                throw new ArgumentException($"{option} requires a value.");
            }

            return args[index];
        }
    }

    private sealed record AspireCommand(string FileName, IReadOnlyList<string> PrefixArguments)
    {
        public static AspireCommand Create(string? aspirePath)
        {
            return string.IsNullOrWhiteSpace(aspirePath)
                ? new AspireCommand("dotnet", ["tool", "run", "aspire", "--"])
                : new AspireCommand(aspirePath, []);
        }
    }

    private sealed record ProcessInvocation(
        string FileName,
        IReadOnlyList<string> Arguments,
        string WorkingDirectory,
        IReadOnlyDictionary<string, string?>? Environment = null);

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
    {
        public bool IsSuccess => ExitCode == 0;

        public string CombinedOutput => $"{StandardOutput}{Environment.NewLine}{StandardError}";
    }

    private sealed class ProcessExecutor
    {
        private static readonly TimeSpan ProcessTimeout = TimeSpan.FromMinutes(10);

        public async Task<ProcessResult> RunAsync(
            ProcessInvocation invocation,
            CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo(invocation.FileName)
            {
                WorkingDirectory = invocation.WorkingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in invocation.Arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
            if (invocation.Environment is not null)
            {
                foreach (var pair in invocation.Environment)
                {
                    startInfo.Environment[pair.Key] = pair.Value;
                }
            }

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                throw new InvalidOperationException($"Unable to start '{invocation.FileName}'.");
            }

            using var timeoutSource = new CancellationTokenSource(ProcessTimeout);
            using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutSource.Token);
            var standardOutput = process.StandardOutput.ReadToEndAsync(linkedSource.Token);
            var standardError = process.StandardError.ReadToEndAsync(linkedSource.Token);
            try
            {
                await process.WaitForExitAsync(linkedSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested &&
                !cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                throw new TimeoutException(
                    $"Process '{invocation.FileName}' exceeded the {ProcessTimeout} E2E timeout.");
            }

            return new ProcessResult(
                process.ExitCode,
                await standardOutput.ConfigureAwait(false),
                await standardError.ConfigureAwait(false));
        }

        private static void TryKill(Process process)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    private enum GitProxyPolicy
    {
        Initialize,
        ReadOnly,
        Refresh
    }

    private sealed record GitProxyOperation(string Operation, string[] Arguments);

    private static class GitProxy
    {
        public const string LogEnvironmentVariable = "MODULAR_E2E_GIT_PROXY_LOG";
        public const string PolicyEnvironmentVariable = "MODULAR_E2E_GIT_PROXY_POLICY";
        public const string RealGitEnvironmentVariable = "MODULAR_E2E_REAL_GIT";
        public const string RemoteRepositoryEnvironmentVariable = "MODULAR_E2E_REMOTE_REPOSITORY";
        public const string SourceRepositoryEnvironmentVariable = "MODULAR_E2E_SOURCE_REPOSITORY";

        private static readonly HashSet<string> NetworkOrMutationOperations = new(StringComparer.Ordinal)
        {
            "clone",
            "fetch",
            "pull",
            "checkout",
            "switch",
            "merge",
            "rebase",
            "reset"
        };

        public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
        {
            var operation = FindOperation(args);
            await AppendOperationAsync(operation, args, cancellationToken).ConfigureAwait(false);
            var policy = Enum.TryParse<GitProxyPolicy>(
                Environment.GetEnvironmentVariable(PolicyEnvironmentVariable),
                ignoreCase: true,
                out var configuredPolicy)
                ? configuredPolicy
                : GitProxyPolicy.ReadOnly;
            if (policy == GitProxyPolicy.ReadOnly && IsNetworkOrMutation(operation))
            {
                await Console.Error.WriteLineAsync(
                    $"Git proxy denied '{operation}' during a default run.").ConfigureAwait(false);
                return 97;
            }

            var realGit = Environment.GetEnvironmentVariable(RealGitEnvironmentVariable) ?? "git";
            var remoteRepository = Environment.GetEnvironmentVariable(RemoteRepositoryEnvironmentVariable);
            var sourceRepository = Environment.GetEnvironmentVariable(SourceRepositoryEnvironmentVariable);
            if (operation == "clone" &&
                !string.IsNullOrWhiteSpace(remoteRepository) &&
                !string.IsNullOrWhiteSpace(sourceRepository) &&
                args.Contains(remoteRepository, StringComparer.Ordinal))
            {
                await Console.Out.WriteLineAsync($"Cloning {remoteRepository}").ConfigureAwait(false);
                var rewritten = args
                    .Select(argument => string.Equals(argument, remoteRepository, StringComparison.Ordinal)
                        ? sourceRepository
                        : argument)
                    .ToArray();
                var exitCode = await ForwardAsync(realGit, rewritten, cancellationToken).ConfigureAwait(false);
                if (exitCode != 0)
                {
                    return exitCode;
                }

                var destination = args[^1];
                return await RunSilentAsync(
                    realGit,
                    ["-C", destination, "remote", "set-url", "origin", remoteRepository],
                    cancellationToken).ConfigureAwait(false);
            }

            if (operation is "fetch" or "pull" &&
                !string.IsNullOrWhiteSpace(remoteRepository) &&
                !string.IsNullOrWhiteSpace(sourceRepository) &&
                FindWorkingDirectory(args) is { } repositoryPath)
            {
                var configuredOrigin = await CaptureAsync(
                    realGit,
                    ["-C", repositoryPath, "config", "--get", "remote.origin.url"],
                    cancellationToken).ConfigureAwait(false);
                if (string.Equals(configuredOrigin.Output.Trim(), remoteRepository, StringComparison.Ordinal))
                {
                    var setLocalExitCode = await RunSilentAsync(
                        realGit,
                        ["-C", repositoryPath, "remote", "set-url", "origin", sourceRepository],
                        cancellationToken).ConfigureAwait(false);
                    if (setLocalExitCode != 0)
                    {
                        return setLocalExitCode;
                    }

                    try
                    {
                        return await ForwardAsync(realGit, args, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        await RunSilentAsync(
                            realGit,
                            ["-C", repositoryPath, "remote", "set-url", "origin", remoteRepository],
                            CancellationToken.None).ConfigureAwait(false);
                    }
                }
            }

            return await ForwardAsync(realGit, args, cancellationToken).ConfigureAwait(false);
        }

        public static bool IsNetworkOrMutation(string operation) =>
            NetworkOrMutationOperations.Contains(operation) ||
            string.Equals(operation, "submodule-update", StringComparison.Ordinal);

        private static string FindOperation(IReadOnlyList<string> args)
        {
            for (var index = 0; index < args.Count; index++)
            {
                var argument = args[index];
                if (string.Equals(argument, "submodule", StringComparison.Ordinal) &&
                    index + 1 < args.Count && string.Equals(args[index + 1], "update", StringComparison.Ordinal))
                {
                    return "submodule-update";
                }
                if (NetworkOrMutationOperations.Contains(argument) || argument is
                    "rev-parse" or "status" or "branch" or "config" or "diff" or "log" or "show" or
                    "describe" or "symbolic-ref")
                {
                    return argument;
                }
            }

            return args.FirstOrDefault(argument => !argument.StartsWith("-", StringComparison.Ordinal)) ?? "unknown";
        }

        private static string? FindWorkingDirectory(IReadOnlyList<string> args)
        {
            for (var index = 0; index + 1 < args.Count; index++)
            {
                if (string.Equals(args[index], "-C", StringComparison.Ordinal))
                {
                    return args[index + 1];
                }
            }

            return null;
        }

        private static async Task AppendOperationAsync(
            string operation,
            string[] args,
            CancellationToken cancellationToken)
        {
            var logPath = Environment.GetEnvironmentVariable(LogEnvironmentVariable)
                ?? throw new InvalidOperationException($"{LogEnvironmentVariable} is not configured.");
            var line = JsonSerializer.Serialize(new GitProxyOperation(operation, args)) + Environment.NewLine;
            await File.AppendAllTextAsync(logPath, line, cancellationToken).ConfigureAwait(false);
        }

        private static async Task<int> ForwardAsync(
            string executable,
            IReadOnlyList<string> args,
            CancellationToken cancellationToken)
        {
            var startInfo = CreateStartInfo(executable, args, redirectOutput: false);
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Unable to start '{executable}'.");
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode;
        }

        private static async Task<int> RunSilentAsync(
            string executable,
            IReadOnlyList<string> args,
            CancellationToken cancellationToken)
        {
            var result = await CaptureAsync(executable, args, cancellationToken).ConfigureAwait(false);
            return result.ExitCode;
        }

        private static async Task<(int ExitCode, string Output)> CaptureAsync(
            string executable,
            IReadOnlyList<string> args,
            CancellationToken cancellationToken)
        {
            var startInfo = CreateStartInfo(executable, args, redirectOutput: true);
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Unable to start '{executable}'.");
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            _ = await error.ConfigureAwait(false);
            return (process.ExitCode, await output.ConfigureAwait(false));
        }

        private static ProcessStartInfo CreateStartInfo(
            string executable,
            IReadOnlyList<string> args,
            bool redirectOutput)
        {
            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = redirectOutput,
                RedirectStandardError = redirectOutput
            };
            foreach (var argument in args)
            {
                startInfo.ArgumentList.Add(argument);
            }

            startInfo.Environment.Remove(LogEnvironmentVariable);
            startInfo.Environment.Remove(PolicyEnvironmentVariable);
            startInfo.Environment.Remove(RemoteRepositoryEnvironmentVariable);
            startInfo.Environment.Remove(SourceRepositoryEnvironmentVariable);
            return startInfo;
        }
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static string Redact(string value)
    {
        return value
            .Replace(DummyUserName, "[REDACTED]", StringComparison.Ordinal)
            .Replace(DummyPassword, "[REDACTED]", StringComparison.Ordinal)
            .Replace(DummyQueryToken, "[REDACTED]", StringComparison.Ordinal)
            .Replace(DummyFragment, "[REDACTED]", StringComparison.Ordinal);
    }

    private static void AssertContains(string value, string expected, string message)
    {
        if (!value.Contains(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{message}{Environment.NewLine}{Redact(value)}");
        }
    }

    private static void AssertNotEmpty(string value, string message)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                $"{message} Expected '{expected}', actual '{actual}'.");
        }
    }
}
