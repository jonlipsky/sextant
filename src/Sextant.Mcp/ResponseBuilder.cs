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

    public static string Build<T>(List<T> results, long? indexFreshness = null)
    {
        var response = new
        {
            Meta = new MetaObject
            {
                QueriedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                IndexFreshness = indexFreshness ?? 0,
                ResultCount = results.Count
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
}
