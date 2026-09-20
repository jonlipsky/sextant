namespace Sextant.Core;

/// <summary>
/// A collision-resistant semantic identity for a declared symbol, distinct from its display
/// fully-qualified name. It combines the logical project and target framework with a stable
/// <see cref="DeclarationKey"/> derived from a Roslyn documentation ID (preferred) or a versioned
/// source-declaration fallback.
/// </summary>
/// <remarks>
/// The display FQN produced by <c>SymbolDisplayFormat.FullyQualifiedFormat</c> omits parameter
/// lists, so overloads and same-named members on different types collapse to the same string. The
/// declaration key does not: documentation IDs encode the containing type, generic arity, and
/// parameter signature. The FQN therefore becomes display/query data while this key becomes the
/// identity used for uniqueness and edge resolution.
/// </remarks>
public sealed record SemanticSymbolKey
{
    /// <summary>The logical project canonical ID (git remote + repo-relative path).</summary>
    public required string ProjectCanonicalId { get; init; }

    /// <summary>The evaluated target framework moniker, when known.</summary>
    public string? TargetFramework { get; init; }

    /// <summary>
    /// The declaration key: a Roslyn documentation comment ID (e.g. <c>M:Ns.Type.M(System.Int32)</c>)
    /// when one is available, otherwise a versioned source-declaration fallback. This is the value
    /// persisted in <c>symbols.symbol_key</c>; uniqueness is enforced per project.
    /// </summary>
    public required string DeclarationKey { get; init; }

    /// <summary>
    /// A flattened single-string logical identity (project + target framework + declaration) used by
    /// in-memory catalogs, diagnostics, and equality assertions. It is intentionally distinct from
    /// the stored identity: the database realizes the same logical key in normalized form as
    /// <c>(project_id, symbol_key)</c>, where <c>symbol_key</c> holds only <see cref="DeclarationKey"/>
    /// and the project dimension is the <c>project_id</c> foreign key. Per the project-identity rule
    /// (git remote + repo-relative path), the target framework is deliberately not part of project
    /// identity, so a multi-target project is a single project row; <see cref="TargetFramework"/> is
    /// carried here only as a forward-looking dimension for the versioned <c>symbol_definition_id</c>
    /// model and is not persisted in the current stored key.
    /// </summary>
    public string Value => $"{ProjectCanonicalId}|{TargetFramework ?? string.Empty}|{DeclarationKey}";

    public override string ToString() => Value;
}
