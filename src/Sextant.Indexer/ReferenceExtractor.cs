using Sextant.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;

namespace Sextant.Indexer;

public static class ReferenceExtractor
{
    public static async Task<List<ReferenceInfo>> ExtractReferencesAsync(
        ISymbol symbol, long symbolId, Solution solution,
        Dictionary<string, long> projectPathToId)
    {
        var references = new List<ReferenceInfo>();

        var referencedSymbols = await SymbolFinder.FindReferencesAsync(symbol, solution);

        foreach (var refSymbol in referencedSymbols)
        {
            foreach (var location in refSymbol.Locations)
            {
                var doc = location.Document;
                if (doc.FilePath == null)
                    continue;

                var projectId = GetProjectId(doc.Project, projectPathToId);
                if (projectId == null)
                    continue;

                var lineSpan = location.Location.GetLineSpan();
                var referenceKind = await ClassifyReferenceKindAsync(location, doc);
                var snippet = await GetContextSnippetAsync(location, doc);
                var accessKind = await ClassifyAccessKindAsync(location, doc, symbol);

                references.Add(new ReferenceInfo
                {
                    SymbolId = symbolId,
                    InProjectId = projectId.Value,
                    FilePath = doc.FilePath,
                    Line = lineSpan.StartLinePosition.Line + 1,
                    ContextSnippet = snippet,
                    ReferenceKind = referenceKind,
                    AccessKind = accessKind
                });
            }
        }

        return references;
    }

    private static async Task<ReferenceKind> ClassifyReferenceKindAsync(ReferenceLocation location, Document document)
    {
        var root = await document.GetSyntaxRootAsync();
        if (root == null)
            return ReferenceKind.TypeRef;

        var node = root.FindNode(location.Location.SourceSpan);
        if (node == null)
            return ReferenceKind.TypeRef;

        // Walk up to find enclosing syntax
        var current = node;
        while (current != null)
        {
            switch (current)
            {
                case InvocationExpressionSyntax:
                    return ReferenceKind.Invocation;
                case ObjectCreationExpressionSyntax:
                case ImplicitObjectCreationExpressionSyntax:
                    return ReferenceKind.ObjectCreation;
                case BaseListSyntax:
                    return ReferenceKind.Inheritance;
                case AttributeSyntax:
                    return ReferenceKind.Attribute;
            }

            // Check for override keyword
            if (current is MemberDeclarationSyntax memberDecl)
            {
                var hasOverride = memberDecl.Modifiers.Any(m => m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.OverrideKeyword));
                if (hasOverride)
                    return ReferenceKind.Override;
            }

            current = current.Parent;
        }

        return ReferenceKind.TypeRef;
    }

    private static async Task<string?> GetContextSnippetAsync(ReferenceLocation location, Document document)
    {
        var text = await document.GetTextAsync();
        var span = location.Location.SourceSpan;

        var start = Math.Max(0, span.Start - 60);
        var end = Math.Min(text.Length, span.End + 60);
        var snippet = text.GetSubText(Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(start, end)).ToString();

        // Collapse whitespace
        snippet = System.Text.RegularExpressions.Regex.Replace(snippet, @"\s+", " ").Trim();

        return snippet.Length > 120 ? snippet[..120] : snippet;
    }

    private static async Task<AccessKind?> ClassifyAccessKindAsync(
        ReferenceLocation location, Document document, ISymbol referencedSymbol)
    {
        if (referencedSymbol is not (IFieldSymbol or IPropertySymbol))
            return null;

        var root = await document.GetSyntaxRootAsync();
        if (root == null) return AccessKind.Read;

        var node = root.FindNode(location.Location.SourceSpan);
        if (node == null) return AccessKind.Read;

        return ClassifyAccessKind(node);
    }

    internal static AccessKind? ClassifyAccessKind(SyntaxNode node)
    {
        var current = node;
        while (current != null)
        {
            if (current.Parent is AssignmentExpressionSyntax assignment)
            {
                if (assignment.Left.Span.Contains(node.Span))
                {
                    return assignment.Kind() == SyntaxKind.SimpleAssignmentExpression
                        ? AccessKind.Write
                        : AccessKind.ReadWrite;
                }
                return AccessKind.Read;
            }

            if (current.Parent is PostfixUnaryExpressionSyntax or PrefixUnaryExpressionSyntax)
            {
                var kind = current.Parent.Kind();
                if (kind is SyntaxKind.PostIncrementExpression or SyntaxKind.PostDecrementExpression
                         or SyntaxKind.PreIncrementExpression or SyntaxKind.PreDecrementExpression)
                    return AccessKind.ReadWrite;
            }

            if (current.Parent is ArgumentSyntax arg)
            {
                if (arg.RefKindKeyword.IsKind(SyntaxKind.OutKeyword))
                    return AccessKind.Write;
                if (arg.RefKindKeyword.IsKind(SyntaxKind.RefKeyword))
                    return AccessKind.ReadWrite;
            }

            current = current.Parent;
        }

        return AccessKind.Read;
    }

    private static long? GetProjectId(Project project, Dictionary<string, long> projectPathToId)
    {
        if (project.FilePath != null && projectPathToId.TryGetValue(project.FilePath, out var id))
            return id;
        return null;
    }
}
