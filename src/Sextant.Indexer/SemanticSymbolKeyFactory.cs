using Microsoft.CodeAnalysis;
using Sextant.Core;

namespace Sextant.Indexer;

/// <summary>
/// The single canonical factory for <see cref="SemanticSymbolKey"/> and its declaration keys.
/// All identity decisions live here so definitions, occurrences, and edges agree on the same key.
/// </summary>
public static class SemanticSymbolKeyFactory
{
    private static readonly SymbolDisplayFormat FqnFormat = SymbolDisplayFormat.FullyQualifiedFormat;

    /// <summary>
    /// The stable declaration key for a symbol: its Roslyn documentation comment ID when one is
    /// available and well-formed, otherwise a deterministic versioned source-declaration fallback.
    /// Documentation IDs cover types and their members (encoding containing type, generic arity, and
    /// parameter signature); constructs without one (e.g. type parameters) use the fallback.
    /// </summary>
    public static string DeclarationKey(ISymbol symbol)
    {
        var docId = symbol.GetDocumentationCommentId();
        return IsUsableDocId(docId) ? docId! : SourceFallbackKey(symbol);
    }

    /// <summary>
    /// Builds the full <see cref="SemanticSymbolKey"/> logical identity — project canonical id, target
    /// framework, and declaration key. This is the canonical constructor for the identity abstraction
    /// the architecture models; the storage layer persists its normalized components (declaration key
    /// in <c>symbols.symbol_key</c>, project via the <c>project_id</c> foreign key), so extraction
    /// flows through <see cref="DeclarationKey"/> for the per-project component while this composes the
    /// flattened logical key used for equality assertions and the forward-looking versioned model.
    /// </summary>
    public static SemanticSymbolKey Create(ISymbol symbol, string projectCanonicalId, string? targetFramework)
        => new()
        {
            ProjectCanonicalId = projectCanonicalId,
            TargetFramework = targetFramework,
            DeclarationKey = DeclarationKey(symbol)
        };

    /// <summary>
    /// True when a symbol is an anonymous implementation artifact or otherwise lacks a stable
    /// top-level identity and must be excluded from query symbols and occurrences. Anonymous-object
    /// property names (e.g. <c>type</c>, <c>description</c>) and tuple field members are excluded.
    /// </summary>
    public static bool IsExcludedArtifact(ISymbol symbol)
    {
        if (symbol is INamedTypeSymbol { IsAnonymousType: true } or INamedTypeSymbol { IsTupleType: true })
            return true;

        var container = symbol.ContainingType;
        if (container is { IsAnonymousType: true } or { IsTupleType: true })
            return true;

        return false;
    }

    private static bool IsUsableDocId(string? docId)
    {
        if (string.IsNullOrEmpty(docId) || docId.Length < 3 || docId[1] != ':')
            return false;

        // Roslyn emits an "!:" prefix (with a diagnostic message) when it cannot form an ID.
        return docId[0] is 'T' or 'M' or 'P' or 'F' or 'E' or 'N';
    }

    private static string SourceFallbackKey(ISymbol symbol)
    {
        var kind = symbol.Kind.ToString();
        var location = symbol.Locations.FirstOrDefault(l => l.IsInSource)
                       ?? symbol.Locations.FirstOrDefault();

        if (location is { IsInSource: true })
        {
            var span = location.GetLineSpan();
            var file = NormalizePath(span.Path);
            var start = span.StartLinePosition;
            return $"src:{kind}:{file}:{start.Line}:{start.Character}:{symbol.MetadataName}";
        }

        // No source location (metadata-only): fall back to a fully-qualified metadata identity.
        var container = symbol.ContainingSymbol?.ToDisplayString(FqnFormat) ?? string.Empty;
        return $"meta:{kind}:{container}.{symbol.MetadataName}";
    }

    private static string NormalizePath(string path)
        => string.IsNullOrEmpty(path) ? string.Empty : path.Replace('\\', '/');
}
