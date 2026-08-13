#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREUSERSECRETS001
#pragma warning disable ASPIREFILESYSTEM001

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aspire.Hosting.ModularAppHosts.Tests;

public sealed class ModuleProjectModeSwitchingPipelineTests
{
    [Fact]
    public void Global_and_resource_steps_use_effective_resource_names()
    {
        using var directory = TemporaryDirectory.Create();
        var inner = CreateBuilder(directory.Path);
        var pipeline = new CapturingPipeline();
        var secrets = new TestUserSecretsManager(Path.Combine(directory.Path, "secrets.json"));
        var switching = new ModuleProjectModeSwitchingPipeline(
            new TestBuilder(inner, pipeline, secrets));

        switching.RegisterProject("sales-orders-api");
        switching.RegisterProject("sales-orders-api");

        Assert.Equal(
            [
                "use-projects",
                "use-containers",
                "use-configured-modes",
                "use-project-sales-orders-api",
                "use-container-sales-orders-api",
                "use-configured-sales-orders-api"
            ],
            pipeline.Steps.Select(step => step.Name));
    }

    [Fact]
    public void Imported_project_steps_follow_the_effective_alias()
    {
        using var directory = TemporaryDirectory.Create();
        var projectPath = Path.Combine(directory.Path, "Orders.Api.csproj");
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var inner = CreateBuilder(directory.Path);
        var pipeline = new CapturingPipeline();
        var builder = new TestBuilder(
            inner,
            pipeline,
            new TestUserSecretsManager(Path.Combine(directory.Path, "secrets.json")));
        builder.ExportModule("orders", module =>
        {
            module.WithRepository(directory.Path);
            module.AddProject("orders-api", projectPath)
                .ExportAsContainer("example/orders-api");
        });
        var import = new ModuleImportOptions();
        import.ResourceAliases["orders-api"] = "sales-api";

        builder.ImportModule("orders", import);

        Assert.Contains(pipeline.Steps, step => step.Name == "use-project-sales-api");
        Assert.Contains(pipeline.Steps, step => step.Name == "use-container-sales-api");
        Assert.DoesNotContain(pipeline.Steps, step => step.Name == "use-project-orders-api");
    }

    [Fact]
    public async Task Pipeline_steps_persist_global_exceptions_and_exact_reset()
    {
        using var directory = TemporaryDirectory.Create();
        var inner = CreateBuilder(directory.Path);
        var pipeline = new CapturingPipeline();
        var secrets = new TestUserSecretsManager(Path.Combine(directory.Path, "secrets.json"));
        var switching = new ModuleProjectModeSwitchingPipeline(
            new TestBuilder(inner, pipeline, secrets));
        switching.RegisterProject("orders-api");
        switching.RegisterProject("catalog-api");
        await using var application = inner.Build();

        await ExecuteAsync(application, pipeline["use-containers"]);
        var state = new ModuleProjectModeSwitchStore(secrets).Read();
        Assert.Equal(ModuleProjectModeSwitchValue.Container, state.Mode);
        Assert.Empty(state.Resources);

        await ExecuteAsync(application, pipeline["use-project-orders-api"]);
        state = new ModuleProjectModeSwitchStore(secrets).Read();
        Assert.Equal(ModuleProjectModeSwitchValue.Project, state.Resources["orders-api"]);

        await ExecuteAsync(application, pipeline["use-configured-orders-api"]);
        state = new ModuleProjectModeSwitchStore(secrets).Read();
        Assert.Equal(ModuleProjectModeSwitchValue.Configured, state.Resources["orders-api"]);

        await ExecuteAsync(application, pipeline["use-projects"]);
        state = new ModuleProjectModeSwitchStore(secrets).Read();
        Assert.Equal(ModuleProjectModeSwitchValue.Project, state.Mode);
        Assert.Empty(state.Resources);

        await ExecuteAsync(application, pipeline["use-configured-modes"]);
        Assert.Null(secrets.Get(ModuleProjectModeSwitchStore.SecretName));
    }

    [Fact]
    public void Resource_and_global_switches_override_configuration_with_configured_escape_hatch()
    {
        using var directory = TemporaryDirectory.Create();
        var secrets = new TestUserSecretsManager(Path.Combine(directory.Path, "secrets.json"));
        new ModuleProjectModeSwitchStore(secrets).Write(new ModuleProjectModeSwitchState
        {
            Mode = ModuleProjectModeSwitchValue.Container,
            Resources = new Dictionary<string, ModuleProjectModeSwitchValue>
            {
                ["orders-api"] = ModuleProjectModeSwitchValue.Project,
                ["catalog-api"] = ModuleProjectModeSwitchValue.Configured
            }
        });
        var inner = CreateBuilder(directory.Path);
        var switching = new ModuleProjectModeSwitchingPipeline(
            new TestBuilder(inner, new CapturingPipeline(), secrets));

        Assert.Equal(
            ModuleProjectMode.Project,
            switching.Resolve("orders-api", ModuleProjectMode.Container, imported: true));
        Assert.Equal(
            ModuleProjectMode.Project,
            switching.Resolve("catalog-api", ModuleProjectMode.Project, imported: true));
        Assert.Equal(
            ModuleProjectMode.Container,
            switching.Resolve("inventory-api", ModuleProjectMode.Project, imported: false));
    }

    [Fact]
    public async Task Unavailable_user_secrets_produce_setup_remediation()
    {
        using var directory = TemporaryDirectory.Create();
        var inner = CreateBuilder(directory.Path);
        var pipeline = new CapturingPipeline();
        _ = new ModuleProjectModeSwitchingPipeline(
            new TestBuilder(inner, pipeline, new TestUserSecretsManager(string.Empty, available: false)));

        await using var application = inner.Build();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ExecuteAsync(application, pipeline["use-containers"]));

        Assert.Contains("UserSecretsId", exception.Message, StringComparison.Ordinal);
        Assert.Contains("dotnet user-secrets init --project", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_state_fails_with_reset_command()
    {
        using var directory = TemporaryDirectory.Create();
        var secrets = new TestUserSecretsManager(Path.Combine(directory.Path, "secrets.json"));
        Assert.True(secrets.TrySetSecret(
            ModuleProjectModeSwitchStore.SecretName,
            "{\"version\":99,\"mode\":\"Container\",\"resources\":{}}"));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new ModuleProjectModeSwitchStore(secrets).Read());

        Assert.Contains(ModuleProjectModeSwitchStore.SecretName, exception.Message, StringComparison.Ordinal);
        Assert.Contains("use-configured-modes", exception.Message, StringComparison.Ordinal);

        new ModuleProjectModeSwitchStore(secrets).Delete();
        Assert.Null(secrets.Get(ModuleProjectModeSwitchStore.SecretName));
    }

    private static IDistributedApplicationBuilder CreateBuilder(string projectDirectory) =>
        DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = [],
            DisableDashboard = true,
            ProjectDirectory = projectDirectory
        });

    private static async Task ExecuteAsync(DistributedApplication application, PipelineStep step)
    {
        var pipelineContext = new PipelineContext(
            application.Services.GetRequiredService<DistributedApplicationModel>(),
            application.Services.GetRequiredService<DistributedApplicationExecutionContext>(),
            application.Services,
            NullLogger.Instance,
            TestContext.Current.CancellationToken);
        await using var reportingStep = await new NullPublishingActivityReporter().CreateStepAsync(
            step.Name,
            TestContext.Current.CancellationToken);
        await step.Action(new PipelineStepContext
        {
            PipelineContext = pipelineContext,
            ReportingStep = reportingStep
        });
    }

    private sealed class CapturingPipeline : IDistributedApplicationPipeline
    {
        public IList<PipelineStep> Steps { get; } = [];

        public PipelineStep this[string name] => Assert.Single(
            Steps,
            step => string.Equals(step.Name, name, StringComparison.Ordinal));

        public void AddStep(
            string name,
            Func<PipelineStepContext, Task> action,
            object? dependsOn = null,
            object? requiredBy = null) => throw new NotSupportedException();

        public void AddStep(PipelineStep step) => Steps.Add(step);

        public void AddPipelineConfiguration(Func<PipelineConfigurationContext, Task> callback)
        {
        }

        public Task ExecuteAsync(PipelineContext context) => throw new NotSupportedException();
    }

    private sealed class TestBuilder(
        IDistributedApplicationBuilder inner,
        IDistributedApplicationPipeline pipeline,
        IUserSecretsManager userSecretsManager) : IDistributedApplicationBuilder
    {
        public ConfigurationManager Configuration => inner.Configuration;
        public string AppHostDirectory => inner.AppHostDirectory;
        public Assembly? AppHostAssembly => inner.AppHostAssembly;
        public IHostEnvironment Environment => inner.Environment;
        public IServiceCollection Services => inner.Services;
        public IDistributedApplicationEventing Eventing => inner.Eventing;
        public DistributedApplicationExecutionContext ExecutionContext => inner.ExecutionContext;
        public IResourceCollection Resources => inner.Resources;
        public IDistributedApplicationPipeline Pipeline => pipeline;
        public IFileSystemService FileSystemService => inner.FileSystemService;
        public IUserSecretsManager UserSecretsManager => userSecretsManager;

        public IResourceBuilder<T> AddResource<T>(T resource)
            where T : IResource => inner.AddResource(resource);

        public IResourceBuilder<T> CreateResourceBuilder<T>(T resource)
            where T : IResource => inner.CreateResourceBuilder(resource);

        public DistributedApplication Build() => inner.Build();
    }

    private sealed class TestUserSecretsManager(string filePath, bool available = true) : IUserSecretsManager
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

        public bool IsAvailable { get; } = available;

        public string FilePath { get; } = filePath;

        public string? Get(string name) => _values.GetValueOrDefault(name);

        public bool TrySetSecret(string name, string value)
        {
            if (!IsAvailable)
            {
                return false;
            }

            _values[name] = value;
            Save();
            return true;
        }

        public bool TryDeleteSecret(string name)
        {
            if (!IsAvailable)
            {
                return false;
            }

            _values.Remove(name);
            Save();
            return true;
        }

        public void GetOrSetSecret(
            IConfigurationManager configuration,
            string name,
            Func<string> valueGenerator) => throw new NotSupportedException();

        public Task SaveStateAsync(JsonObject state, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        private void Save()
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(FilePath, JsonSerializer.Serialize(_values));
        }
    }
}
