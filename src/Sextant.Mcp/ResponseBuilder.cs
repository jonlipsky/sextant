using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sextant.Mcp;

public static class ResponseBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public static string Build<T>(
        List<T> results, long? indexFreshness = null, SymbolAmbiguity? ambiguity = null,
        SnapshotProvenance? provenance = null, string? nextCursor = null, string? message = null)
    {
        var response = new
        {
            Meta = new MetaObject
            {
                QueriedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                IndexFreshness = indexFreshness ?? 0,
                ResultCount = results.Count,
                Ambiguous = ambiguity != null ? true : null,
                AmbiguousMatchCount = ambiguity?.Candidates.Count,
                SelectedProjectId = ambiguity?.SelectedProjectId,
                SelectedSymbolKey = ambiguity?.SelectedSymbolKey,
                Candidates = ambiguity?.Candidates,
                Snapshot = SnapshotMeta.From(provenance),
                NextCursor = nextCursor
            },
            Results = results,
            // An explicit statement about a valid answer (e.g. "the type has no implementors"); omitted when null so
            // every other response is byte-identical.
            Message = message
        };

        return JsonSerializer.Serialize(response, JsonOptions);
    }

    /// <summary>
    /// Builds an index-status response (Phase 8) — the standard results/meta envelope plus a top-level
    /// <c>index</c> object describing the active profile, its enabled feature capabilities, and retained
    /// storage. Serialized with the same snake_case policy as every other response.
    /// </summary>
    public static string BuildStatus<T>(List<T> results, long indexFreshness, object index)
    {
        var response = new
        {
            Meta = new MetaObject
            {
                QueriedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                IndexFreshness = indexFreshness,
                ResultCount = results.Count
            },
            Index = index,
            Results = results
        };

        return JsonSerializer.Serialize(response, JsonOptions);
    }

    /// <summary>
    /// The single uniform "nothing to serve" response returned for EVERY fail-closed read denial under an
    /// enforced policy (Phase 17, criterion 1). An unauthorized principal, a cross-tenant repository, an
    /// unidentifiable/nonexistent repository, a branch with no complete snapshot, and an unprovisioned service
    /// ALL collapse to these exact bytes: the one <see cref="RepositoryNotFoundCode"/> error with one fixed
    /// message, no provenance, and <c>result_count</c> 0. An unauthorized caller therefore cannot distinguish
    /// "exists but forbidden" from "does not exist" or "service not provisioned" — no data, counts, names,
    /// existence, or artifact-access signal leaks (only the always-varying <c>queried_at</c> differs). What
    /// criterion 1 forbids is a DISTINCT denial code (the Phase-11 <c>authorization_denied</c>), which told an
    /// unauthorized caller "this exists but you may not see it"; a code every case shares reveals nothing. It is
    /// an error rather than an empty result (issue #163) so a client never reads "you cannot read this" as "no
    /// matches". A denial only ever happens under an ENABLED policy, which only the index service configures, so
    /// the message may point at its <c>list_repositories</c> tool.
    /// </summary>
    public static string BuildNotFound() => BuildError(RepositoryNotFoundCode, RepositoryNotFoundMessage);

    /// <summary>
    /// The <c>meta.error.code</c> of <see cref="BuildNotFound"/>, <see cref="BuildSelectionUnresolved"/> and
    /// <see cref="BuildBranchSelectionUnresolved"/>: the request names a repository or branch that serves nothing.
    /// </summary>
    public const string RepositoryNotFoundCode = "repository_not_found";

    private const string RepositoryNotFoundMessage =
        "Nothing you can read matches the requested repository or branch: it is not indexed, has no complete " +
        "snapshot on that branch, or you cannot read it. Pass a repository you can read (list_repositories lists " +
        "them), and omit 'branch' to read its default branch.";

    /// <summary>The <c>meta.error.code</c> of <see cref="BuildRepositoryRequired"/>.</summary>
    public const string RepositoryRequiredCode = "repository_required";

    /// <summary>The <c>meta.error.code</c> when a symbol argument matches no indexed symbol.</summary>
    public const string SymbolNotFoundCode = "symbol_not_found";

    /// <summary>The <c>meta.error.code</c> when a symbol argument matches several symbols and the tool needs one.</summary>
    public const string AmbiguousSymbolCode = "ambiguous_symbol";

    /// <summary>The <c>meta.error.code</c> when an argument is malformed or names nothing the tool accepts.</summary>
    public const string InvalidArgumentCode = "invalid_argument";

    private const string RepositoryRequiredMessage =
        "This request must select a repository to read. Name the repository and retry.";

    /// <summary>
    /// The error returned when a request that must select a repository names none
    /// (<see cref="DatabaseProvider.RequireRepositorySelection"/>). Its bytes depend only on the request, never
    /// on the catalog: it names no repository, carries no provenance or counts, and is identical whether the
    /// service is provisioned, empty, or holds many repositories, so it is not an existence oracle.
    /// </summary>
    public static string BuildRepositoryRequired() => BuildError(RepositoryRequiredCode, RepositoryRequiredMessage);

    /// <summary>
    /// <see cref="BuildRepositoryRequired()"/> followed by host <paramref name="guidance"/> (how to name a repository,
    /// and which ones the verified caller may read). The guidance is the host's to keep request-shaped: it must only
    /// describe what the CALLER can already see (its own grants), never other catalog content.
    /// </summary>
    public static string BuildRepositoryRequired(string? guidance) =>
        string.IsNullOrWhiteSpace(guidance)
            ? BuildRepositoryRequired()
            : BuildError(RepositoryRequiredCode, RepositoryRequiredMessage + " " + guidance.Trim());

    /// <summary>
    /// The actionable <see cref="RepositoryNotFoundCode"/> error for a NAMED repository selection that resolves to
    /// no complete default-branch snapshot on a NON-enforcing read path. It never echoes the requested repository.
    /// Under an enforced policy the same case is the uniform <see cref="BuildNotFound"/> instead.
    /// </summary>
    public static string BuildSelectionUnresolved() =>
        BuildError(RepositoryNotFoundCode,
            "No complete default-branch snapshot is available for the requested repository. " +
            "Name an indexed repository, or index this one first.");

    /// <summary>
    /// The actionable <see cref="RepositoryNotFoundCode"/> error for a NAMED repository branch selection (SVC-2)
    /// that resolves to no complete snapshot on a NON-enforcing read path. It never echoes the requested repository
    /// or branch. Under an enforced policy the same case is the uniform <see cref="BuildNotFound"/> instead.
    /// </summary>
    public static string BuildBranchSelectionUnresolved() =>
        BuildError(RepositoryNotFoundCode,
            "No complete snapshot is available for the requested repository branch. " +
            "Omit 'branch' to read the default branch, or name an indexed branch.");

    public static string BuildEmpty(string? message = null, SnapshotProvenance? provenance = null)
    {
        var response = new
        {
            Meta = new MetaObject
            {
                QueriedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                IndexFreshness = 0L,
                ResultCount = 0,
                Snapshot = SnapshotMeta.From(provenance)
            },
            Results = Array.Empty<object>(),
            Message = message
        };

        return JsonSerializer.Serialize(response, JsonOptions);
    }

    /// <summary>Joins the non-empty message parts with a space, or null when there are none.</summary>
    public static string? JoinMessages(params string?[] parts)
    {
        var present = parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()).ToList();
        return present.Count == 0 ? null : string.Join(" ", present);
    }

    /// <summary>
    /// Builds a structured ERROR response (Phase 11, criterion 6). Distinct from <see cref="BuildEmpty"/>:
    /// the <c>meta.error</c> block names a failure the caller must NOT read as "no matches" — most
    /// importantly a fail-closed authorization denial, which must never be presented as an empty
    /// successful result.
    /// </summary>
    public static string BuildError(string code, string message, SnapshotProvenance? provenance = null)
    {
        var response = new
        {
            Meta = new MetaObject
            {
                QueriedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                IndexFreshness = 0L,
                ResultCount = 0,
                Snapshot = SnapshotMeta.From(provenance),
                Error = new ErrorInfo { Code = code, Message = message }
            },
            Results = Array.Empty<object>(),
            Message = message
        };

        return JsonSerializer.Serialize(response, JsonOptions);
    }

    /// <summary>
    /// A structured error for a symbol argument the tool could not bind to exactly one symbol: <paramref name="code"/>
    /// is <see cref="SymbolNotFoundCode"/> (with the closest <paramref name="candidates"/>) or
    /// <see cref="AmbiguousSymbolCode"/> (with the matching ones and the total <paramref name="matchCount"/>). Each
    /// candidate's <c>fully_qualified_name</c> is in a form the tool accepts back, so the caller can retry with it.
    /// </summary>
    public static string BuildSymbolError(
        string code, string message, IReadOnlyList<SymbolCandidate> candidates, int? matchCount,
        SnapshotProvenance? provenance)
    {
        var response = new
        {
            Meta = new MetaObject
            {
                QueriedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                IndexFreshness = 0L,
                ResultCount = 0,
                Ambiguous = code == AmbiguousSymbolCode ? true : null,
                AmbiguousMatchCount = code == AmbiguousSymbolCode ? matchCount : null,
                Candidates = candidates.Count > 0 ? candidates : null,
                Snapshot = SnapshotMeta.From(provenance),
                Error = new ErrorInfo { Code = code, Message = message }
            },
            Results = Array.Empty<object>(),
            Message = message
        };

        return JsonSerializer.Serialize(response, JsonOptions);
    }

    /// <summary>
    /// Builds a structured "feature unavailable" response (Phase 8, criterion 3). Returned by a
    /// capability-aware query tool when the data it needs was not indexed under the active profile,
    /// instead of crashing or silently returning an empty result. The <c>feature_unavailable</c> block
    /// in <c>meta</c> names the missing feature, the active profile, and the minimum profile that would
    /// provide it, so an agent can act on it deterministically.
    /// </summary>
    public static string BuildFeatureUnavailable(
        string feature, string requiredProfile, string? activeProfile, string message)
    {
        var response = new
        {
            Meta = new MetaObject
            {
                QueriedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                IndexFreshness = 0L,
                ResultCount = 0,
                FeatureUnavailable = new FeatureUnavailableInfo
                {
                    Feature = feature,
                    RequiredProfile = requiredProfile,
                    ActiveProfile = activeProfile,
                    Message = message
                }
            },
            Results = Array.Empty<object>(),
            Message = message
        };

        return JsonSerializer.Serialize(response, JsonOptions);
    }
}

/// <summary>The <c>feature_unavailable</c> block surfaced in <see cref="MetaObject"/> (Phase 8).</summary>
public sealed class FeatureUnavailableInfo
{
    [JsonPropertyName("feature")]
    public required string Feature { get; set; }

    [JsonPropertyName("required_profile")]
    public required string RequiredProfile { get; set; }

    [JsonPropertyName("active_profile")]
    public string? ActiveProfile { get; set; }

    [JsonPropertyName("message")]
    public required string Message { get; set; }
}

/// <summary>The <c>error</c> block surfaced in <see cref="MetaObject"/> (Phase 11, criterion 6).</summary>
public sealed class ErrorInfo
{
    [JsonPropertyName("code")]
    public required string Code { get; set; }

    [JsonPropertyName("message")]
    public required string Message { get; set; }
}

/// <summary>One incompatibility dimension serialized under <see cref="SnapshotMeta.Incompatibilities"/>.</summary>
public sealed class IncompatibilityMeta
{
    [JsonPropertyName("dimension")]
    public required string Dimension { get; set; }

    [JsonPropertyName("expected")]
    public required string Expected { get; set; }

    [JsonPropertyName("actual")]
    public required string Actual { get; set; }
}

/// <summary>
/// The <c>snapshot</c> provenance block surfaced in <see cref="MetaObject"/> (Phase 11, criterion 4):
/// the committed base snapshot and its commit, the overlay generation (when the read rests on a Phase-10
/// overlay), completeness, the federation partition, working-tree dirtiness, any fallback reason, the
/// read-time compatibility verdict (issue #41), and the served generation's freshness. Emitted only when
/// a snapshot generation is actually selected — a pure legacy/direct-seed database omits it so its
/// response is byte-identical to pre-Phase-11 (the Phase-9 legacy-parity invariant).
/// </summary>
public sealed class SnapshotMeta
{
    [JsonPropertyName("base_snapshot_id")]
    public long? BaseSnapshotId { get; set; }

    [JsonPropertyName("base_commit")]
    public string? BaseCommit { get; set; }

    [JsonPropertyName("overlay_generation")]
    public long? OverlayGeneration { get; set; }

    [JsonPropertyName("is_overlay")]
    public bool IsOverlay { get; set; }

    [JsonPropertyName("completeness")]
    public required string Completeness { get; set; }

    [JsonPropertyName("scope")]
    public required string Scope { get; set; }

    [JsonPropertyName("dirty")]
    public bool Dirty { get; set; }

    [JsonPropertyName("fallback_reason")]
    public string? FallbackReason { get; set; }

    [JsonPropertyName("compatible")]
    public bool Compatible { get; set; }

    [JsonPropertyName("incompatibilities")]
    public IReadOnlyList<IncompatibilityMeta>? Incompatibilities { get; set; }

    [JsonPropertyName("freshness")]
    public long Freshness { get; set; }

    [JsonPropertyName("origin")]
    public string? Origin { get; set; }

    [JsonPropertyName("base_identity_hash")]
    public string? BaseIdentityHash { get; set; }

    /// <summary>The served base snapshot's durable checkout coverage (issue #119); omitted when not recorded.</summary>
    [JsonPropertyName("coverage")]
    public Sextant.Core.SnapshotCoverage? Coverage { get; set; }

    /// <summary>The repository the host selected for a request that named none (omitted otherwise).</summary>
    [JsonPropertyName("repository")]
    public string? Repository { get; set; }

    /// <summary>"implicit" when <see cref="Repository"/> was selected by the host (omitted otherwise).</summary>
    [JsonPropertyName("repository_selection")]
    public string? RepositorySelection { get; set; }

    /// <summary>Projects planner provenance into the serializable meta block, or null to omit it.</summary>
    public static SnapshotMeta? From(SnapshotProvenance? p)
    {
        if (p == null) return null;
        return new SnapshotMeta
        {
            BaseSnapshotId = p.BaseSnapshotId,
            BaseCommit = p.BaseCommit,
            OverlayGeneration = p.OverlayGeneration,
            IsOverlay = p.IsOverlay,
            Completeness = p.Completeness,
            Scope = p.Scope,
            Dirty = p.Dirty,
            FallbackReason = p.FallbackReason,
            Compatible = p.Compatible,
            Incompatibilities = p.Incompatibilities?
                .Select(i => new IncompatibilityMeta { Dimension = i.Dimension, Expected = i.Expected, Actual = i.Actual })
                .ToList(),
            Freshness = p.Freshness,
            Origin = p.Origin,
            BaseIdentityHash = p.BaseIdentityHash,
            Coverage = p.Coverage,
            Repository = p.Repository,
            RepositorySelection = p.RepositorySelection
        };
    }
}

public sealed class MetaObject
{
    [JsonPropertyName("queried_at")]
    public long QueriedAt { get; set; }

    [JsonPropertyName("index_freshness")]
    public long IndexFreshness { get; set; }

    [JsonPropertyName("result_count")]
    public int ResultCount { get; set; }

    [JsonPropertyName("ambiguous")]
    public bool? Ambiguous { get; set; }

    [JsonPropertyName("ambiguous_match_count")]
    public int? AmbiguousMatchCount { get; set; }

    [JsonPropertyName("selected_project_id")]
    public string? SelectedProjectId { get; set; }

    [JsonPropertyName("selected_symbol_key")]
    public string? SelectedSymbolKey { get; set; }

    [JsonPropertyName("candidates")]
    public IReadOnlyList<SymbolCandidate>? Candidates { get; set; }

    [JsonPropertyName("feature_unavailable")]
    public FeatureUnavailableInfo? FeatureUnavailable { get; set; }

    [JsonPropertyName("snapshot")]
    public SnapshotMeta? Snapshot { get; set; }

    [JsonPropertyName("next_cursor")]
    public string? NextCursor { get; set; }

    [JsonPropertyName("error")]
    public ErrorInfo? Error { get; set; }
}
