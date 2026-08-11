using System.Text;

namespace Aspire.Hosting.ModularAppHosts;

internal static class ManagedRepositoryIsolation
{
    internal const string BoundaryContents = "<Project />\n";

    private static readonly string[] BoundaryFiles =
    [
        "Directory.Build.props",
        "Directory.Build.targets",
        "Directory.Packages.props"
    ];

    public static void EnsureBoundary(string repositoryBasePath)
    {
        Directory.CreateDirectory(repositoryBasePath);

        foreach (var fileName in BoundaryFiles)
        {
            CreateBoundaryFileIfMissing(Path.Combine(repositoryBasePath, fileName));
        }
    }

    private static void CreateBoundaryFileIfMissing(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read);
            using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.Write(BoundaryContents);
        }
        catch (IOException) when (File.Exists(path))
        {
        }
    }
}
