using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Aspire.Hosting.ModularAppHosts;
using Xunit;

namespace Shirubasoft.Aspire.ModularAppHosts.Tool.Tests;

public sealed class PreviewWorkflowCommandTests
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private const string BaseCommit = "89abcdef0123456789abcdef0123456789abcdef";
    private const string Repository = "https://github.com/shirubasoft/preview-producer.git";
    private const string ExternalModuleRepository = "https://github.com/shirubasoft/module-owner.git";
    private const string ContractPackageId = "Shirubasoft.PreviewProducer.Contract";
    private const string ContractVersion = "2.0.0-preview.1";
    private const string ApiImageRepository = "ghcr.io/shirubasoft/preview-producer/api";
    private const string SidecarImageRepository = "docker.io/library/nginx";
    private const string ApiImageDigest =
        "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string SidecarImageDigest =
        "sha256:89abcdef0123456789abcdef0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public async Task Produce_emits_contract_request_and_requires_every_declared_image()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = WorkflowTestDirectory.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        var descriptorPath = await WriteProducerDescriptorAsync(directory, cancellationToken);
        var git = await WriteGitExecutableAsync(directory, cancellationToken);
        var manifestPath = Path.Combine(directory.Path, "module-preview.json");

        var exitCode = await PreviewTool.RunAsync(
            [
                "preview", "produce",
                "--descriptor", descriptorPath,
                "--output", manifestPath,
                "--working-directory", directory.Path,
                "--git-executable", git,
                "--image", $"preview-producer-api={ApiImageRepository}@{ApiImageDigest}",
                "--image", $"preview-producer-sidecar={SidecarImageRepository}@{SidecarImageDigest}"
            ],
            cancellationToken);

        Assert.Equal(0, exitCode);
        var manifest = await ModulePreviewManifest.LoadAsync(manifestPath, cancellationToken);
        Assert.Equal(Repository, manifest.Producer.Repository);
        Assert.Equal(Commit, manifest.Producer.Commit);
        Assert.Equal("feat/immutable-preview", manifest.Producer.Branch);

        var contract = Assert.Single(manifest.Contracts);
        Assert.Equal("preview-producer", contract.Module);
        Assert.Equal(ContractPackageId, contract.PackageId);
        Assert.Equal(ContractVersion, contract.Version);

        Assert.Collection(
            manifest.Images,
            image =>
            {
                Assert.Equal("preview-producer-api", image.Resource);
                Assert.Equal(ModulePreviewResourceKind.Container, image.ResourceKind);
                Assert.Equal(ApiImageRepository, image.Repository);
                Assert.Equal(ApiImageDigest, image.Sha256);
            },
            image =>
            {
                Assert.Equal("preview-producer-sidecar", image.Resource);
                Assert.Equal(ModulePreviewResourceKind.Container, image.ResourceKind);
                Assert.Equal(SidecarImageRepository, image.Repository);
                Assert.Equal(SidecarImageDigest, image.Sha256);
            });

        exitCode = await PreviewTool.RunAsync(
            [
                "preview", "produce",
                "--descriptor", descriptorPath,
                "--output", Path.Combine(directory.Path, "incomplete-preview.json"),
                "--working-directory", directory.Path,
                "--git-executable", git,
                "--image", $"preview-producer-api={ApiImageRepository}@{ApiImageDigest}"
            ],
            cancellationToken);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task Produce_accepts_a_computed_contract_version_when_the_descriptor_omits_it()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = WorkflowTestDirectory.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        var descriptorPath = await WriteProducerDescriptorWithoutVersionAsync(directory, cancellationToken);
        var git = await WriteGitExecutableAsync(directory, cancellationToken);
        var manifestPath = Path.Combine(directory.Path, "module-preview.json");
        const string computedVersion = "2.0.0-preview.42";

        var exitCode = await PreviewTool.RunAsync(
            [
                "preview", "produce",
                "--descriptor", descriptorPath,
                "--contract-version", computedVersion,
                "--output", manifestPath,
                "--working-directory", directory.Path,
                "--git-executable", git,
                "--image", $"preview-producer-api={ApiImageRepository}@{ApiImageDigest}",
                "--image", $"preview-producer-sidecar={SidecarImageRepository}@{SidecarImageDigest}"
            ],
            cancellationToken);

        Assert.Equal(0, exitCode);
        var manifest = await ModulePreviewManifest.LoadAsync(manifestPath, cancellationToken);
        Assert.Equal(computedVersion, Assert.Single(manifest.Contracts).Version);
    }

    [Fact]
    public async Task Produce_treats_case_variants_of_a_GitHub_module_repository_as_owned()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = WorkflowTestDirectory.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        var descriptorPath = await WriteProducerDescriptorAsync(directory, cancellationToken);
        var git = await WriteGitExecutableAsync(directory, cancellationToken);
        var manifestPath = Path.Combine(directory.Path, "module-preview.json");

        var exitCode = await PreviewTool.RunAsync(
            [
                "preview", "produce",
                "--descriptor", descriptorPath,
                "--output", manifestPath,
                "--working-directory", directory.Path,
                "--git-executable", git,
                "--pin", $"preview-producer=https://github.com/SHIRUBASOFT/PREVIEW-PRODUCER.git@{Commit}",
                "--image", $"preview-producer-api={ApiImageRepository}@{ApiImageDigest}",
                "--image", $"preview-producer-sidecar={SidecarImageRepository}@{SidecarImageDigest}"
            ],
            cancellationToken);

        Assert.Equal(0, exitCode);
        var manifest = await ModulePreviewManifest.LoadAsync(manifestPath, cancellationToken);
        Assert.Single(manifest.Contracts);
    }

    [Fact]
    public async Task Produce_discovers_and_pushes_AppHost_images_with_named_Aspire_steps()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = WorkflowTestDirectory.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        var descriptorPath = await WriteProducerDescriptorAsync(directory, cancellationToken);
        var git = await WriteGitExecutableAsync(directory, cancellationToken);
        var imageDescriptionPath = Path.Combine(directory.Path, "module-images.json");
        await directory.WriteTextAsync(
            imageDescriptionPath,
            $$"""
            {
              "schemaVersion": 1,
              "images": [
                {
                  "module": "preview-producer",
                  "resource": "preview-producer-api",
                  "effectiveResource": "imported-api",
                  "resourceKind": "container",
                  "registry": "ghcr.io",
                  "repository": "shirubasoft/preview-producer/api",
                  "tag": "preview",
                  "digest": null,
                  "reference": "{{ApiImageRepository}}:preview",
                  "pullReference": "{{ApiImageRepository}}:preview",
                  "pushReference": "{{ApiImageRepository}}:preview",
                  "build": {
                    "command": "docker",
                    "arguments": [],
                    "workingDirectory": "{{directory.Path}}",
                    "repository": null,
                    "revision": null,
                    "step": "build-imported-api"
                  }
                },
                {
                  "module": "preview-producer",
                  "resource": "preview-producer-sidecar",
                  "effectiveResource": "imported-sidecar",
                  "resourceKind": "container",
                  "registry": "docker.io",
                  "repository": "library/nginx",
                  "tag": "preview",
                  "digest": null,
                  "reference": "{{SidecarImageRepository}}:preview",
                  "pullReference": "{{SidecarImageRepository}}:preview",
                  "pushReference": "{{SidecarImageRepository}}:preview",
                  "build": {
                    "command": "docker",
                    "arguments": [],
                    "workingDirectory": "{{directory.Path}}",
                    "repository": null,
                    "revision": null,
                    "step": "build-imported-sidecar"
                  }
                }
              ]
            }
            """,
            cancellationToken);
        var aspireLog = Path.Combine(directory.Path, "aspire-arguments.txt");
        var aspire = await directory.WriteExecutableAsync(
            "fake-aspire",
            $$"""
            #!/usr/bin/env bash
            set -euo pipefail
            printf '%s\n' "$*" >> '{{aspireLog}}'
            if [[ "$1" == "do" && "$2" == "describe-images" ]]; then
              while (( $# > 0 )); do
                if [[ "$1" == "--output-path" ]]; then
                  mkdir -p "$2"
                  cp '{{imageDescriptionPath}}' "$2/module-images.json"
                  exit 0
                fi
                shift
              done
              exit 2
            fi
            """,
            cancellationToken);
        var dockerLog = Path.Combine(directory.Path, "docker-arguments.txt");
        var docker = await directory.WriteExecutableAsync(
            "fake-docker",
            $$"""
            #!/usr/bin/env bash
            set -euo pipefail
            printf '%s\n' "$*" >> '{{dockerLog}}'
            case "$4" in
              '{{ApiImageRepository}}:preview') printf '{{ApiImageDigest}}\n' ;;
              '{{SidecarImageRepository}}:preview') printf '{{SidecarImageDigest}}\n' ;;
              *) exit 2 ;;
            esac
            """,
            cancellationToken);
        var manifestPath = Path.Combine(directory.Path, "module-preview.json");
        var appHostPath = Path.Combine(directory.Path, "Preview.AppHost.csproj");
        var artifactsDirectory = Path.Combine(directory.Path, "artifacts");

        var exitCode = await PreviewTool.RunAsync(
            [
                "preview", "produce",
                "--descriptor", descriptorPath,
                "--output", manifestPath,
                "--working-directory", directory.Path,
                "--git-executable", git,
                "--apphost", appHostPath,
                "--artifacts-directory", artifactsDirectory,
                "--aspire-executable", aspire,
                "--docker-executable", docker
            ],
            cancellationToken);

        Assert.Equal(0, exitCode);
        var aspireArguments = await File.ReadAllLinesAsync(aspireLog, cancellationToken);
        Assert.Equal(2, aspireArguments.Length);
        Assert.StartsWith("do describe-images ", aspireArguments[0], StringComparison.Ordinal);
        Assert.StartsWith(
            "do push imported-api imported-sidecar ",
            aspireArguments[1],
            StringComparison.Ordinal);
        var dockerArguments = await File.ReadAllLinesAsync(dockerLog, cancellationToken);
        Assert.All(dockerArguments, argument =>
            Assert.EndsWith("--format {{.Manifest.Digest}}", argument, StringComparison.Ordinal));
        var manifest = await ModulePreviewManifest.LoadAsync(manifestPath, cancellationToken);
        Assert.Equal(ApiImageDigest, manifest.Images[0].Sha256);
        Assert.Equal(SidecarImageDigest, manifest.Images[1].Sha256);
    }

    [Fact]
    public async Task Materialize_resolves_policy_owned_contract_images_and_GitHub_environment()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = WorkflowTestDirectory.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        var descriptorPath = await WriteProducerDescriptorAsync(directory, cancellationToken);
        var git = await WriteGitExecutableAsync(directory, cancellationToken);
        var manifestPath = Path.Combine(directory.Path, "module-preview.json");
        var produceExitCode = await PreviewTool.RunAsync(
            [
                "preview", "produce",
                "--descriptor", descriptorPath,
                "--output", manifestPath,
                "--working-directory", directory.Path,
                "--git-executable", git,
                "--image", $"preview-producer-api={ApiImageRepository}@{ApiImageDigest}",
                "--image", $"preview-producer-sidecar={SidecarImageRepository}@{SidecarImageDigest}"
            ],
            cancellationToken);
        Assert.Equal(0, produceExitCode);

        var policyPath = await WriteConsumerPolicyAsync(directory, cancellationToken);
        var packageFeed = Path.Combine(directory.Path, "preview-feed");
        var packageTemplate = Path.Combine(directory.Path, "contract-template.nupkg");
        await CreateContractPackageAsync(packageTemplate, cancellationToken);
        var expectedPackageSha256 = await ComputeSha256Async(packageTemplate, cancellationToken);
        var packagePath = Path.Combine(packageFeed, $"{ContractPackageId}.{ContractVersion}.nupkg");

        var dotnetLog = Path.Combine(directory.Path, "dotnet-arguments.txt");
        var dotnet = await directory.WriteExecutableAsync(
            "fake-dotnet",
            $$"""
            #!/usr/bin/env bash
            set -euo pipefail
            printf '%s\n' "$*" >> '{{dotnetLog}}'
            if [[ "$1" == "pack" ]]; then
              output=''
              while (( $# > 0 )); do
                if [[ "$1" == "--output" ]]; then
                  output="$2"
                  break
                fi
                shift
              done
              test -n "$output"
              mkdir -p "$output"
              cp '{{packageTemplate}}' "$output/{{ContractPackageId}}.{{ContractVersion}}.nupkg"
            fi
            """,
            cancellationToken);
        var dockerLog = Path.Combine(directory.Path, "docker-arguments.txt");
        var docker = await directory.WriteExecutableAsync(
            "fake-docker",
            $$"""
            #!/usr/bin/env bash
            set -euo pipefail
            printf '%s\n' "$*" >> '{{dockerLog}}'
            """,
            cancellationToken);
        var githubCli = await directory.WriteExecutableAsync(
            "fake-gh",
            """
            #!/usr/bin/env bash
            set -euo pipefail
            if [[ "$*" == "auth git-credential get" ]]; then
              cat >/dev/null
              printf 'username=x-access-token\npassword=hidden-test-token\n'
              exit 0
            fi
            exit 2
            """,
            cancellationToken);

        var workDirectory = Path.Combine(directory.Path, "materialization-work");
        var resolutionPath = Path.Combine(directory.Path, "resolved-preview.json");
        var githubEnvironmentPath = Path.Combine(directory.Path, "github.env");
        var materializeExitCode = await PreviewTool.RunAsync(
            [
                "preview", "materialize",
                "--manifest", manifestPath,
                "--policy", policyPath,
                "--work-directory", workDirectory,
                "--package-feed", packageFeed,
                "--resolution", resolutionPath,
                "--consumer-repository", "https://github.com/shirubasoft/preview-consumer.git",
                "--consumer-commit", BaseCommit,
                "--github-env", githubEnvironmentPath,
                "--git-executable", git,
                "--gh-executable", githubCli,
                "--dotnet-executable", dotnet,
                "--docker-executable", docker,
                "--property", "ModularAppHostsVersion=1.2.3"
            ],
            cancellationToken);

        Assert.Equal(0, materializeExitCode);
        var resolution = await ModulePreviewResolution.LoadAsync(resolutionPath, cancellationToken);
        Assert.Equal(BaseCommit, resolution.Consumer.Commit);
        var resolvedContract = Assert.Single(resolution.Contracts);
        Assert.Equal(ContractPackageId, resolvedContract.PackageId);
        Assert.Equal(ContractVersion, resolvedContract.Version);
        Assert.Equal(expectedPackageSha256, resolvedContract.Sha256);
        Assert.Equal(Path.GetFullPath(packagePath), resolvedContract.PackagePath);
        Assert.Equal(2, resolution.Images.Count);

        var gitArguments = await File.ReadAllLinesAsync(
            Path.Combine(directory.Path, "git-arguments.txt"),
            cancellationToken);
        var fetchArguments = Assert.Single(gitArguments, arguments =>
            arguments.EndsWith(
                $"fetch --quiet --no-tags --depth 1 origin {Commit}",
                StringComparison.Ordinal));
        Assert.Contains(
            $"credential.https://github.com.helper=!'{githubCli}' auth git-credential",
            fetchArguments,
            StringComparison.Ordinal);
        Assert.DoesNotContain("hidden-test-token", fetchArguments, StringComparison.Ordinal);
        Assert.Contains($"remote add origin {Repository}", gitArguments);

        var dotnetArguments = await File.ReadAllLinesAsync(dotnetLog, cancellationToken);
        Assert.Collection(
            dotnetArguments,
            restore =>
            {
                Assert.StartsWith("restore ", restore, StringComparison.Ordinal);
                Assert.Contains("-p:ModularAppHostsVersion=1.2.3", restore, StringComparison.Ordinal);
            },
            pack =>
            {
                Assert.StartsWith("pack ", pack, StringComparison.Ordinal);
                Assert.Contains($"-p:PackageVersion={ContractVersion}", pack, StringComparison.Ordinal);
                Assert.Contains($"-p:Version={ContractVersion}", pack, StringComparison.Ordinal);
                Assert.Contains("-p:ModularAppHostsVersion=1.2.3", pack, StringComparison.Ordinal);
                Assert.DoesNotContain($"--output {packageFeed}", pack, StringComparison.Ordinal);
            });

        var dockerArguments = await File.ReadAllLinesAsync(dockerLog, cancellationToken);
        Assert.Equal(
            [
                $"buildx imagetools inspect {ApiImageRepository}@{ApiImageDigest}",
                $"buildx imagetools inspect {SidecarImageRepository}@{SidecarImageDigest}"
            ],
            dockerArguments);

        var environment = await File.ReadAllLinesAsync(githubEnvironmentPath, cancellationToken);
        Assert.Equal(
            [
                $"ModulePreview__Resolution={Path.GetFullPath(resolutionPath)}",
                $"ModulePreview__PackageFeed={Path.GetFullPath(packageFeed)}",
                $"PreviewContractVersion={ContractVersion}"
            ],
            environment);
    }

    [Fact]
    public async Task Image_only_preview_materializes_without_contract_tools_or_package_feed()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = WorkflowTestDirectory.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        var descriptorPath = await WriteImageOnlyProducerDescriptorAsync(directory, cancellationToken);
        var git = await WriteGitExecutableAsync(directory, cancellationToken);
        var manifestPath = Path.Combine(directory.Path, "module-preview.json");
        var produceExitCode = await PreviewTool.RunAsync(
            [
                "preview", "produce",
                "--descriptor", descriptorPath,
                "--output", manifestPath,
                "--working-directory", directory.Path,
                "--git-executable", git,
                "--image", $"preview-producer-api={ApiImageRepository}@{ApiImageDigest}",
                "--image", $"preview-producer-sidecar={SidecarImageRepository}@{SidecarImageDigest}"
            ],
            cancellationToken);
        Assert.Equal(0, produceExitCode);
        Assert.Empty((await ModulePreviewManifest.LoadAsync(manifestPath, cancellationToken)).Contracts);
        await File.WriteAllTextAsync(
            Path.Combine(directory.Path, "git-arguments.txt"),
            string.Empty,
            cancellationToken);

        var policyPath = await WriteOptionalConsumerPolicyAsync(directory, cancellationToken);
        var dotnetMarker = Path.Combine(directory.Path, "dotnet-was-called");
        var dotnet = await directory.WriteExecutableAsync(
            "never-dotnet",
            $$"""
            #!/usr/bin/env bash
            touch '{{dotnetMarker}}'
            exit 97
            """,
            cancellationToken);
        var docker = await directory.WriteExecutableAsync(
            "fake-docker",
            """
            #!/usr/bin/env bash
            set -euo pipefail
            """,
            cancellationToken);
        var workDirectory = Path.Combine(directory.Path, "materialization-work");
        var resolutionPath = Path.Combine(directory.Path, "resolved-preview.json");
        var githubEnvironmentPath = Path.Combine(directory.Path, "github.env");

        var materializeExitCode = await PreviewTool.RunAsync(
            [
                "preview", "materialize",
                "--manifest", manifestPath,
                "--policy", policyPath,
                "--work-directory", workDirectory,
                "--resolution", resolutionPath,
                "--consumer-repository", "https://github.com/shirubasoft/preview-consumer.git",
                "--consumer-commit", BaseCommit,
                "--github-env", githubEnvironmentPath,
                "--git-executable", git,
                "--dotnet-executable", dotnet,
                "--docker-executable", docker
            ],
            cancellationToken);

        Assert.Equal(0, materializeExitCode);
        var resolution = await ModulePreviewResolution.LoadAsync(resolutionPath, cancellationToken);
        Assert.Empty(resolution.Contracts);
        Assert.Equal(2, resolution.Images.Count);
        Assert.False(File.Exists(dotnetMarker));
        Assert.Empty(await File.ReadAllLinesAsync(
            Path.Combine(directory.Path, "git-arguments.txt"),
            cancellationToken));
        Assert.Equal(
            [$"ModulePreview__Resolution={Path.GetFullPath(resolutionPath)}"],
            await File.ReadAllLinesAsync(githubEnvironmentPath, cancellationToken));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("86401")]
    [InlineData("1.5")]
    public void Materialize_rejects_command_timeouts_outside_the_bounded_integer_range(string seconds)
    {
        var exception = Assert.Throws<PreviewToolException>(() =>
            PreviewTool.ParseMaterializationCommandTimeout(seconds));

        Assert.Contains("integer from 1 through 86400", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Materialize_command_timeout_defaults_to_two_minutes_and_accepts_the_upper_bound()
    {
        Assert.Equal(TimeSpan.FromSeconds(120), PreviewTool.ParseMaterializationCommandTimeout(null));
        Assert.Equal(TimeSpan.FromSeconds(86400), PreviewTool.ParseMaterializationCommandTimeout("86400"));
    }

    [Fact]
    public async Task Materialization_command_timeout_identifies_the_failed_operation()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = WorkflowTestDirectory.Create();
        var command = await directory.WriteExecutableAsync(
            "slow-dotnet",
            """
            #!/usr/bin/env bash
            sleep 10
            """,
            TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<PreviewToolException>(() =>
            PreviewTool.RunRequiredCommandAsync(
                command,
                ["restore"],
                directory.Path,
                "restore contract 'Example.Contract'",
                TimeSpan.FromSeconds(1),
                TestContext.Current.CancellationToken));

        Assert.Contains("restore contract 'Example.Contract'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("command timeout of 1 seconds", exception.Message, StringComparison.Ordinal);
        Assert.Contains("slow-dotnet", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task External_image_producer_can_override_the_owning_module_selection()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = WorkflowTestDirectory.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        var descriptorPath = await WriteImageOnlyProducerDescriptorAsync(directory, cancellationToken);
        var git = await WriteGitExecutableAsync(directory, cancellationToken);
        var manifestPath = Path.Combine(directory.Path, "external-preview.json");

        var exitCode = await PreviewTool.RunAsync(
            [
                "preview", "produce",
                "--descriptor", descriptorPath,
                "--output", manifestPath,
                "--working-directory", directory.Path,
                "--git-executable", git,
                "--pin", $"preview-producer={ExternalModuleRepository}@{BaseCommit}",
                "--image", $"preview-producer-api={ApiImageRepository}@{ApiImageDigest}",
                "--image", $"preview-producer-sidecar={SidecarImageRepository}@{SidecarImageDigest}"
            ],
            cancellationToken);

        Assert.Equal(0, exitCode);
        var manifest = await ModulePreviewManifest.LoadAsync(manifestPath, cancellationToken);
        Assert.Equal(Repository, manifest.Producer.Repository);
        Assert.Equal(Commit, manifest.Producer.Commit);
        var module = Assert.Single(manifest.Modules);
        Assert.Equal("preview-producer", module.Name);
        Assert.Equal(ExternalModuleRepository, module.Repository);
        Assert.Equal(BaseCommit, module.Commit);
        Assert.Empty(manifest.Contracts);
        Assert.Equal(2, manifest.Images.Count);
    }

    [Fact]
    public async Task External_image_producer_cannot_offer_a_contract()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = WorkflowTestDirectory.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        var descriptorPath = await WriteProducerDescriptorAsync(directory, cancellationToken);
        var git = await WriteGitExecutableAsync(directory, cancellationToken);

        var exitCode = await PreviewTool.RunAsync(
            [
                "preview", "produce",
                "--descriptor", descriptorPath,
                "--output", Path.Combine(directory.Path, "invalid-external-preview.json"),
                "--working-directory", directory.Path,
                "--git-executable", git,
                "--pin", $"preview-producer={ExternalModuleRepository}@{BaseCommit}",
                "--pin", $"build-support={Repository}@{Commit}",
                "--image", $"preview-producer-api={ApiImageRepository}@{ApiImageDigest}",
                "--image", $"preview-producer-sidecar={SidecarImageRepository}@{SidecarImageDigest}"
            ],
            cancellationToken);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task Materialize_resolves_and_hashes_an_exact_published_contract_without_source_checkout()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = WorkflowTestDirectory.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        var descriptorPath = await WriteProducerDescriptorAsync(directory, cancellationToken);
        var produceGit = await WriteGitExecutableAsync(directory, cancellationToken);
        var manifestPath = Path.Combine(directory.Path, "module-preview.json");
        Assert.Equal(0, await PreviewTool.RunAsync(
            [
                "preview", "produce",
                "--descriptor", descriptorPath,
                "--output", manifestPath,
                "--working-directory", directory.Path,
                "--git-executable", produceGit,
                "--image", $"preview-producer-api={ApiImageRepository}@{ApiImageDigest}",
                "--image", $"preview-producer-sidecar={SidecarImageRepository}@{SidecarImageDigest}"
            ],
            cancellationToken));

        var policyPath = await WritePublishedConsumerPolicyAsync(directory, cancellationToken);
        var packageTemplate = Path.Combine(directory.Path, "published-contract.nupkg");
        await CreateContractPackageAsync(packageTemplate, cancellationToken);
        var expectedPackageSha256 = await ComputeSha256Async(packageTemplate, cancellationToken);
        var expectedPackageContentHash = await ComputeSha512Base64Async(packageTemplate, cancellationToken);
        var dotnetLog = Path.Combine(directory.Path, "dotnet-arguments.txt");
        var dotnet = await directory.WriteExecutableAsync(
            "fake-published-dotnet",
            $$"""
            #!/usr/bin/env bash
            set -euo pipefail
            printf '%s\n' "$*" >> '{{dotnetLog}}'
            test "$1" = 'restore'
            packages=''
            while (( $# > 0 )); do
              if [[ "$1" == '--packages' ]]; then
                packages="$2"
                break
              fi
              shift
            done
            test -n "$packages"
            target="$packages/{{ContractPackageId.ToLowerInvariant()}}/{{ContractVersion}}"
            mkdir -p "$target"
            cp '{{packageTemplate}}' "$target/{{ContractPackageId.ToLowerInvariant()}}.{{ContractVersion}}.nupkg"
            printf '%s\n' \
              '{"version":2,"contentHash":"{{expectedPackageContentHash}}","source":"https://nuget.pkg.github.com/shirubasoft/index.json"}' \
              > "$target/.nupkg.metadata"
            """,
            cancellationToken);
        var gitMarker = Path.Combine(directory.Path, "git-was-called");
        var git = await directory.WriteExecutableAsync(
            "never-git",
            $$"""
            #!/usr/bin/env bash
            touch '{{gitMarker}}'
            exit 98
            """,
            cancellationToken);
        var docker = await directory.WriteExecutableAsync(
            "fake-docker",
            """
            #!/usr/bin/env bash
            set -euo pipefail
            """,
            cancellationToken);
        var workDirectory = Path.Combine(directory.Path, "materialization-work");
        var packageFeed = Path.Combine(directory.Path, "preview-feed");
        var resolutionPath = Path.Combine(directory.Path, "resolved-preview.json");
        var githubEnvironmentPath = Path.Combine(directory.Path, "github.env");

        var materializeExitCode = await PreviewTool.RunAsync(
            [
                "preview", "materialize",
                "--manifest", manifestPath,
                "--policy", policyPath,
                "--work-directory", workDirectory,
                "--package-feed", packageFeed,
                "--resolution", resolutionPath,
                "--consumer-repository", "https://github.com/shirubasoft/preview-consumer.git",
                "--consumer-commit", BaseCommit,
                "--github-env", githubEnvironmentPath,
                "--git-executable", git,
                "--dotnet-executable", dotnet,
                "--docker-executable", docker
            ],
            cancellationToken);

        Assert.Equal(0, materializeExitCode);
        var resolution = await ModulePreviewResolution.LoadAsync(resolutionPath, cancellationToken);
        var contract = Assert.Single(resolution.Contracts);
        Assert.Equal("https://nuget.pkg.github.com/shirubasoft/index.json", contract.Source);
        Assert.Equal(expectedPackageSha256, contract.Sha256);
        Assert.True(File.Exists(contract.PackagePath));
        Assert.False(File.Exists(gitMarker));
        var restore = Assert.Single(await File.ReadAllLinesAsync(dotnetLog, cancellationToken));
        Assert.StartsWith("restore ", restore, StringComparison.Ordinal);
        Assert.DoesNotContain("--source", restore, StringComparison.Ordinal);
        var resolverProject = Assert.Single(
            Directory.GetFiles(workDirectory, "ContractResolver.csproj", SearchOption.AllDirectories));
        Assert.Contains(
            $"Version=\"[{ContractVersion}]\"",
            await File.ReadAllTextAsync(resolverProject, cancellationToken),
            StringComparison.Ordinal);
    }

    private static async Task<string> WriteProducerDescriptorAsync(
        WorkflowTestDirectory directory,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory.Path, "module-preview.producer.json");
        await directory.WriteTextAsync(
            path,
            $$"""
            {
              "schemaVersion": 1,
              "module": "preview-producer",
              "contract": {
                "packageId": "{{ContractPackageId}}",
                "version": "{{ContractVersion}}"
              },
              "images": [
                {
                  "resource": "preview-producer-api",
                  "resourceKind": "container",
                  "repository": "{{ApiImageRepository}}",
                  "required": true
                },
                {
                  "resource": "preview-producer-sidecar",
                  "resourceKind": "container",
                  "repository": "{{SidecarImageRepository}}",
                  "required": true
                }
              ]
            }
            """,
            cancellationToken);
        return path;
    }

    private static Task<string> WriteProducerDescriptorWithoutVersionAsync(
        WorkflowTestDirectory directory,
        CancellationToken cancellationToken) =>
        WriteProducerDescriptorVariantAsync(directory, includeContract: true, cancellationToken);

    private static Task<string> WriteImageOnlyProducerDescriptorAsync(
        WorkflowTestDirectory directory,
        CancellationToken cancellationToken) =>
        WriteProducerDescriptorVariantAsync(directory, includeContract: false, cancellationToken);

    private static async Task<string> WriteProducerDescriptorVariantAsync(
        WorkflowTestDirectory directory,
        bool includeContract,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory.Path, $"module-preview-{includeContract}.producer.json");
        var contract = includeContract
            ? $$"""
                "contract": {
                  "packageId": "{{ContractPackageId}}"
                },
                """
            : string.Empty;
        await directory.WriteTextAsync(
            path,
            $$"""
            {
              "schemaVersion": 1,
              "module": "preview-producer",
              {{contract}}
              "images": [
                {
                  "resource": "preview-producer-api",
                  "resourceKind": "container",
                  "repository": "{{ApiImageRepository}}",
                  "required": true
                },
                {
                  "resource": "preview-producer-sidecar",
                  "resourceKind": "container",
                  "repository": "{{SidecarImageRepository}}",
                  "required": true
                }
              ]
            }
            """,
            cancellationToken);
        return path;
    }

    private static async Task<string> WriteConsumerPolicyAsync(
        WorkflowTestDirectory directory,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory.Path, "module-preview-policy.json");
        await directory.WriteTextAsync(
            path,
            $$"""
            {
              "schemaVersion": 2,
              "modules": [
                {
                  "module": "preview-producer",
                  "repository": "{{Repository}}",
                  "contract": {
                    "packageId": "{{ContractPackageId}}",
                    "versionEnvironment": "PreviewContractVersion",
                    "sourceFallback": {
                      "enabled": true,
                      "project": "src/PreviewProducer.Contract/PreviewProducer.Contract.csproj"
                    },
                    "allowedPackProperties": ["ModularAppHostsVersion"]
                  },
                  "images": [
                    {
                      "resource": "preview-producer-api",
                      "resourceKind": "container",
                      "repositories": ["{{ApiImageRepository}}"],
                      "required": true
                    },
                    {
                      "resource": "preview-producer-sidecar",
                      "resourceKind": "container",
                      "repositories": ["{{SidecarImageRepository}}"],
                      "required": true
                    }
                  ]
                }
              ]
            }
            """,
            cancellationToken);
        return path;
    }

    private static async Task<string> WriteOptionalConsumerPolicyAsync(
        WorkflowTestDirectory directory,
        CancellationToken cancellationToken)
    {
        var path = await WriteConsumerPolicyAsync(directory, cancellationToken);
        var json = await File.ReadAllTextAsync(path, cancellationToken);
        await directory.WriteTextAsync(
            path,
            json.Replace(
                $"\"versionEnvironment\": \"PreviewContractVersion\",",
                $"\"versionEnvironment\": \"PreviewContractVersion\",{Environment.NewLine}        \"required\": false,",
                StringComparison.Ordinal),
            cancellationToken);
        return path;
    }

    private static async Task<string> WritePublishedConsumerPolicyAsync(
        WorkflowTestDirectory directory,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory.Path, "module-preview-published-policy.json");
        await directory.WriteTextAsync(
            path,
            $$"""
            {
              "schemaVersion": 2,
              "modules": [
                {
                  "module": "preview-producer",
                  "repository": "{{Repository}}",
                  "contract": {
                    "packageId": "{{ContractPackageId}}",
                    "versionEnvironment": "PreviewContractVersion",
                    "required": false,
                    "published": {
                      "source": "https://nuget.pkg.github.com/shirubasoft/index.json"
                    },
                    "sourceFallback": {
                      "enabled": false
                    }
                  },
                  "images": [
                    {
                      "resource": "preview-producer-api",
                      "resourceKind": "container",
                      "repositories": ["{{ApiImageRepository}}"],
                      "required": true
                    },
                    {
                      "resource": "preview-producer-sidecar",
                      "resourceKind": "container",
                      "repositories": ["{{SidecarImageRepository}}"],
                      "required": true
                    }
                  ]
                }
              ]
            }
            """,
            cancellationToken);
        return path;
    }

    [UnsupportedOSPlatform("windows")]
    private static Task<string> WriteGitExecutableAsync(
        WorkflowTestDirectory directory,
        CancellationToken cancellationToken) =>
        directory.WriteExecutableAsync(
            "fake-git",
            $$"""
            #!/usr/bin/env bash
            set -euo pipefail
            printf '%s\n' "$*" >> '{{Path.Combine(directory.Path, "git-arguments.txt")}}'
            case "$1" in
              init)
                checkout="$3"
                mkdir -p "$checkout/src/PreviewProducer.Contract"
                printf '<Project Sdk="Microsoft.NET.Sdk"></Project>\n' \
                  > "$checkout/src/PreviewProducer.Contract/PreviewProducer.Contract.csproj"
                ;;
              rev-parse)
                if [[ "${2:-}" == "--show-toplevel" ]]; then
                  printf '{{directory.Path}}\n'
                else
                  printf '{{Commit}}\n'
                fi
                ;;
              ls-files) exit 1 ;;
              status) exit 0 ;;
              symbolic-ref) printf 'feat/immutable-preview\n' ;;
              remote)
                if [[ "${2:-}" == "get-url" ]]; then
                  printf '{{Repository}}\n'
                fi
                ;;
              ls-remote)
                printf 'ref: refs/heads/main\tHEAD\n'
                printf '{{BaseCommit}}\tHEAD\n'
                printf '{{Commit}}\trefs/heads/feat/immutable-preview\n'
                ;;
              fetch) ;;
              -c) ;;
              *) exit 2 ;;
            esac
            """,
            cancellationToken);

    private static async Task CreateContractPackageAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
            var entry = archive.CreateEntry($"{ContractPackageId}.nuspec");
            await using var entryStream = entry.Open();
            var nuspec = Encoding.UTF8.GetBytes(
                $"<package><metadata><id>{ContractPackageId}</id><version>{ContractVersion}</version></metadata></package>");
            await entryStream.WriteAsync(nuspec, cancellationToken);
        }
        finally
        {
            await stream.DisposeAsync();
        }
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            var digest = await SHA256.HashDataAsync(stream, cancellationToken);
            return Convert.ToHexStringLower(digest);
        }
        finally
        {
            await stream.DisposeAsync();
        }
    }

    private static async Task<string> ComputeSha512Base64Async(
        string path,
        CancellationToken cancellationToken)
    {
        var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            var digest = await SHA512.HashDataAsync(stream, cancellationToken);
            return Convert.ToBase64String(digest);
        }
        finally
        {
            await stream.DisposeAsync();
        }
    }

    private sealed class WorkflowTestDirectory : IDisposable
    {
        private WorkflowTestDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static WorkflowTestDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"aspire-module-preview-workflow-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new WorkflowTestDirectory(path);
        }

        public async Task WriteTextAsync(
            string path,
            string content,
            CancellationToken cancellationToken)
        {
            await File.WriteAllTextAsync(
                path,
                content.Replace("\r\n", "\n", StringComparison.Ordinal),
                cancellationToken);
        }

        [UnsupportedOSPlatform("windows")]
        public async Task<string> WriteExecutableAsync(
            string name,
            string content,
            CancellationToken cancellationToken)
        {
            var path = System.IO.Path.Combine(Path, name);
            await WriteTextAsync(path, content, cancellationToken);
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return path;
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
