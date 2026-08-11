using System.Text;

namespace Aspire.Hosting.ModularAppHosts;

internal static class ManagedRepositoryIsolation
{
    internal const string BoundaryContents = "<Project />\n";
    internal const string ResponseBoundaryContents = "# Managed repository boundary.\n";

    private static readonly (string FileName, string Contents)[] BoundaryFiles =
    [
        ("Directory.Build.props", BoundaryContents),
        ("Directory.Build.targets", BoundaryContents),
        ("Directory.Packages.props", BoundaryContents),
        ("Directory.Build.rsp", ResponseBoundaryContents)
    ];

    public static void EnsureBoundary(string repositoryBasePath)
    {
        Directory.CreateDirectory(repositoryBasePath);

        foreach (var (fileName, contents) in BoundaryFiles)
        {
            CreateBoundaryFileIfMissing(Path.Combine(repositoryBasePath, fileName), contents);
        }
    }

    private static void CreateBoundaryFileIfMissing(string path, string contents)
    {
        if (File.Exists(path))
        {
            return;
        }

        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"Unable to determine the directory for '{path}'.");
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(
                temporaryPath,
                contents,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            try
            {
                File.Move(temporaryPath, path);
            }
            catch (IOException) when (File.Exists(path))
            {
            }
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
