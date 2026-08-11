namespace Aspire.Hosting.Testing;

internal static class TestEndpointHealthPath
{
    public static bool IsRootRelative(string path)
    {
        return path.Length > 0 &&
            path[0] == '/' &&
            (path.Length == 1 || path[1] is not ('/' or '\\')) &&
            Uri.IsWellFormedUriString(path, UriKind.Relative);
    }
}
