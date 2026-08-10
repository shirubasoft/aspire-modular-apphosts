using Aspire.Hosting.ModularAppHosts;
using Shirubasoft.Aspire.ModularAppHosts.Tool;
using Xunit;

namespace Aspire.Hosting.ModularAppHosts.Tool.Tests;

public sealed class ToolApplicationTests
{
    [Fact]
    public async Task Apply_writes_full_environment_with_global_then_resource_tag_precedence()
    {
        using var directory = TestDirectory.Create();
        var githubEnvironment = Path.Combine(directory.Path, "github-env");
        var document = CreateManifest();
        document.Images[0].Tag = null;
        document.Images[0].Digest = $"sha256:{new string('a', 64)}";
        var (exitCode, _, error, runner) = await RunAsync(
            directory,
            [
                "manifest", "apply",
                "--json", document.ToJson(),
                "--tag", "global",
                "--resource-tag", "orders/api=specific",
                "--github-env", githubEnvironment
            ]);

        Assert.Equal(ToolExitCode.Success, exitCode);
        Assert.Empty(error);
        Assert.Empty(runner.Invocations);
        var values = await ReadGitHubFileAsync(githubEnvironment);
        Assert.Equal("catalog", values["Aspire__ModularAppHosts__WorkflowImageOverrides__0__Module"]);
        Assert.Equal("global", values["Aspire__ModularAppHosts__WorkflowImageOverrides__0__Tag"]);
        Assert.Equal("orders", values["Aspire__ModularAppHosts__WorkflowImageOverrides__1__Module"]);
        Assert.Equal("specific", values["Aspire__ModularAppHosts__WorkflowImageOverrides__1__Tag"]);
        Assert.DoesNotContain(
            "Aspire__ModularAppHosts__WorkflowImageOverrides__1__Digest",
            values.Keys);
    }

    [Theory]
    [InlineData()]
    [InlineData("--file", "manifest.json", "--json", "{}")]
    public async Task Apply_requires_exactly_one_manifest_source(params string[] sourceArguments)
    {
        using var directory = TestDirectory.Create();
        var args = new List<string> { "manifest", "apply" };
        args.AddRange(sourceArguments);
        args.Add("--github-env");
        args.Add(Path.Combine(directory.Path, "github-env"));

        var (exitCode, _, error, _) = await RunAsync(directory, [.. args]);

        Assert.Equal(ToolExitCode.Usage, exitCode);
        Assert.Contains("exactly one", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Publish_requires_explicit_selection()
    {
        using var directory = TestDirectory.Create();

        var (exitCode, _, error, runner) = await RunAsync(
            directory,
            ["manifest", "publish", "--apphost", directory.Path]);

        Assert.Equal(ToolExitCode.Usage, exitCode);
        Assert.Contains("--selector", error, StringComparison.Ordinal);
        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public async Task Publish_discovers_selects_pushes_and_emits_exact_manifest_and_github_outputs()
    {
        using var directory = TestDirectory.Create();
        var destination = Path.Combine(directory.Path, "artifacts", "images.json");
        var githubOutput = Path.Combine(directory.Path, "github-output");
        var runner = new FakeProcessRunner(async (invocation, cancellationToken) =>
        {
            var step = invocation.Arguments[1];
            var outputPath = GetOption(invocation.Arguments, "--output-path");
            if (string.Equals(step, "describe-images", StringComparison.Ordinal))
            {
                await CreateDescriptions().SaveAsync(
                    Path.Combine(outputPath!, "module-images.json"),
                    cancellationToken);
            }
            else if (string.Equals(step, "workflow-images", StringComparison.Ordinal))
            {
                var manifest = CreateManifest();
                manifest.Images.Remove(manifest.Images.Single(image => image.Module == "catalog"));
                manifest.Images.Single(image => image.Resource == "worker").Tag = "global";
                manifest.Images.Single(image => image.Resource == "api").Tag = "api-tag";
                await manifest.SaveAsync(
                    Path.Combine(outputPath!, "module-image-manifest.json"),
                    cancellationToken);
            }

            return new ProcessExecutionResult(0, string.Empty, string.Empty);
        });
        var environment = new FakeEnvironment(directory.Path, new Dictionary<string, string>
        {
            ["GITHUB_OUTPUT"] = githubOutput
        });

        var (exitCode, output, error, _) = await RunAsync(
            directory,
            [
                "manifest", "publish",
                "--apphost", directory.Path,
                "--selector", "orders",
                "--tag", "global",
                "--resource-tag", "orders/api=api-tag",
                "--output", destination,
                "--github-output", "manifest",
                "--aspire-path", "custom-aspire"
            ],
            runner,
            environment);

        Assert.Equal(ToolExitCode.Success, exitCode);
        Assert.Empty(error);
        Assert.Contains(destination, output, StringComparison.Ordinal);
        Assert.Collection(
            runner.Invocations,
            invocation =>
            {
                Assert.Equal("custom-aspire", invocation.FileName);
                Assert.Equal("describe-images", invocation.Arguments[1]);
                Assert.DoesNotContain("--", invocation.Arguments);
                Assert.Null(invocation.EnvironmentVariables);
                Assert.False(invocation.CaptureOutput);
            },
            invocation => AssertProducerInvocation(invocation, "push"),
            invocation => AssertProducerInvocation(invocation, "workflow-images"));

        var written = await ModuleImageManifestDocument.LoadAsync(
            destination,
            TestContext.Current.CancellationToken);
        Assert.Equal("global", written.Images.Single(image => image.Resource == "worker").Tag);
        Assert.Equal("api-tag", written.Images.Single(image => image.Resource == "api").Tag);
        var outputs = await ReadGitHubFileAsync(githubOutput);
        Assert.Equal(destination, outputs["manifest-path"]);
        var outputManifest = ModuleImageManifestDocument.Parse(outputs["manifest"]);
        Assert.Equal("api-tag", outputManifest.Images.Single(image => image.Resource == "api").Tag);
    }

    [Fact]
    public async Task Publish_stops_on_aspire_failure_and_maps_interruption()
    {
        using var directory = TestDirectory.Create();
        var failedRunner = new FakeProcessRunner((_, _) =>
            Task.FromResult(new ProcessExecutionResult(17, string.Empty, "failed")));
        var (failedExit, _, failedError, _) = await RunAsync(
            directory,
            ["manifest", "publish", "--apphost", directory.Path, "--all"],
            failedRunner);

        Assert.Equal(ToolExitCode.Usage, failedExit);
        Assert.Contains("17", failedError, StringComparison.Ordinal);
        Assert.Single(failedRunner.Invocations);

        var cancelledRunner = new FakeProcessRunner((_, cancellationToken) =>
            Task.FromCanceled<ProcessExecutionResult>(
                cancellationToken.IsCancellationRequested
                    ? cancellationToken
                    : new CancellationToken(canceled: true)));
        var (cancelledExit, _, _, _) = await RunAsync(
            directory,
            ["manifest", "publish", "--apphost", directory.Path, "--all"],
            cancelledRunner);
        Assert.Equal(ToolExitCode.Interrupted, cancelledExit);
    }

    [Fact]
    public async Task Publish_accepts_declared_module_resource_identity_selectors()
    {
        using var directory = TestDirectory.Create();
        var runner = new FakeProcessRunner(async (invocation, cancellationToken) =>
        {
            var step = invocation.Arguments[1];
            var outputPath = GetOption(invocation.Arguments, "--output-path");
            if (step == "describe-images")
            {
                await CreateDescriptions().SaveAsync(
                    Path.Combine(outputPath!, "module-images.json"),
                    cancellationToken);
            }
            else if (step == "workflow-images")
            {
                var manifest = new ModuleImageManifestDocument();
                manifest.Images.Add(CreateManifestImage("orders", "api", ModuleResourceKind.Project));
                await manifest.SaveAsync(
                    Path.Combine(outputPath!, "module-image-manifest.json"),
                    cancellationToken);
            }

            return new ProcessExecutionResult(0, string.Empty, string.Empty);
        });

        var (exitCode, _, error, _) = await RunAsync(
            directory,
            ["manifest", "publish", "--apphost", directory.Path, "--selector", "orders/api"],
            runner);

        Assert.Equal(ToolExitCode.Success, exitCode);
        Assert.Empty(error);
        Assert.Equal(
            ["imported-api"],
            runner.Invocations[1].Arguments.SkipWhile(value => value != "--").Skip(1));
    }

    [Fact]
    public async Task Publish_rejects_ambiguous_bare_resource_selectors()
    {
        using var directory = TestDirectory.Create();
        var runner = new FakeProcessRunner(async (invocation, cancellationToken) =>
        {
            await CreateDescriptions().SaveAsync(
                Path.Combine(GetOption(invocation.Arguments, "--output-path")!, "module-images.json"),
                cancellationToken);
            return new ProcessExecutionResult(0, string.Empty, string.Empty);
        });

        var (exitCode, _, error, _) = await RunAsync(
            directory,
            ["manifest", "publish", "--apphost", directory.Path, "--selector", "api"],
            runner);

        Assert.Equal(ToolExitCode.Usage, exitCode);
        Assert.Contains("ambiguous", error, StringComparison.OrdinalIgnoreCase);
        Assert.Single(runner.Invocations);
    }

    [Fact]
    public async Task Parse_errors_use_the_usage_exit_code()
    {
        using var directory = TestDirectory.Create();

        var (exitCode, _, error, _) = await RunAsync(
            directory,
            ["manifest", "publish", "--all"]);

        Assert.Equal(ToolExitCode.Usage, exitCode);
        Assert.Contains("--apphost", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CliWrap_runner_streams_and_captures_output_cross_platform()
    {
        using var directory = TestDirectory.Create();
        var output = new StringWriter();
        var error = new StringWriter();
        var runner = new CliWrapProcessRunner(output, error);

        var result = await runner.RunAsync(
            new ProcessInvocation("dotnet", ["--version"], directory.Path),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.StandardError);
        Assert.False(string.IsNullOrWhiteSpace(result.StandardOutput));
        Assert.Equal(result.StandardOutput.Trim(), output.ToString().Trim());
        Assert.Empty(error.ToString());
    }

    private static async Task<(
        int ExitCode,
        string Output,
        string Error,
        FakeProcessRunner Runner)> RunAsync(
        TestDirectory directory,
        string[] args,
        FakeProcessRunner? runner = null,
        FakeEnvironment? environment = null)
    {
        runner ??= new FakeProcessRunner((_, _) =>
            Task.FromResult(new ProcessExecutionResult(0, string.Empty, string.Empty)));
        environment ??= new FakeEnvironment(directory.Path);
        var output = new StringWriter();
        var error = new StringWriter();
        var exitCode = await ToolApplication.RunAsync(
            args,
            runner,
            environment,
            output,
            error,
            TestContext.Current.CancellationToken);
        return (exitCode, output.ToString(), error.ToString(), runner);
    }

    private static ModuleImageManifestDocument CreateManifest()
    {
        var document = new ModuleImageManifestDocument();
        document.Images.Add(CreateManifestImage("orders", "worker", ModuleResourceKind.Container));
        document.Images.Add(CreateManifestImage("orders", "api", ModuleResourceKind.Project));
        document.Images.Add(CreateManifestImage("catalog", "api", ModuleResourceKind.Project));
        return document;
    }

    private static ModuleImageManifestEntry CreateManifestImage(
        string module,
        string resource,
        ModuleResourceKind kind) => new()
        {
            Module = module,
            Resource = resource,
            ResourceKind = kind,
            Registry = "registry.example.test",
            Repository = $"acme/{module}-{resource}",
            Tag = "original"
        };

    private static ModuleImageDescriptionDocument CreateDescriptions()
    {
        var document = new ModuleImageDescriptionDocument();
        document.Modules.Add(new ModuleImageModuleDescription { Name = "orders" });
        document.Modules.Add(new ModuleImageModuleDescription { Name = "catalog" });
        document.Images.Add(CreateDescription("orders", "worker", "imported-worker", ModuleResourceKind.Container));
        document.Images.Add(CreateDescription("catalog", "api", "catalog-api", ModuleResourceKind.Project));
        document.Images.Add(CreateDescription("orders", "api", "imported-api", ModuleResourceKind.Project));
        return document;
    }

    private static ModuleImageDescription CreateDescription(
        string module,
        string resource,
        string effectiveResource,
        ModuleResourceKind kind)
    {
        var reference = $"registry.example.test/acme/{module}-{resource}:original";
        return new ModuleImageDescription
        {
            Module = module,
            Resource = resource,
            EffectiveResource = effectiveResource,
            ResourceKind = kind,
            Registry = "registry.example.test",
            Repository = $"acme/{module}-{resource}",
            Tag = "original",
            Reference = reference,
            PullReference = reference,
            PushReference = reference,
            Build = new ModuleImageBuildDescription
            {
                Command = "docker",
                WorkingDirectory = "/work",
                Step = $"build-{effectiveResource}"
            }
        };
    }

    private static void AssertProducerInvocation(ProcessInvocation invocation, string step)
    {
        Assert.Equal(step, invocation.Arguments[1]);
        Assert.False(invocation.CaptureOutput);
        Assert.Equal(["imported-api", "imported-worker"], invocation.Arguments.SkipWhile(value => value != "--").Skip(1));
        Assert.NotNull(invocation.EnvironmentVariables);
        Assert.Equal(
            "api-tag",
            invocation.EnvironmentVariables["Aspire__ModularAppHosts__WorkflowImageOverrides__0__Tag"]);
        Assert.Equal(
            "global",
            invocation.EnvironmentVariables["Aspire__ModularAppHosts__WorkflowImageOverrides__1__Tag"]);
    }

    private static string? GetOption(IReadOnlyList<string> arguments, string name)
    {
        for (var index = 0; index < arguments.Count - 1; index++)
        {
            if (string.Equals(arguments[index], name, StringComparison.Ordinal))
            {
                return arguments[index + 1];
            }
        }

        return null;
    }

    private static async Task<IReadOnlyDictionary<string, string>> ReadGitHubFileAsync(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in await File.ReadAllLinesAsync(path, TestContext.Current.CancellationToken))
        {
            var separator = line.IndexOf('=', StringComparison.Ordinal);
            Assert.True(separator > 0);
            values.Add(line[..separator], line[(separator + 1)..]);
        }

        return values;
    }

    private sealed class FakeProcessRunner(
        Func<ProcessInvocation, CancellationToken, Task<ProcessExecutionResult>> handler) : IProcessRunner
    {
        public IList<ProcessInvocation> Invocations { get; } = [];

        public Task<ProcessExecutionResult> RunAsync(
            ProcessInvocation invocation,
            CancellationToken cancellationToken)
        {
            Invocations.Add(invocation);
            return handler(invocation, cancellationToken);
        }
    }

    private sealed class FakeEnvironment(
        string currentDirectory,
        IReadOnlyDictionary<string, string>? variables = null) : IEnvironmentAccessor
    {
        public string CurrentDirectory { get; } = currentDirectory;

        public string? GetEnvironmentVariable(string name) =>
            variables?.TryGetValue(name, out var value) == true ? value : null;
    }

    private sealed class TestDirectory : IDisposable
    {
        private TestDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TestDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"modular-apphosts-tool-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TestDirectory(path);
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
