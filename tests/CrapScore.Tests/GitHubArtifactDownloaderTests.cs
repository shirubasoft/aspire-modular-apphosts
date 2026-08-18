using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Xunit;

namespace CrapScore.Tests;

public sealed class GitHubArtifactDownloaderTests
{
    [Fact]
    public void SelectArtifactDownloadUrl_requires_exact_artifact_and_commit()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "artifacts": [
                {
                  "name": "crap-score-target-sha",
                  "expired": false,
                  "created_at": "2026-08-18T10:00:00Z",
                  "archive_download_url": "https://api.example.test/artifacts/wrong-commit",
                  "workflow_run": { "head_sha": "other-sha" }
                },
                {
                  "name": "crap-score-target-sha",
                  "expired": false,
                  "created_at": "2026-08-18T11:00:00Z",
                  "archive_download_url": "https://api.example.test/artifacts/matching",
                  "workflow_run": { "head_sha": "target-sha" }
                }
              ]
            }
            """);

        var url = GitHubArtifactDownloader.SelectArtifactDownloadUrl(
            document,
            "crap-score-target-sha",
            "target-sha");

        Assert.Equal("https://api.example.test/artifacts/matching", url);
    }

    [Fact]
    public async Task ExtractArchiveAsync_rejects_paths_outside_destination()
    {
        await using var archiveStream = new MemoryStream();
        using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("../outside.txt");
            await using var entryStream = entry.Open();
            await entryStream.WriteAsync(
                Encoding.UTF8.GetBytes("unsafe"),
                TestContext.Current.CancellationToken);
        }

        archiveStream.Position = 0;
        var destination = Path.Combine(Path.GetTempPath(), "CrapScore.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(
                () => GitHubArtifactDownloader.ExtractArchiveAsync(
                    archiveStream,
                    destination,
                    TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, recursive: true);
            }
        }
    }
}
