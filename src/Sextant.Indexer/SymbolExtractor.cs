using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Sextant.Indexer;

public static partial class SymbolExtractor
{
    [GeneratedRegex(@"[/\\]obj[/\\]", RegexOptions.IgnoreCase)]
    private static partial Regex ObjDirPattern();

    private static readonly SymbolDisplayFormat FqnFormat = SymbolDisplayFormat.FullyQualifiedFormat;

    public static async Task<List<Sextant.Core.SymbolInfo>> ExtractFromProjectAsync(Project project, long projectId)
    {
        var compilation = await project.GetCompilationAsync();
        if (compilation == null)
            return [];

        var symbols = new List<Sextant.Core.SymbolInfo>();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        foreach (var syntaxTree in compilation.SyntaxTrees)
        {
            if (IsGeneratedFile(syntaxTree.FilePath))
                continue;

            var semanticModel = compilation.GetSemanticModel(syntaxTree);
            var root = await syntaxTree.GetRootAsync();

            foreach (var node in root.DescendantNodes())
            {
                var declaredSymbol = semanticModel.GetDeclaredSymbol(node);
                if (declaredSymbol == null)
                    continue;

                if (declaredSymbol.IsImplicitlyDeclared)
                    continue;

                var kind = MapSymbolKind(declaredSymbol);
                if (kind == null)
                    continue;

                var location = declaredSymbol.Locations.FirstOrDefault();
                if (location == null || !location.IsInSource)
                    continue;

                var lineSpan = location.GetLineSpan();
                var signature = GetSignature(declaredSymbol);

                symbols.Add(new Sextant.Core.SymbolInfo
                {
                    ProjectId = projectId,
                    FullyQualifiedName = declaredSymbol.ToDisplayString(FqnFormat),
                    DisplayName = declaredSymbol.Name,
                    Kind = kind.Value,
                    Accessibility = MapAccessibility(declaredSymbol.DeclaredAccessibility),
                    IsStatic = declaredSymbol.IsStatic,
                    IsAbstract = declaredSymbol.IsAbstract,
                    IsVirtual = declaredSymbol.IsVirtual,
                    IsOverride = declaredSymbol.IsOverride,
                    Signature = signature,
                    SignatureHash = signature != null ? HashSignature(signature) : null,
                    DocComment = GetDocComment(declaredSymbol),
                    FilePath = syntaxTree.FilePath,
                    LineStart = lineSpan.StartLinePosition.Line + 1,
                    LineEnd = lineSpan.EndLinePosition.Line + 1,
                    Attributes = GetAttributes(declaredSymbol),
                    LastIndexedAt = now
                });
            }
        }

        return symbols;
    }

    public static Sextant.Core.SymbolInfo? ExtractSymbolInfo(ISymbol declaredSymbol, long projectId)
    {
        var kind = MapSymbolKind(declaredSymbol);
        if (kind == null) return null;

        var location = declaredSymbol.Locations.FirstOrDefault();
        if (location == null || !location.IsInSource) return null;

        var lineSpan = location.GetLineSpan();
        var signature = GetSignature(declaredSymbol);

        return new Sextant.Core.SymbolInfo
        {
            ProjectId = projectId,
            FullyQualifiedName = declaredSymbol.ToDisplayString(FqnFormat),
            DisplayName = declaredSymbol.Name,
            Kind = kind.Value,
            Accessibility = MapAccessibility(declaredSymbol.DeclaredAccessibility),
            IsStatic = declaredSymbol.IsStatic,
            IsAbstract = declaredSymbol.IsAbstract,
            IsVirtual = declaredSymbol.IsVirtual,
            IsOverride = declaredSymbol.IsOverride,
            Signature = signature,
            SignatureHash = signature != null ? HashSignature(signature) : null,
            DocComment = GetDocComment(declaredSymbol),
            FilePath = location.SourceTree?.FilePath ?? "",
            LineStart = lineSpan.StartLinePosition.Line + 1,
            LineEnd = lineSpan.EndLinePosition.Line + 1,
            Attributes = GetAttributes(declaredSymbol),
            LastIndexedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
    }

    public static bool IsGeneratedFile(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return false;

        if (filePath.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
            filePath.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase))
            return true;

        if (ObjDirPattern().IsMatch(filePath))
            return true;

        return false;
    }

    public static Sextant.Core.SymbolKind? MapSymbolKind(ISymbol symbol)
    {
        return symbol switch
        {
            INamedTypeSymbol nts => nts.TypeKind switch
            {
                TypeKind.Class when nts.IsRecord => Sextant.Core.SymbolKind.Record,
                TypeKind.Class => Sextant.Core.SymbolKind.Class,
                TypeKind.Interface => Sextant.Core.SymbolKind.Interface,
                TypeKind.Struct when nts.IsRecord => Sextant.Core.SymbolKind.Record,
                TypeKind.Struct => Sextant.Core.SymbolKind.Struct,
                TypeKind.Enum => Sextant.Core.SymbolKind.Enum,
                TypeKind.Delegate => Sextant.Core.SymbolKind.Delegate,
                _ => null
            },
            IMethodSymbol ms => ms.MethodKind switch
            {
                MethodKind.Constructor or MethodKind.StaticConstructor => Sextant.Core.SymbolKind.Constructor,
                MethodKind.Ordinary or MethodKind.ExplicitInterfaceImplementation => Sextant.Core.SymbolKind.Method,
                _ => null
            },
            IPropertySymbol ps => ps.IsIndexer ? Sextant.Core.SymbolKind.Indexer : Sextant.Core.SymbolKind.Property,
            IFieldSymbol => Sextant.Core.SymbolKind.Field,
            IEventSymbol => Sextant.Core.SymbolKind.Event,
            ITypeParameterSymbol => Sextant.Core.SymbolKind.TypeParameter,
            _ => null
        };
    }

    public static Sextant.Core.Accessibility MapAccessibility(Microsoft.CodeAnalysis.Accessibility accessibility)
    {
        return accessibility switch
        {
            Microsoft.CodeAnalysis.Accessibility.Public => Sextant.Core.Accessibility.Public,
            Microsoft.CodeAnalysis.Accessibility.Internal => Sextant.Core.Accessibility.Internal,
            Microsoft.CodeAnalysis.Accessibility.Protected => Sextant.Core.Accessibility.Protected,
            Microsoft.CodeAnalysis.Accessibility.Private => Sextant.Core.Accessibility.Private,
            Microsoft.CodeAnalysis.Accessibility.ProtectedOrInternal => Sextant.Core.Accessibility.ProtectedInternal,
            Microsoft.CodeAnalysis.Accessibility.ProtectedAndInternal => Sextant.Core.Accessibility.PrivateProtected,
            _ => Sextant.Core.Accessibility.Private
        };
    }

    private static string? GetSignature(ISymbol symbol)
    {
        if (symbol is IMethodSymbol method)
            return method.ToDisplayString();
        if (symbol is IPropertySymbol property)
            return property.ToDisplayString();
        return null;
    }

    private static string HashSignature(string signature)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(signature));
        return Convert.ToHexStringLower(bytes);
    }

    private static string? GetDocComment(ISymbol symbol)
    {
        var xml = symbol.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(xml))
            return null;

        // Strip XML tags, keeping content
        var stripped = Regex.Replace(xml, @"<[^>]+>", " ").Trim();
        stripped = Regex.Replace(stripped, @"\s+", " ");
        return string.IsNullOrWhiteSpace(stripped) ? null : stripped;
    }

    private static string? GetAttributes(ISymbol symbol)
    {
        var attrs = symbol.GetAttributes();
        if (attrs.Length == 0)
            return null;

        var fqns = attrs
            .Where(a => a.AttributeClass != null)
            .Select(a => a.AttributeClass!.ToDisplayString(FqnFormat))
            .ToList();

        return fqns.Count > 0 ? JsonSerializer.Serialize(fqns) : null;
    }
}
