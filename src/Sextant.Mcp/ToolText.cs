namespace Sextant.Mcp;

/// <summary>
/// Shared, deliberately short parameter descriptions for the MCP query tools, so each tool's
/// <c>tools/list</c> entry stays small (an agent reads every one before choosing a tool).
/// </summary>
internal static class ToolText
{
    public const string SymbolFqn = "Fully qualified name.";
    public const string Scope = "file:PATH, project:ID or solution:PATH.";
    public const string Kind = "e.g. class, method";
    public const string ProjectId = "canonical_id";
    public const string Federation = "federated, base_only or overlay_only";
    public const string Limit = Paging.LimitDescription;
    public const string Cursor = Paging.CursorDescription;
}
