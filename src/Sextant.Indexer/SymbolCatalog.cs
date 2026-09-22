namespace Sextant.Indexer;

/// <summary>
/// A project-aware catalog mapping a symbol's stable declaration key to its stored row id. It
/// replaces the former global FQN dictionary, whose single display-string key let one project's
/// symbol shadow another's and let overloads overwrite each other. Resolution prefers a match in the
/// requesting project and reports when a key is ambiguous across projects.
/// </summary>
/// <remarks>
/// Phase-2 boundary: when an edge references a key that is defined in several *other* projects (with
/// no match in the requesting project), this catalog binds the edge to a deterministic pick (lowest
/// project id) and flags the resolution ambiguous, counting it in <see cref="AmbiguousEdgeBindings"/>
/// for a diagnostic. A same-project edge resolves exactly (the requesting project is preferred) and
/// is never ambiguous. Binding the deterministic pick — rather than dropping the edge — keeps the
/// graph complete for the common ambiguous case, a multi-targeted dependency whose two TFM instances
/// are copies of the *same* logical symbol, where either row is a semantically-correct target.
/// Resolving exactly which project such an edge truly targets (via the referencing compilation's
/// assembly identity) is the compilation-scoped resolution introduced by the later document-oriented
/// extractor phase; it is intentionally out of scope here.
/// </remarks>
public sealed class SymbolCatalog
{
    // declarationKey -> (projectId -> symbolId). At most one entry per (project, key), matching the
    // database uniqueness constraint on (project_id, symbol_key).
    private readonly Dictionary<string, Dictionary<long, long>> _byKey = new(StringComparer.Ordinal);

    /// <summary>
    /// Count of edges bound to a deterministic pick because their target key resolved ambiguously
    /// across projects. Surfaced as a diagnostic so callers can see how many cross-project edges await
    /// the document-oriented extractor phase's exact resolution.
    /// </summary>
    public int AmbiguousEdgeBindings { get; private set; }

    /// <summary>Registers a stored definition under its declaration key and owning project.</summary>
    public void Add(long projectId, string declarationKey, long symbolId)
    {
        if (!_byKey.TryGetValue(declarationKey, out var perProject))
        {
            perProject = new Dictionary<long, long>();
            _byKey[declarationKey] = perProject;
        }
        perProject[projectId] = symbolId;
    }

    public readonly record struct Resolution(long SymbolId, bool Found, bool Ambiguous);

    /// <summary>
    /// Resolves a declaration key to a symbol id, preferring the requesting project. When the key is
    /// defined only in other projects and appears in more than one, a deterministic match (lowest
    /// project id) is returned and flagged ambiguous.
    /// </summary>
    public Resolution Resolve(string declarationKey, long? preferProjectId = null)
    {
        if (!_byKey.TryGetValue(declarationKey, out var perProject) || perProject.Count == 0)
            return new Resolution(0, false, false);

        if (preferProjectId is { } pid && perProject.TryGetValue(pid, out var sameProjectId))
            return new Resolution(sameProjectId, true, false);

        if (perProject.Count == 1)
        {
            foreach (var only in perProject)
                return new Resolution(only.Value, true, false);
        }

        long chosenProject = long.MaxValue;
        long chosenSymbol = 0;
        foreach (var kv in perProject)
        {
            if (kv.Key < chosenProject)
            {
                chosenProject = kv.Key;
                chosenSymbol = kv.Value;
            }
        }
        return new Resolution(chosenSymbol, true, true);
    }

    /// <summary>
    /// Convenience resolution that ignores ambiguity, matching the old dictionary lookup shape. Used
    /// where the key is resolved in the context that declares it (so a same-project match is
    /// guaranteed) or by tests. Edge extractors that bind a cross-project target should use
    /// <see cref="TryResolveEdge"/>, which counts ambiguous bindings for diagnostics.
    /// </summary>
    public bool TryResolve(string declarationKey, long? preferProjectId, out long symbolId)
    {
        var resolution = Resolve(declarationKey, preferProjectId);
        symbolId = resolution.SymbolId;
        return resolution.Found;
    }

    /// <summary>
    /// Resolves a declaration key to a single target for an edge (call/relationship/reference). Binds
    /// the deterministic pick and returns true when found; when the pick was ambiguous (the key is
    /// defined in several other projects with no match in the requesting project) it increments
    /// <see cref="AmbiguousEdgeBindings"/> so the binding is observable. A same-project target
    /// resolves exactly and is never counted. Keeping the edge — instead of dropping it — preserves
    /// graph completeness for the common ambiguous case (a multi-targeted dependency whose TFM
    /// instances are copies of the same logical symbol); exact per-project resolution is the later
    /// document-oriented extractor phase.
    /// </summary>
    public bool TryResolveEdge(string declarationKey, long? preferProjectId, out long symbolId)
    {
        var resolution = Resolve(declarationKey, preferProjectId);
        symbolId = resolution.SymbolId;
        if (resolution.Found && resolution.Ambiguous)
            AmbiguousEdgeBindings++;
        return resolution.Found;
    }
}
