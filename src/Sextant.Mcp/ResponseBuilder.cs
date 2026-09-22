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
}
