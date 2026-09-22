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
            var file = RepoRelativeOrNormalized(span.Path);
            var start = span.StartLinePosition;
            return $"src:{kind}:{file}:{start.Line}:{start.Character}:{symbol.MetadataName}";
        }

        // No source location (metadata-only): fall back to a fully-qualified metadata identity. Include
        // the declaring assembly and a signature suffix so overloaded members that lack a documentation
        // ID (same container + metadata name, differing arity/parameters) do not collide.
        var container = symbol.ContainingSymbol?.ToDisplayString(FqnFormat) ?? string.Empty;
        var assembly = symbol.ContainingAssembly?.Identity.GetDisplayName() ?? string.Empty;
        return $"meta:{kind}:{assembly}:{container}.{symbol.MetadataName}{MetadataSignatureSuffix(symbol)}";
    }

    /// <summary>
    /// A deterministic disambiguating suffix for metadata symbols that would otherwise collide by
    /// container and metadata name: generic arity plus the parameter signature (each parameter's
    /// by-ref kind and fully-qualified type) for methods and parameterized properties (indexers).
    /// Conversion operators additionally carry their return type, since two conversions can share a
    /// source parameter but differ only in target type.
    /// </summary>
    private static string MetadataSignatureSuffix(ISymbol symbol) => symbol switch
    {
        IMethodSymbol m =>
            $"`{m.Arity}({string.Join(",", m.Parameters.Select(ParameterSignature))})"
            + (m.MethodKind == MethodKind.Conversion ? $":{m.ReturnType.ToDisplayString(FqnFormat)}" : string.Empty),
        IPropertySymbol { Parameters.Length: > 0 } p =>
            $"[{string.Join(",", p.Parameters.Select(ParameterSignature))}]",
        _ => string.Empty
    };

    /// <summary>
    /// Encodes one parameter as its by-ref kind (when not by-value) plus its fully-qualified type, so
    /// overloads differing only by <c>ref</c>/<c>out</c>/<c>in</c> get distinct fallback keys.
    /// </summary>
    private static string ParameterSignature(IParameterSymbol p)
    {
        var type = p.Type.ToDisplayString(FqnFormat);
        return p.RefKind == RefKind.None
            ? type
            : $"{p.RefKind.ToString().ToLowerInvariant()} {type}";
    }

    private static string NormalizePath(string path)
        => string.IsNullOrEmpty(path) ? string.Empty : path.Replace('\\', '/');

    // Memoize git-root resolution per directory: source-fallback keys are rare, but a single file's
    // symbols share a directory, so this avoids repeatedly walking the tree for the same path.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string?> GitRootByDir = new();

    /// <summary>
    /// Renders a source-declaration path as repo-relative when a git root can be located, so the
    /// resulting key is stable across machines and checkout locations rather than embedding an
    /// absolute path. Falls back to the normalized absolute path when no repo root is found.
    /// </summary>
    private static string RepoRelativeOrNormalized(string path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            var gitRoot = GitRootByDir.GetOrAdd(dir, GitRemoteResolver.ResolveGitRoot);
            if (gitRoot != null)
            {
                var rel = Path.GetRelativePath(gitRoot, path).Replace('\\', '/');
                if (!rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel))
                    return rel;
            }
        }

        return NormalizePath(path);
    }
}
