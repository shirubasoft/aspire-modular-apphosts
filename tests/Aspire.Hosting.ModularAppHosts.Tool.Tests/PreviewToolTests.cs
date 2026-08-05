using System.Text.Json;
using System.Runtime.Versioning;
using Aspire.Hosting.ModularAppHosts;
using Xunit;

namespace Shirubasoft.Aspire.ModularAppHosts.Tool.Tests;

public sealed class PreviewToolTests
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private const string BaseCommit = "89abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task Export_can_refresh_its_untracked_output_and_writes_sorted_pins()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = TestDirectory.Create();
        var output = Path.Combine(directory.Path, "preview.json");
        var git = directory.WriteExecutable("fake-git", $$"""
            #!/usr/bin/env bash
            set -euo pipefail
            case "$1" in
              status)
                if [[ -f '{{output}}' ]]; then
                  printf '?? preview.json\0'
                fi
                ;;
              symbolic-ref) printf 'feat/preview\n' ;;
              rev-parse)
                if [[ "${2:-}" == "--show-toplevel" ]]; then
                  printf '{{directory.Path}}\n'
                else
                  printf '{{Commit}}\n'
                fi
                ;;
              ls-files) exit 1 ;;
              remote) printf 'git@github.com:shirubasoft/repo-c.git\n' ;;
              ls-remote)
                printf 'ref: refs/heads/main\tHEAD\n'
                printf '{{BaseCommit}}\tHEAD\n'
                printf '{{Commit}}\trefs/heads/feat/preview\n'
                ;;
              *) exit 2 ;;
            esac
            """);

        var exitCode = await PreviewTool.RunAsync(
            [
                "preview", "export",
                "--module", "module-c",
                "--output", output,
                "--working-directory", directory.Path,
                "--git-executable", git,
                "--pin", $"module-a=https://github.com/shirubasoft/repo-a.git@{BaseCommit}"
            ],
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        var manifest = await ModulePreviewManifest.LoadAsync(output, TestContext.Current.CancellationToken);
        Assert.Equal("https://github.com/shirubasoft/repo-c.git", manifest.Producer.Repository);
        Assert.Equal("feat/preview", manifest.Producer.Branch);
        Assert.False(manifest.Producer.Dirty);
        Assert.Equal(["module-a", "module-c"], manifest.Modules.Select(module => module.Name));

        exitCode = await PreviewTool.RunAsync(
            [
                "preview", "export",
                "--module", "module-c",
                "--output", output,
                "--working-directory", directory.Path,
                "--git-executable", git
            ],
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task Export_rejects_dirty_worktrees()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = TestDirectory.Create();
        var git = directory.WriteExecutable("dirty-git", """
            #!/usr/bin/env bash
            case "$1" in
              rev-parse) pwd ;;
              ls-files) exit 1 ;;
              status) printf ' M source.cs\0' ;;
              *) exit 2 ;;
            esac
            """);

        var exitCode = await PreviewTool.RunAsync(
            [
                "preview", "export",
                "--module", "module-c",
                "--output", Path.Combine(directory.Path, "preview.json"),
                "--working-directory", directory.Path,
                "--git-executable", git
            ],
            TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task Export_rejects_a_tracked_manifest_output()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = TestDirectory.Create();
        var git = directory.WriteExecutable("tracked-output-git", """
            #!/usr/bin/env bash
            case "$1" in
              rev-parse) pwd ;;
              ls-files) exit 0 ;;
              *) exit 2 ;;
            esac
            """);

        var exitCode = await PreviewTool.RunAsync(
            [
                "preview", "export",
                "--module", "module-c",
                "--output", Path.Combine(directory.Path, "preview.json"),
                "--working-directory", directory.Path,
                "--git-executable", git
            ],
            TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task Export_rejects_non_GitHub_SCP_remotes_instead_of_changing_their_identity()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = TestDirectory.Create();
        var git = directory.WriteExecutable("scp-remote-git", $$"""
            #!/usr/bin/env bash
            case "$1" in
              rev-parse)
                if [[ "${2:-}" == "--show-toplevel" ]]; then
                  pwd
                else
                  printf '{{Commit}}\n'
                fi
                ;;
              ls-files) exit 1 ;;
              status) exit 0 ;;
              symbolic-ref) printf 'feat/preview\n' ;;
              remote) printf 'git@gitlab.com:shirubasoft/repo-c.git\n' ;;
              *) exit 2 ;;
            esac
            """);

        var exitCode = await PreviewTool.RunAsync(
            [
                "preview", "export",
                "--module", "module-c",
                "--output", Path.Combine(directory.Path, "preview.json"),
                "--working-directory", directory.Path,
                "--git-executable", git
            ],
            TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task Trigger_posts_typed_manifest_input_to_explicit_trusted_ref()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = TestDirectory.Create();
        var manifestPath = Path.Combine(directory.Path, "preview.json");
        var manifest = new ModulePreviewManifest
        {
            Producer = new ModulePreviewProducer
            {
                Repository = "https://github.com/shirubasoft/repo-c.git",
                Commit = Commit,
                Dirty = false
            }
        };
        manifest.Modules.Add(new ModulePreviewSelection
        {
            Name = "module-c",
            Repository = "https://github.com/shirubasoft/repo-c.git",
            Commit = Commit
        });
        manifest.Images.Add(new ModulePreviewImageArtifact
        {
            Module = "module-c",
            Resource = "module-c-api",
            ResourceKind = ModulePreviewResourceKind.Container,
            Repository = "ghcr.io/shirubasoft/module-c",
            Sha256 = $"sha256:{new string('a', 64)}"
        });
        await manifest.SaveAsync(manifestPath, TestContext.Current.CancellationToken);
        var argumentsPath = Path.Combine(directory.Path, "arguments.txt");
        var bodyPath = Path.Combine(directory.Path, "body.json");
        var gh = directory.WriteExecutable("fake-gh", $$"""
            #!/usr/bin/env bash
            set -euo pipefail
            printf '%s\n' "$@" > '{{argumentsPath}}'
            tee '{{bodyPath}}' >/dev/null
            printf '{"workflow_run_id":42,"html_url":"https://github.com/shirubasoft/repo-d/actions/runs/42"}'
            """);

        var exitCode = await PreviewTool.RunAsync(
            [
                "preview", "trigger",
                "--manifest", manifestPath,
                "--repo", "shirubasoft/repo-d",
                "--workflow", "preview-e2e.yml",
                "--ref", "main",
                "--input", $"library_commit={BaseCommit}",
                "--gh-executable", gh
            ],
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        var arguments = await File.ReadAllLinesAsync(argumentsPath, TestContext.Current.CancellationToken);
        Assert.Contains("repos/shirubasoft/repo-d/actions/workflows/preview-e2e.yml/dispatches", arguments);
        using var body = JsonDocument.Parse(
            await File.ReadAllTextAsync(bodyPath, TestContext.Current.CancellationToken));
        Assert.Equal("main", body.RootElement.GetProperty("ref").GetString());
        Assert.True(body.RootElement.GetProperty("return_run_details").GetBoolean());
        Assert.Equal(
            BaseCommit,
            body.RootElement.GetProperty("inputs").GetProperty("library_commit").GetString());
        var manifestJson = body.RootElement.GetProperty("inputs").GetProperty("manifest_json").GetString();
        Assert.NotNull(manifestJson);
        using var dispatchedManifest = JsonDocument.Parse(manifestJson);
        Assert.Equal(Commit, dispatchedManifest.RootElement.GetProperty("producer").GetProperty("commit").GetString());
        Assert.Equal(
            "container",
            dispatchedManifest.RootElement.GetProperty("images")[0].GetProperty("resourceKind").GetString());
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
                $"aspire-module-preview-tool-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TestDirectory(path);
        }

        [UnsupportedOSPlatform("windows")]
        public string WriteExecutable(string name, string content)
        {
            var path = System.IO.Path.Combine(Path, name);
            File.WriteAllText(path, content.Replace("\r\n", "\n", StringComparison.Ordinal));
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return path;
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
