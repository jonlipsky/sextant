using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sextant.Mcp;

public static class ResponseBuilder
{
    // A tool result is JSON text an agent reads as is, never embedded in HTML, so characters such as `<`, `>`, `&`,
    // `'` and `+` are written literally: `Task<int>` instead of `Task\u003Cint\u003E`, which is shorter and readable.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>The writer options for re-serializing a tool result (<see cref="RemoteResponsePresenter"/>).</summary>
    internal static readonly JsonSerializerOptions NodeWriteOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string Build<T>(
        List<T> results, long? indexFreshness = null, SymbolAmbiguity? ambiguity = null,
        SnapshotProvenance? provenance = null, string? nextCursor = null, string? message = null, string? warning = null)
    {
        var response = new
        {
            Meta = new MetaObject
            {
                QueriedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                IndexFreshness = indexFreshness ?? 0,
                ResultCount = results.Count,
                Warning = warning,
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
    /// Builds one page of a bounded result (<see cref="Paging"/>): <paramref name="rows"/> are this page's rows
    /// (already sliced to <c>limit</c> and mapped), <paramref name="total"/> the size of the whole result.
    /// The page then ends at the last row whose response fits <see cref="PageRequest.MaxChars"/>
    /// (<see cref="ResponseBudget"/>; at least one row): a page cut that way says so in
    /// <c>meta.page_truncated_by: "size"</c> and its message, and its <c>meta.next_cursor</c> resumes at the first
    /// row it left out, exactly like a page that ended at <c>limit</c>. <c>meta.total</c> is always present,
    /// <c>meta.next_cursor</c> only when rows remain, and <paramref name="summary"/> (counts per file/project/…)
    /// is computed only for a first page that does not hold the whole result and comes BEFORE the rows so an
    /// agent can narrow the query instead of paging. <c>meta.result_count</c> is the number of rows on this page.
    /// <paramref name="shape"/> turns the page's rows into the <c>results</c> value when it is not the plain list
    /// (e.g. grouped). <paramref name="message"/> is an explicit statement about a valid answer, as in
    /// <see cref="Build{T}"/>.
    /// </summary>
    public static string BuildPage<T>(
        List<T> rows, int total, PageRequest page, long? indexFreshness = null, SymbolAmbiguity? ambiguity = null,
        SnapshotProvenance? provenance = null, Func<object?>? summary = null, string? message = null,
        Func<List<T>, object>? shape = null, string? warning = null)
    {
        var queriedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var summaryValue = new Lazy<object?>(() => summary?.Invoke());

        string Render(int count)
        {
            var pageRows = count == rows.Count ? rows : rows.GetRange(0, count);
            var end = page.Offset + count;
            var more = end < total;
            var cut = count < rows.Count;
            var response = new
            {
                Meta = new MetaObject
                {
                    QueriedAt = queriedAt,
                    IndexFreshness = indexFreshness ?? 0,
                    ResultCount = count,
                    Total = total,
                    Warning = warning,
                    Ambiguous = ambiguity != null ? true : null,
                    AmbiguousMatchCount = ambiguity?.Candidates.Count,
                    SelectedProjectId = ambiguity?.SelectedProjectId,
                    SelectedSymbolKey = ambiguity?.SelectedSymbolKey,
                    Candidates = ambiguity?.Candidates,
                    Snapshot = SnapshotMeta.From(provenance),
                    NextCursor = more ? Paging.EncodeCursor(end, page.Binding) : null,
                    PageTruncatedBy = cut ? ResponseBudget.SizeTruncation : null
                },
                Summary = page.Offset == 0 && more ? summaryValue.Value : null,
                Results = shape != null ? shape(pageRows) : (object)pageRows,
                Message = cut ? JoinMessages(message, ResponseBudget.PageCutMessage(count, page.MaxChars)) : message
            };

            return JsonSerializer.Serialize(response, JsonOptions);
        }

        return ResponseBudget.Fit(rows.Count, page.MaxChars, Render, page.Measure);
    }

    /// <summary>
    /// Builds the response of a tool that does not page, bounded like <see cref="BuildPage{T}"/>: when every row
    /// does not fit <see cref="FederatedReadContext.MaxResponseChars"/>, it keeps the most leading rows that do
    /// (at least one), sets <c>meta.total</c> to the full count and <c>meta.page_truncated_by: "size"</c>, and says
    /// how to narrow the query. There is no cursor. A result that fits is byte-identical to
    /// <see cref="Build{T}"/>. <paramref name="shape"/> turns the kept rows into the <c>results</c> value.
    /// </summary>
    public static string BuildBounded<T>(
        List<T> rows, FederatedReadContext context, long? indexFreshness = null, SymbolAmbiguity? ambiguity = null,
        SnapshotProvenance? provenance = null, string? message = null, Func<List<T>, object>? shape = null,
        string? warning = null)
    {
        var queriedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var maxChars = context.MaxResponseChars;

        string Render(int count)
        {
            var kept = count == rows.Count ? rows : rows.GetRange(0, count);
            var cut = count < rows.Count;
            var response = new
            {
                Meta = new MetaObject
                {
                    QueriedAt = queriedAt,
                    IndexFreshness = indexFreshness ?? 0,
                    ResultCount = count,
                    Total = cut ? rows.Count : null,
                    Warning = warning,
                    Ambiguous = ambiguity != null ? true : null,
                    AmbiguousMatchCount = ambiguity?.Candidates.Count,
                    SelectedProjectId = ambiguity?.SelectedProjectId,
                    SelectedSymbolKey = ambiguity?.SelectedSymbolKey,
                    Candidates = ambiguity?.Candidates,
                    Snapshot = SnapshotMeta.From(provenance),
                    PageTruncatedBy = cut ? ResponseBudget.SizeTruncation : null
                },
                Results = shape != null ? shape(kept) : (object)kept,
                Message = cut ? JoinMessages(message, ResponseBudget.ResultCutMessage(count, rows.Count, maxChars)) : message
            };

            return JsonSerializer.Serialize(response, JsonOptions);
        }

        return ResponseBudget.Fit(rows.Count, maxChars, Render, text => ResponseBudget.Measure(text, context));
    }

    /// <summary>
    /// Builds one page of a keyset-paged result (a cursor that names the last row returned, not an offset), bounded
    /// like <see cref="BuildPage{T}"/>: a page over <see cref="FederatedReadContext.MaxResponseChars"/> keeps its
    /// most leading rows that fit (at least one) and resumes after the last of them
    /// (<paramref name="cursorAfter"/>), with <c>meta.page_truncated_by: "size"</c>. A page that fits is
    /// byte-identical to <see cref="Build{T}"/> with <paramref name="nextCursor"/>.
    /// </summary>
    public static string BuildKeysetPage<T>(
        List<T> rows, Func<T, string> cursorAfter, string? nextCursor, FederatedReadContext context,
        long? indexFreshness = null, SnapshotProvenance? provenance = null)
    {
        var maxChars = context.MaxResponseChars;

        string Render(int count)
        {
            if (count == rows.Count)
                return Build(rows, indexFreshness, ambiguity: null, provenance, nextCursor);
            var response = new
            {
                Meta = new MetaObject
                {
                    QueriedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    IndexFreshness = indexFreshness ?? 0,
                    ResultCount = count,
                    Snapshot = SnapshotMeta.From(provenance),
                    NextCursor = cursorAfter(rows[count - 1]),
                    PageTruncatedBy = ResponseBudget.SizeTruncation
                },
                Results = rows.GetRange(0, count),
                Message = ResponseBudget.PageCutMessage(count, maxChars)
            };

            return JsonSerializer.Serialize(response, JsonOptions);
        }

        return ResponseBudget.Fit(rows.Count, maxChars, Render, text => ResponseBudget.Measure(text, context));
    }

    /// <summary>
    /// Builds an index-status response (Phase 8) — the standard results/meta envelope plus a top-level
    /// <c>index</c> object describing the active profile, its enabled feature capabilities, and retained
    /// storage, and one page of the per-project rows (<see cref="BuildPage{T}"/> paging and size budget).
    /// Serialized with the same snake_case policy as every other response.
    /// </summary>
    public static string BuildStatus<T>(List<T> rows, int total, PageRequest page, long indexFreshness, object index)
    {
        var queriedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        string Render(int count)
        {
            var end = page.Offset + count;
            var cut = count < rows.Count;
            var response = new
            {
                Meta = new MetaObject
                {
                    QueriedAt = queriedAt,
                    IndexFreshness = indexFreshness,
                    ResultCount = count,
                    Total = total,
                    NextCursor = end < total ? Paging.EncodeCursor(end, page.Binding) : null,
                    PageTruncatedBy = cut ? ResponseBudget.SizeTruncation : null
                },
                Index = index,
                Results = count == rows.Count ? rows : rows.GetRange(0, count),
                Message = cut ? ResponseBudget.PageCutMessage(count, page.MaxChars) : null
            };

            return JsonSerializer.Serialize(response, JsonOptions);
        }

        return ResponseBudget.Fit(rows.Count, page.MaxChars, Render, page.Measure);
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

    /// <summary>The size of the whole result of a paged tool (<see cref="Paging"/>); omitted by unpaged tools.</summary>
    [JsonPropertyName("total")]
    public int? Total { get; set; }

    /// <summary>
    /// A short note when the answer is for a symbol the argument did not name exactly (a qualified name resolved by
    /// its trailing <c>Type.Member</c> or <c>Type</c>); omitted otherwise.
    /// </summary>
    [JsonPropertyName("warning")]
    public string? Warning { get; set; }

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

    /// <summary>
    /// <c>"size"</c> when the response holds fewer rows than it could because the next row would exceed the
    /// response size budget (<see cref="ResponseBudget"/>); omitted otherwise.
    /// </summary>
    [JsonPropertyName("page_truncated_by")]
    public string? PageTruncatedBy { get; set; }

    [JsonPropertyName("error")]
    public ErrorInfo? Error { get; set; }
}
