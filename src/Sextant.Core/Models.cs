namespace Sextant.Core;

public sealed class SymbolInfo
{
    public long Id { get; set; }
    public long ProjectId { get; set; }

    /// <summary>
    /// The stable semantic declaration key (documentation ID or versioned source fallback). This is
    /// the collision-resistant identity; the fully-qualified name below is display/query data only.
    /// </summary>
    public required string SymbolKey { get; init; }
    public required string FullyQualifiedName { get; init; }
    public required string DisplayName { get; init; }
    public required SymbolKind Kind { get; init; }
    public required Accessibility Accessibility { get; init; }
    public bool IsStatic { get; init; }
    public bool IsAbstract { get; init; }
    public bool IsVirtual { get; init; }
    public bool IsOverride { get; init; }
    public string? Signature { get; init; }
    public string? SignatureHash { get; init; }
    public string? DocComment { get; init; }
    public required string FilePath { get; init; }
    public int LineStart { get; init; }
    public int LineEnd { get; init; }
    public string? Attributes { get; init; }
    public long LastIndexedAt { get; init; }
}

public sealed class ReferenceInfo
{
    public long Id { get; set; }
    public long SymbolId { get; set; }
    public long InProjectId { get; set; }
    public required string FilePath { get; init; }
    public int Line { get; init; }
    public string? ContextSnippet { get; init; }
    public required ReferenceKind ReferenceKind { get; init; }
    public AccessKind? AccessKind { get; init; }
    public long LastIndexedAt { get; init; }
}

public sealed class RelationshipInfo
{
    public long Id { get; set; }
    public long FromSymbolId { get; set; }
    public long ToSymbolId { get; set; }
    public required RelationshipKind Kind { get; init; }
    public long LastIndexedAt { get; init; }
}

public sealed class CallGraphEdge
{
    public long Id { get; set; }
    public long CallerSymbolId { get; set; }
    public long CalleeSymbolId { get; set; }
    public required string CallSiteFile { get; init; }
    public int CallSiteLine { get; init; }
    public int CallSiteColumn { get; init; }
    public long LastIndexedAt { get; init; }
}

public sealed class FileIndexEntry
{
    public long Id { get; set; }
    public long ProjectId { get; set; }
    public required string FilePath { get; init; }
    public required string ContentHash { get; init; }
    public long LastIndexedAt { get; init; }
}

public sealed class ProjectDependency
{
    public long Id { get; set; }
    public long ConsumerProjectId { get; set; }
    public long DependencyProjectId { get; set; }
    public required string ReferenceKind { get; init; } // project_ref, submodule_ref, nuget_ref
    public string? SubmodulePinnedCommit { get; init; }
}

public sealed class ApiSurfaceSnapshot
{
    public long Id { get; set; }
    public long ProjectId { get; set; }

    /// <summary>
    /// Soft back-pointer to the current generation's symbol row. Nulled (not cascade-deleted) when the
    /// working symbols are rebuilt, so a historical snapshot survives a re-index. Do not use it to
    /// recover a snapshot's identity across rebuilds — use <see cref="SymbolKey"/> instead.
    /// </summary>
    public long? SymbolId { get; set; }

    /// <summary>Stable semantic declaration key captured at snapshot time (rebuild-invariant identity).</summary>
    public string SymbolKey { get; set; } = "";

    /// <summary>Display fully-qualified name captured at snapshot time.</summary>
    public string FullyQualifiedName { get; set; } = "";

    /// <summary>Accessibility captured at snapshot time (stored form, e.g. "public").</summary>
    public string Accessibility { get; set; } = "";

    public required string SignatureHash { get; init; }
    public long CapturedAt { get; init; }
    public required string GitCommit { get; init; }
}

public sealed class ArgumentFlowInfo
{
    public long Id { get; set; }
    public long CallGraphId { get; set; }
    public int ParameterOrdinal { get; init; }
    public required string ParameterName { get; init; }
    public required string ArgumentExpression { get; init; }
    public required string ArgumentKind { get; init; }
    public string? SourceSymbolFqn { get; init; }
    public long LastIndexedAt { get; init; }
}

public sealed class ReturnFlowInfo
{
    public long Id { get; set; }
    public long CallGraphId { get; set; }
    public required string DestinationKind { get; init; }
    public string? DestinationVariable { get; init; }
    public string? DestinationSymbolFqn { get; init; }
    public long LastIndexedAt { get; init; }
}

public sealed class CommentInfo
{
    public long Id { get; set; }
    public long ProjectId { get; set; }
    public required string FilePath { get; init; }
    public int Line { get; init; }
    public required string Tag { get; init; }
    public required string Text { get; init; }
    public long? EnclosingSymbolId { get; init; }
    public long LastIndexedAt { get; init; }
}

public sealed class SubmoduleInfo
{
    public required string Path { get; init; }
    public required string CommitSha { get; init; }
    public required string RemoteUrl { get; init; }

    /// <summary>
    /// True when <c>git submodule status</c> reported the submodule with a non-clean prefix — its
    /// checked-out commit differs from the recorded pin (<c>+</c>), it is uninitialized (<c>-</c>), or it
    /// has merge conflicts (<c>U</c>) — i.e. the parent's working tree does NOT match the clean pinned
    /// commit (issue #48). A dirty submodule must never be identified as the clean pinned commit; Phase 12
    /// folds this into the provider snapshot identity and records it on the dependency edge.
    /// </summary>
    public bool IsDirty { get; init; }
}

/// <summary>
/// A Phase-12 snapshot dependency edge: one consumer project version's pinned reference into a
/// deduplicated submodule PROVIDER project version. Immutable per consumer generation; the reverse index
/// backing cross-repository usage queries and provider retention protection.
/// </summary>
public sealed class SnapshotDependencyEdge
{
    public long Id { get; set; }
    public required long ConsumerSnapshotId { get; init; }
    public required long ConsumerProjectId { get; init; }
    public required long ProviderSnapshotId { get; init; }
    public required long ProviderProjectId { get; init; }
    public required long ProviderRepositoryId { get; init; }
    public required string ProviderCommitSha { get; init; }
    public required string ReferenceKind { get; init; }
    public bool SubmoduleDirty { get; init; }
    public long CreatedAt { get; init; }
}

/// <summary>
/// One authorized cross-repository usage of a producer (submodule provider) symbol: where a consumer
/// repository references the provider symbol, with the consumer's location and the exact pinned provider
/// version. Returned by the Phase-12 reverse-usage query, narrowed to authorized consumer snapshots.
/// </summary>
public sealed class CrossRepositoryUsage
{
    public required string ConsumerRepositoryUrl { get; init; }
    public required string ConsumerBranch { get; init; }
    public string? ConsumerCommitSha { get; init; }
    public required string ConsumerProjectCanonicalId { get; init; }
    public required string FilePath { get; init; }
    public int Line { get; init; }
    public int Column { get; init; }
    public required string OccurrenceKind { get; init; }
    public required string ProviderCommitSha { get; init; }
    public bool SubmoduleDirty { get; init; }
}

/// <summary>
/// One authorized reverse-dependency of a shared submodule (provider) repository: a consumer repository /
/// project that pins the provider at a specific commit. Returned by the Phase-12 reverse-dependency query
/// ("which repositories consume this submodule, and at what pin"), narrowed to authorized consumers.
/// </summary>
public sealed class SubmoduleConsumer
{
    public required long ConsumerRepositoryId { get; init; }
    public required string ConsumerRepositoryUrl { get; init; }
    public required string ConsumerBranch { get; init; }
    public string? ConsumerCommitSha { get; init; }
    public required string ConsumerProjectCanonicalId { get; init; }
    public required string ProviderCommitSha { get; init; }
    public bool SubmoduleDirty { get; init; }
}
