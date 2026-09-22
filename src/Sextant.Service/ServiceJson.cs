using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sextant.Service;

/// <summary>Shared JSON options for the service wire format — <c>snake_case</c> to match the MCP tools.</summary>
public static class ServiceJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
