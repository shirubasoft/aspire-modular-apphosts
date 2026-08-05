namespace Aspire.Hosting.ModularAppHosts.Tests;

internal sealed class TemporaryDirectory : IDisposable
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
            $"aspire-modular-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return new TemporaryDirectory(path);
    }

    public void Dispose()
    {
        const int maximumAttempts = 5;
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            if (!Directory.Exists(Path))
            {
                return;
            }

            try
            {
                NormalizeAttributes();
                Directory.Delete(Path, recursive: true);
                return;
            }
            catch (Exception exception) when (
                attempt < maximumAttempts &&
                exception is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(50 * attempt));
            }
        }
    }

    private void NormalizeAttributes()
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        foreach (var entry in Directory.EnumerateFileSystemEntries(Path, "*", options))
        {
            File.SetAttributes(entry, FileAttributes.Normal);
        }

        File.SetAttributes(Path, FileAttributes.Normal);
    }
}
