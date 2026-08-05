using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Options;
using Xunit;

namespace Aspire.Hosting.ModularAppHosts.Tests;

public sealed class ModulePreviewManifestTests
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private const string OtherCommit = "89abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task Save_and_load_round_trip_deterministic_validated_json()
    {
        using var directory = TemporaryDirectory.Create();
        var firstPath = Path.Combine(directory.Path, "first.json");
        var secondPath = Path.Combine(directory.Path, "second.json");
        var manifest = CreateManifest();

        await manifest.SaveAsync(firstPath, TestContext.Current.CancellationToken);
        var loaded = await ModulePreviewManifest.LoadAsync(
            firstPath,
            TestContext.Current.CancellationToken);
        await loaded.SaveAsync(secondPath, TestContext.Current.CancellationToken);

        Assert.Equal(
            await File.ReadAllTextAsync(firstPath, TestContext.Current.CancellationToken),
            await File.ReadAllTextAsync(secondPath, TestContext.Current.CancellationToken));
        Assert.False(loaded.Producer.Dirty);
        var module = Assert.Single(loaded.Modules);
        Assert.Equal("orders", module.Name);
        Assert.Equal(Commit, module.Commit);
    }

    [Fact]
    public void Validate_rejects_unknown_schema_duplicates_secrets_and_non_immutable_content()
    {
        var schema = CreateManifest();
        schema.SchemaVersion = 2;
        Assert.Contains("schema version", Assert.Throws<InvalidDataException>(schema.Validate).Message);

        var duplicate = CreateManifest();
        duplicate.Modules.Add(new ModulePreviewSelection
        {
            Name = "ORDERS",
            Repository = "https://github.com/acme/orders.git",
            Commit = OtherCommit
        });
        Assert.Contains("duplicate", Assert.Throws<InvalidDataException>(duplicate.Validate).Message);

        var secret = CreateManifest();
        secret.Modules[0].Repository = "https://token@github.com/acme/orders.git";
        Assert.Contains("credentials", Assert.Throws<InvalidDataException>(secret.Validate).Message);

        var abbreviated = CreateManifest();
        abbreviated.Modules[0].Commit = "0123456";
        Assert.Contains("40- or 64-character", Assert.Throws<InvalidDataException>(abbreviated.Validate).Message);

        var dirty = CreateManifest();
        dirty.Producer.Dirty = true;
        Assert.Contains("must be false", Assert.Throws<InvalidDataException>(dirty.Validate).Message);
    }

    [Fact]
    public async Task Apply_manifest_sets_authoritative_programmatic_module_options()
    {
        using var directory = TemporaryDirectory.Create();
        var manifestPath = Path.Combine(directory.Path, "preview.json");
        await CreateManifest().SaveAsync(manifestPath, TestContext.Current.CancellationToken);
        var builder = CreateBuilder(directory.Path);
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:Modules:orders:Repository"] =
            "https://github.com/acme/configured.git";

        var returned = await builder.ApplyModulePreviewManifestAsync(
            manifestPath,
            TestContext.Current.CancellationToken);
        builder.ConfigureModularAppHosts(options =>
        {
            options.Modules["orders"].Repository = "https://github.com/acme/later-override.git";
            options.Modules["orders"].RepositoryRevision = OtherCommit;
        });

        Assert.Same(builder, returned);
        var options = Assert.Single(
            builder.Services,
            descriptor => descriptor.ServiceType == typeof(IOptions<ModularAppHostsOptions>));
        var configured = Assert.IsType<OptionsWrapper<ModularAppHostsOptions>>(options.ImplementationInstance).Value;
        Assert.Equal("https://github.com/acme/orders.git", configured.Modules["orders"].Repository);
        Assert.Equal(Commit, configured.Modules["orders"].RepositoryRevision);
    }

    [Fact]
    public async Task Preview_repository_bypasses_conflicting_repository_configuration_at_materialization()
    {
        using var directory = TemporaryDirectory.Create();
        var builder = CreateBuilder(directory.Path);
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:Modules:orders:Repository"] =
            "https://github.com/acme/configured.git";
        builder.ApplyModulePreviewManifest(CreateManifest());

        await builder.DefineModuleAsync("orders", "1", definition =>
        {
            definition.WithRepository("https://github.com/acme/orders.git");
            definition.AddContainer("api", "nginx");
        }, TestContext.Current.CancellationToken);
        await builder.ImportModuleAsync("orders", TestContext.Current.CancellationToken);

        Assert.DoesNotContain(builder.Resources, resource => resource is ParameterResource);
    }

    [Fact]
    public async Task Preview_repository_must_match_the_exported_module_contract()
    {
        using var directory = TemporaryDirectory.Create();
        var builder = CreateBuilder(directory.Path);
        builder.ApplyModulePreviewManifest(CreateManifest());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            builder.DefineModuleAsync("orders", "1", definition =>
            {
                definition.WithRepository("https://github.com/acme/different.git");
                definition.AddContainer("api", "nginx");
            }, TestContext.Current.CancellationToken));

        Assert.Contains("different.git", exception.Message, StringComparison.Ordinal);
        Assert.Contains("orders.git", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preview_manifest_must_be_applied_before_module_materialization()
    {
        using var directory = TemporaryDirectory.Create();
        var builder = CreateBuilder(directory.Path);
        await builder.DefineModuleAsync("orders", "1", definition =>
            definition.AddContainer("api", "nginx"), TestContext.Current.CancellationToken);
        await builder.ImportModuleAsync("orders", TestContext.Current.CancellationToken);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            builder.ApplyModulePreviewManifest(CreateManifest()));

        Assert.Contains("before importing", exception.Message, StringComparison.Ordinal);
    }

    private static ModulePreviewManifest CreateManifest()
    {
        var manifest = new ModulePreviewManifest
        {
            Producer = new ModulePreviewProducer
            {
                Repository = "https://github.com/acme/orders.git",
                Commit = Commit,
                Dirty = false,
                Branch = "feat/preview",
                BaseRef = "refs/heads/main",
                BaseCommit = OtherCommit
            }
        };
        manifest.Modules.Add(new ModulePreviewSelection
        {
            Name = "orders",
            Repository = "https://github.com/acme/orders.git",
            Commit = Commit,
            Branch = "feat/preview",
            BaseRef = "refs/heads/main",
            BaseCommit = OtherCommit
        });
        return manifest;
    }

    private static IDistributedApplicationBuilder CreateBuilder(string projectDirectory) =>
        DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = [],
            DisableDashboard = true,
            ProjectDirectory = projectDirectory
        });
}
