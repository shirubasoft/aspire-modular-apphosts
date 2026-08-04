using System.Diagnostics;

namespace Aspire.Hosting.ModularAppHosts;

internal static class RepositoryInspector
{
    public static string FindRepositoryRoot(string projectPath)
    {
        var startDirectory = Directory.Exists(projectPath)
            ? projectPath
            : Path.GetDirectoryName(projectPath)
                ?? throw new InvalidOperationException($"Unable to determine the directory for '{projectPath}'.");

        if (TryRunGit(startDirectory, ["rev-parse", "--show-toplevel"], out var root) &&
            !string.IsNullOrWhiteSpace(root))
        {
            return Path.GetFullPath(root.Trim());
        }

        return Path.GetFullPath(startDirectory);
    }

    public static string? TryGetRemote(string repositoryPath)
    {
        return TryRunGit(repositoryPath, ["config", "--get", "remote.origin.url"], out var remote)
            ? remote.Trim() is { Length: > 0 } value ? value : null
            : null;
    }

    public static bool IsGitRepository(string repositoryPath)
    {
        return Directory.Exists(repositoryPath) &&
               TryRunGit(repositoryPath, ["rev-parse", "--is-inside-work-tree"], out var result) &&
               string.Equals(result.Trim(), "true", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsDirty(string repositoryPath)
    {
        return IsGitRepository(repositoryPath) &&
               TryRunGit(repositoryPath, ["status", "--porcelain", "--untracked-files=normal"], out var status) &&
               !string.IsNullOrWhiteSpace(status);
    }

    private static bool TryRunGit(string workingDirectory, IReadOnlyList<string> arguments, out string output)
    {
        output = string.Empty;

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    WorkingDirectory = Directory.Exists(workingDirectory)
                        ? workingDirectory
                        : Path.GetDirectoryName(workingDirectory) ?? workingDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            if (!process.Start())
            {
                return false;
            }

            output = process.StandardOutput.ReadToEnd();
            _ = process.StandardError.ReadToEnd();

            if (!process.WaitForExit(milliseconds: 5_000))
            {
                process.Kill(entireProcessTree: true);
                return false;
            }

            return process.ExitCode == 0;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return false;
        }
    }
}

internal sealed record RepositorySyncCommand(string Executable, IReadOnlyList<string> Arguments);

internal static class RepositorySynchronizer
{
    public static RepositorySyncCommand? CreateCommand(
        string repositoryPath,
        string? repository,
        bool updateRepository)
    {
        if (!RepositoryInspector.IsGitRepository(repositoryPath))
        {
            if (Directory.Exists(repositoryPath))
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(repository))
            {
                throw new InvalidOperationException(
                    $"Repository '{repositoryPath}' does not exist and the module does not define a Git remote.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(repositoryPath)
                ?? throw new InvalidOperationException($"Unable to determine the parent of '{repositoryPath}'."));

            return new RepositorySyncCommand(
                "git",
                ["clone", "--recurse-submodules", "--", repository, repositoryPath]);
        }

        if (!updateRepository || RepositoryInspector.IsDirty(repositoryPath))
        {
            return null;
        }

        return new RepositorySyncCommand(
            "git",
            ["-C", repositoryPath, "pull", "--ff-only", "--recurse-submodules"]);
    }

    public static async Task SynchronizeAsync(
        string repositoryPath,
        string? repository,
        bool updateRepository,
        CancellationToken cancellationToken)
    {
        var command = CreateCommand(repositoryPath, repository, updateRepository);
        if (command is null)
        {
            return;
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = command.Executable,
                WorkingDirectory = Path.GetDirectoryName(repositoryPath)
                    ?? throw new InvalidOperationException($"Unable to determine the parent of '{repositoryPath}'."),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        foreach (var argument in command.Arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start repository synchronization for '{repositoryPath}'.");
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            var error = await standardError.ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Repository synchronization failed for '{repositoryPath}' with exit code {process.ExitCode}: {error.Trim()}");
        }

        _ = await standardOutput.ConfigureAwait(false);
    }
}
