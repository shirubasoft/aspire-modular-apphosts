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

            [GenerateDistributedApplicationModule(Name, Version = "2")]
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

            public static class Consumer
            {
                public static System.Threading.Tasks.Task<OrdersModule.Module> ImportAsync(
                    Aspire.Hosting.IDistributedApplicationBuilder builder) =>
                    builder.ImportOrdersModuleAsync();
            }
            """;

        var result = RunGenerator(source);

        Assert.Empty(result.GeneratorDiagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.Empty(result.CompilationDiagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var generated = Assert.Single(result.GeneratedSources);
        Assert.Contains("Task<Module> AddOrdersModuleAsync(", generated);
        Assert.Contains("this global::Aspire.Hosting.IDistributedApplicationBuilder builder", generated);
        Assert.Contains("DefineModuleAsync(builder, \"orders\", \"2\", Define, cancellationToken)", generated);
        Assert.Contains("return await AddOrdersModuleAsync(builder, module, cancellationToken)", generated);
        Assert.Contains("Task<Module> ImportOrdersModuleAsync(", generated);
        Assert.Contains("ModuleImportOptions options", generated);
        Assert.Contains("Module : global::Aspire.Hosting.ModularAppHosts.DistributedApplicationModuleReference", generated);
        Assert.Contains("IResourceBuilder<global::Aspire.Hosting.ApplicationModel.IResourceWithEndpoints> OrdersApi", generated);
        Assert.Contains("IResourceBuilder<global::Aspire.Hosting.ApplicationModel.ContainerResource> Cache", generated);
        Assert.Contains("IResourceBuilder<global::Aspire.Hosting.ApplicationModel.ParameterResource> Region", generated);
        Assert.Contains("ImportModuleAsync(builder, \"orders\", options, cancellationToken)", generated);
        Assert.Contains(
            typeof(DistributedApplicationModuleGenerator).Assembly.GetName().Version!.ToString(),
            generated,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_creates_a_typed_property_for_a_resource_factory_that_publishes_its_image()
    {
        const string source = """
            using Aspire.Hosting.ApplicationModel;
            using Aspire.Hosting.ModularAppHosts;

            namespace GeneratedSample;

            [GenerateDistributedApplicationModule(Name, Version = "1")]
            public static partial class DatabaseModule
            {
                public const string Name = "database";
                public const string ServerResourceName = "database-server";

                public static void Define(IDistributedApplicationModuleBuilder module)
                {
                    module.AddResource<ContainerResource>(
                        ServerResourceName,
                        context => throw new System.NotSupportedException(),
                        new ModuleContainerExportOptions("example/database", "pwsh", "build-docker.ps1"));
                }
            }
            """;

        var result = RunGenerator(source);

        Assert.Empty(result.GeneratorDiagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.Empty(result.CompilationDiagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var generated = Assert.Single(result.GeneratedSources);
        Assert.Contains(
            "IResourceBuilder<global::Aspire.Hosting.ApplicationModel.ContainerResource> Server",
            generated,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_creates_a_strongly_typed_reference_for_use_in_other_module_definitions()
    {
        const string source = """
            using Aspire.Hosting.ModularAppHosts;

            namespace GeneratedSample;

            [GenerateDistributedApplicationModule("catalog", Version = "2")]
            public static partial class CatalogModule
            {
                public static void Define(IDistributedApplicationModuleBuilder module)
                {
                    module.AddContainer("api", "catalog");
                }
            }

            [GenerateDistributedApplicationModule("orders")]
            public static partial class OrdersModule
            {
                public static void Define(IDistributedApplicationModuleBuilder module)
                {
                    var catalog = CatalogModule.Reference(module);
                    _ = catalog.Api;
                    module.AddContainer("api", "orders");
                }
            }
            """;

        var result = RunGenerator(source);

        Assert.Empty(result.GeneratorDiagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.Empty(result.CompilationDiagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.Contains(result.GeneratedSources, generated =>
            generated.Contains("public static Module Reference(", StringComparison.Ordinal) &&
            generated.Contains("GetRequiredModule(\"catalog\", \"2\")", StringComparison.Ordinal));
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
    public void Generator_rejects_an_empty_module_version()
    {
        const string source = """
            using Aspire.Hosting.ModularAppHosts;

            [GenerateDistributedApplicationModule("invalid", Version = "  ")]
            public static partial class InvalidModule
            {
                public static void Define(IDistributedApplicationModuleBuilder module)
                {
                    module.AddContainer("cache", "redis");
                }
            }
            """;

        var result = RunGenerator(source);

        Assert.Contains(result.GeneratorDiagnostics, diagnostic => diagnostic.Id == "SAMHSG007");
        Assert.Empty(result.GeneratedSources);
    }

    [Fact]
    public void Generator_keeps_the_advanced_overload_when_no_conventional_define_method_exists()
    {
        const string source = """
            using Aspire.Hosting.ModularAppHosts;

            [GenerateDistributedApplicationModule("advanced")]
            public static partial class AdvancedModule
            {
                public static void DefineWithInput(IDistributedApplicationModuleBuilder module, string repository)
                {
                    module.AddContainer("cache", "redis");
                }
            }
            """;

        var result = RunGenerator(source);

        var generated = Assert.Single(result.GeneratedSources);
        Assert.DoesNotContain("DefineModuleAsync(builder", generated, StringComparison.Ordinal);
        Assert.Contains("IDistributedApplicationModule module,", generated, StringComparison.Ordinal);
        Assert.Contains("String.Equals(module.Name, \"advanced\"", generated, StringComparison.Ordinal);
        Assert.Contains("String.Equals(module.Version, \"1\"", generated, StringComparison.Ordinal);
        Assert.Contains("Expected module 'advanced' with contract version '1'.", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_ignores_resource_calls_outside_the_conventional_definition()
    {
        const string source = """
            using System;
            using Aspire.Hosting.ModularAppHosts;

            [GenerateDistributedApplicationModule("focused")]
            public static partial class FocusedModule
            {
                public static void Define(IDistributedApplicationModuleBuilder module)
                {
                    module.AddContainer("included", "redis");
                }

                public static void Unrelated(IDistributedApplicationModuleBuilder module)
                {
                    module.AddContainer("excluded-method", "redis");
                }

                public static Action<IDistributedApplicationModuleBuilder> CreateUnrelated() =>
                    module => module.AddContainer("excluded-lambda", "redis");
            }
            """;

        var result = RunGenerator(source);

        Assert.Empty(result.GeneratorDiagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var generated = Assert.Single(result.GeneratedSources);
        Assert.Contains("> Included =>", generated, StringComparison.Ordinal);
        Assert.DoesNotContain("ExcludedMethod", generated, StringComparison.Ordinal);
        Assert.DoesNotContain("ExcludedLambda", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_discovers_resources_in_an_advanced_export_lambda()
    {
        const string source = """
            using Aspire.Hosting;
            using Aspire.Hosting.ModularAppHosts;

            [GenerateDistributedApplicationModule("advanced")]
            public static partial class AdvancedModule
            {
                public static global::System.Threading.Tasks.Task<IDistributedApplicationModule> RegisterAsync(
                    IDistributedApplicationBuilder builder) =>
                    builder.ExportModuleAsync("advanced", module =>
                        module.AddContainer("cache", "redis"));
            }
            """;

        var result = RunGenerator(source);

        Assert.Empty(result.GeneratorDiagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var generated = Assert.Single(result.GeneratedSources);
        Assert.Contains("> Cache =>", generated, StringComparison.Ordinal);
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
    public void Generator_reports_an_inaccessible_custom_resource_type()
    {
        const string source = """
            using Aspire.Hosting.ApplicationModel;
            using Aspire.Hosting.ModularAppHosts;

            internal sealed class HiddenResource(string name) : Resource(name);

            [GenerateDistributedApplicationModule("invalid")]
            public static partial class InvalidModule
            {
                public static void Define(IDistributedApplicationModuleBuilder module)
                {
                    module.AddResource<HiddenResource>("hidden", context =>
                        context.ApplicationBuilder.AddResource(new HiddenResource(context.ResourceName)));
                }
            }
            """;

        var result = RunGenerator(source);

        var diagnostic = Assert.Single(
            result.GeneratorDiagnostics.Where(diagnostic => diagnostic.Id == "SAMHSG008"));
        Assert.Contains("HiddenResource", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.DoesNotContain(result.CompilationDiagnostics, diagnostic => diagnostic.Id == "CS0053");
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
    [InlineData("public static void AddInvalidModuleAsync() { }", "AddInvalidModuleAsync")]
    [InlineData("public static int ImportInvalidModuleAsync => 0;", "ImportInvalidModuleAsync")]
    [InlineData("public static void Reference() { }", "Reference")]
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
        Assert.Contains("Task<Module> AddEmptyModuleAsync(", generated);
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
        Assert.Contains("Task<Module> ImportClassAsync(", generated);
        Assert.Contains("ImportModuleAsync(builder, \"orders\\\"north\\\\region\\n\", options, cancellationToken)", generated);
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
