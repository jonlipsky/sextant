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

    public static string Build<T>(List<T> results, long? indexFreshness = null, SymbolAmbiguity? ambiguity = null)
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
                Candidates = ambiguity?.Candidates
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

    public static string BuildEmpty(string? message = null)
    {
        var response = new
        {
            Meta = new MetaObject
            {
                QueriedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                IndexFreshness = 0L,
                ResultCount = 0
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
}
