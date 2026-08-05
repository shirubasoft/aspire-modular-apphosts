using System.Text;
using Aspire.Hosting.ModularAppHosts;
using Xunit;

namespace Shirubasoft.Aspire.ModularAppHosts.Tool.Tests;

public sealed class PreviewPolicyTests
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private const string Repository = "https://github.com/shirubasoft/preview-producer.git";
    private const string ImageRepository = "ghcr.io/shirubasoft/preview-producer";
    private const string ImageDigest =
        "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task Producer_descriptor_loads_strict_document()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "module": "preview-producer",
              "contract": {
                "packageId": "Shirubasoft.PreviewProducer.Contract",
                "version": "2.0.0-preview.1"
              },
              "images": [
                {
                  "resource": "preview-producer-api",
                  "resourceKind": "project",
                  "repository": "ghcr.io/shirubasoft/preview-producer",
                  "required": true
                }
              ]
            }
            """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var descriptor = await ModulePreviewProducerDescriptor.LoadAsync(
            stream,
            TestContext.Current.CancellationToken);

        Assert.Equal("preview-producer", descriptor.Module);
        Assert.Equal("Shirubasoft.PreviewProducer.Contract", descriptor.Contract?.PackageId);
        var image = Assert.Single(descriptor.Images);
        Assert.True(image.Required);
    }

    [Fact]
    public async Task Documents_reject_unknown_members()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "module": "preview-producer",
              "contract": {
                "packageId": "Shirubasoft.PreviewProducer.Contract",
                "version": "2.0.0-preview.1",
                "project": "producer-controlled.csproj"
              },
              "images": []
            }
            """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ModulePreviewProducerDescriptor.LoadAsync(
                stream,
                TestContext.Current.CancellationToken));

        Assert.Contains("not valid JSON", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Policy_rejects_source_fallback_that_escapes_repository()
    {
        var policy = CreatePolicy();
        policy.Modules[0].Contract!.SourceFallback.Project = "../attacker.csproj";

        var exception = Assert.Throws<InvalidDataException>(policy.Validate);

        Assert.Contains("must not escape", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Policy_rejects_duplicate_contract_environment_variables()
    {
        var policy = CreatePolicy();
        policy.Modules.Add(new ModulePreviewConsumerModulePolicy
        {
            Module = "other-module",
            Repository = "https://github.com/shirubasoft/other-module.git",
            Contract = new ModulePreviewConsumerContractPolicy
            {
                PackageId = "Shirubasoft.Other.Contract",
                VersionEnvironment = "PreviewContractVersion"
            }
        });

        var exception = Assert.Throws<InvalidDataException>(policy.Validate);

        Assert.Contains("more than one module", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("modulepreview__resolution")]
    [InlineData("MODULEPREVIEW__PACKAGEFEED")]
    [InlineData("github_token")]
    [InlineData("Runner_Temp")]
    public void Policy_rejects_reserved_contract_environment_variables(string environmentName)
    {
        var policy = CreatePolicy();
        policy.Modules[0].Contract!.VersionEnvironment = environmentName;

        var exception = Assert.Throws<InvalidDataException>(policy.Validate);

        Assert.Contains("reserved environment variable", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluator_accepts_allowlisted_contract_and_required_image()
    {
        var manifest = CreateManifest(includeImage: true);
        var policy = CreatePolicy();

        var evaluation = PreviewPolicyEvaluator.Evaluate(manifest, policy);

        var module = Assert.Single(evaluation.Modules);
        Assert.Equal("PreviewContractVersion", module.Policy.Contract?.VersionEnvironment);
        Assert.Equal("src/PreviewProducer.Contract/PreviewProducer.Contract.csproj",
            module.Policy.Contract?.SourceFallback.Project);
        Assert.Equal("2.0.0-preview.1", module.Contract?.Version);
        Assert.Single(module.Images);
    }

    [Fact]
    public void Evaluator_rejects_omitted_policy_required_image()
    {
        var manifest = CreateManifest(includeImage: false);
        var policy = CreatePolicy();

        var exception = Assert.Throws<InvalidDataException>(() =>
            PreviewPolicyEvaluator.Evaluate(manifest, policy));

        Assert.Contains("must provide an immutable image", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluator_rejects_image_from_unallowlisted_repository()
    {
        var manifest = CreateManifest(includeImage: true);
        manifest.Images[0].Repository = "ghcr.io/attacker/preview-producer";
        var policy = CreatePolicy();

        var exception = Assert.Throws<InvalidDataException>(() =>
            PreviewPolicyEvaluator.Evaluate(manifest, policy));

        Assert.Contains("is not allowed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluator_rejects_contract_package_substitution()
    {
        var manifest = CreateManifest(includeImage: true);
        manifest.Contracts[0].PackageId = "Attacker.Contract";
        var policy = CreatePolicy();

        var exception = Assert.Throws<InvalidDataException>(() =>
            PreviewPolicyEvaluator.Evaluate(manifest, policy));

        Assert.Contains("is not allowed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Image_digest_validation_requires_lowercase_sha256()
    {
        var manifest = CreateManifest(includeImage: true);
        manifest.Images[0].Sha256 =
            "sha256:ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

        var exception = Assert.Throws<InvalidDataException>(() =>
            PreviewPolicyEvaluator.Evaluate(manifest, CreatePolicy()));

        Assert.Contains("lowercase", exception.Message, StringComparison.Ordinal);
    }

    private static ModulePreviewManifest CreateManifest(bool includeImage)
    {
        var manifest = new ModulePreviewManifest
        {
            Producer = new ModulePreviewProducer
            {
                Repository = Repository,
                Commit = Commit,
                Dirty = false
            }
        };
        manifest.Modules.Add(new ModulePreviewSelection
        {
            Name = "preview-producer",
            Repository = Repository,
            Commit = Commit
        });
        manifest.Contracts.Add(new ModulePreviewContractRequest
        {
            Module = "preview-producer",
            PackageId = "Shirubasoft.PreviewProducer.Contract",
            Version = "2.0.0-preview.1"
        });
        if (includeImage)
        {
            manifest.Images.Add(new ModulePreviewImageArtifact
            {
                Module = "preview-producer",
                Resource = "preview-producer-api",
                ResourceKind = ModulePreviewResourceKind.Project,
                Repository = ImageRepository,
                Sha256 = ImageDigest
            });
        }

        return manifest;
    }

    private static ModulePreviewConsumerPolicy CreatePolicy()
    {
        var policy = new ModulePreviewConsumerPolicy();
        var module = new ModulePreviewConsumerModulePolicy
        {
            Module = "preview-producer",
            Repository = Repository,
            Contract = new ModulePreviewConsumerContractPolicy
            {
                PackageId = "Shirubasoft.PreviewProducer.Contract",
                VersionEnvironment = "PreviewContractVersion",
                SourceFallback = new ModulePreviewSourceFallbackPolicy
                {
                    Enabled = true,
                    Project = "src/PreviewProducer.Contract/PreviewProducer.Contract.csproj"
                }
            }
        };
        module.Contract.AllowedPackProperties.Add("ModularAppHostsVersion");
        var image = new ModulePreviewConsumerImagePolicy
        {
            Resource = "preview-producer-api",
            ResourceKind = "project",
            Required = true
        };
        image.Repositories.Add(ImageRepository);
        module.Images.Add(image);
        policy.Modules.Add(module);
        return policy;
    }
}
