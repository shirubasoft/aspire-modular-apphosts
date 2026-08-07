using System.Text.Json;
using System.Runtime.Versioning;
using Aspire.Hosting.ModularAppHosts;
using Xunit;

namespace Shirubasoft.Aspire.ModularAppHosts.Tool.Tests;

public sealed class PreviewDescriptorCommandTests
{
    private const string ContractPackageId = "Shirubasoft.Sample.Contract";

    [Fact]
    public async Task Generate_producer_derives_contract_and_selected_publishers_from_the_AppHost()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = TemporaryDirectory.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        var imageDocument = await WriteImageDocumentAsync(directory, cancellationToken);
        var aspire = await WriteAspireExecutableAsync(directory, imageDocument, cancellationToken);
        var output = Path.Combine(directory.Path, "module-preview.producer.json");

        var exitCode = await PreviewTool.RunAsync(
            [
                "preview", "descriptor", "generate", "producer",
                "--apphost", Path.Combine(directory.Path, "Sample.AppHost.csproj"),
                "--module", "SAMPLE",
                "--resource", "imported-worker",
                "--output", output,
                "--working-directory", directory.Path,
                "--aspire-executable", aspire,
                "--contract-version", "2.1.0-preview.4"
            ],
            cancellationToken);

        Assert.Equal(0, exitCode);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(output, cancellationToken));
        var root = json.RootElement;
        Assert.Equal(ModulePreviewProducerDescriptor.SchemaUri, root.GetProperty("$schema").GetString());
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("sample", root.GetProperty("module").GetString());
        Assert.Equal(ContractPackageId, root.GetProperty("contract").GetProperty("packageId").GetString());
        Assert.Equal("2.1.0-preview.4", root.GetProperty("contract").GetProperty("version").GetString());
        var image = Assert.Single(root.GetProperty("images").EnumerateArray());
        Assert.Equal("worker", image.GetProperty("resource").GetString());
        Assert.Equal("project", image.GetProperty("resourceKind").GetString());
        Assert.Equal("registry.example.test/team/worker", image.GetProperty("repository").GetString());
        Assert.True(image.GetProperty("required").GetBoolean());
        Assert.EndsWith("\n", await File.ReadAllTextAsync(output, cancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Generate_producer_checks_semantic_drift_and_protects_existing_files()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = TemporaryDirectory.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        var imageDocument = await WriteImageDocumentAsync(directory, cancellationToken);
        var aspire = await WriteAspireExecutableAsync(directory, imageDocument, cancellationToken);
        var output = Path.Combine(directory.Path, "module-preview.producer.json");
        var baseArguments = new[]
        {
            "preview", "descriptor", "generate", "producer",
            "--apphost", Path.Combine(directory.Path, "Sample.AppHost.csproj"),
            "--module", "sample",
            "--output", output,
            "--working-directory", directory.Path,
            "--aspire-executable", aspire
        };

        Assert.Equal(0, await PreviewTool.RunAsync(baseArguments, cancellationToken));
        Assert.Equal(1, await PreviewTool.RunAsync(baseArguments, cancellationToken));
        Assert.Equal(0, await PreviewTool.RunAsync([.. baseArguments, "--check"], cancellationToken));

        var text = await File.ReadAllTextAsync(output, cancellationToken);
        await File.WriteAllTextAsync(
            output,
            text.Replace("registry.example.test/team/api", "registry.example.test/team/drift", StringComparison.Ordinal),
            cancellationToken);

        Assert.Equal(1, await PreviewTool.RunAsync([.. baseArguments, "--check"], cancellationToken));
        Assert.Equal(0, await PreviewTool.RunAsync([.. baseArguments, "--force"], cancellationToken));
        Assert.Equal(0, await PreviewTool.RunAsync([.. baseArguments, "--check"], cancellationToken));
    }

    [Fact]
    public async Task Generate_producer_supports_contract_only_modules()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = TemporaryDirectory.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        var imageDocument = await WriteImageDocumentAsync(directory, cancellationToken);
        var aspire = await WriteAspireExecutableAsync(directory, imageDocument, cancellationToken);
        var output = Path.Combine(directory.Path, "contract-only.producer.json");

        var exitCode = await PreviewTool.RunAsync(
            [
                "preview", "descriptor", "generate", "producer",
                "--apphost", Path.Combine(directory.Path, "Sample.AppHost.csproj"),
                "--module", "contract-only",
                "--output", output,
                "--working-directory", directory.Path,
                "--aspire-executable", aspire,
                "--contract-version", "1.2.3"
            ],
            cancellationToken);

        Assert.Equal(0, exitCode);
        var descriptor = await ModulePreviewProducerDescriptor.LoadAsync(output, cancellationToken);
        Assert.Equal("Shirubasoft.ContractOnly", descriptor.Contract?.PackageId);
        Assert.Equal("1.2.3", descriptor.Contract?.Version);
        Assert.Empty(descriptor.Images);
    }

    [Fact]
    public async Task Generate_producer_rejects_unknown_resources_and_modules_without_artifacts()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = TemporaryDirectory.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        var imageDocument = await WriteImageDocumentAsync(directory, cancellationToken);
        var aspire = await WriteAspireExecutableAsync(directory, imageDocument, cancellationToken);

        var unknownExitCode = await RunGenerateAsync(
            directory,
            aspire,
            ["--resource", "missing"],
            cancellationToken);
        var emptyExitCode = await PreviewTool.RunAsync(
            [
                "preview", "descriptor", "generate", "producer",
                "--apphost", Path.Combine(directory.Path, "Sample.AppHost.csproj"),
                "--module", "empty",
                "--output", Path.Combine(directory.Path, "empty.producer.json"),
                "--working-directory", directory.Path,
                "--aspire-executable", aspire
            ],
            cancellationToken);

        Assert.Equal(1, unknownExitCode);
        Assert.Equal(1, emptyExitCode);
    }

    private static Task<int> RunGenerateAsync(
        TemporaryDirectory directory,
        string aspire,
        IReadOnlyList<string> extraArguments,
        CancellationToken cancellationToken) =>
        PreviewTool.RunAsync(
            [
                "preview", "descriptor", "generate", "producer",
                "--apphost", Path.Combine(directory.Path, "Sample.AppHost.csproj"),
                "--module", "sample",
                "--output", Path.Combine(directory.Path, $"descriptor-{Guid.NewGuid():N}.json"),
                "--working-directory", directory.Path,
                "--aspire-executable", aspire,
                .. extraArguments
            ],
            cancellationToken);

    private static async Task<string> WriteImageDocumentAsync(
        TemporaryDirectory directory,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory.Path, "source-module-images.json");
        var document = new ModuleImageDescriptionDocument();
        document.Modules.Add(new ModuleImageModuleDescription
        {
            Name = "sample",
            ContractPackageId = ContractPackageId
        });
        document.Modules.Add(new ModuleImageModuleDescription
        {
            Name = "contract-only",
            ContractPackageId = "Shirubasoft.ContractOnly"
        });
        document.Modules.Add(new ModuleImageModuleDescription { Name = "empty" });
        document.Modules.Add(new ModuleImageModuleDescription
        {
            Name = "other-module",
            ContractPackageId = "Shirubasoft.Other.Module"
        });
        document.Images.Add(CreateImage(
            "api",
            "imported-api",
            ModulePreviewResourceKind.Container,
            "registry.example.test/team/api:preview"));
        document.Images.Add(CreateImage(
            "worker",
            "imported-worker",
            ModulePreviewResourceKind.Project,
            "registry.example.test/team/worker:preview"));
        document.Images.Add(new ModuleImageDescription
        {
            Module = "sample",
            Resource = "dependency",
            EffectiveResource = "imported-dependency",
            ResourceKind = ModulePreviewResourceKind.Container,
            Registry = "registry.example.test",
            Repository = "library/dependency",
            Tag = "latest",
            Reference = "registry.example.test/library/dependency:latest",
            PullReference = "registry.example.test/library/dependency:latest"
        });
        document.Images.Add(CreateImage(
            "other",
            "other",
            ModulePreviewResourceKind.Container,
            "registry.example.test/team/other:preview",
            module: "other-module"));
        await document.SaveAsync(path, cancellationToken);
        return path;
    }

    private static ModuleImageDescription CreateImage(
        string resource,
        string effectiveResource,
        ModulePreviewResourceKind kind,
        string reference,
        string module = "sample")
    {
        var separator = reference.LastIndexOf(':');
        var repository = reference["registry.example.test/".Length..separator];
        return new ModuleImageDescription
        {
            Module = module,
            Resource = resource,
            EffectiveResource = effectiveResource,
            ResourceKind = kind,
            Registry = "registry.example.test",
            Repository = repository,
            Tag = reference[(separator + 1)..],
            Reference = reference,
            PullReference = reference,
            PushReference = reference,
            Build = new ModuleImageBuildDescription
            {
                Command = "docker",
                WorkingDirectory = "/workspace",
                Step = $"build-{effectiveResource}"
            }
        };
    }

    [UnsupportedOSPlatform("windows")]
    private static async Task<string> WriteAspireExecutableAsync(
        TemporaryDirectory directory,
        string imageDocument,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory.Path, "fake-aspire");
        await File.WriteAllTextAsync(
            path,
            $$"""
            #!/usr/bin/env bash
            set -euo pipefail
            while (( $# > 0 )); do
              if [[ "$1" == "--output-path" ]]; then
                mkdir -p "$2"
                cp '{{imageDocument}}' "$2/module-images.json"
                exit 0
              fi
              shift
            done
            exit 2
            """,
            cancellationToken);
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path) => Path = path;

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"modular-apphosts-descriptor-tests-{Guid.NewGuid():N}");
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
}
