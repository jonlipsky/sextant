using System.ComponentModel;
using Sextant.Core;
using Sextant.Store;
using ModelContextProtocol.Server;

namespace Sextant.Mcp.Tools;

[McpServerToolType]
public static class FindSymbolTool
{
    [McpServerTool(Name = "find_symbol"), Description("A declaration by name or fully qualified name (fuzzy=true: full-text). Use instead of grepping for it.")]
    public static async Task<string> FindSymbol(
        DatabaseProvider dbProvider,
        [Description("Name or fully qualified name.")] string name,
        [Description(ToolText.Kind)] string? kind = null,
        [Description(ToolText.ProjectId)] string? project_id = null,
        bool fuzzy = false,
        bool include_source = false,
        [Description(ToolText.Scope)] string? scope = null)
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

        if (!SymbolKindNames.TryParse(kind, out var kinds, out var kindDescription, out var kindError))
            return ResponseBuilder.BuildError(ResponseBuilder.InvalidArgumentCode, kindError!, readContext.Provenance);

        // Resolve scope to project filter. An unknown project/solution or an unrecognized scope is an error, never
        // a silently unfiltered query.
        var scopeFilter = ScopeResolver.Resolve(scope, conn, readContext.Scope, readContext.Paths);
        if (scopeFilter.Error != null)
            return scopeFilter.ErrorResponse(readContext.Provenance);

        long? projectDbId = null;
        if (project_id != null)
        {
            var proj = projectStore.GetByCanonicalId(project_id);
            if (proj == null)
                return ResponseBuilder.BuildError(ResponseBuilder.InvalidArgumentCode,
                    $"Unknown project_id '{project_id}'. Use a project_id from a find_symbol result.",
                    readContext.Provenance);
            projectDbId = proj.Value.id;
        }

        // Issue #108: this pinned generation is a REMOTE-base overlay when readContext.RemoteBase is set —
        // the committed base lives only on a configured peer (a thin machine indexed just its diff). We
        // federate the base ONLY for a genuinely whole-repo lookup ("all" or no scope) with no project
        // filter: a file:/project:/solution: scope is inherently local (remote base rows carry no local
        // file/project id, so they can never satisfy such a filter). Gate on the RAW scope request, not
        // ScopeFilter.IsEmpty — a known solution with no mapped project yields an EMPTY project set, and
        // keying off IsEmpty could silently promote a scoped miss into an UNSCOPED remote federation,
        // returning base symbols the caller scoped out.
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
        var namer = new SymbolNamer(symbolStore);

        if (fuzzy)
        {
            var config = SextantConfiguration.FromEnvironment();
            // One kind narrows the FTS query itself; a kind family ("type") filters its results.
            var results = symbolStore.SearchFts(name, config.FtsMaxResults, kinds is { Count: 1 } ? kinds.First().ToString() : null);
            if (kinds is { Count: > 1 })
                results = results.Where(s => kinds.Contains(s.Kind)).ToList();
            if (projectDbId != null)
                results = results.Where(s => s.ProjectId == projectDbId.Value).ToList();

            if (!scopeFilter.IsEmpty)
            {
                if (scopeFilter.FilePath != null)
                    results = results.Where(s => scopeFilter.MatchesFile(s.FilePath)).ToList();
                else if (scopeFilter.ProjectIds != null)
                    results = results.Where(s => scopeFilter.ProjectIds.Contains(s.ProjectId)).ToList();
            }

            var mapped = results
                .Select(s => MapSymbol(s, ResolveCanonicalId(s.ProjectId, canonicalIdCache), serveSource, namer.QualifiedName(s)))
                .ToList();
            var freshness = results.Count > 0 ? results.Min(s => s.LastIndexedAt) : 0L;

            if (!isRemoteBaseOverlay)
                return ResponseBuilder.BuildBounded(mapped, readContext, freshness, provenance: readContext.Provenance);

            RemoteBaseSymbolFederation.Outcome? outcome = null;
            if (federateRemote && mapped.Count < config.FtsMaxResults)
            {
                var term = name.Trim();
                // Apply the SAME kind filter the local FTS query used to the remote base rows, so a kind-filtered
                // fuzzy search does not surface unfiltered base symbols.
                outcome = await RemoteBaseSymbolFederation.FetchAsync(
                    remoteSource!, remoteBase!.BaseIdentityHash, touchedCanonicalIds,
                    row => (kinds is null || kinds.Contains((SymbolKind)row.Kind))
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
            return ResponseBuilder.BuildBounded(mapped, readContext, effectiveFreshness, provenance: provenance);
        }
        else
        {
            var restriction = projectDbId != null
                ? scopeFilter.ProjectIds is { } scoped
                    ? scoped.Where(id => id == projectDbId.Value).ToHashSet()
                    : new HashSet<long> { projectDbId.Value }
                : scopeFilter.ProjectIds;
            var scopeDescription = (project_id, wholeRepoScope) switch
            {
                (not null, false) => $"in project '{project_id}' and scope '{scope}'",
                (not null, true) => $"in project '{project_id}'",
                (null, false) => $"in scope '{scope}'",
                _ => null
            };
            var options = new SymbolLookupOptions
            {
                Kinds = kinds,
                KindsExplicit = kinds != null,
                KindDescription = kindDescription,
                ProjectIds = restriction,
                FilePath = scopeFilter.FilePath,
                FileMatches = scopeFilter.FilePath != null ? scopeFilter.MatchesFile : null,
                ScopeDescription = scopeDescription
            };
            var lookup = SymbolResolver.Lookup(symbolStore, projectStore, name, options);
            // Served from the local overlay; for a remote-base overlay stamp origin=local (the base is
            // addressable remotely but this answer never needed it) while keeping the base identity hash.
            var localProv = isRemoteBaseOverlay
                ? FinalizeRemoteBaseProvenance(readContext.Provenance!, remoteSource != null, outcome: null)
                : readContext.Provenance;

            if (lookup.Status == SymbolLookupStatus.Resolved)
            {
                var symbol = lookup.Symbol!;
                var localHit = new List<object>
                {
                    MapSymbol(symbol, ResolveCanonicalId(symbol.ProjectId, canonicalIdCache), serveSource, namer.QualifiedName(symbol))
                };
                return ResponseBuilder.Build(localHit, symbol.LastIndexedAt, lookup.Ambiguity, localProv,
                    message: SymbolResolver.ResolutionNote(symbolStore, lookup));
            }

            if (lookup.Status == SymbolLookupStatus.Ambiguous)
            {
                // find_symbol is a search, so several equally good matches are an answer, not an error: list them
                // (best first) and say how to narrow to one for the tools that need exactly one symbol.
                var top = lookup.Matches.Take(Math.Min(lookup.TopMatchCount, MaxAmbiguousResults)).ToList();
                var listed = top
                    .Select(s => MapSymbol(s, ResolveCanonicalId(s.ProjectId, canonicalIdCache), serveSource, namer.QualifiedName(s)))
                    .ToList();
                var shown = top.Count < lookup.TopMatchCount ? $" (the first {top.Count} are listed)" : string.Empty;
                var message =
                    $"'{name.Trim()}' matches {lookup.TopMatchCount} different symbols{shown}. Tools that need one " +
                    "symbol accept any fully_qualified_name listed here (or qualify the name with its containing type " +
                    "and parameter list, e.g. 'Type.Method(int)').";
                return ResponseBuilder.BuildBounded(listed, readContext, top.Min(s => s.LastIndexedAt), provenance: localProv,
                    message: message);
            }

            // Not resolvable locally. For a remote-base overlay, the definition may live in an UNCHANGED
            // (untouched) project that exists only in the committed base on the peer — federate it.
            if (federateRemote && lookup.Status == SymbolLookupStatus.NotFound)
            {
                var term = name.Trim();
                var bare = term.StartsWith("global::", StringComparison.Ordinal) ? term["global::".Length..] : term;
                var outcome = await RemoteBaseSymbolFederation.FetchAsync(
                    remoteSource!, remoteBase!.BaseIdentityHash, touchedCanonicalIds,
                    row => (kinds is null || kinds.Contains((SymbolKind)row.Kind))
                        && (string.Equals(row.FullyQualifiedName, term, StringComparison.Ordinal)
                            || string.Equals(row.FullyQualifiedName, "global::" + bare, StringComparison.Ordinal)
                            || string.Equals(row.DisplayName, term, StringComparison.Ordinal)),
                    maxMatches: 1,
                    CancellationToken.None).ConfigureAwait(false);

                var provenance = FinalizeRemoteBaseProvenance(readContext.Provenance!, remoteConfigured: true, outcome);
                if (outcome.Rows.Count > 0)
                {
                    var remoteHit = new List<object> { RemoteBaseSymbolFederation.MapRemoteSymbol(outcome.Rows[0]) };
                    return ResponseBuilder.Build(remoteHit, provenance.Freshness, provenance: provenance);
                }
                return SymbolResolver.ErrorResponse(symbolStore, projectStore, lookup, provenance);
            }

            return SymbolResolver.ErrorResponse(symbolStore, projectStore, lookup, localProv);
        }
    }

    private const int MaxAmbiguousResults = 25;
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

    /// <param name="qualifiedName">
    /// The name to print as <c>fully_qualified_name</c>, in a form the tools accept back (<see cref="SymbolNamer"/>);
    /// the stored name when null.
    /// </param>
    internal static object MapSymbol(
        SymbolInfo s, string? canonicalId = null, bool includeSource = false, string? qualifiedName = null)
    {
        var result = new Dictionary<string, object?>
        {
            ["fully_qualified_name"] = qualifiedName ?? s.FullyQualifiedName,
            ["display_name"] = s.DisplayName,
            ["kind"] = s.Kind.ToString().ToLowerInvariant(),
            ["project_id"] = canonicalId ?? s.ProjectId.ToString(),
            ["file_path"] = s.FilePath,
            ["line_start"] = s.LineStart,
            ["line_end"] = s.LineEnd,
            ["accessibility"] = SymbolStore.FormatAccessibility(s.Accessibility),
            ["signature"] = s.Declaration ?? s.Signature
        };

        if (includeSource)
            result["source_context"] = SourceReader.ReadDeclaration(s.FilePath, s.LineStart, s.LineEnd);

        return result;
    }
}
