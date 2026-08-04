using System.Collections.Immutable;
using Aspire.Hosting.ModularAppHosts.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Aspire.Hosting.ModularAppHosts.Tests;

public sealed class DistributedApplicationModuleGeneratorTests
{
    [Fact]
    public void Generator_creates_a_typed_module_for_every_declared_resource()
    {
        const string source = """
            using Aspire.Hosting.ApplicationModel;
            using Aspire.Hosting.ModularAppHosts;

            namespace GeneratedSample;

            [GenerateDistributedApplicationModule(Name)]
            public static partial class OrdersModule
            {
                public const string Name = "orders";
                public const string CacheResourceName = "orders-cache";

                public static void Define(IDistributedApplicationModuleBuilder module)
                {
                    module.AddProject("orders-api", "Orders.Api.csproj");
                    module.AddContainer(CacheResourceName, "redis");
                    module.AddResource<ParameterResource>("region", context =>
                        throw new System.NotSupportedException());
                }
            }
            """;

        var result = RunGenerator(source);

        Assert.Empty(result.GeneratorDiagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.Empty(result.CompilationDiagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var generated = Assert.Single(result.GeneratedSources);
        Assert.Contains("public static Module AddModule(", generated);
        Assert.Contains("public static Module ImportModule(", generated);
        Assert.Contains("IResourceBuilder<global::Aspire.Hosting.ApplicationModel.ContainerResource> OrdersApi", generated);
        Assert.Contains("IResourceBuilder<global::Aspire.Hosting.ApplicationModel.ContainerResource> Cache", generated);
        Assert.Contains("IResourceBuilder<global::Aspire.Hosting.ApplicationModel.ParameterResource> Region", generated);
        Assert.Contains("ImportModule(builder, \"orders\")", generated);
    }

    [Fact]
    public void Generator_requires_a_static_partial_top_level_class()
    {
        const string source = """
            using Aspire.Hosting.ModularAppHosts;

            [GenerateDistributedApplicationModule("invalid")]
            public static class InvalidModule
            {
            }
            """;

        var result = RunGenerator(source);

        Assert.Contains(result.GeneratorDiagnostics, diagnostic => diagnostic.Id == "SAMHSG001");
    }

    [Fact]
    public void Generator_rejects_resource_names_that_are_not_compile_time_constants()
    {
        const string source = """
            using Aspire.Hosting.ModularAppHosts;

            [GenerateDistributedApplicationModule("invalid")]
            public static partial class InvalidModule
            {
                public static void Define(IDistributedApplicationModuleBuilder module, string resourceName)
                {
                    module.AddContainer(resourceName, "redis");
                }
            }
            """;

        var result = RunGenerator(source);

        Assert.Contains(result.GeneratorDiagnostics, diagnostic => diagnostic.Id == "SAMHSG003");
    }

    [Fact]
    public void Generator_rejects_colliding_resource_property_names()
    {
        const string source = """
            using Aspire.Hosting.ModularAppHosts;

            [GenerateDistributedApplicationModule("invalid")]
            public static partial class InvalidModule
            {
                public const string ApiResourceName = "first-api";
                public const string Api = "second-api";

                public static void Define(IDistributedApplicationModuleBuilder module)
                {
                    module.AddContainer(ApiResourceName, "redis");
                    module.AddContainer(Api, "redis");
                }
            }
            """;

        var result = RunGenerator(source);

        Assert.Contains(result.GeneratorDiagnostics, diagnostic => diagnostic.Id == "SAMHSG004");
    }

    private static GeneratorTestResult RunGenerator(string source)
    {
        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);
        var compilation = CSharpCompilation.Create(
            "GeneratorTests",
            [CSharpSyntaxTree.ParseText(source, parseOptions)],
            GetMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new DistributedApplicationModuleGenerator().AsSourceGenerator()],
            parseOptions: parseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out var updatedCompilation,
            out var generatorDiagnostics);

        var runResult = driver.GetRunResult();
        var generatedSources = runResult.Results
            .SelectMany(result => result.GeneratedSources)
            .Select(result => result.SourceText.ToString())
            .ToImmutableArray();

        return new GeneratorTestResult(
            generatedSources,
            generatorDiagnostics,
            updatedCompilation.GetDiagnostics());
    }

    private static ImmutableArray<MetadataReference> GetMetadataReferences()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var trustedPlatformAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (trustedPlatformAssemblies is not null)
        {
            paths.UnionWith(trustedPlatformAssemblies.Split(Path.PathSeparator));
        }

        paths.UnionWith(Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll"));

        return paths
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToImmutableArray();
    }

    private sealed class GeneratorTestResult(
        ImmutableArray<string> generatedSources,
        ImmutableArray<Diagnostic> generatorDiagnostics,
        ImmutableArray<Diagnostic> compilationDiagnostics)
    {
        public ImmutableArray<string> GeneratedSources { get; } = generatedSources;

        public ImmutableArray<Diagnostic> GeneratorDiagnostics { get; } = generatorDiagnostics;

        public ImmutableArray<Diagnostic> CompilationDiagnostics { get; } = compilationDiagnostics;
    }
}
