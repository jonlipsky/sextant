namespace Sextant.Core;

public enum SymbolKind
{
    Class,
    Interface,
    Struct,
    Enum,
    Delegate,
    Record,
    Method,
    Constructor,
    Property,
    Field,
    Event,
    Indexer,
    TypeParameter
}

public enum ReferenceKind
{
    Invocation,
    TypeRef,
    Attribute,
    Inheritance,
    Override,
    ObjectCreation
}

public enum RelationshipKind
{
    Implements,
    Inherits,
    Overrides,
    Instantiates,
    Returns,
    ParameterOf
}

public enum AccessKind
{
    Read,
    Write,
    ReadWrite
}

public enum Accessibility
{
    Public,
    Internal,
    Protected,
    Private,
    ProtectedInternal,
    PrivateProtected
}

/// <summary>
/// The optional-and-core index-time capabilities a run can produce, as a bit set so a run's exact
/// feature configuration is recorded in one integer (Phase 8). The <see cref="Core"/> capabilities are
/// always built; the rest are gated by the selected <see cref="IndexProfiles">profile</see> so a
/// repository only pays for the semantic depth it needs. The persisted value on an <c>index_runs</c>
/// row is what capability-aware query tools consult to decide whether requested data was indexed.
/// </summary>
[Flags]
public enum IndexFeature : long
{
    None = 0,

    /// <summary>Symbol definitions (always built).</summary>
    Definitions = 1L << 0,

    /// <summary>Reference occurrences (always built).</summary>
    Occurrences = 1L << 1,

    /// <summary>Call edges (always built).</summary>
    Calls = 1L << 2,

    /// <summary>Type relationships — inherits/implements/overrides/instantiates (always built).</summary>
    TypeRelationships = 1L << 3,

    /// <summary>Project dependency graph + API surface (always built).</summary>
    ProjectDependencies = 1L << 4,

    /// <summary>Documentation-comment text + FTS documentation column (standard and up).</summary>
    DocumentationSearch = 1L << 5,

    /// <summary>Tagged comments — TODO/HACK/FIXME/BUG/NOTE (standard and up).</summary>
    Comments = 1L << 6,

    /// <summary>Test discovery queries (standard and up). A query capability only — test projects are
    /// always indexed so cross-project references stay complete.</summary>
    TestIndexing = 1L << 7,

    /// <summary>Detailed argument/return dataflow — <c>argument_flow</c>/<c>return_flow</c> (deep only).</summary>
    Dataflow = 1L << 8,

    /// <summary>The always-built core capabilities. Omitting any of these is never valid.</summary>
    Core = Definitions | Occurrences | Calls | TypeRelationships | ProjectDependencies,

    /// <summary>Core plus documentation search, comments, and test indexing.</summary>
    Standard = Core | DocumentationSearch | Comments | TestIndexing,

    /// <summary>Standard plus detailed dataflow. Every capability is on.</summary>
    Deep = Standard | Dataflow
}
