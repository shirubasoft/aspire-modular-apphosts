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
        Assert.Contains(
            $"fetch --quiet --no-tags --depth 1 origin {Commit}",
            gitArguments);
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

    private static async Task<string> WriteConsumerPolicyAsync(
        WorkflowTestDirectory directory,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory.Path, "module-preview-policy.json");
        await directory.WriteTextAsync(
            path,
            $$"""
            {
              "schemaVersion": 1,
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
