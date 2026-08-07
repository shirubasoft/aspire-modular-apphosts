using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aspire.Hosting.ModularAppHosts;
using Xunit;

namespace Shirubasoft.Aspire.ModularAppHosts.Tool.Tests;

public sealed class PreviewPolicyTests
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private const string ExternalCommit = "89abcdef0123456789abcdef0123456789abcdef";
    private const string Repository = "https://github.com/shirubasoft/preview-producer.git";
    private const string ExternalRepository = "https://github.com/shirubasoft/image-builder.git";
    private const string ImageRepository = "ghcr.io/shirubasoft/preview-producer";
    private const string OwnerOnlyImageRepository = "ghcr.io/shirubasoft/preview-producer/worker";
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
    public async Task Producer_descriptor_schema_matches_runtime_repository_validation()
    {
        var schemaPath = Path.Combine(
            AppContext.BaseDirectory,
            "TestData",
            "module-preview-producer.schema.json");
        await using var stream = File.OpenRead(schemaPath);
        using var schema = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: TestContext.Current.CancellationToken);
        var root = schema.RootElement;
        Assert.DoesNotContain(
            "$schema",
            root.GetProperty("required").EnumerateArray().Select(element => element.GetString()));
        var pattern = root
            .GetProperty("properties")
            .GetProperty("images")
            .GetProperty("items")
            .GetProperty("properties")
            .GetProperty("repository")
            .GetProperty("pattern")
            .GetString()!;
        var regex = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        var packageIdPattern = root
            .GetProperty("$defs")
            .GetProperty("packageId")
            .GetProperty("pattern")
            .GetString()!;
        var packageIdRegex = new Regex(
            packageIdPattern,
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));

        Assert.Matches(packageIdRegex, "Sample.Module-Contract_2");
        Assert.DoesNotMatch(packageIdRegex, ".Sample.Module");
        Assert.Throws<InvalidDataException>(() =>
            CreateContractOnlyDescriptor(".Sample.Module").Validate());
        Assert.Throws<InvalidDataException>(() =>
            CreateContractOnlyDescriptor("Sample.Module", string.Empty).Validate());

        foreach (var repository in new[]
                 {
                     "ghcr.io/example/api",
                     "registry.example.test:5000/a.b/image_name-v2"
                 })
        {
            Assert.True(regex.IsMatch(repository), repository);
            CreateImageOnlyDescriptor(repository).Validate();
        }

        foreach (var repository in new[]
                 {
                     "registry.example.test/a//b",
                     "registry.example.test/a-/b",
                     "-registry.example.test/a/b",
                     "registry.example.test/a/b-"
                 })
        {
            Assert.False(regex.IsMatch(repository), repository);
            Assert.Throws<InvalidDataException>(() => CreateImageOnlyDescriptor(repository).Validate());
        }
    }

    [Fact]
    public async Task Producer_descriptor_allows_an_image_only_preview()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "module": "preview-producer",
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

        Assert.Null(descriptor.Contract);
        Assert.Single(descriptor.Images);
    }

    [Fact]
    public async Task Consumer_policy_loads_external_producer_repositories_per_image()
    {
        const string json = """
            {
              "schemaVersion": 2,
              "modules": [
                {
                  "module": "preview-producer",
                  "repository": "https://github.com/shirubasoft/preview-producer.git",
                  "images": [
                    {
                      "resource": "preview-producer-api",
                      "resourceKind": "project",
                      "repositories": ["ghcr.io/shirubasoft/preview-producer"],
                      "producerRepositories": ["https://github.com/shirubasoft/image-builder.git"],
                      "required": true
                    }
                  ]
                }
              ]
            }
            """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var policy = await ModulePreviewConsumerPolicy.LoadAsync(
            stream,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            ExternalRepository,
            Assert.Single(Assert.Single(policy.Modules).Images).ProducerRepositories.Single());
    }

    [Fact]
    public void Consumer_policy_rejects_schema_version_one()
    {
        var policy = CreatePolicy();
        policy.SchemaVersion = 1;

        var exception = Assert.Throws<InvalidDataException>(policy.Validate);

        Assert.Contains("schema version '1'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Expected '2'", exception.Message, StringComparison.Ordinal);
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
                VersionEnvironment = "PreviewContractVersion",
                SourceFallback = new ModulePreviewSourceFallbackPolicy
                {
                    Enabled = true,
                    Project = "src/Other.Contract/Other.Contract.csproj"
                }
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
    public void Evaluator_compares_GitHub_repository_owner_and_path_without_case_sensitivity()
    {
        var manifest = CreateManifest(includeImage: true);
        manifest.Producer.Repository = "https://github.com/SHIRUBASOFT/preview-PRODUCER.git";
        manifest.Modules[0].Repository = "https://GITHUB.com/Shirubasoft/PREVIEW-producer.git";

        var evaluation = PreviewPolicyEvaluator.Evaluate(manifest, CreatePolicy());

        Assert.Single(evaluation.Modules);
    }

    [Fact]
    public void Evaluator_keeps_non_GitHub_repository_paths_case_sensitive_and_reports_expected_identity()
    {
        const string actual = "https://git.example.com/Shirubasoft/Preview-Producer.git";
        const string expected = "https://git.example.com/shirubasoft/Preview-Producer.git";
        var manifest = CreateManifest(includeImage: true);
        manifest.Producer.Repository = actual;
        manifest.Modules[0].Repository = actual;
        var policy = CreatePolicy();
        policy.Modules[0].Repository = expected;

        var exception = Assert.Throws<InvalidDataException>(() =>
            PreviewPolicyEvaluator.Evaluate(manifest, policy));

        Assert.Contains(actual, exception.Message, StringComparison.Ordinal);
        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluator_accepts_an_omitted_optional_contract()
    {
        var manifest = CreateManifest(includeImage: true);
        manifest.Contracts.Clear();
        var policy = CreatePolicy();
        policy.Modules[0].Contract!.Required = false;

        var evaluation = PreviewPolicyEvaluator.Evaluate(manifest, policy);

        Assert.Null(Assert.Single(evaluation.Modules).Contract);
    }

    [Fact]
    public void Evaluator_rejects_an_omitted_required_contract()
    {
        var manifest = CreateManifest(includeImage: true);
        manifest.Contracts.Clear();

        var exception = Assert.Throws<InvalidDataException>(() =>
            PreviewPolicyEvaluator.Evaluate(manifest, CreatePolicy()));

        Assert.Contains("required policy-owned contract", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Policy_accepts_a_trusted_published_contract_source_without_pack_configuration()
    {
        var policy = CreatePolicy();
        var contract = policy.Modules[0].Contract!;
        contract.Required = false;
        contract.Published = new ModulePreviewPublishedContractPolicy
        {
            Source = "https://nuget.pkg.github.com/shirubasoft/index.json"
        };
        contract.SourceFallback = new ModulePreviewSourceFallbackPolicy();
        contract.AllowedPackProperties.Clear();

        policy.Validate();
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
    public void Evaluator_accepts_an_explicitly_allowlisted_external_image_producer()
    {
        var manifest = CreateManifest(includeImage: true);
        manifest.Producer.Repository = ExternalRepository;
        manifest.Producer.Commit = ExternalCommit;
        manifest.Contracts.Clear();
        var policy = CreatePolicy();
        policy.Modules[0].Images[0].ProducerRepositories.Add(ExternalRepository);
        AddRequiredOwnerOnlyImage(policy);

        var evaluation = PreviewPolicyEvaluator.Evaluate(manifest, policy);

        Assert.Single(evaluation.Modules);
        Assert.Single(evaluation.Manifest.Images);
    }

    [Fact]
    public void Evaluator_requires_an_external_producers_authorized_required_image()
    {
        var manifest = CreateManifest(includeImage: false);
        manifest.Producer.Repository = ExternalRepository;
        manifest.Producer.Commit = ExternalCommit;
        manifest.Contracts.Clear();
        var policy = CreatePolicy();
        policy.Modules[0].Images[0].ProducerRepositories.Add(ExternalRepository);

        var exception = Assert.Throws<InvalidDataException>(() =>
            PreviewPolicyEvaluator.Evaluate(manifest, policy));

        Assert.Contains("must provide an immutable image", exception.Message, StringComparison.Ordinal);
        Assert.Contains("preview-producer-api", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluator_matches_external_GitHub_producer_allowlists_without_case_sensitivity()
    {
        var manifest = CreateManifest(includeImage: true);
        manifest.Producer.Repository = "https://github.com/SHIRUBASOFT/IMAGE-BUILDER.git";
        manifest.Producer.Commit = ExternalCommit;
        manifest.Contracts.Clear();
        var policy = CreatePolicy();
        policy.Modules[0].Contract!.Required = false;
        policy.Modules[0].Images[0].ProducerRepositories.Add(ExternalRepository);

        var evaluation = PreviewPolicyEvaluator.Evaluate(manifest, policy);

        Assert.Single(evaluation.Modules);
    }

    [Fact]
    public void Evaluator_rejects_an_external_producer_without_per_image_authorization()
    {
        var manifest = CreateManifest(includeImage: true);
        manifest.Producer.Repository = ExternalRepository;
        manifest.Producer.Commit = ExternalCommit;
        manifest.Contracts.Clear();
        var policy = CreatePolicy();
        policy.Modules[0].Contract!.Required = false;
        AddProducerAsUnrelatedSelectedModule(manifest, policy);

        var exception = Assert.Throws<InvalidDataException>(() =>
            PreviewPolicyEvaluator.Evaluate(manifest, policy));

        Assert.Contains(ExternalRepository, exception.Message, StringComparison.Ordinal);
        Assert.Contains("is not allowed for image", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluator_rejects_contracts_from_an_external_image_producer()
    {
        var manifest = CreateManifest(includeImage: true);
        manifest.Producer.Repository = ExternalRepository;
        manifest.Producer.Commit = ExternalCommit;
        var policy = CreatePolicy();
        policy.Modules[0].Images[0].ProducerRepositories.Add(ExternalRepository);
        AddProducerAsUnrelatedSelectedModule(manifest, policy);

        var exception = Assert.Throws<InvalidDataException>(() =>
            PreviewPolicyEvaluator.Evaluate(manifest, policy));

        Assert.Contains("cannot request its contract package", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluator_rejects_an_external_producer_without_images()
    {
        var manifest = CreateManifest(includeImage: false);
        manifest.Producer.Repository = ExternalRepository;
        manifest.Producer.Commit = ExternalCommit;
        manifest.Contracts.Clear();
        var policy = CreatePolicy();
        policy.Modules[0].Contract!.Required = false;
        policy.Modules[0].Images[0].Required = false;

        var exception = Assert.Throws<InvalidDataException>(() =>
            PreviewPolicyEvaluator.Evaluate(manifest, policy));

        Assert.Contains("must offer at least one contract package or immutable image", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Policy_rejects_duplicate_external_producer_repositories()
    {
        var policy = CreatePolicy();
        policy.Modules[0].Images[0].ProducerRepositories.Add(ExternalRepository);
        policy.Modules[0].Images[0].ProducerRepositories.Add(ExternalRepository);

        var exception = Assert.Throws<InvalidDataException>(policy.Validate);

        Assert.Contains("duplicate producer repository", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Policy_rejects_case_only_duplicate_GitHub_producer_repositories()
    {
        var policy = CreatePolicy();
        policy.Modules[0].Images[0].ProducerRepositories.Add(ExternalRepository);
        policy.Modules[0].Images[0].ProducerRepositories.Add(
            "https://GITHUB.com/Shirubasoft/IMAGE-BUILDER.git");

        var exception = Assert.Throws<InvalidDataException>(policy.Validate);

        Assert.Contains("duplicate producer repository", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Policy_allows_case_distinct_non_GitHub_producer_repository_paths()
    {
        var policy = CreatePolicy();
        policy.Modules[0].Images[0].ProducerRepositories.Add(
            "https://git.example.com/Shirubasoft/image-builder.git");
        policy.Modules[0].Images[0].ProducerRepositories.Add(
            "https://git.example.com/shirubasoft/image-builder.git");

        policy.Validate();
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

    private static ModulePreviewProducerDescriptor CreateImageOnlyDescriptor(string repository)
    {
        var descriptor = new ModulePreviewProducerDescriptor
        {
            Module = "preview-producer"
        };
        descriptor.Images.Add(new ModulePreviewProducerImageDescriptor
        {
            Resource = "preview-producer-api",
            ResourceKind = "container",
            Repository = repository,
            Required = true
        });
        return descriptor;
    }

    private static ModulePreviewProducerDescriptor CreateContractOnlyDescriptor(
        string packageId,
        string? version = null) =>
        new()
        {
            Module = "preview-producer",
            Contract = new ModulePreviewProducerContractDescriptor
            {
                PackageId = packageId,
                Version = version
            }
        };

    private static void AddProducerAsUnrelatedSelectedModule(
        ModulePreviewManifest manifest,
        ModulePreviewConsumerPolicy policy)
    {
        manifest.Modules.Add(new ModulePreviewSelection
        {
            Name = "build-support",
            Repository = ExternalRepository,
            Commit = ExternalCommit
        });
        policy.Modules.Add(new ModulePreviewConsumerModulePolicy
        {
            Module = "build-support",
            Repository = ExternalRepository
        });
    }

    private static void AddRequiredOwnerOnlyImage(ModulePreviewConsumerPolicy policy)
    {
        var image = new ModulePreviewConsumerImagePolicy
        {
            Resource = "preview-producer-worker",
            ResourceKind = "container",
            Required = true
        };
        image.Repositories.Add(OwnerOnlyImageRepository);
        policy.Modules[0].Images.Add(image);
    }
}
