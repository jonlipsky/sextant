using Sextant.Core;
using Sextant.Mcp.Tools;
using Sextant.Store;

namespace Sextant.Mcp;

/// <summary>
/// One candidate definition for an ambiguous fully-qualified-name lookup. Emitted in response
/// metadata so callers can see exactly which definitions share the requested FQN.
/// </summary>
public sealed record SymbolCandidate(
    string ProjectId,
    string SymbolKey,
    string FullyQualifiedName,
    string Kind,
    string FilePath,
    int LineStart);

/// <summary>
/// Ambiguity metadata for an FQN that resolved to more than one definition. The resolver still
/// selects a deterministic best match, but records every candidate here so the tool response can
/// disclose the ambiguity instead of silently hiding the other rows.
/// </summary>
public sealed class SymbolAmbiguity
{
    public required IReadOnlyList<SymbolCandidate> Candidates { get; init; }
    public required string SelectedProjectId { get; init; }
    public required string SelectedSymbolKey { get; init; }
}

/// <summary>
/// The outcome of resolving a display FQN. <see cref="Symbol"/> is the deterministic best match (or
/// null when nothing matched). <see cref="Ambiguity"/> is populated only when several definitions
/// share the FQN — for a single match it stays null so responses remain byte-identical.
/// </summary>
public readonly record struct SymbolResolution(SymbolInfo? Symbol, SymbolAmbiguity? Ambiguity);

/// <summary>
/// Resolves a user-supplied display FQN to a concrete definition. Because the FQN is no longer a
/// unique identity (overloads share one, and the same FQN can exist in several projects), this
/// centralizes the "pick a deterministic best match but report ambiguity" behavior so every MCP
/// tool handles collisions the same way.
/// </summary>
public static class SymbolResolver
{
    public static SymbolResolution Resolve(
        SymbolStore symbolStore,
        ProjectStore projectStore,
        string fullyQualifiedName,
        long? projectId = null)
    {
        var matches = symbolStore.ResolveByFqn(fullyQualifiedName, projectId);
        if (matches.Count == 0)
            return new SymbolResolution(null, null);

        var best = matches[0];
        if (matches.Count == 1)
            return new SymbolResolution(best, null);

        var cache = FindSymbolTool.BuildCanonicalIdCache(projectStore);
        var candidates = matches
            .Select(m => new SymbolCandidate(
                FindSymbolTool.ResolveCanonicalId(m.ProjectId, cache),
                m.SymbolKey,
                m.FullyQualifiedName,
                m.Kind.ToString().ToLowerInvariant(),
                m.FilePath,
                m.LineStart))
            .ToList();

        var ambiguity = new SymbolAmbiguity
        {
            Candidates = candidates,
            SelectedProjectId = FindSymbolTool.ResolveCanonicalId(best.ProjectId, cache),
            SelectedSymbolKey = best.SymbolKey
        };

        return new SymbolResolution(best, ambiguity);
    }
}
