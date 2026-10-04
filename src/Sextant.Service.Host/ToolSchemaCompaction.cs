using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Sextant.Service.Host;

/// <summary>
/// Shrinks the remote <c>tools/list</c> (issue #145): an optional parameter's generated schema
/// <c>"type":["string","null"],"default":null</c> becomes <c>"type":"string"</c>. Omitting an optional argument means
/// the same as passing null, so nothing a client can call changes; a client still may send an explicit null. Added
/// FIRST among the list filters, so it runs outermost and compacts every other filter's output too. The SDK's tool
/// definitions are shared across requests, so each changed tool is cloned, never mutated.
/// </summary>
internal static class ToolSchemaCompaction
{
    public static McpRequestFilter<ListToolsRequestParams, ListToolsResult> ListToolsFilter() =>
        next => async (context, cancellationToken) =>
        {
            var result = await next(context, cancellationToken);
            result.Tools = result.Tools.Select(Compact).ToList();
            return result;
        };

    internal static Tool Compact(Tool tool)
    {
        var schema = JsonNode.Parse(tool.InputSchema.GetRawText())?.AsObject();
        if (schema?["properties"] is not JsonObject properties)
            return tool;

        var changed = false;
        foreach (var (_, value) in properties)
        {
            if (value is not JsonObject property)
                continue;
            if (property["type"] is JsonArray types && NonNullType(types) is { } type)
            {
                property["type"] = type;
                changed = true;
            }
            if (property.TryGetPropertyValue("default", out var defaultValue) && defaultValue is null)
            {
                property.Remove("default");
                changed = true;
            }
        }
        if (!changed)
            return tool;

        var clone = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(tool, McpJsonUtilities.DefaultOptions), typeof(Tool), McpJsonUtilities.DefaultOptions) as Tool
            ?? throw new InvalidOperationException($"The tool '{tool.Name}' could not be copied.");
        clone.InputSchema = JsonSerializer.SerializeToElement(schema, McpJsonUtilities.DefaultOptions);
        return clone;
    }

    // ["T","null"] (either order) => "T"; any other union is left alone.
    private static string? NonNullType(JsonArray types)
    {
        if (types.Count != 2)
            return null;
        var names = types.Select(t => t?.GetValueKind() == JsonValueKind.String ? t.GetValue<string>() : null).ToList();
        if (names[0] == "null" && names[1] is { } second && second != "null")
            return second;
        if (names[1] == "null" && names[0] is { } first && first != "null")
            return first;
        return null;
    }
}
