using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace Aspire.Hosting.ModularAppHosts.Generators;

/// <summary>
/// Generates strongly typed accessors for resources declared by distributed application modules.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class DistributedApplicationModuleGenerator : IIncrementalGenerator
{
    private const string AttributeMetadataName =
        "Aspire.Hosting.ModularAppHosts.GenerateDistributedApplicationModuleAttribute";

    private const string ModuleBuilderMetadataName =
        "Aspire.Hosting.ModularAppHosts.IDistributedApplicationModuleBuilder";

    private static readonly DiagnosticDescriptor InvalidModuleDeclaration = new(
        "SAMHSG001",
        "Invalid generated module declaration",
        "Type '{0}' must be a top-level, non-generic, static partial class to generate module resources",
        "Shirubasoft.Aspire.ModularAppHosts",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidModuleName = new(
        "SAMHSG002",
        "Invalid generated module name",
        "The module name supplied to GenerateDistributedApplicationModule must be a non-empty compile-time string",
        "Shirubasoft.Aspire.ModularAppHosts",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidResourceName = new(
        "SAMHSG003",
        "Resource name cannot be generated",
        "The name passed to '{0}' must be a non-empty compile-time string so a typed resource property can be generated",
        "Shirubasoft.Aspire.ModularAppHosts",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor ResourcePropertyCollision = new(
        "SAMHSG004",
        "Generated resource property name collision",
        "Module resource '{0}' generates property '{1}', which conflicts with another generated module member",
        "Shirubasoft.Aspire.ModularAppHosts",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor GeneratedMemberCollision = new(
        "SAMHSG005",
        "Generated module member collision",
        "Type '{0}' already declares '{1}', which is reserved for the generated module API",
        "Shirubasoft.Aspire.ModularAppHosts",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor NoResourcesFound = new(
        "SAMHSG006",
        "No module resources found",
        "Type '{0}' does not contain any supported module resource declarations",
        "Shirubasoft.Aspire.ModularAppHosts",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly HashSet<string> ReservedResourcePropertyNames = new(StringComparer.Ordinal)
    {
        "Name",
        "Resources",
        "Projects",
        "Containers",
        "GetResource"
    };

    /// <summary>Registers the incremental pipeline that discovers and generates module accessors.</summary>
    /// <param name="context">The initialization context used to register generator inputs and outputs.</param>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var modules = context.SyntaxProvider.ForAttributeWithMetadataName(
            AttributeMetadataName,
            static (node, _) => node is ClassDeclarationSyntax,
            static (attributeContext, cancellationToken) => CreateModel(attributeContext, cancellationToken));

        context.RegisterSourceOutput(modules, static (sourceContext, module) => Generate(sourceContext, module));
    }

    private static ModuleModel CreateModel(
        GeneratorAttributeSyntaxContext context,
        CancellationToken cancellationToken)
    {
        var symbol = (INamedTypeSymbol)context.TargetSymbol;
        var diagnostics = ImmutableArray.CreateBuilder<DiagnosticInfo>();
        var declaration = (ClassDeclarationSyntax)context.TargetNode;
        var moduleLocation = declaration.Identifier.GetLocation();

        var canGenerate = symbol.IsStatic &&
            declaration.Modifiers.Any(SyntaxKind.PartialKeyword) &&
            symbol.ContainingType is null &&
            symbol.TypeParameters.Length == 0;

        if (!canGenerate)
        {
            diagnostics.Add(new DiagnosticInfo(
                InvalidModuleDeclaration,
                moduleLocation,
                symbol.ToDisplayString()));
        }

        var attribute = context.Attributes[0];
        var moduleName = attribute.ConstructorArguments.Length == 1
            ? attribute.ConstructorArguments[0].Value as string
            : null;

        if (string.IsNullOrWhiteSpace(moduleName))
        {
            diagnostics.Add(new DiagnosticInfo(InvalidModuleName, moduleLocation));
            canGenerate = false;
        }

        foreach (var reservedMemberName in new[] { "AddModule", "ImportModule", "Module" })
        {
            if (symbol.GetMembers(reservedMemberName).Length > 0)
            {
                diagnostics.Add(new DiagnosticInfo(
                    GeneratedMemberCollision,
                    moduleLocation,
                    symbol.ToDisplayString(),
                    reservedMemberName));
                canGenerate = false;
            }
        }

        var resources = CollectResources(symbol, context.SemanticModel.Compilation, diagnostics, cancellationToken);
        if (resources.Length == 0)
        {
            diagnostics.Add(new DiagnosticInfo(NoResourcesFound, moduleLocation, symbol.ToDisplayString()));
        }

        var generatedNames = new HashSet<string>(ReservedResourcePropertyNames, StringComparer.Ordinal);
        foreach (var resource in resources)
        {
            if (!generatedNames.Add(resource.PropertyName))
            {
                diagnostics.Add(new DiagnosticInfo(
                    ResourcePropertyCollision,
                    resource.Location,
                    resource.ResourceName,
                    resource.PropertyName));
                canGenerate = false;
            }
        }

        return new ModuleModel(
            symbol.ContainingNamespace.IsGlobalNamespace
                ? null
                : symbol.ContainingNamespace.ToDisplayString(),
            EscapeIdentifier(symbol.Name),
            symbol.DeclaredAccessibility == Accessibility.Public ? "public" : "internal",
            moduleName ?? string.Empty,
            resources,
            diagnostics.ToImmutable(),
            canGenerate);
    }

    private static ImmutableArray<ResourceModel> CollectResources(
        INamedTypeSymbol moduleSymbol,
        Compilation compilation,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        var resources = new List<ResourceModel>();
        var containerType = compilation.GetTypeByMetadataName(
            "Aspire.Hosting.ApplicationModel.ContainerResource");
        var resourceWithEndpointsType = compilation.GetTypeByMetadataName(
            "Aspire.Hosting.ApplicationModel.IResourceWithEndpoints");

        foreach (var syntaxReference in moduleSymbol.DeclaringSyntaxReferences)
        {
            if (syntaxReference.GetSyntax(cancellationToken) is not TypeDeclarationSyntax declaration)
            {
                continue;
            }

            var semanticModel = compilation.GetSemanticModel(declaration.SyntaxTree);
            foreach (var invocation in declaration.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var nearestType = invocation.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
                if (nearestType is null ||
                    !SymbolEqualityComparer.Default.Equals(
                        semanticModel.GetDeclaredSymbol(nearestType, cancellationToken),
                        moduleSymbol))
                {
                    continue;
                }

                if (semanticModel.GetOperation(invocation, cancellationToken) is not IInvocationOperation operation ||
                    operation.TargetMethod.ContainingType.ToDisplayString() != ModuleBuilderMetadataName)
                {
                    continue;
                }

                var methodName = operation.TargetMethod.Name;
                ITypeSymbol? resourceType = methodName switch
                {
                    "AddProject" => resourceWithEndpointsType,
                    "AddContainer" => containerType,
                    "AddResource" when operation.TargetMethod.TypeArguments.Length == 1 =>
                        operation.TargetMethod.TypeArguments[0],
                    _ => null
                };

                if (resourceType is null)
                {
                    continue;
                }

                var nameArgument = operation.Arguments.FirstOrDefault(
                    argument => argument.Parameter?.Name == "name");
                if (nameArgument is null ||
                    !nameArgument.Value.ConstantValue.HasValue ||
                    nameArgument.Value.ConstantValue.Value is not string resourceName ||
                    string.IsNullOrWhiteSpace(resourceName))
                {
                    diagnostics.Add(new DiagnosticInfo(
                        InvalidResourceName,
                        nameArgument?.Syntax.GetLocation() ?? invocation.GetLocation(),
                        methodName));
                    continue;
                }

                resources.Add(new ResourceModel(
                    resourceName,
                    GetPropertyName(nameArgument.Value, resourceName),
                    resourceType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    GetExperimentalDiagnosticIds(resourceType),
                    invocation.SyntaxTree.FilePath ?? string.Empty,
                    invocation.SpanStart,
                    nameArgument.Syntax.GetLocation()));
            }
        }

        return resources
            .OrderBy(resource => resource.FilePath, StringComparer.Ordinal)
            .ThenBy(resource => resource.SpanStart)
            .ToImmutableArray();
    }

    private static ImmutableArray<string> GetExperimentalDiagnosticIds(ITypeSymbol resourceType)
    {
        var diagnosticIds = ImmutableArray.CreateBuilder<string>();
        for (var currentType = resourceType as INamedTypeSymbol;
             currentType is not null;
             currentType = currentType.BaseType)
        {
            foreach (var attribute in currentType.GetAttributes())
            {
                if (attribute.AttributeClass?.ToDisplayString() ==
                        "System.Diagnostics.CodeAnalysis.ExperimentalAttribute" &&
                    attribute.ConstructorArguments.Length == 1 &&
                    attribute.ConstructorArguments[0].Value is string diagnosticId)
                {
                    diagnosticIds.Add(diagnosticId);
                }
            }
        }

        return diagnosticIds.Distinct(StringComparer.Ordinal).ToImmutableArray();
    }

    private static string GetPropertyName(IOperation nameOperation, string resourceName)
    {
        while (nameOperation is IConversionOperation conversion)
        {
            nameOperation = conversion.Operand;
        }

        if (nameOperation is IFieldReferenceOperation fieldReference)
        {
            var fieldName = fieldReference.Field.Name;
            const string suffix = "ResourceName";
            if (fieldName.EndsWith(suffix, StringComparison.Ordinal) && fieldName.Length > suffix.Length)
            {
                fieldName = fieldName.Substring(0, fieldName.Length - suffix.Length);
            }

            return ToPascalIdentifier(fieldName);
        }

        return ToPascalIdentifier(resourceName);
    }

    private static string ToPascalIdentifier(string value)
    {
        var builder = new StringBuilder(value.Length);
        var upperNext = true;

        foreach (var character in value)
        {
            if (!char.IsLetterOrDigit(character) && character != '_')
            {
                upperNext = true;
                continue;
            }

            if (builder.Length == 0 && char.IsDigit(character))
            {
                builder.Append('_');
            }

            if (character == '_')
            {
                upperNext = true;
                continue;
            }

            builder.Append(upperNext ? char.ToUpperInvariant(character) : character);
            upperNext = false;
        }

        return builder.Length == 0 ? "Resource" : EscapeIdentifier(builder.ToString());
    }

    private static string EscapeIdentifier(string identifier)
    {
        return SyntaxFacts.GetKeywordKind(identifier) == SyntaxKind.None &&
            SyntaxFacts.GetContextualKeywordKind(identifier) == SyntaxKind.None
                ? identifier
                : "@" + identifier;
    }

    private static void Generate(SourceProductionContext context, ModuleModel module)
    {
        foreach (var diagnostic in module.Diagnostics)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                diagnostic.Descriptor,
                diagnostic.Location,
                diagnostic.MessageArguments));
        }

        if (!module.CanGenerate)
        {
            return;
        }

        var source = new StringBuilder();
        source.AppendLine("// <auto-generated/>");
        source.AppendLine("#nullable enable");

        var experimentalDiagnosticIds = module.Resources
            .SelectMany(resource => resource.ExperimentalDiagnosticIds)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(diagnosticId => diagnosticId, StringComparer.Ordinal)
            .ToArray();
        foreach (var diagnosticId in experimentalDiagnosticIds)
        {
            source.Append("#pragma warning disable ").AppendLine(diagnosticId);
        }

        source.AppendLine();

        if (module.Namespace is not null)
        {
            source.Append("namespace ").Append(module.Namespace).AppendLine(";");
            source.AppendLine();
        }

        source.AppendLine("[global::System.CodeDom.Compiler.GeneratedCode(\"Shirubasoft.Aspire.ModularAppHosts\", \"1.0.0\")]");
        source.Append(module.Accessibility)
            .Append(" static partial class ")
            .Append(module.TypeName)
            .AppendLine();
        source.AppendLine("{");
        source.AppendLine("    /// <summary>Adds the exported module to the AppHost and returns its typed resources.</summary>");
        source.AppendLine("    public static Module AddModule(");
        source.AppendLine("        global::Aspire.Hosting.IDistributedApplicationBuilder builder,");
        source.AppendLine("        global::Aspire.Hosting.ModularAppHosts.IDistributedApplicationModule module)");
        source.AppendLine("    {");
        source.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(builder);");
        source.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(module);");
        source.AppendLine("        global::Aspire.Hosting.ModularAppHosts.DistributedApplicationModuleExtensions.Add(builder, module);");
        source.AppendLine("        return new Module(module);");
        source.AppendLine("    }");
        source.AppendLine();
        source.AppendLine("    /// <summary>Imports the module and returns its typed resources.</summary>");
        source.AppendLine("    public static Module ImportModule(global::Aspire.Hosting.IDistributedApplicationBuilder builder)");
        source.AppendLine("    {");
        source.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(builder);");
        source.Append("        return new Module(global::Aspire.Hosting.ModularAppHosts.DistributedApplicationModuleExtensions.ImportModule(builder, ")
            .Append(SymbolDisplay.FormatLiteral(module.ModuleName, quote: true))
            .AppendLine("));");
        source.AppendLine("    }");
        source.AppendLine();
        source.AppendLine("    /// <summary>A materialized module with strongly typed access to every declared resource.</summary>");
        source.AppendLine("    public sealed class Module : global::Aspire.Hosting.ModularAppHosts.IDistributedApplicationModule");
        source.AppendLine("    {");
        source.AppendLine("        private readonly global::Aspire.Hosting.ModularAppHosts.IDistributedApplicationModule _module;");
        source.AppendLine();
        source.AppendLine("        internal Module(global::Aspire.Hosting.ModularAppHosts.IDistributedApplicationModule module)");
        source.AppendLine("        {");
        source.AppendLine("            _module = module;");
        source.AppendLine("        }");
        source.AppendLine();
        source.AppendLine("        /// <inheritdoc />");
        source.AppendLine("        public string Name => _module.Name;");
        source.AppendLine();
        source.AppendLine("        /// <inheritdoc />");
        source.AppendLine("        public global::System.Collections.Generic.IReadOnlyList<global::Aspire.Hosting.ModularAppHosts.IDistributedApplicationModuleResource> Resources => _module.Resources;");
        source.AppendLine();
        source.AppendLine("        /// <inheritdoc />");
        source.AppendLine("        public global::System.Collections.Generic.IReadOnlyList<global::Aspire.Hosting.ModularAppHosts.IDistributedApplicationModuleProject> Projects => _module.Projects;");
        source.AppendLine();
        source.AppendLine("        /// <inheritdoc />");
        source.AppendLine("        public global::System.Collections.Generic.IReadOnlyList<global::Aspire.Hosting.ModularAppHosts.IDistributedApplicationModuleContainer> Containers => _module.Containers;");
        source.AppendLine();
        source.AppendLine("        /// <inheritdoc />");
        source.AppendLine("        public global::Aspire.Hosting.ApplicationModel.IResourceBuilder<TResource> GetResource<TResource>(string name)");
        source.AppendLine("            where TResource : global::Aspire.Hosting.ApplicationModel.IResource");
        source.AppendLine("            => _module.GetResource<TResource>(name);");

        foreach (var resource in module.Resources)
        {
            source.AppendLine();
            source.Append("        /// <summary>Gets the '")
                .Append(resource.ResourceName)
                .AppendLine("' module resource.</summary>");
            source.Append("        public global::Aspire.Hosting.ApplicationModel.IResourceBuilder<")
                .Append(resource.TypeName)
                .Append("> ")
                .Append(resource.PropertyName)
                .Append(" => _module.GetResource<")
                .Append(resource.TypeName)
                .Append(">(")
                .Append(SymbolDisplay.FormatLiteral(resource.ResourceName, quote: true))
                .AppendLine(");");
        }

        source.AppendLine("    }");
        source.AppendLine("}");

        if (experimentalDiagnosticIds.Length > 0)
        {
            source.AppendLine();
            foreach (var diagnosticId in experimentalDiagnosticIds)
            {
                source.Append("#pragma warning restore ").AppendLine(diagnosticId);
            }
        }

        context.AddSource(
            GetHintName(module),
            SourceText.From(source.ToString(), Encoding.UTF8));
    }

    private static string GetHintName(ModuleModel module)
    {
        var fullName = module.Namespace is null
            ? module.TypeName
            : module.Namespace + "." + module.TypeName;
        var builder = new StringBuilder(fullName.Length + 12);

        foreach (var character in fullName)
        {
            builder.Append(char.IsLetterOrDigit(character) ? character : '_');
        }

        return builder.Append(".Module.g.cs").ToString();
    }

    private sealed class ModuleModel
    {
        public ModuleModel(
            string? @namespace,
            string typeName,
            string accessibility,
            string moduleName,
            ImmutableArray<ResourceModel> resources,
            ImmutableArray<DiagnosticInfo> diagnostics,
            bool canGenerate)
        {
            Namespace = @namespace;
            TypeName = typeName;
            Accessibility = accessibility;
            ModuleName = moduleName;
            Resources = resources;
            Diagnostics = diagnostics;
            CanGenerate = canGenerate;
        }

        public string? Namespace { get; }

        public string TypeName { get; }

        public string Accessibility { get; }

        public string ModuleName { get; }

        public ImmutableArray<ResourceModel> Resources { get; }

        public ImmutableArray<DiagnosticInfo> Diagnostics { get; }

        public bool CanGenerate { get; }
    }

    private sealed class ResourceModel
    {
        public ResourceModel(
            string resourceName,
            string propertyName,
            string typeName,
            ImmutableArray<string> experimentalDiagnosticIds,
            string filePath,
            int spanStart,
            Location location)
        {
            ResourceName = resourceName;
            PropertyName = propertyName;
            TypeName = typeName;
            ExperimentalDiagnosticIds = experimentalDiagnosticIds;
            FilePath = filePath;
            SpanStart = spanStart;
            Location = location;
        }

        public string ResourceName { get; }

        public string PropertyName { get; }

        public string TypeName { get; }

        public ImmutableArray<string> ExperimentalDiagnosticIds { get; }

        public string FilePath { get; }

        public int SpanStart { get; }

        public Location Location { get; }
    }

    private sealed class DiagnosticInfo
    {
        public DiagnosticInfo(DiagnosticDescriptor descriptor, Location location, params object[] messageArguments)
        {
            Descriptor = descriptor;
            Location = location;
            MessageArguments = messageArguments;
        }

        public DiagnosticDescriptor Descriptor { get; }

        public Location Location { get; }

        public object[] MessageArguments { get; }
    }
}
