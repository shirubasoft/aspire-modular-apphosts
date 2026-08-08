using System.Text.Json;
using Aspire.Hosting.ModularAppHosts;

namespace Shirubasoft.Aspire.ModularAppHosts.Tool;

internal static partial class PreviewTool
{
    private static async Task<IReadOnlyList<ModulePreviewContractDependency>> ResolveContractDependenciesAsync(
        string projectPath,
        IReadOnlyList<string> dependencyPackageIds,
        string? nugetConfig,
        string dotnetExecutable,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(projectPath))
        {
            throw new PreviewToolException($"Contract project '{projectPath}' does not exist.");
        }

        if (nugetConfig is not null && !File.Exists(nugetConfig))
        {
            throw new PreviewToolException($"NuGet configuration '{nugetConfig}' does not exist.");
        }

        var requestedPackageIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var packageId in dependencyPackageIds)
        {
            ModulePreviewValidation.ValidatePackageId(packageId, "--contract-dependency");
            if (!requestedPackageIds.TryAdd(packageId, packageId))
            {
                throw new PreviewToolException(
                    $"Contract dependency package '{packageId}' was specified more than once.");
            }
        }

        var projectDirectory = Path.GetDirectoryName(projectPath)!;
        var restoreArguments = new List<string>
        {
            "restore",
            projectPath,
            "--force-evaluate",
            "--nologo"
        };
        if (nugetConfig is not null)
        {
            restoreArguments.Add("--configfile");
            restoreArguments.Add(nugetConfig);
        }
        var restoreResult = await RunCommandAsync(
            dotnetExecutable,
            restoreArguments,
            projectDirectory,
            standardInput: null,
            cancellationToken,
            applyTimeout: false).ConfigureAwait(false);
        EnsureSuccess(restoreResult, $"restore contract project '{projectPath}'");

        return await ReadContractDependenciesFromAssetsAsync(
            projectPath,
            requestedPackageIds,
            dotnetExecutable,
            commandTimeout: null,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task VerifyRestoredContractDependenciesAsync(
        string projectPath,
        IList<ModulePreviewContractDependency> expectedDependencies,
        string dotnetExecutable,
        TimeSpan commandTimeout,
        CancellationToken cancellationToken)
    {
        if (expectedDependencies.Count == 0)
        {
            return;
        }

        var packageIds = expectedDependencies.ToDictionary(
            dependency => dependency.PackageId,
            dependency => dependency.PackageId,
            StringComparer.OrdinalIgnoreCase);
        var actualDependencies = await ReadContractDependenciesFromAssetsAsync(
            projectPath,
            packageIds,
            dotnetExecutable,
            commandTimeout,
            cancellationToken).ConfigureAwait(false);
        var actualByPackage = actualDependencies.ToDictionary(
            dependency => dependency.PackageId,
            StringComparer.OrdinalIgnoreCase);
        foreach (var expected in expectedDependencies)
        {
            var actual = actualByPackage[expected.PackageId];
            if (!string.Equals(actual.Version, expected.Version, StringComparison.OrdinalIgnoreCase))
            {
                throw new PreviewToolException(
                    $"Materialized contract project '{projectPath}' restored direct dependency " +
                    $"'{actual.PackageId}' at '{actual.Version}', but the verified preview lock requires " +
                    $"'{expected.Version}'.");
            }
        }
    }

    private static async Task<IReadOnlyList<ModulePreviewContractDependency>> ReadContractDependenciesFromAssetsAsync(
        string projectPath,
        IReadOnlyDictionary<string, string> requestedPackageIds,
        string dotnetExecutable,
        TimeSpan? commandTimeout,
        CancellationToken cancellationToken)
    {
        var projectDirectory = Path.GetDirectoryName(projectPath)!;
        var assetsPropertyResult = await RunCommandAsync(
            dotnetExecutable,
            ["msbuild", projectPath, "-getProperty:ProjectAssetsFile", "-nologo"],
            projectDirectory,
            standardInput: null,
            cancellationToken,
            applyTimeout: commandTimeout.HasValue,
            timeout: commandTimeout,
            timeoutOperation: "locate contract project assets").ConfigureAwait(false);
        EnsureSuccess(assetsPropertyResult, "locate contract project assets");
        var assetsLines = assetsPropertyResult.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (assetsLines.Length != 1 || string.IsNullOrWhiteSpace(assetsLines[0]))
        {
            throw new PreviewToolException(
                $"Unable to determine ProjectAssetsFile for contract project '{projectPath}'.");
        }
        var assetsPath = Path.GetFullPath(assetsLines[0], projectDirectory);

        if (!File.Exists(assetsPath))
        {
            throw new PreviewToolException(
                $"Contract restore did not produce assets file '{assetsPath}'.");
        }

        var stream = new FileStream(
            assetsPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        JsonDocument assets;
        try
        {
            assets = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw new PreviewToolException(
                $"Contract assets file '{assetsPath}' is not valid JSON.",
                exception);
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }

        using (assets)
        {
            ValidateAssetsBelongToProject(assets.RootElement, projectPath, assetsPath);
            var directPackages = ReadDirectPackages(assets.RootElement, assetsPath);
            var resolvedVersions = ReadResolvedPackageVersions(assets.RootElement, assetsPath);
            var dependencies = new List<ModulePreviewContractDependency>(requestedPackageIds.Count);
            foreach (var requested in requestedPackageIds.Values.Order(StringComparer.OrdinalIgnoreCase))
            {
                if (!directPackages.TryGetValue(requested, out var directPackage))
                {
                    throw new PreviewToolException(
                        $"Package '{requested}' is not a direct dependency of contract project '{projectPath}'.");
                }

                var declaredPackageId = directPackage.PackageId;

                if (!resolvedVersions.TryGetValue(requested, out var versions) || versions.Count == 0)
                {
                    throw new PreviewToolException(
                        $"Contract restore did not resolve direct dependency '{declaredPackageId}'.");
                }

                if (versions.Count != 1)
                {
                    throw new PreviewToolException(
                        $"Direct contract dependency '{declaredPackageId}' resolved different versions across " +
                        $"target frameworks: {string.Join(", ", versions.Order(StringComparer.OrdinalIgnoreCase))}.");
                }

                var version = versions.Single();
                ModulePreviewValidation.ValidatePackageVersion(
                    version,
                    $"Resolved contract dependency '{declaredPackageId}' version");
                foreach (var range in directPackage.Ranges.Order(StringComparer.OrdinalIgnoreCase))
                {
                    if (!NuGetVersionRangePinsExact(range, version, out var error))
                    {
                        throw new PreviewToolException(
                            $"Direct contract dependency '{declaredPackageId}' declares range '{range}', which " +
                            $"does not pin resolved version '{version}' exactly. Declare the dependency as " +
                            $"'[{version}]'.{error}");
                    }
                }

                dependencies.Add(new ModulePreviewContractDependency
                {
                    PackageId = declaredPackageId,
                    Version = version
                });
            }

            return dependencies;
        }
    }

    private static void ValidateAssetsBelongToProject(
        JsonElement root,
        string expectedProjectPath,
        string assetsPath)
    {
        if (!root.TryGetProperty("project", out var project) ||
            !project.TryGetProperty("restore", out var restore) ||
            !restore.TryGetProperty("projectPath", out var projectPathElement) ||
            projectPathElement.ValueKind != JsonValueKind.String ||
            projectPathElement.GetString() is not { } actualProjectPath ||
            !string.Equals(
                Path.GetFullPath(actualProjectPath),
                Path.GetFullPath(expectedProjectPath),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new PreviewToolException(
                $"Contract assets file '{assetsPath}' does not belong to project '{expectedProjectPath}'.");
        }
    }

    private static Dictionary<string, DirectPackageDependency> ReadDirectPackages(
        JsonElement root,
        string assetsPath)
    {
        if (!root.TryGetProperty("project", out var project) ||
            !project.TryGetProperty("frameworks", out var frameworks) ||
            frameworks.ValueKind != JsonValueKind.Object)
        {
            throw new PreviewToolException(
                $"Contract assets file '{assetsPath}' does not contain project frameworks.");
        }

        var directPackages = new Dictionary<string, DirectPackageDependency>(StringComparer.OrdinalIgnoreCase);
        foreach (var framework in frameworks.EnumerateObject())
        {
            if (!framework.Value.TryGetProperty("dependencies", out var dependencies) ||
                dependencies.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var dependency in dependencies.EnumerateObject())
            {
                if (!dependency.Value.TryGetProperty("version", out var versionElement) ||
                    versionElement.ValueKind != JsonValueKind.String ||
                    versionElement.GetString() is not { } range)
                {
                    throw new PreviewToolException(
                        $"Direct contract dependency '{dependency.Name}' in assets file '{assetsPath}' " +
                        "does not declare a version range.");
                }

                if (!directPackages.TryGetValue(dependency.Name, out var package))
                {
                    package = new DirectPackageDependency(
                        dependency.Name,
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                    directPackages.Add(dependency.Name, package);
                }
                package.Ranges.Add(range);
            }
        }

        return directPackages;
    }

    private static Dictionary<string, HashSet<string>> ReadResolvedPackageVersions(
        JsonElement root,
        string assetsPath)
    {
        if (!root.TryGetProperty("libraries", out var libraries) ||
            libraries.ValueKind != JsonValueKind.Object)
        {
            throw new PreviewToolException(
                $"Contract assets file '{assetsPath}' does not contain resolved libraries.");
        }

        var versions = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in libraries.EnumerateObject())
        {
            if (!library.Value.TryGetProperty("type", out var type) ||
                type.ValueKind != JsonValueKind.String ||
                !string.Equals(type.GetString(), "package", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var separator = library.Name.LastIndexOf('/');
            if (separator <= 0 || separator == library.Name.Length - 1)
            {
                continue;
            }

            var packageId = library.Name[..separator];
            var version = library.Name[(separator + 1)..];
            if (!versions.TryGetValue(packageId, out var packageVersions))
            {
                packageVersions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                versions.Add(packageId, packageVersions);
            }
            packageVersions.Add(version);
        }

        return versions;
    }

    private static ModulePreviewContractDependency CopyContractDependency(
        ModulePreviewContractDependency dependency) => new()
        {
            PackageId = dependency.PackageId,
            Version = dependency.Version
        };

    private sealed record DirectPackageDependency(string PackageId, HashSet<string> Ranges);
}
