using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sextant.Store;

/// <summary>
/// The wire JSON options for the remote snapshot-federation surface (issue #51/#60): <c>snake_case</c>
/// property names with null omission. It is deliberately co-located with the base-snapshot seam in
/// <c>Sextant.Store</c> so BOTH the client (<see cref="RemoteHttpBaseSnapshotSource"/>, reachable from the
/// MCP planner) and the service response side speak the identical format without the client having to
/// reference <c>Sextant.Service</c>. Matches <c>Sextant.Service.ServiceJson.Options</c> byte-for-byte
/// (snake_case + <see cref="JsonIgnoreCondition.WhenWritingNull"/>), so a page serialized by the service
/// deserializes here unchanged.
/// </summary>
public static class SnapshotWireJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
