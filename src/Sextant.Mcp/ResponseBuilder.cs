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
        SnapshotProvenance? provenance = null)
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
                Snapshot = SnapshotMeta.From(provenance)
            },
            Results = results
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

    /// <summary>
    /// Builds a structured ERROR response (Phase 11, criterion 6). Distinct from <see cref="BuildEmpty"/>:
    /// the <c>meta.error</c> block names a failure the caller must NOT read as "no matches" — most
    /// importantly a fail-closed authorization denial, which must never be presented as an empty
    /// successful result.
    /// </summary>
    public static string BuildError(string code, string message)
    {
        var response = new
        {
            Meta = new MetaObject
            {
                QueriedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                IndexFreshness = 0L,
                ResultCount = 0,
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
            Freshness = p.Freshness
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

    [JsonPropertyName("error")]
    public ErrorInfo? Error { get; set; }
}
