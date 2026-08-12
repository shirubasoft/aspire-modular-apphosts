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

namespace Aspire.Hosting;

/// <summary>
/// Generates strongly typed accessors for resources declared by distributed application modules.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class DistributedApplicationModuleGenerator : IIncrementalGenerator
{
    private static readonly string GeneratorVersion =
        typeof(DistributedApplicationModuleGenerator).Assembly.GetName().Version?.ToString() ?? "unknown";

    private const string AttributeMetadataName =
        "Aspire.Hosting.GenerateDistributedApplicationModuleAttribute";

    private const string ModuleBuilderMetadataName =
        "Aspire.Hosting.IDistributedApplicationModuleBuilder";

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

    private static readonly DiagnosticDescriptor InvalidModuleVersion = new(
        "SAMHSG007",
        "Invalid generated module version",
        "The module version supplied to GenerateDistributedApplicationModule must be a non-empty compile-time string",
        "Shirubasoft.Aspire.ModularAppHosts",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InaccessibleResourceType = new(
        "SAMHSG008",
        "Resource type is less accessible than the generated module API",
        "Resource type '{0}' cannot be exposed by generated module '{1}'; make the resource type and its containing types at least as accessible as the module",
        "Shirubasoft.Aspire.ModularAppHosts",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidPackageId = new(
        "SAMHSG009",
        "Invalid module package ID",
        "The PackageId supplied to GenerateDistributedApplicationModule must be a valid NuGet package ID",
        "Shirubasoft.Aspire.ModularAppHosts",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly HashSet<string> ReservedResourcePropertyNames = new(StringComparer.Ordinal)
    {
        "Name",
        "Version",
        "PackageId",
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

        var moduleVersion = attribute.NamedArguments
            .FirstOrDefault(argument => argument.Key == "Version").Value.Value as string ?? "1";
        if (string.IsNullOrWhiteSpace(moduleVersion))
        {
            diagnostics.Add(new DiagnosticInfo(InvalidModuleVersion, moduleLocation));
            canGenerate = false;
        }

        var packageId = attribute.NamedArguments
            .FirstOrDefault(argument => argument.Key == "PackageId").Value.Value as string;
        if (packageId is not null && !IsValidPackageId(packageId))
        {
            diagnostics.Add(new DiagnosticInfo(InvalidPackageId, moduleLocation));
            canGenerate = false;
        }

        var moduleBuilderType = context.SemanticModel.Compilation.GetTypeByMetadataName(ModuleBuilderMetadataName);
        var conventionalDefineMethods = symbol.GetMembers("Define")
            .OfType<IMethodSymbol>()
            .Where(method => method.IsStatic &&
                method.ReturnsVoid &&
                method.Parameters.Length == 1 &&
                SymbolEqualityComparer.Default.Equals(method.Parameters[0].Type, moduleBuilderType))
            .ToImmutableArray();
        var definitionMethods = conventionalDefineMethods.Length > 0
            ? conventionalDefineMethods
            : symbol.GetMembers()
                .OfType<IMethodSymbol>()
                .Where(method => method.IsStatic && method.Parameters.Any(parameter =>
                    SymbolEqualityComparer.Default.Equals(parameter.Type, moduleBuilderType)))
                .ToImmutableArray();

        var extensionMethodStem = char.ToUpperInvariant(symbol.Name[0]) + symbol.Name.Substring(1);
        var addExtensionMethodName = "Add" + extensionMethodStem;
        var importExtensionMethodName = "Import" + extensionMethodStem;
        foreach (var reservedMemberName in new[] { addExtensionMethodName, importExtensionMethodName, "Reference", "Module" })
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

        var resources = CollectResources(
            symbol,
            definitionMethods,
            context.SemanticModel.Compilation,
            diagnostics,
            cancellationToken);
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
            addExtensionMethodName,
            importExtensionMethodName,
            symbol.DeclaredAccessibility == Accessibility.Public ? "public" : "internal",
            moduleName ?? string.Empty,
            moduleVersion,
            packageId,
            conventionalDefineMethods.Length > 0,
            resources,
            diagnostics.ToImmutable(),
            canGenerate);
    }

    private static bool IsValidPackageId(string packageId) =>
        !string.IsNullOrWhiteSpace(packageId) &&
        packageId.Length <= 100 &&
        packageId.All(character =>
            character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '-' or '_');

    private static ImmutableArray<ResourceModel> CollectResources(
        INamedTypeSymbol moduleSymbol,
        ImmutableArray<IMethodSymbol> definitionMethods,
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
                var enclosingSymbol = semanticModel.GetEnclosingSymbol(invocation.SpanStart, cancellationToken);
                var belongsToDefinitionMethod = definitionMethods.Any(method =>
                    SymbolEqualityComparer.Default.Equals(enclosingSymbol, method));
                if (!belongsToDefinitionMethod &&
                    !IsInsideModuleDefinitionLambda(invocation, semanticModel, cancellationToken))
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

                if (!IsAccessibleForGeneratedApi(resourceType, moduleSymbol, compilation))
                {
                    diagnostics.Add(new DiagnosticInfo(
                        InaccessibleResourceType,
                        invocation.GetLocation(),
                        resourceType.ToDisplayString(),
                        moduleSymbol.ToDisplayString()));
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

    private static bool IsAccessibleForGeneratedApi(
        ITypeSymbol resourceType,
        INamedTypeSymbol moduleSymbol,
        Compilation compilation)
    {
        if (!compilation.IsSymbolAccessibleWithin(resourceType, moduleSymbol))
        {
            return false;
        }

        var requiresPublicAccessibility = moduleSymbol.DeclaredAccessibility == Accessibility.Public;
        return HasSufficientAccessibility(resourceType, requiresPublicAccessibility);
    }

    private static bool HasSufficientAccessibility(ITypeSymbol type, bool requiresPublicAccessibility)
    {
        if (type is IArrayTypeSymbol arrayType)
        {
            return HasSufficientAccessibility(arrayType.ElementType, requiresPublicAccessibility);
        }

        if (type is not INamedTypeSymbol namedType)
        {
            return true;
        }

        for (var current = namedType; current is not null; current = current.ContainingType)
        {
            if (requiresPublicAccessibility)
            {
                if (current.DeclaredAccessibility != Accessibility.Public)
                {
                    return false;
                }
            }
            else if (current.DeclaredAccessibility is not (
                Accessibility.Public or
                Accessibility.Internal or
                Accessibility.ProtectedOrInternal))
            {
                return false;
            }
        }

        return namedType.TypeArguments.All(argument =>
            HasSufficientAccessibility(argument, requiresPublicAccessibility));
    }

    private static bool IsInsideModuleDefinitionLambda(
        InvocationExpressionSyntax resourceInvocation,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        foreach (var lambda in resourceInvocation.Ancestors().OfType<AnonymousFunctionExpressionSyntax>())
        {
            IOperation? operation = semanticModel.GetOperation(lambda, cancellationToken);
            while (operation is not null && operation is not IArgumentOperation)
            {
                operation = operation.Parent;
            }

            if (operation is IArgumentOperation { Parameter.Name: "moduleBuilder", Parent: IInvocationOperation owner } &&
                owner.TargetMethod.Name is "DefineModule" or "ExportModule" &&
                owner.TargetMethod.ContainingType.ToDisplayString() ==
                    "Aspire.Hosting.DistributedApplicationModuleExtensions")
            {
                return true;
            }
        }

        return false;
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

        source.Append("[global::System.CodeDom.Compiler.GeneratedCode(\"Shirubasoft.Aspire.ModularAppHosts\", ")
            .Append(SymbolDisplay.FormatLiteral(GeneratorVersion, quote: true))
            .AppendLine(")]");
        source.Append(module.Accessibility)
            .Append(" static partial class ")
            .Append(module.TypeName)
            .AppendLine();
        source.AppendLine("{");
        AppendSummary(
            source,
            "    ",
            $"Gets a strongly typed reference to module '{module.ModuleName}' version '{module.ModuleVersion}' from another module definition.");
        AppendParameter(source, "    ", "moduleBuilder", "The module definition that requires this contract.");
        AppendReturns(source, "    ", $"A typed reference to module '{module.ModuleName}'.");
        AppendException(
            source,
            "    ",
            "global::System.ArgumentNullException",
            "moduleBuilder is null.");
        AppendException(
            source,
            "    ",
            "global::System.InvalidOperationException",
            $"Module '{module.ModuleName}' is missing, has an incompatible version, or is not materialized yet.");
        source.AppendLine("    public static Module Reference(");
        source.AppendLine("        global::Aspire.Hosting.IDistributedApplicationModuleBuilder moduleBuilder)");
        source.AppendLine("    {");
        source.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(moduleBuilder);");
        source.Append("        return new Module(moduleBuilder.GetRequiredModule(")
            .Append(SymbolDisplay.FormatLiteral(module.ModuleName, quote: true))
            .Append(", ")
            .Append(SymbolDisplay.FormatLiteral(module.ModuleVersion, quote: true))
            .AppendLine("));");
        source.AppendLine("    }");
        source.AppendLine();
        if (module.HasConventionalDefineMethod)
        {
            AppendSummary(
                source,
                "    ",
                $"Defines and adds module '{module.ModuleName}' version '{module.ModuleVersion}' and returns its typed resources.");
            AppendParameter(source, "    ", "builder", "The receiving Aspire application builder.");
            AppendReturns(source, "    ", $"The materialized '{module.ModuleName}' module.");
            AppendException(source, "    ", "global::System.ArgumentNullException", "builder is null.");
            AppendException(
                source,
                "    ",
                "global::System.InvalidOperationException",
                "The definition or synchronous resource materialization is invalid.");
            source.Append("    public static Module ")
                .Append(module.AddExtensionMethodName)
                .AppendLine("(");
            source.AppendLine("        this global::Aspire.Hosting.IDistributedApplicationBuilder builder)");
            source.AppendLine("    {");
            source.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(builder);");
            source.Append("        var module = global::Aspire.Hosting.DistributedApplicationModuleExtensions.DefineModule(builder, ")
                .Append(SymbolDisplay.FormatLiteral(module.ModuleName, quote: true))
                .Append(", ")
                .Append(SymbolDisplay.FormatLiteral(module.ModuleVersion, quote: true))
                .Append(", ")
                .Append(module.PackageId is null
                    ? "null"
                    : SymbolDisplay.FormatLiteral(module.PackageId, quote: true))
                .AppendLine(", Define);");
            source.Append("        return ")
                .Append(module.AddExtensionMethodName)
                .AppendLine("(builder, module);");
            source.AppendLine("    }");
            source.AppendLine();
        }

        AppendSummary(
            source,
            "    ",
            $"Adds an exported '{module.ModuleName}' module definition to the AppHost and returns its typed resources.");
        AppendParameter(source, "    ", "builder", "The receiving Aspire application builder.");
        AppendParameter(source, "    ", "module", $"The exported '{module.ModuleName}' module definition.");
        AppendReturns(source, "    ", $"The materialized '{module.ModuleName}' module.");
        AppendException(
            source,
            "    ",
            "global::System.ArgumentNullException",
            "builder or module is null.");
        AppendException(
            source,
            "    ",
            "global::System.ArgumentException",
            $"module does not identify '{module.ModuleName}' version '{module.ModuleVersion}' with the expected package ID.");
        AppendException(
            source,
            "    ",
            "global::System.InvalidOperationException",
            "Synchronous resource materialization is invalid.");
        source.Append("    public static Module ")
            .Append(module.AddExtensionMethodName)
            .AppendLine("(");
        source.AppendLine("        this global::Aspire.Hosting.IDistributedApplicationBuilder builder,");
        source.AppendLine("        global::Aspire.Hosting.IDistributedApplicationModule module)");
        source.AppendLine("    {");
        source.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(builder);");
        source.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(module);");
        source.Append("        if (!global::System.String.Equals(module.Name, ")
            .Append(SymbolDisplay.FormatLiteral(module.ModuleName, quote: true))
            .AppendLine(", global::System.StringComparison.Ordinal) ||");
        source.Append("            !global::System.String.Equals(module.Version, ")
            .Append(SymbolDisplay.FormatLiteral(module.ModuleVersion, quote: true))
            .AppendLine(", global::System.StringComparison.Ordinal) ||");
        source.Append("            !global::System.String.Equals(module.PackageId, ")
            .Append(module.PackageId is null
                ? "null"
                : SymbolDisplay.FormatLiteral(module.PackageId, quote: true))
            .AppendLine(", global::System.StringComparison.Ordinal))");
        source.AppendLine("        {");
        source.Append("            throw new global::System.ArgumentException(")
            .Append(SymbolDisplay.FormatLiteral(
                $"Expected module '{module.ModuleName}' with contract version '{module.ModuleVersion}' " +
                $"and package ID '{module.PackageId ?? "none"}'.",
                quote: true))
            .AppendLine(", nameof(module));");
        source.AppendLine("        }");
        source.AppendLine();
        source.AppendLine("        global::Aspire.Hosting.DistributedApplicationModuleExtensions.AddModule(builder, module);");
        source.AppendLine("        return new Module(module);");
        source.AppendLine("    }");
        source.AppendLine();
        AppendSummary(
            source,
            "    ",
            $"Imports module '{module.ModuleName}' version '{module.ModuleVersion}' with default resource names.");
        AppendInitializationRemarks(source, "    ", module.ModuleName);
        AppendParameter(source, "    ", "builder", "The receiving Aspire application builder.");
        AppendReturns(source, "    ", $"The imported '{module.ModuleName}' module.");
        AppendException(source, "    ", "global::System.ArgumentNullException", "builder is null.");
        AppendException(
            source,
            "    ",
            "global::System.InvalidOperationException",
            "The definition, repository preflight requirements, or synchronous materialization is invalid.");
        source.Append("    public static Module ")
            .Append(module.ImportExtensionMethodName)
            .AppendLine("(");
        source.AppendLine("        this global::Aspire.Hosting.IDistributedApplicationBuilder builder)");
        source.AppendLine("    {");
        source.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(builder);");
        source.Append("        return ")
            .Append(module.ImportExtensionMethodName)
            .AppendLine("(builder, new global::Aspire.Hosting.ModuleImportOptions());");
        source.AppendLine("    }");
        source.AppendLine();
        AppendSummary(
            source,
            "    ",
            $"Imports module '{module.ModuleName}' version '{module.ModuleVersion}' with resource naming options.");
        AppendInitializationRemarks(source, "    ", module.ModuleName);
        AppendParameter(source, "    ", "builder", "The receiving Aspire application builder.");
        AppendParameter(source, "    ", "options", "Resource prefixes and aliases for this import.");
        AppendReturns(source, "    ", $"The imported '{module.ModuleName}' module.");
        AppendException(
            source,
            "    ",
            "global::System.ArgumentNullException",
            "builder or options is null.");
        AppendException(
            source,
            "    ",
            "global::System.InvalidOperationException",
            "The definition, import naming, repository preflight requirements, or synchronous materialization is invalid.");
        source.Append("    public static Module ")
            .Append(module.ImportExtensionMethodName)
            .AppendLine("(");
        source.AppendLine("        this global::Aspire.Hosting.IDistributedApplicationBuilder builder,");
        source.AppendLine("        global::Aspire.Hosting.ModuleImportOptions options)");
        source.AppendLine("    {");
        source.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(builder);");
        source.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(options);");
        if (module.HasConventionalDefineMethod)
        {
            source.Append("        global::Aspire.Hosting.DistributedApplicationModuleExtensions.DefineModule(builder, ")
                .Append(SymbolDisplay.FormatLiteral(module.ModuleName, quote: true))
                .Append(", ")
                .Append(SymbolDisplay.FormatLiteral(module.ModuleVersion, quote: true))
                .Append(", ")
                .Append(module.PackageId is null
                    ? "null"
                    : SymbolDisplay.FormatLiteral(module.PackageId, quote: true))
                .AppendLine(", Define);");
        }

        source.Append("        var module = global::Aspire.Hosting.DistributedApplicationModuleExtensions.ImportModule(builder, ")
            .Append(SymbolDisplay.FormatLiteral(module.ModuleName, quote: true))
            .AppendLine(", options);");
        source.AppendLine("        return new Module(module);");
        source.AppendLine("    }");
        source.AppendLine();
        AppendSummary(
            source,
            "    ",
            $"A materialized '{module.ModuleName}' module with strongly typed access to every declared resource.");
        source.AppendLine("    public sealed class Module : global::Aspire.Hosting.DistributedApplicationModuleReference");
        source.AppendLine("    {");
        source.AppendLine("        internal Module(global::Aspire.Hosting.IDistributedApplicationModule module)");
        source.AppendLine("            : base(module)");
        source.AppendLine("        {");
        source.AppendLine("        }");

        foreach (var resource in module.Resources)
        {
            source.AppendLine();
            AppendSummary(
                source,
                "        ",
                $"Gets the '{resource.ResourceName}' resource declared by module '{module.ModuleName}'.");
            AppendException(
                source,
                "        ",
                "global::System.InvalidOperationException",
                $"Resource '{resource.ResourceName}' is unavailable or its materialized type is incompatible with the contract.");
            source.Append("        public global::Aspire.Hosting.ApplicationModel.IResourceBuilder<")
                .Append(resource.TypeName)
                .Append("> ")
                .Append(resource.PropertyName)
                .Append(" => GetResource<")
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

    private static void AppendSummary(
        StringBuilder source,
        string indentation,
        string summary) =>
        source.Append(indentation)
            .Append("/// <summary>")
            .Append(EscapeXmlDocumentation(summary))
            .AppendLine("</summary>");

    private static void AppendInitializationRemarks(
        StringBuilder source,
        string indentation,
        string moduleName) =>
        source.Append(indentation)
            .Append("/// <remarks>Repository-backed imports of '")
            .Append(EscapeXmlDocumentation(moduleName))
            .Append("' require aspire do initialize --apphost &lt;path&gt; --non-interactive when normal-run preflight requests initialization.</remarks>")
            .AppendLine();

    private static void AppendParameter(
        StringBuilder source,
        string indentation,
        string name,
        string description) =>
        source.Append(indentation)
            .Append("/// <param name=\"")
            .Append(name)
            .Append("\">")
            .Append(EscapeXmlDocumentation(description))
            .AppendLine("</param>");

    private static void AppendReturns(
        StringBuilder source,
        string indentation,
        string description) =>
        source.Append(indentation)
            .Append("/// <returns>")
            .Append(EscapeXmlDocumentation(description))
            .AppendLine("</returns>");

    private static void AppendException(
        StringBuilder source,
        string indentation,
        string exceptionType,
        string description) =>
        source.Append(indentation)
            .Append("/// <exception cref=\"")
            .Append(exceptionType)
            .Append("\">")
            .Append(EscapeXmlDocumentation(description))
            .AppendLine("</exception>");

    private static string EscapeXmlDocumentation(string value) =>
        value.Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;")
            .Replace("\r", "&#xD;")
            .Replace("\n", "&#xA;");

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
            string addExtensionMethodName,
            string importExtensionMethodName,
            string accessibility,
            string moduleName,
            string moduleVersion,
            string? packageId,
            bool hasConventionalDefineMethod,
            ImmutableArray<ResourceModel> resources,
            ImmutableArray<DiagnosticInfo> diagnostics,
            bool canGenerate)
        {
            Namespace = @namespace;
            TypeName = typeName;
            AddExtensionMethodName = addExtensionMethodName;
            ImportExtensionMethodName = importExtensionMethodName;
            Accessibility = accessibility;
            ModuleName = moduleName;
            ModuleVersion = moduleVersion;
            PackageId = packageId;
            HasConventionalDefineMethod = hasConventionalDefineMethod;
            Resources = resources;
            Diagnostics = diagnostics;
            CanGenerate = canGenerate;
        }

        public string? Namespace { get; }

        public string TypeName { get; }

        public string AddExtensionMethodName { get; }

        public string ImportExtensionMethodName { get; }

        public string Accessibility { get; }

        public string ModuleName { get; }

        public string ModuleVersion { get; }

        public string? PackageId { get; }

        public bool HasConventionalDefineMethod { get; }

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
