namespace Sextant.Mcp.Tests;

public static class McpTestFixtureInstance
{
    private static readonly Lazy<McpTestFixture> _lazy = new(() => new McpTestFixture());

    public static McpTestFixture Instance => _lazy.Value;
}
