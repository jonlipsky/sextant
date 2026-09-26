using System.ComponentModel;
using Sextant.Core;
using Sextant.Store;
using ModelContextProtocol.Server;

namespace Sextant.Mcp.Tools;

[McpServerToolType]
public static class FindSymbolTool
{
    [McpServerTool(Name = "find_symbol"), Description("Instant indexed lookup of a symbol by name. Faster than grep/file reading for .NET codebases. Use fuzzy=true for FTS5 search.")]
    public static async Task<string> FindSymbol(
        DatabaseProvider dbProvider,
        [Description("The symbol name or fully qualified name to search for")] string name,
        [Description("Optional symbol kind filter (class, method, property, etc.)")] string? kind = null,
        [Description("Optional project canonical ID filter")] string? project_id = null,
        [Description("Use FTS5 fuzzy search instead of exact match")] bool fuzzy = false,
        [Description("Include the symbol's source declaration")] bool include_source = false,
        [Description("Scope filter: 'file:/path', 'project:canonical_id', 'solution:/path', or 'all'")] string? scope = null)
    {
        if (!dbProvider.TryBeginRead(out var db, out var readContext, out var authError))
            return authError;

        using var conn = db.OpenReadConnection();
        var symbolStore = new SymbolStore(conn) { Scope = readContext.Scope };
        var projectStore = new ProjectStore(conn) { Scope = readContext.Scope };

        // Raw host-filesystem source reads (SourceReader, no content-hash verification) must not be served
        // over an enforced multi-tenant surface: the reconstructed absolute path can point outside the
        // caller's authorized repository (e.g. a crafted project with '..' in a source item, or another
        // tenant's retained checkout). Under enforcement the caller gets locations only; the zero-policy
        // local path is byte-identical (hardening review, criteria 1 & 2).
        var serveSource = include_source && !dbProvider.Authorizer.IsEnforcing;

        var canonicalIdCache = BuildCanonicalIdCache(projectStore);

        // Resolve scope to project filter
        var scopeFilter = ScopeResolver.Resolve(scope, conn, readContext.Scope);

        // Issue #108: this pinned generation is a REMOTE-base overlay when readContext.RemoteBase is set —
        // the committed base lives only on a configured peer (a thin machine indexed just its diff). We
        // federate the base ONLY for a genuinely whole-repo lookup ("all" or no scope) with no project
        // filter: a file:/project:/solution: scope is inherently local (remote base rows carry no local
        // file/project id, so they can never satisfy such a filter). Gate on the RAW scope request, not
        // ScopeFilter.IsEmpty — a project:/solution: scope that does not resolve locally collapses to an
        // EMPTY filter, indistinguishable from "no scope", so keying off IsEmpty would silently promote a
        // scoped miss into an UNSCOPED remote federation, returning base symbols the caller scoped out.
        var remoteBase = readContext.RemoteBase;
        var remoteSource = dbProvider.RemoteBaseSource;
        var isRemoteBaseOverlay = remoteBase != null;
        var wholeRepoScope = string.IsNullOrEmpty(scope) || scope == "all";
        var federateRemote = remoteBase != null && remoteSource != null
            && wholeRepoScope && project_id == null;
        // The overlay's mapped projects ARE the touched-closure project-versions; their canonical ids are
        // what a base row must be shadowed against so a symbol changed/deleted in a touched project never
        // resurfaces from the remote base.
        var touchedCanonicalIds = new HashSet<string>(canonicalIdCache.Values, StringComparer.Ordinal);

        if (fuzzy)
        {
            var config = SextantConfiguration.FromEnvironment();
            var results = symbolStore.SearchFts(name, config.FtsMaxResults, kind);

            if (!scopeFilter.IsEmpty)
            {
                if (scopeFilter.FilePath != null)
                    results = results.Where(s => s.FilePath == scopeFilter.FilePath).ToList();
                else if (scopeFilter.ProjectIds != null)
                    results = results.Where(s => scopeFilter.ProjectIds.Contains(s.ProjectId)).ToList();
            }

            var mapped = results.Select(s => MapSymbol(s, ResolveCanonicalId(s.ProjectId, canonicalIdCache), serveSource)).ToList();
            var freshness = results.Count > 0 ? results.Min(s => s.LastIndexedAt) : 0L;

            if (!isRemoteBaseOverlay)
                return ResponseBuilder.Build(mapped, freshness, provenance: readContext.Provenance);

            RemoteBaseSymbolFederation.Outcome? outcome = null;
            if (federateRemote && mapped.Count < config.FtsMaxResults)
            {
                var term = name.Trim();
                // Apply the SAME kind filter the local FTS query used (SearchFts(..., kind)) to the remote
                // base rows, so a kind-filtered fuzzy search does not surface unfiltered base symbols
                // (an unknown kind maps to -1 and matches nothing, exactly as locally).
                var kindOrdinal = kind != null ? SymbolStore.KindNameToInt(kind) : (int?)null;
                outcome = await RemoteBaseSymbolFederation.FetchAsync(
                    remoteSource!, remoteBase!.BaseIdentityHash, touchedCanonicalIds,
                    row => (kindOrdinal is null || row.Kind == kindOrdinal.Value)
                        && (Contains(row.DisplayName, term) || Contains(row.FullyQualifiedName, term)),
                    maxMatches: config.FtsMaxResults - mapped.Count,
                    CancellationToken.None).ConfigureAwait(false);
                foreach (var row in outcome.Rows)
                    mapped.Add(RemoteBaseSymbolFederation.MapRemoteSymbol(row));
            }

            var provenance = FinalizeRemoteBaseProvenance(readContext.Provenance!, remoteSource != null, outcome);
            // When the local FTS matched nothing, the answer's freshness is the overlay generation's (the
            // base was federated in), not 0 — mirroring the exact branch, which stamps provenance.Freshness
            // for a remote hit. Pure-local (non-overlay) freshness is untouched (criterion 4).
            var effectiveFreshness = results.Count > 0 ? freshness : provenance.Freshness;
            return ResponseBuilder.Build(mapped, effectiveFreshness, provenance: provenance);
        }
        else
        {
            long? projectDbId = null;
            if (project_id != null)
            {
                var proj = projectStore.GetByCanonicalId(project_id);
                if (proj == null)
                    return ResponseBuilder.BuildEmpty("Project not found.", readContext.Provenance);
                projectDbId = proj.Value.id;
            }

            var resolution = SymbolResolver.Resolve(symbolStore, projectStore, name, projectDbId);
            if (resolution.Symbol != null)
            {
                var symbol = resolution.Symbol;

                if (!scopeFilter.IsEmpty)
                {
                    if (scopeFilter.FilePath != null && symbol.FilePath != scopeFilter.FilePath)
                        return ResponseBuilder.BuildEmpty("Symbol not in scope.", readContext.Provenance);
                    if (scopeFilter.ProjectIds != null && !scopeFilter.ProjectIds.Contains(symbol.ProjectId))
                        return ResponseBuilder.BuildEmpty("Symbol not in scope.", readContext.Provenance);
                }

                var localHit = new List<object> { MapSymbol(symbol, ResolveCanonicalId(symbol.ProjectId, canonicalIdCache), serveSource) };
                // Served from the local overlay; for a remote-base overlay stamp origin=local (the base is
                // addressable remotely but this answer never needed it) while keeping the base identity hash.
                var localProv = isRemoteBaseOverlay
                    ? FinalizeRemoteBaseProvenance(readContext.Provenance!, remoteSource != null, outcome: null)
                    : readContext.Provenance;
                return ResponseBuilder.Build(localHit, symbol.LastIndexedAt, resolution.Ambiguity, localProv);
            }

            // Not resolvable locally. For a remote-base overlay, the definition may live in an UNCHANGED
            // (untouched) project that exists only in the committed base on the peer — federate it.
            if (federateRemote)
            {
                var outcome = await RemoteBaseSymbolFederation.FetchAsync(
                    remoteSource!, remoteBase!.BaseIdentityHash, touchedCanonicalIds,
                    row => string.Equals(row.FullyQualifiedName, name, StringComparison.Ordinal)
                        || string.Equals(row.DisplayName, name, StringComparison.Ordinal),
                    maxMatches: 1,
                    CancellationToken.None).ConfigureAwait(false);

                var provenance = FinalizeRemoteBaseProvenance(readContext.Provenance!, remoteConfigured: true, outcome);
                if (outcome.Rows.Count > 0)
                {
                    var remoteHit = new List<object> { RemoteBaseSymbolFederation.MapRemoteSymbol(outcome.Rows[0]) };
                    return ResponseBuilder.Build(remoteHit, provenance.Freshness, provenance: provenance);
                }
                return ResponseBuilder.BuildEmpty("Symbol not found.", provenance);
            }

            var notFoundProv = isRemoteBaseOverlay
                ? FinalizeRemoteBaseProvenance(readContext.Provenance!, remoteSource != null, outcome: null)
                : readContext.Provenance;
            return ResponseBuilder.BuildEmpty("Symbol not found.", notFoundProv);
        }
    }

    private static bool Contains(string? haystack, string needle)
        => haystack != null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Derives the response provenance for a REMOTE-base overlay read (issue #108) from the base provenance
    /// and the outcome of the remote base fetch. Origin states where the answer's data came from
    /// (local overlay vs the federated peer base), completeness degrades to partial when the base could not
    /// be fully federated, and an actionable fallback reason is stamped when the peer was unreachable /
    /// unconfigured / served from the offline cache (criteria 3 & 6).
    /// </summary>
    private static SnapshotProvenance FinalizeRemoteBaseProvenance(
        SnapshotProvenance baseProvenance, bool remoteConfigured, RemoteBaseSymbolFederation.Outcome? outcome)
    {
        if (!remoteConfigured)
            return baseProvenance with
            {
                Origin = "local",
                Completeness = "partial",
                FallbackReason = "committed base snapshot is not in the local catalog and no peers are configured; served local overlay only"
            };

        if (outcome is null)
            return baseProvenance with { Origin = "local" };

        if (outcome.UnavailableReason is { } reason)
            return baseProvenance with { Origin = "local", Completeness = "partial", FallbackReason = reason };

        return baseProvenance with
        {
            Origin = outcome.Rows.Count > 0 ? "remote" : "local",
            Completeness = outcome.Complete ? baseProvenance.Completeness : "partial",
            FallbackReason = outcome.FromOfflineCache ? "remote base served from offline cache" : baseProvenance.FallbackReason,
            Coverage = WorstCoverage(outcome.Coverage, baseProvenance.Coverage)
        };
    }

    // Issue #119: the live peer page and the coverage recorded on the remote-base overlay at publish can
    // disagree (a different peer answered, or one recorded none). Never let a "complete" verdict mask a
    // "partial" one — the partial record wins, so `completeness` and `coverage` stay consistent.
    private static SnapshotCoverage? WorstCoverage(SnapshotCoverage? live, SnapshotCoverage? recorded)
        => recorded is { IsPartial: true } && live is not { IsPartial: true } ? recorded : live ?? recorded;

    internal static Dictionary<long, string> BuildCanonicalIdCache(ProjectStore projectStore)
    {
        var cache = new Dictionary<long, string>();
        foreach (var (id, project) in projectStore.GetAll())
            cache[id] = project.CanonicalId;
        return cache;
    }

    internal static string ResolveCanonicalId(long projectId, Dictionary<long, string> cache)
        => cache.TryGetValue(projectId, out var cid) ? cid : projectId.ToString();

    internal static object MapSymbol(SymbolInfo s, string? canonicalId = null, bool includeSource = false)
    {
        var result = new Dictionary<string, object?>
        {
            ["fully_qualified_name"] = s.FullyQualifiedName,
            ["display_name"] = s.DisplayName,
            ["kind"] = s.Kind.ToString().ToLowerInvariant(),
            ["project_id"] = canonicalId ?? s.ProjectId.ToString(),
            ["file_path"] = s.FilePath,
            ["line_start"] = s.LineStart,
            ["line_end"] = s.LineEnd,
            ["accessibility"] = SymbolStore.FormatAccessibility(s.Accessibility),
            ["signature"] = s.Signature
        };

        if (includeSource)
            result["source_context"] = SourceReader.ReadDeclaration(s.FilePath, s.LineStart, s.LineEnd);

        return result;
    }
}
