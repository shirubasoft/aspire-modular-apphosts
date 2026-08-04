using System.Diagnostics;
using System.IO.Compression;
using System.Xml.Linq;
using Xunit;

namespace Aspire.Hosting.ModularAppHosts.PackageTests;

public sealed class PackedPackageContractTests
{
    private const string CorePackageId = "Shirubasoft.Aspire.ModularAppHosts";
    private const string TestingPackageId = "Shirubasoft.Aspire.ModularAppHosts.Testing";
    private static readonly SemaphoreSlim PackageBuildLock = new(1, 1);
    private static PackageArtifacts? _packageArtifacts;

    [Fact]
    public async Task Core_package_excludes_testing_and_Docker_dependencies()
    {
        var packages = await GetPackagesAsync(TestContext.Current.CancellationToken);

        var dependencies = ReadDependencies(packages.CorePackagePath);

        Assert.Contains(dependencies, dependency => dependency.Id == "Aspire.Hosting");
        Assert.DoesNotContain(dependencies, dependency => dependency.Id == "Aspire.Hosting.Testing");
        Assert.DoesNotContain(dependencies, dependency => dependency.Id == "Aspire.Hosting.Docker");
        Assert.DoesNotContain(dependencies, dependency => dependency.Id == TestingPackageId);
    }

    [Fact]
    public async Task Testing_package_declares_core_Docker_and_testing_dependencies()
    {
        var packages = await GetPackagesAsync(TestContext.Current.CancellationToken);

        var dependencies = ReadDependencies(packages.TestingPackagePath);

        var core = Assert.Single(dependencies, dependency => dependency.Id == CorePackageId);
        Assert.Contains(packages.Version, core.Version, StringComparison.Ordinal);
        Assert.Contains(dependencies, dependency => dependency.Id == "Aspire.Hosting.Docker");
        Assert.Contains(dependencies, dependency => dependency.Id == "Aspire.Hosting.Testing");
    }

    [Fact]
    public async Task Packed_core_package_compiles_a_source_generator_consumer()
    {
        var packages = await GetPackagesAsync(TestContext.Current.CancellationToken);
        const string source = """
            using Aspire.Hosting.ModularAppHosts;

            namespace PackedCoreConsumer;

            [GenerateDistributedApplicationModule("orders")]
            public static partial class OrdersModule
            {
                public static void Define(IDistributedApplicationModuleBuilder module)
                {
                    module.AddContainer("orders-api", "nginx");
                }
            }

            public static class Contract
            {
                public static System.Type GeneratedModuleType => typeof(OrdersModule.Module);
            }
            """;

        await BuildConsumerAsync(
            packages,
            "CoreConsumer",
            CorePackageId,
            source,
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Packed_testing_package_compiles_a_Compose_testing_consumer()
    {
        var packages = await GetPackagesAsync(TestContext.Current.CancellationToken);
        const string source = """
            using Aspire.Hosting.ApplicationModel;
            using Aspire.Hosting.Docker;
            using Aspire.Hosting.ModularAppHosts;
            using Aspire.Hosting.Testing;

            namespace PackedTestingConsumer;

            public static class Contract
            {
                public static IDistributedApplicationTestingBuilder Create(string configurationFile) =>
                    DockerComposeDeploymentTestingBuilder.Create<EntryPoint>(configurationFile);

                public static IResourceBuilder<DockerComposeEnvironmentResource> ExportEndpoint(
                    IResourceBuilder<DockerComposeEnvironmentResource> environment,
                    EndpointReference endpoint) =>
                    environment.WithTestEndpoint("api", endpoint, healthCheckPath: "/health");
            }

            public sealed class EntryPoint;
            """;

        await BuildConsumerAsync(
            packages,
            "TestingConsumer",
            TestingPackageId,
            source,
            TestContext.Current.CancellationToken);
    }

    private static async Task<PackageArtifacts> GetPackagesAsync(CancellationToken cancellationToken)
    {
        if (_packageArtifacts is not null)
        {
            return _packageArtifacts;
        }

        await PackageBuildLock.WaitAsync(cancellationToken);
        try
        {
            if (_packageArtifacts is not null)
            {
                return _packageArtifacts;
            }

            var repositoryRoot = FindRepositoryRoot();
            var outputPath = Path.Combine(
                Path.GetTempPath(),
                $"aspire-modular-package-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(outputPath);
            var version = $"0.0.0-package-tests-{Guid.NewGuid():N}";

            await RunDotNetAsync(
                repositoryRoot,
                cancellationToken,
                "pack",
                "src/Aspire.Hosting.ModularAppHosts/Aspire.Hosting.ModularAppHosts.csproj",
                "--configuration",
                "Release",
                "--no-restore",
                "--output",
                outputPath,
                $"-p:PackageVersion={version}");
            await RunDotNetAsync(
                repositoryRoot,
                cancellationToken,
                "pack",
                "src/Aspire.Hosting.ModularAppHosts.Testing/Aspire.Hosting.ModularAppHosts.Testing.csproj",
                "--configuration",
                "Release",
                "--no-restore",
                "--output",
                outputPath,
                $"-p:PackageVersion={version}");

            return _packageArtifacts = new PackageArtifacts(
                repositoryRoot,
                outputPath,
                version,
                Path.Combine(outputPath, $"{CorePackageId}.{version}.nupkg"),
                Path.Combine(outputPath, $"{TestingPackageId}.{version}.nupkg"));
        }
        finally
        {
            PackageBuildLock.Release();
        }
    }

    private static IReadOnlyList<PackageDependency> ReadDependencies(string packagePath)
    {
        Assert.True(File.Exists(packagePath), $"Package '{packagePath}' was not created.");
        using var archive = ZipFile.OpenRead(packagePath);
        var nuspec = Assert.Single(
            archive.Entries,
            entry => entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
        using var stream = nuspec.Open();
        var document = XDocument.Load(stream);
        return document
            .Descendants()
            .Where(element => element.Name.LocalName == "dependency")
            .Select(element => new PackageDependency(
                (string?)element.Attribute("id") ?? string.Empty,
                (string?)element.Attribute("version") ?? string.Empty))
            .ToArray();
    }

    private static async Task BuildConsumerAsync(
        PackageArtifacts packages,
        string name,
        string packageId,
        string source,
        CancellationToken cancellationToken)
    {
        var projectDirectory = Path.Combine(packages.OutputPath, name);
        Directory.CreateDirectory(projectDirectory);
        var projectPath = Path.Combine(projectDirectory, $"{name}.csproj");
        var nugetConfigPath = Path.Combine(projectDirectory, "NuGet.Config");
        File.WriteAllText(projectPath, $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="{{packageId}}" Version="{{packages.Version}}" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(projectDirectory, "Contract.cs"), source);
        new XDocument(
            new XElement("configuration",
                new XElement("packageSources",
                    new XElement("clear"),
                    new XElement("add",
                        new XAttribute("key", "package-tests"),
                        new XAttribute("value", packages.OutputPath)),
                    new XElement("add",
                        new XAttribute("key", "nuget.org"),
                        new XAttribute("value", "https://api.nuget.org/v3/index.json")))))
            .Save(nugetConfigPath);

        await RunDotNetAsync(
            projectDirectory,
            cancellationToken,
            "restore",
            projectPath,
            "--configfile",
            nugetConfigPath);
        await RunDotNetAsync(
            projectDirectory,
            cancellationToken,
            "build",
            projectPath,
            "--configuration",
            "Release",
            "--no-restore");
    }

    private static async Task RunDotNetAsync(
        string workingDirectory,
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        Assert.True(process.Start(), $"Failed to start dotnet {string.Join(' ', arguments)}.");
        var standardOutput = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var standardError = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }

        var output = await standardOutput;
        var error = await standardError;
        Assert.True(
            process.ExitCode == 0,
            $"dotnet {string.Join(' ', arguments)} failed with exit code {process.ExitCode}.{Environment.NewLine}" +
            output + Environment.NewLine + error);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Aspire.ModularAppHosts.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Unable to locate the repository root.");
    }

    private sealed record PackageArtifacts(
        string RepositoryRoot,
        string OutputPath,
        string Version,
        string CorePackagePath,
        string TestingPackagePath);

    private sealed record PackageDependency(string Id, string Version);
}
