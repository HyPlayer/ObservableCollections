using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ObservableCollections.SourceGenerator;

/// <summary>
/// Generates a real C# input before CoreCompile so CsWinRT's source generator
/// can observe the exposure attributes in its initial compilation.
/// </summary>
internal static class ObservableCollectionsWinRTGenerator
{
    private static readonly SymbolDisplayFormat TypeDisplayFormat =
        new(
            globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Included,
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
            miscellaneousOptions:
                SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers |
                SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    public static int Main(string[] args)
    {
        try
        {
            var options = ParseArguments(args);
            Generate(options.OutputFile, options.SourcesFile, options.ReferencesFile);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"ObservableCollections.SourceGenerator: {exception}");
            return 1;
        }
    }

    private static string? GetViewType(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel,
        System.Threading.CancellationToken cancellationToken)
    {
        var method = semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol as IMethodSymbol;
        if (method is null)
            return null;

        var definition = method.ReducedFrom ?? method;
        var containingType = method.ContainingType;
        var containingTypeDefinition = containingType.OriginalDefinition.ToDisplayString();

        if (definition.ContainingType.ToDisplayString() ==
            "ObservableCollections.ObservableCollectionExtensions")
        {
            if (method.Name is not ("ToNotifyCollectionChanged" or "ToViewList") ||
                method.TypeArguments.Length is < 1 or > 2 ||
                method.TypeArguments.Any(static type => !IsClosed(type)))
            {
                return null;
            }

            var sourceType = method.TypeArguments[0].ToDisplayString(TypeDisplayFormat);
            var viewType = method.TypeArguments.Length == 1
                ? sourceType
                : method.TypeArguments[1].ToDisplayString(TypeDisplayFormat);

            return ClosedGenericType(
                "NonFilteredSynchronizedViewList",
                sourceType,
                viewType);
        }

        if (containingTypeDefinition is
                "ObservableCollections.ISynchronizedView<T, TView>" or
                "ObservableCollections.IWritableSynchronizedView<T, TView>" &&
            method.Name is
                "ToNotifyCollectionChanged" or
                "ToViewList" or
                "ToWritableNotifyCollectionChanged" or
                "ToWritableViewList" &&
            containingType.TypeArguments is [var source, var view] &&
            IsClosed(source) &&
            IsClosed(view))
        {
            return ClosedGenericType(
                "FiltableSynchronizedViewList",
                source.ToDisplayString(TypeDisplayFormat),
                view.ToDisplayString(TypeDisplayFormat));
        }

        if (containingTypeDefinition == "ObservableCollections.ObservableList<T>" &&
            containingType.TypeArguments is [var item] &&
            IsClosed(item))
        {
            var itemType = item.ToDisplayString(TypeDisplayFormat);
            if (method.Name == "ToNotifyCollectionChangedSlim")
                return $"global::ObservableCollections.ObservableListSynchronizedViewList<{itemType}>";

            if (method.Name == "ToWritableNotifyCollectionChanged")
            {
                if (method.TypeArguments.Any(static type => !IsClosed(type)))
                    return null;

                var viewType = method.TypeArguments.Length == 0
                    ? itemType
                    : method.TypeArguments[0].ToDisplayString(TypeDisplayFormat);
                return ClosedGenericType(
                    "NonFilteredSynchronizedViewList",
                    itemType,
                    viewType);
            }
        }

        return null;
    }

    private static string ClosedGenericType(string name, string sourceType, string viewType) =>
        $"global::ObservableCollections.{name}<{sourceType}, {viewType}>";

    private static string? GetIncrementalCollectionType(
        ExpressionSyntax creation,
        SemanticModel semanticModel,
        System.Threading.CancellationToken cancellationToken)
    {
        var type = semanticModel.GetTypeInfo(creation, cancellationToken).Type;

        if (type is not INamedTypeSymbol namedType ||
            namedType.IsAbstract ||
            !IsClosed(namedType) ||
            !namedType.AllInterfaces.Any(static interfaceType =>
                interfaceType.ToDisplayString() is
                    "Windows.UI.Xaml.Data.ISupportIncrementalLoading" or
                    "Microsoft.UI.Xaml.Data.ISupportIncrementalLoading"))
        {
            return null;
        }

        return namedType.ToDisplayString(TypeDisplayFormat);
    }

    private static bool IsClosed(ITypeSymbol type)
    {
        if (type.TypeKind is TypeKind.Error or TypeKind.TypeParameter)
            return false;

        return type is not INamedTypeSymbol namedType ||
               namedType.TypeArguments.All(static argument => IsClosed(argument));
    }

    private static void Generate(string outputFile, string sourcesFile, string referencesFile)
    {
        var outputPath = Path.GetFullPath(outputFile);
        var syntaxTrees = ReadPaths(sourcesFile)
            .Where(File.Exists)
            .Where(path => !Path.GetFullPath(path).Equals(outputPath, StringComparison.OrdinalIgnoreCase))
            .Select(path => CSharpSyntaxTree.ParseText(
                File.ReadAllText(path),
                new CSharpParseOptions(LanguageVersion.Preview),
                path))
            .ToArray();

        var references = ReadPaths(referencesFile)
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(static path => MetadataReference.CreateFromFile(path))
            .ToArray();

        var compilation = CSharpCompilation.Create(
            "ObservableCollections.WinRTExposureAnalysis",
            syntaxTrees,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var typeNames = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var syntaxTree in syntaxTrees)
        {
            var semanticModel = compilation.GetSemanticModel(syntaxTree);
            var root = syntaxTree.GetRoot();

            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                AddIfPresent(typeNames, GetViewType(invocation, semanticModel, default));

            foreach (var creation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
                AddIfPresent(typeNames, GetIncrementalCollectionType(creation, semanticModel, default));

            foreach (var creation in root.DescendantNodes().OfType<ImplicitObjectCreationExpressionSyntax>())
                AddIfPresent(typeNames, GetIncrementalCollectionType(creation, semanticModel, default));
        }

        var source = new StringBuilder(
            "// <auto-generated/>\n" +
            "// Generated before CoreCompile. Do not edit.\n" +
            "#nullable enable\n\n");

        foreach (var typeName in typeNames)
        {
            source.Append("[assembly: global::WinRT.GeneratedWinRTExposedExternalTypeAttribute(typeof(")
                .Append(typeName)
                .AppendLine("))]");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var contents = source.ToString();
        if (!File.Exists(outputPath) || File.ReadAllText(outputPath) != contents)
            File.WriteAllText(outputPath, contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        Console.WriteLine(
            $"ObservableCollections.SourceGenerator generated {typeNames.Count} WinRT exposed type(s) at {outputPath}.");
    }

    private static void AddIfPresent(ISet<string> typeNames, string? candidate)
    {
        if (!string.IsNullOrWhiteSpace(candidate))
            typeNames.Add(candidate!);
    }

    private static IEnumerable<string> ReadPaths(string responseFile) =>
        File.ReadLines(responseFile)
            .Select(static line => line.Trim().Trim('"'))
            .Where(static line => line.Length != 0);

    private static Options ParseArguments(string[] args)
    {
        string? output = null;
        string? sources = null;
        string? references = null;

        for (var index = 0; index < args.Length; index++)
        {
            var value = index + 1 < args.Length ? args[index + 1] : null;
            switch (args[index])
            {
                case "--output":
                    output = value;
                    index++;
                    break;
                case "--sources":
                    sources = value;
                    index++;
                    break;
                case "--references":
                    references = value;
                    index++;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {args[index]}");
            }
        }

        if (string.IsNullOrWhiteSpace(output) ||
            string.IsNullOrWhiteSpace(sources) ||
            string.IsNullOrWhiteSpace(references))
        {
            throw new ArgumentException("Required arguments: --output, --sources, --references.");
        }

        return new Options(output!, sources!, references!);
    }

    private sealed record Options(string OutputFile, string SourcesFile, string ReferencesFile);
}
