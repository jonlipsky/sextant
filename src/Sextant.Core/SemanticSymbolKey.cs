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
    /// <summary>
    /// The logical project canonical ID. Each evaluated target framework of a multi-targeted project
    /// is a distinct logical project, so this id folds in the git remote, the repo-relative path, and
    /// the evaluated target framework.
    /// </summary>
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
    /// and the project dimension is the <c>project_id</c> foreign key. Because each evaluated target
    /// framework is its own logical project — the <c>project_id</c> already encodes the target
    /// framework via the canonical id — the framework does not need to be repeated inside
    /// <c>symbol_key</c>; <see cref="TargetFramework"/> is carried here for the flattened logical
    /// identity and the forward-looking versioned <c>symbol_definition_id</c> model.
    /// </summary>
    public string Value => $"{ProjectCanonicalId}|{TargetFramework ?? string.Empty}|{DeclarationKey}";

    public override string ToString() => Value;
}
