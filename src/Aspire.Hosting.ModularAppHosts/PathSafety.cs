namespace Aspire.Hosting.ModularAppHosts;

internal static class PathSafety
{
    public static StringComparer Comparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static bool AreEqual(string? first, string? second)
    {
        return first is not null && second is not null &&
            string.Equals(Normalize(first), Normalize(second), PathComparison);
    }

    public static bool IsContainedBy(string root, string path)
    {
        var fullRoot = Normalize(root);
        var candidate = Normalize(path);
        var relativePath = Path.GetRelativePath(fullRoot, candidate);
        if (Path.IsPathRooted(relativePath) ||
            relativePath.Equals("..", PathComparison) ||
            relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", PathComparison) ||
            relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", PathComparison))
        {
            return false;
        }

        return IsResolvedPathContainedBy(fullRoot, candidate);
    }

    public static string GetContainedPath(string root, string path, string parameterName)
    {
        var candidate = Path.GetFullPath(path, root);
        if (!IsContainedBy(root, candidate))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                path,
                "The path must remain inside the module repository and cannot traverse a symbolic link outside it.");
        }

        return candidate;
    }

    private static bool IsResolvedPathContainedBy(string root, string candidate)
    {
        var resolvedRoot = ResolvePath(root);
        var resolvedCandidate = ResolvePath(candidate);
        return IsLexicallyContainedBy(resolvedRoot, resolvedCandidate);
    }

    private static string ResolvePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var pathRoot = Path.GetPathRoot(fullPath)
            ?? throw new InvalidOperationException($"Unable to determine the root of '{path}'.");
        var current = pathRoot;
        var relativePath = Path.GetRelativePath(pathRoot, fullPath);

        foreach (var segment in relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo? entry = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : File.Exists(current)
                    ? new FileInfo(current)
                    : null;
            if (entry?.LinkTarget is not null)
            {
                current = ResolveLink(entry);
            }
        }

        return Normalize(current);
    }

    private static bool IsLexicallyContainedBy(string root, string candidate)
    {
        var relativePath = Path.GetRelativePath(root, candidate);
        return !Path.IsPathRooted(relativePath) &&
            !relativePath.Equals("..", PathComparison) &&
            !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", PathComparison) &&
            !relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", PathComparison);
    }

    private static string ResolveLink(FileSystemInfo entry)
    {
        return Path.GetFullPath(entry.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? entry.FullName);
    }

    private static string Normalize(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        return root is not null && string.Equals(fullPath, root, PathComparison)
            ? root
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
