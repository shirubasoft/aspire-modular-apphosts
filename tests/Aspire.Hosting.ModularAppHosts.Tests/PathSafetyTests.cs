using Aspire.Hosting.ModularAppHosts;
using Xunit;

namespace Aspire.Hosting.ModularAppHosts.Tests;

public sealed class PathSafetyTests
{
    [Fact]
    public void Containment_uses_the_operating_system_path_comparison()
    {
        using var directory = TemporaryDirectory.Create();
        var root = Path.Combine(directory.Path, "Module");
        Directory.CreateDirectory(root);
        var differentlyCasedPath = Path.Combine(directory.Path, "module", "service.csproj");

        Assert.Equal(OperatingSystem.IsWindows(), PathSafety.IsContainedBy(root, differentlyCasedPath));
    }

    [Fact]
    public void Containment_rejects_parent_traversal()
    {
        using var directory = TemporaryDirectory.Create();
        var root = Path.Combine(directory.Path, "module");
        Directory.CreateDirectory(root);

        Assert.False(PathSafety.IsContainedBy(root, Path.Combine(root, "..", "outside.csproj")));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PathSafety.GetContainedPath(root, "../outside.csproj", "path"));
    }

    [Fact]
    public void Containment_rejects_a_symbolic_link_that_escapes_the_repository()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = TemporaryDirectory.Create();
        var root = Path.Combine(directory.Path, "module");
        var outside = Path.Combine(directory.Path, "outside");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(root, "linked"), outside);

        Assert.False(PathSafety.IsContainedBy(root, Path.Combine(root, "linked", "service.csproj")));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"aspire-path-safety-{Guid.NewGuid():N}");
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
