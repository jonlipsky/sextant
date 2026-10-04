using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sextant.Service.Tests;

/// <summary>Measurement probe for the agent-sized-output work (prints sizes; asserts nothing).</summary>
[TestClass]
public class AgentOutputMeasurementTests
{
    [TestMethod]
    [TestCategory("Measurement")]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Measure_ToolsList_And_FindReferences(bool delegateCallers)
    {
        await using var host = await AgentOutputHarness.StartAsync(delegateCallers);

        var list = await host.RpcAsync("tools/list");
        using var doc = JsonDocument.Parse(list);
        var tools = doc.RootElement.GetProperty("tools");
        var descriptionChars = tools.EnumerateArray().Sum(t => t.TryGetProperty("description", out var d) ? d.GetString()!.Length : 0);
        var schemaChars = tools.EnumerateArray().Sum(t => t.GetProperty("inputSchema").GetRawText().Length);
        Console.WriteLine($"tools/list (delegate callers: {delegateCallers}): {tools.GetArrayLength()} tools, {list.Length} chars total, {descriptionChars} description chars, {schemaChars} schema chars");
        foreach (var tool in tools.EnumerateArray())
            Console.WriteLine($"  tool {tool.GetProperty("name").GetString()}: {tool.GetRawText().Length}");
        var dump = Environment.GetEnvironmentVariable("SEXTANT_TOOLS_LIST_DUMP");
        if (!string.IsNullOrEmpty(dump))
            File.WriteAllText(delegateCallers ? dump + ".delegate" : dump, list);
        if (delegateCallers)
            return;

        var flat = await host.CallTextAsync("find_references", new JsonObject
        {
            ["symbol_fqn"] = AgentOutputFixture.TargetInterface, ["repository"] = AgentOutputFixture.RepoA
        });
        var grouped = await host.CallTextAsync("find_references", new JsonObject
        {
            ["symbol_fqn"] = AgentOutputFixture.TargetInterface, ["repository"] = AgentOutputFixture.RepoA, ["group_by"] = "project"
        });
        Console.WriteLine($"find_references flat: {flat.Length} chars; group_by=project: {grouped.Length} chars");
        Console.WriteLine($"find_references sample: {flat[..Math.Min(1500, flat.Length)]}");
    }
}
