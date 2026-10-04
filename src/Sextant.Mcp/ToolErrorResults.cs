using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Sextant.Mcp;

/// <summary>
/// Marks a tool result that carries a structured error as an MCP tool error (<c>isError: true</c>). Every tool
/// answers with the <see cref="ResponseBuilder"/> envelope, and a failure is a <c>meta.error</c> object in it
/// (<see cref="ResponseBuilder.BuildError"/>). A client that reads only the result text then sees an empty-looking
/// answer, so it may conclude "nothing matches" when the call actually failed (issue #163). This call-tool filter
/// sets <see cref="CallToolResult.IsError"/> on exactly those results, leaving their text unchanged, so a client
/// can tell "the tool failed" (fix the arguments and retry) from "the answer is empty".
/// </summary>
public static class ToolErrorResults
{
    private static readonly byte[] MetaProperty = Encoding.UTF8.GetBytes("meta");
    private static readonly byte[] ErrorProperty = Encoding.UTF8.GetBytes("error");

    /// <summary>The call-tool filter. Register it innermost, so it sees the tool's own result.</summary>
    public static McpRequestFilter<CallToolRequestParams, CallToolResult> CallToolFilter() =>
        next => async (context, cancellationToken) =>
        {
            var result = await next(context, cancellationToken);
            if (result.IsError != true && CarriesError(result))
                result.IsError = true;
            return result;
        };

    /// <summary>Whether <paramref name="result"/> is one text block whose JSON envelope has a <c>meta.error</c> object.</summary>
    public static bool CarriesError(CallToolResult result) =>
        result.Content is [TextContentBlock { Text: { } text }] && HasMetaError(text);

    /// <summary>Whether <paramref name="json"/> is an object whose top-level <c>meta</c> object has an <c>error</c> object.</summary>
    public static bool HasMetaError(string json)
    {
        if (string.IsNullOrEmpty(json) || json[0] != '{')
            return false;
        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json));
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return false;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var isMeta = reader.ValueTextEquals(MetaProperty);
                if (!reader.Read())
                    return false;
                if (!isMeta)
                {
                    reader.Skip();
                    continue;
                }
                if (reader.TokenType != JsonTokenType.StartObject)
                    return false;
                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    var isError = reader.ValueTextEquals(ErrorProperty);
                    if (!reader.Read())
                        return false;
                    if (isError)
                        return reader.TokenType == JsonTokenType.StartObject;
                    reader.Skip();
                }
                return false;
            }
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
