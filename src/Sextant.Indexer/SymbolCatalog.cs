namespace Sextant.Indexer;

/// <summary>
/// A project-aware catalog mapping a symbol's stable declaration key to its stored row id. It
/// replaces the former global FQN dictionary, whose single display-string key let one project's
/// symbol shadow another's and let overloads overwrite each other. Resolution prefers a match in the
/// requesting project and reports when a key is ambiguous across projects.
/// </summary>
/// <remarks>
/// Phase-2 boundary: when an edge references a key that is defined in several *other* projects, this
/// catalog returns a deterministic pick (lowest project id) and flags it ambiguous, which is already
/// a strict improvement over the old last-write-wins global dictionary. Resolving exactly which
/// project an edge truly targets (via the referencing compilation's assembly identity) requires the
/// compilation-scoped resolution introduced by the later document-oriented extractor phase; it is
/// intentionally out of scope here.
/// </remarks>
public sealed class SymbolCatalog
{
    // declarationKey -> (projectId -> symbolId). At most one entry per (project, key), matching the
    // database uniqueness constraint on (project_id, symbol_key).
    private readonly Dictionary<string, Dictionary<long, long>> _byKey = new(StringComparer.Ordinal);

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
    /// Convenience resolution that ignores ambiguity, matching the old dictionary lookup shape. Edge
    /// extractors record a single target per edge, so they intentionally accept the deterministic
    /// pick from <see cref="Resolve"/> and drop the ambiguous flag; representing an edge's true
    /// cross-project fan-out is deferred to the document-oriented extractor phase (see remarks on the
    /// type).
    /// </summary>
    public bool TryResolve(string declarationKey, long? preferProjectId, out long symbolId)
    {
        var resolution = Resolve(declarationKey, preferProjectId);
        symbolId = resolution.SymbolId;
        return resolution.Found;
    }
}
