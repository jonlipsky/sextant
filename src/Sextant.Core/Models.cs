namespace Sextant.Core;

public sealed class SymbolInfo
{
    public long Id { get; set; }
    public long ProjectId { get; set; }
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
    public long SymbolId { get; set; }
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
}
