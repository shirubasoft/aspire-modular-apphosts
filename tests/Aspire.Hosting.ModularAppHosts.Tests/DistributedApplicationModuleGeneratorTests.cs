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

    [Theory]
    [InlineData("public partial class InvalidModule")]
    [InlineData("public static partial class InvalidModule<T>")]
    public void Generator_rejects_non_static_and_generic_module_classes(string declaration)
    {
        var source = $$"""
            using Aspire.Hosting.ModularAppHosts;

            [GenerateDistributedApplicationModule("invalid")]
            {{declaration}}
            {
            }
            """;

        var result = RunGenerator(source);

        Assert.Contains(result.GeneratorDiagnostics, diagnostic => diagnostic.Id == "SAMHSG001");
        Assert.Empty(result.GeneratedSources);
    }

    [Fact]
    public void Generator_rejects_nested_module_classes()
    {
        const string source = """
            using Aspire.Hosting.ModularAppHosts;

            public static class Outer
            {
                [GenerateDistributedApplicationModule("invalid")]
                public static partial class InvalidModule
                {
                }
            }
            """;

        var result = RunGenerator(source);

        Assert.Contains(result.GeneratorDiagnostics, diagnostic => diagnostic.Id == "SAMHSG001");
        Assert.Empty(result.GeneratedSources);
    }

    [Fact]
    public void Generator_rejects_an_empty_module_name()
    {
        const string source = """
            using Aspire.Hosting.ModularAppHosts;

            [GenerateDistributedApplicationModule("   ")]
            public static partial class InvalidModule
            {
            }
            """;

        var result = RunGenerator(source);

        Assert.Contains(result.GeneratorDiagnostics, diagnostic => diagnostic.Id == "SAMHSG002");
        Assert.Empty(result.GeneratedSources);
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

    [Fact]
    public void Generator_rejects_resource_properties_that_collide_with_the_module_contract()
    {
        const string source = """
            using Aspire.Hosting.ModularAppHosts;

            [GenerateDistributedApplicationModule("invalid")]
            public static partial class InvalidModule
            {
                public static void Define(IDistributedApplicationModuleBuilder module)
                {
                    module.AddContainer("name", "redis");
                }
            }
            """;

        var result = RunGenerator(source);

        Assert.Contains(result.GeneratorDiagnostics, diagnostic => diagnostic.Id == "SAMHSG004");
        Assert.Empty(result.GeneratedSources);
    }

    [Theory]
    [InlineData("public static void AddModule() { }", "AddModule")]
    [InlineData("public static int ImportModule => 0;", "ImportModule")]
    [InlineData("public sealed class Module { }", "Module")]
    public void Generator_rejects_members_reserved_for_the_generated_api(
        string member,
        string reservedName)
    {
        var source = $$"""
            using Aspire.Hosting.ModularAppHosts;

            [GenerateDistributedApplicationModule("invalid")]
            public static partial class InvalidModule
            {
                {{member}}

                public static void Define(IDistributedApplicationModuleBuilder module)
                {
                    module.AddContainer("cache", "redis");
                }
            }
            """;

        var result = RunGenerator(source);

        var diagnostic = Assert.Single(
            result.GeneratorDiagnostics.Where(diagnostic => diagnostic.Id == "SAMHSG005"));
        Assert.Contains(reservedName, diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Empty(result.GeneratedSources);
    }

    [Fact]
    public void Generator_warns_but_still_generates_a_module_without_resources()
    {
        const string source = """
            using Aspire.Hosting.ModularAppHosts;

            [GenerateDistributedApplicationModule("empty")]
            public static partial class EmptyModule
            {
            }
            """;

        var result = RunGenerator(source);

        var diagnostic = Assert.Single(
            result.GeneratorDiagnostics.Where(diagnostic => diagnostic.Id == "SAMHSG006"));
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        var generated = Assert.Single(result.GeneratedSources);
        Assert.Contains("public static Module AddModule(", generated);
        Assert.Empty(result.CompilationDiagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void Generator_escapes_identifiers_and_preserves_resource_name_constants()
    {
        const string source = """
            using Aspire.Hosting.ModularAppHosts;

            [GenerateDistributedApplicationModule("orders\"north\\region\n")]
            internal static partial class @class
            {
                public const string InventoryResourceName = "inventory-db";

                public static void Define(IDistributedApplicationModuleBuilder module)
                {
                    module.AddContainer(InventoryResourceName, "postgres");
                    module.AddContainer("2fa-service", "service");
                    module.AddContainer("order_items", "service");
                    module.AddContainer("---", "service");
                }
            }
            """;

        var result = RunGenerator(source);

        Assert.Empty(result.GeneratorDiagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.Empty(result.CompilationDiagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var generated = Assert.Single(result.GeneratedSources);
        Assert.Contains("internal static partial class @class", generated);
        Assert.Contains("> Inventory =>", generated);
        Assert.Contains("> _2faService =>", generated);
        Assert.Contains("> OrderItems =>", generated);
        Assert.Contains("> Resource =>", generated);
        Assert.Contains("ImportModule(builder, \"orders\\\"north\\\\region\\n\")", generated);
    }

    [Fact]
    public void Generator_orders_resources_across_partial_declarations_by_file_then_position()
    {
        const string secondFile = """
            using Aspire.Hosting.ModularAppHosts;

            namespace MultiFile;

            [GenerateDistributedApplicationModule("inventory")]
            internal static partial class InventoryModule
            {
                public static void DefineSecond(IDistributedApplicationModuleBuilder module)
                {
                    module.AddContainer("z-last", "redis");
                }
            }
            """;
        const string firstFile = """
            using Aspire.Hosting.ModularAppHosts;

            namespace MultiFile;

            internal static partial class InventoryModule
            {
                public static void DefineFirst(IDistributedApplicationModuleBuilder module)
                {
                    module.AddContainer("a-first", "redis");
                    module.AddContainer("b-second", "redis");
                }
            }
            """;

        var result = RunGenerator(
            ("02.Inventory.cs", secondFile),
            ("01.Inventory.cs", firstFile));

        Assert.Empty(result.GeneratorDiagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.Empty(result.CompilationDiagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var generated = Assert.Single(result.GeneratedSources);
        Assert.Contains("internal static partial class InventoryModule", generated);
        Assert.True(generated.IndexOf("> AFirst =>", StringComparison.Ordinal) <
            generated.IndexOf("> BSecond =>", StringComparison.Ordinal));
        Assert.True(generated.IndexOf("> BSecond =>", StringComparison.Ordinal) <
            generated.IndexOf("> ZLast =>", StringComparison.Ordinal));
    }

    [Fact]
    public void Generator_suppresses_and_restores_experimental_resource_diagnostics()
    {
        const string source = """
            using System.Diagnostics.CodeAnalysis;
            using Aspire.Hosting.ApplicationModel;
            using Aspire.Hosting.ModularAppHosts;

            [Experimental("SAMPLE001")]
            public sealed class ExperimentalResource(string name) : Resource(name);

            [GenerateDistributedApplicationModule("experimental")]
            public static partial class ExperimentalModule
            {
                public static void Define(IDistributedApplicationModuleBuilder module)
                {
                    module.AddResource<ExperimentalResource>("preview", context =>
                        context.ApplicationBuilder.AddResource(new ExperimentalResource(context.ResourceName)));
                }
            }
            """;

        var result = RunGenerator(source);

        var generated = Assert.Single(result.GeneratedSources);
        var disable = generated.IndexOf("#pragma warning disable SAMPLE001", StringComparison.Ordinal);
        var resource = generated.IndexOf("IResourceBuilder<global::ExperimentalResource> Preview", StringComparison.Ordinal);
        var restore = generated.IndexOf("#pragma warning restore SAMPLE001", StringComparison.Ordinal);
        Assert.True(disable >= 0);
        Assert.True(resource > disable);
        Assert.True(restore > resource);
    }

    private static GeneratorTestResult RunGenerator(string source) =>
        RunGenerator(("GeneratorTest.cs", source));

    private static GeneratorTestResult RunGenerator(params (string FilePath, string Source)[] sources)
    {
        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);
        var compilation = CSharpCompilation.Create(
            "GeneratorTests",
            sources.Select(source => CSharpSyntaxTree.ParseText(
                source.Source,
                parseOptions,
                path: source.FilePath)),
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
