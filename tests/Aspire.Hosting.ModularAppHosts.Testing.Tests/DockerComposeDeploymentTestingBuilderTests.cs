using System.Text;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ModularAppHosts;
using Aspire.Hosting.Testing;
using Xunit;

namespace Aspire.Hosting.ModularAppHosts.Testing.Tests;

public sealed class DockerComposeDeploymentTestingBuilderTests
{
    [Fact]
    public async Task Create_imports_endpoints_and_configuration_into_an_Aspire_testing_builder()
    {
        var endpointName = Encode("catalog-api");
        var configurationKey = Encode("Parameters:orders-api-key");
        using var file = TemporaryFile.Create($$"""
            # External test endpoint catalog-api
            ASPIRE_TEST_ENDPOINT__{{endpointName}}=http://localhost:5101/
            ASPIRE_TEST_ENDPOINT_HEALTH_PATH__{{endpointName}}=/health

            # External test configuration value Parameters:orders-api-key
            ASPIRE_TEST_VALUE__{{configurationKey}}=secret=with=separators
            """);

        var builder = DockerComposeDeploymentTestingBuilder
            .Create<DockerComposeDeploymentTestingBuilderTests>(file.Path);

        Assert.IsAssignableFrom<IDistributedApplicationTestingBuilder>(builder);
        Assert.Equal("secret=with=separators", builder.Configuration["Parameters:orders-api-key"]);
        Assert.True(builder.Resources.TryGetByName("catalog-api", out var resource));
        Assert.IsAssignableFrom<IResourceWithEndpoints>(resource);

        var endpoint = Assert.Single(resource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal("http", endpoint.Name);
        Assert.Equal("localhost", endpoint.AllocatedEndpoint?.Address);
        Assert.Equal(5101, endpoint.AllocatedEndpoint?.Port);
        Assert.Contains(resource.Annotations, annotation => annotation is HealthCheckAnnotation);

        await using var application = await builder.BuildAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Create_parses_bom_comments_whitespace_duplicates_and_https_without_health_checks()
    {
        var endpointName = Encode("orders-api");
        var configurationKey = Encode("Feature:Mode");
        using var file = TemporaryFile.Create(
            $"\uFEFF   # generated values{Environment.NewLine}" +
            $"ignored line{Environment.NewLine}" +
            $"  ASPIRE_TEST_VALUE__{configurationKey}=first{Environment.NewLine}" +
            $"ASPIRE_TEST_VALUE__{configurationKey}=second=with=equals  {Environment.NewLine}" +
            $" ASPIRE_TEST_ENDPOINT__{endpointName}=https://example.test:5443/base/{Environment.NewLine}");

        var builder = DockerComposeDeploymentTestingBuilder
            .Create<DockerComposeDeploymentTestingBuilderTests>(file.Path);

        Assert.Equal("second=with=equals  ", builder.Configuration["Feature:Mode"]);
        Assert.True(builder.Resources.TryGetByName("orders-api", out var resource));
        var endpoint = Assert.Single(resource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal("https", endpoint.Name);
        Assert.Equal("example.test", endpoint.AllocatedEndpoint?.Address);
        Assert.Equal(5443, endpoint.AllocatedEndpoint?.Port);
        Assert.DoesNotContain(resource.Annotations, annotation => annotation is HealthCheckAnnotation);

        await builder.DisposeAsync();
    }

    [Theory]
    [InlineData("not-an-endpoint")]
    [InlineData("ftp://example.test:21/")]
    public void Create_rejects_an_invalid_or_unsupported_exported_uri(string uri)
    {
        var endpointName = Encode("catalog");
        using var file = TemporaryFile.Create($"ASPIRE_TEST_ENDPOINT__{endpointName}={uri}");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            DockerComposeDeploymentTestingBuilder
                .Create<DockerComposeDeploymentTestingBuilderTests>(file.Path));

        Assert.Contains("invalid HTTP URI", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_rejects_an_invalid_encoded_name()
    {
        using var file = TemporaryFile.Create("ASPIRE_TEST_VALUE__NOT_HEX=value");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            DockerComposeDeploymentTestingBuilder
                .Create<DockerComposeDeploymentTestingBuilderTests>(file.Path));

        Assert.Contains("invalid encoded name", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateFromEnvironment_uses_the_configured_file()
    {
        using var file = TemporaryFile.Create(
            $"ASPIRE_TEST_VALUE__{Encode("Feature:Enabled")}=true");
        using var environment = EnvironmentVariable.Set(
            DockerComposeDeploymentTestingBuilder.FilePathEnvironmentVariableName,
            file.Path);

        var builder = DockerComposeDeploymentTestingBuilder
            .CreateFromEnvironment<DockerComposeDeploymentTestingBuilderTests>();

        Assert.Equal("true", builder.Configuration["Feature:Enabled"]);
        await builder.DisposeAsync();
    }

    [Fact]
    public void CreateFromEnvironment_requires_a_configured_file()
    {
        using var environment = EnvironmentVariable.Set(
            DockerComposeDeploymentTestingBuilder.FilePathEnvironmentVariableName,
            null);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            DockerComposeDeploymentTestingBuilder
                .CreateFromEnvironment<DockerComposeDeploymentTestingBuilderTests>());

        Assert.Contains(
            DockerComposeDeploymentTestingBuilder.FilePathEnvironmentVariableName,
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeployAsync_rejects_an_environment_name_that_cannot_be_an_env_file_suffix()
    {
        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            DockerComposeDeploymentTestingBuilder.DeployAsync<DockerComposeDeploymentTestingBuilderTests>(
                "../CI",
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("environmentName", exception.ParamName);
    }

    [Fact]
    public async Task DeployAsync_owns_a_temporary_deployment_until_idempotent_disposal()
    {
        var runner = new RecordingAspireCommandRunner(WriteConfigurationOnDeploy);
        var builder = await DeployAsync(
            runner,
            cancellationToken: TestContext.Current.CancellationToken);
        var deploy = Assert.Single(runner.Invocations);

        Assert.Equal("deploy", deploy.Command);
        Assert.Equal("CI", deploy.EnvironmentName);
        Assert.Equal(GetAppHostPath(), deploy.AppHostPath);
        Assert.True(Directory.Exists(deploy.OutputPath));

        await builder.DisposeAsync();
        await builder.DisposeAsync();

        Assert.Equal(["deploy", "destroy"], runner.Invocations.Select(invocation => invocation.Command));
        Assert.False(Directory.Exists(deploy.OutputPath));
    }

    [Fact]
    public async Task DeployAsync_retains_an_explicit_output_directory_after_destroy()
    {
        using var output = TemporaryDirectory.Create();
        var runner = new RecordingAspireCommandRunner(WriteConfigurationOnDeploy);
        var builder = await DeployAsync(
            runner,
            output.Path,
            TestContext.Current.CancellationToken);

        await builder.DisposeAsync();

        Assert.Equal(["deploy", "destroy"], runner.Invocations.Select(invocation => invocation.Command));
        Assert.True(Directory.Exists(output.Path));
        Assert.True(File.Exists(Path.Combine(output.Path, ".env.CI")));
    }

    [Fact]
    public async Task DeployAsync_preserves_a_deploy_failure_while_destroying_and_deleting_temporary_output()
    {
        var failure = new InvalidOperationException("deploy failed");
        var runner = new RecordingAspireCommandRunner(invocation =>
            invocation.Command == "deploy" ? Task.FromException(failure) : Task.CompletedTask);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DeployAsync(runner, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Same(failure, exception);
        Assert.Equal(["deploy", "destroy"], runner.Invocations.Select(invocation => invocation.Command));
        Assert.False(Directory.Exists(runner.Invocations[0].OutputPath));
    }

    [Fact]
    public async Task DeployAsync_cleans_up_when_deployment_does_not_create_the_environment_file()
    {
        var runner = new RecordingAspireCommandRunner(_ => Task.CompletedTask);

        var exception = await Assert.ThrowsAsync<FileNotFoundException>(() =>
            DeployAsync(runner, cancellationToken: TestContext.Current.CancellationToken));

        Assert.EndsWith(".env.CI", exception.FileName, StringComparison.Ordinal);
        Assert.Equal(["deploy", "destroy"], runner.Invocations.Select(invocation => invocation.Command));
        Assert.False(Directory.Exists(runner.Invocations[0].OutputPath));
    }

    [Fact]
    public async Task DeployAsync_forwards_cancellation_and_uses_a_fresh_token_for_cleanup()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var runner = new RecordingAspireCommandRunner(invocation =>
        {
            if (invocation.Command == "deploy")
            {
                invocation.CancellationToken.ThrowIfCancellationRequested();
            }

            Assert.False(invocation.CancellationToken.IsCancellationRequested);
            return Task.CompletedTask;
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DeployAsync(runner, cancellationToken: cancellation.Token));

        Assert.True(runner.Invocations[0].CancellationToken.IsCancellationRequested);
        Assert.Equal("destroy", runner.Invocations[1].Command);
        Assert.False(Directory.Exists(runner.Invocations[0].OutputPath));
    }

    [Fact]
    public async Task DisposeAsync_deletes_temporary_output_even_when_destroy_fails()
    {
        var failure = new InvalidOperationException("destroy failed");
        var runner = new RecordingAspireCommandRunner(async invocation =>
        {
            await WriteConfigurationOnDeploy(invocation);
            if (invocation.Command == "destroy")
            {
                throw failure;
            }
        });
        var builder = await DeployAsync(
            runner,
            cancellationToken: TestContext.Current.CancellationToken);
        var outputPath = runner.Invocations[0].OutputPath;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => builder.DisposeAsync().AsTask());

        Assert.Same(failure, exception);
        Assert.False(Directory.Exists(outputPath));
    }

    [Fact]
    public async Task BuildAsync_honors_pre_cancellation_without_building_the_application()
    {
        using var file = TemporaryFile.Create(string.Empty);
        var builder = DockerComposeDeploymentTestingBuilder
            .Create<DockerComposeDeploymentTestingBuilderTests>(file.Path);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            builder.BuildAsync(cancellation.Token));

        await builder.DisposeAsync();
    }

    [Fact]
    public async Task Builder_can_only_build_the_distributed_application_once()
    {
        using var file = TemporaryFile.Create(string.Empty);
        var builder = DockerComposeDeploymentTestingBuilder
            .Create<DockerComposeDeploymentTestingBuilderTests>(file.Path);
        await using var application = await builder.BuildAsync(TestContext.Current.CancellationToken);

        Assert.Throws<InvalidOperationException>(() => builder.Build());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            builder.BuildAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DisposeAsync_builds_an_unbuilt_application_and_is_idempotent()
    {
        using var file = TemporaryFile.Create(string.Empty);
        var builder = DockerComposeDeploymentTestingBuilder
            .Create<DockerComposeDeploymentTestingBuilderTests>(file.Path);

        await builder.DisposeAsync();
        await builder.DisposeAsync();

        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    private static Task<DockerComposeDeploymentTestingBuilder> DeployAsync(
        IAspireCommandRunner runner,
        string? outputPath = null,
        CancellationToken cancellationToken = default) =>
        DockerComposeDeploymentTestingBuilder.DeployAsync<DockerComposeDeploymentTestingBuilderTests>(
            "CI",
            outputPath,
            GetAppHostPath(),
            runner,
            cancellationToken);

    private static Task WriteConfigurationOnDeploy(AspireCommandInvocation invocation)
    {
        if (invocation.Command == "deploy")
        {
            Directory.CreateDirectory(invocation.OutputPath);
            File.WriteAllText(
                Path.Combine(invocation.OutputPath, $".env.{invocation.EnvironmentName}"),
                $"ASPIRE_TEST_VALUE__{Encode("Deployment:Ready")}=true");
        }

        return Task.CompletedTask;
    }

    private static string GetAppHostPath() => Path.GetFullPath(
        "Test.AppHost.csproj",
        AppContext.BaseDirectory);

    private static string Encode(string value) => Convert.ToHexString(Encoding.UTF8.GetBytes(value));

    private sealed class RecordingAspireCommandRunner(
        Func<AspireCommandInvocation, Task> run) : IAspireCommandRunner
    {
        public List<AspireCommandInvocation> Invocations { get; } = [];

        public Task RunAsync(
            string command,
            string appHostPath,
            string outputPath,
            string environmentName,
            CancellationToken cancellationToken)
        {
            var invocation = new AspireCommandInvocation(
                command,
                appHostPath,
                outputPath,
                environmentName,
                cancellationToken);
            Invocations.Add(invocation);
            return run(invocation);
        }
    }

    private sealed record AspireCommandInvocation(
        string Command,
        string AppHostPath,
        string OutputPath,
        string EnvironmentName,
        CancellationToken CancellationToken);

    private sealed class TemporaryFile : IDisposable
    {
        private TemporaryFile(string path) => Path = path;

        public string Path { get; }

        public static TemporaryFile Create(string content)
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"aspire-compose-test-{Guid.NewGuid():N}.env");
            File.WriteAllText(path, content);
            return new TemporaryFile(path);
        }

        public void Dispose() => File.Delete(Path);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path) => Path = path;

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"aspire-compose-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed class EnvironmentVariable : IDisposable
    {
        private readonly string _name;
        private readonly string? _originalValue;

        private EnvironmentVariable(string name, string? value)
        {
            _name = name;
            _originalValue = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public static EnvironmentVariable Set(string name, string? value) => new(name, value);

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _originalValue);
    }
}
