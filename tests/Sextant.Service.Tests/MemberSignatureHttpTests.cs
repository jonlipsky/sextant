using System.Text.Json;
using System.Text.Json.Nodes;
using Sextant.Core;
using Sextant.Mcp;
using Sextant.Mcp.Tools;
using Sextant.Store;

namespace Sextant.Service.Tests;

/// <summary>
/// A member's <c>signature</c> is its C# declaration (return type, parameter names, modifiers and defaults; migration
/// 026, analyzer version 5), so an agent can answer "what does this interface declare" without opening the file,
/// while its <c>fully_qualified_name</c> is printed exactly as before and still resolves (#219). A row indexed before
/// 026 (no declaration) keeps its legacy signature, so a catalog holding snapshots from both builds serves both.
/// The tools the remote surface lists go through the service's <c>/mcp</c> over HTTP; the local-only signature tools
/// (<c>get_api_surface</c>, <c>find_by_signature</c>) read the same catalog through a local provider.
/// </summary>
[TestClass]
public sealed class MemberSignatureHttpTests
{
    private const string ChannelStore = "global::App.Core.Channels.IChannelStore";
    private const string ChannelFile = "src/App.Core/Channels/IChannelStore.cs";
    private const string CreateDeclaration =
        "Task<ChannelRecord> CreateAsync(string tenantId, string channelChatKind, string applicationInstanceId, DateTime installedAt, CancellationToken cancellationToken)";
    private const string CreateLegacy =
        "App.Core.Channels.IChannelStore.CreateAsync(string, string, string, System.DateTime, System.Threading.CancellationToken)";
    private const string FindDeclaration = "Task<ChannelRecord?> FindAsync(string tenantId, CancellationToken cancellationToken = default)";
    private const string LegacyStore = "global::Other.ILegacyStore";
    private const string LoadLegacy = "Other.ILegacyStore.LoadAsync(string, System.Threading.CancellationToken)";

    // Repository A as analyzer version 5 indexes it (declarations), repository B as version 4 did (none).
    private static void Seed(IndexDatabase db, AgentOutputFixture fixture)
    {
        var conn = db.GetConnection();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var file = AgentOutputFixture.SourceFile(conn, fixture.CheckoutRoot, fixture.CoreProject, ChannelFile, ChannelFile,
        [
            "namespace App.Core.Channels;", "", "public interface IChannelStore", "{",
            "    Task<ChannelRecord> CreateAsync(string tenantId, string channelChatKind, string applicationInstanceId, DateTime installedAt, CancellationToken cancellationToken);",
            "    Task<ChannelRecord?> FindAsync(string tenantId, CancellationToken cancellationToken = default);",
            "    int Count { get; }", "}"
        ], now);
        AgentOutputFixture.Symbol(conn, fixture.CoreProject, ChannelStore, "IChannelStore", SymbolKind.Interface, file, 3, 8, now);
        AgentOutputFixture.Symbol(conn, fixture.CoreProject, "CreateAsync", "CreateAsync", SymbolKind.Method, file, 5, 5, now,
            key: "M:App.Core.Channels.IChannelStore.CreateAsync(System.String,System.String,System.String,System.DateTime,System.Threading.CancellationToken)",
            signature: CreateLegacy, declaration: CreateDeclaration);
        AgentOutputFixture.Symbol(conn, fixture.CoreProject, "FindAsync", "FindAsync", SymbolKind.Method, file, 6, 6, now,
            key: "M:App.Core.Channels.IChannelStore.FindAsync(System.String,System.Threading.CancellationToken)",
            signature: "App.Core.Channels.IChannelStore.FindAsync(string, System.Threading.CancellationToken)",
            declaration: FindDeclaration);
        AgentOutputFixture.Symbol(conn, fixture.CoreProject, "Count", "Count", SymbolKind.Property, file, 7, 7, now,
            key: "P:App.Core.Channels.IChannelStore.Count", signature: "App.Core.Channels.IChannelStore.Count",
            declaration: "int Count { get; }");

        var otherCheckout = Path.Combine(fixture.DataRoot, "checkouts", AgentOutputFixture.OtherCheckoutDirectory);
        var legacyFile = AgentOutputFixture.SourceFile(conn, otherCheckout, fixture.OtherProject, "src/Other/ILegacyStore.cs",
            "src/Other/ILegacyStore.cs",
            ["namespace Other;", "public interface ILegacyStore", "{", "    Task LoadAsync(string key, CancellationToken ct);", "}"], now);
        AgentOutputFixture.Symbol(conn, fixture.OtherProject, LegacyStore, "ILegacyStore", SymbolKind.Interface, legacyFile, 2, 5, now);
        AgentOutputFixture.Symbol(conn, fixture.OtherProject, "LoadAsync", "LoadAsync", SymbolKind.Method, legacyFile, 4, 4, now,
            key: "M:Other.ILegacyStore.LoadAsync(System.String,System.Threading.CancellationToken)", signature: LoadLegacy);
    }

    private static Task<AgentOutputHarness> StartAsync() => AgentOutputHarness.StartAsync(seed: Seed);

    [TestMethod]
    public async Task GetTypeMembers_SignatureIsTheDeclaration_AndTheNameStillResolves()
    {
        await using var host = await StartAsync();

        var text = await host.CallTextAsync("get_type_members",
            new JsonObject { ["symbol_fqn"] = ChannelStore, ["repository"] = AgentOutputFixture.RepoA });
        var rows = JsonDocument.Parse(text).RootElement.GetProperty("results").EnumerateArray().ToList();

        CollectionAssert.AreEqual(new[] { CreateDeclaration, FindDeclaration, "int Count { get; }" },
            rows.Select(r => r.GetProperty("signature").GetString()).ToArray());
        var create = rows[0];
        Assert.AreEqual("global::" + CreateLegacy, create.GetProperty("fully_qualified_name").GetString(),
            "the printed name is unchanged (#219)");
        StringAssert.Contains(text, "Task<ChannelRecord>", "angle brackets are written literally, not as \\u003C");

        // The printed name goes back into any tool and finds the same member.
        var found = await host.CallAsync("find_symbol", new JsonObject { ["name"] = create.GetProperty("fully_qualified_name").GetString() });
        var hit = found.GetProperty("results").EnumerateArray().Single();
        Assert.AreEqual(CreateDeclaration, hit.GetProperty("signature").GetString());
        Assert.AreEqual("global::" + CreateLegacy, hit.GetProperty("fully_qualified_name").GetString());
    }

    [TestMethod]
    public async Task FileSymbolsAndLocalApiSurface_ShowTheDeclaration()
    {
        await using var host = await StartAsync();

        var outline = await host.CallAsync("get_file_symbols", new JsonObject { ["file_path"] = ChannelFile });
        var create = outline.GetProperty("results").EnumerateArray()
            .Single(r => r.GetProperty("display_name").GetString() == "CreateAsync");
        Assert.AreEqual(CreateDeclaration, create.GetProperty("signature").GetString());

        using var local = host.LocalProvider();
        string? cursor = null;
        JsonElement? surfaceRow = null;
        for (var pages = 0; pages < 20 && surfaceRow is null; pages++)
        {
            var surface = Parse(GetApiSurfaceTool.GetApiSurface(local, $"App.Core:{host.Fixture.SnapshotA}", limit: 200, cursor: cursor));
            Assert.IsFalse(surface.GetProperty("meta").TryGetProperty("error", out var error), error.ToString());
            surfaceRow = surface.GetProperty("results").EnumerateArray()
                .Where(r => r.GetProperty("display_name").GetString() == "CreateAsync").Select(r => (JsonElement?)r).FirstOrDefault();
            cursor = surface.GetProperty("meta").TryGetProperty("next_cursor", out var next) ? next.GetString() : null;
            if (cursor is null) break;
        }
        Assert.IsNotNull(surfaceRow, "get_api_surface lists the member");
        Assert.AreEqual(CreateDeclaration, surfaceRow.Value.GetProperty("signature").GetString());
    }

    [TestMethod]
    public async Task GetTypeMembers_Pages()
    {
        await using var host = await StartAsync();

        var first = await host.CallAsync("get_type_members", new JsonObject { ["symbol_fqn"] = ChannelStore, ["limit"] = 2 });
        Assert.AreEqual(3, first.GetProperty("meta").GetProperty("total").GetInt32());
        Assert.AreEqual(2, first.GetProperty("results").GetArrayLength());
        var cursor = first.GetProperty("meta").GetProperty("next_cursor").GetString();

        var second = await host.CallAsync("get_type_members",
            new JsonObject { ["symbol_fqn"] = ChannelStore, ["limit"] = 2, ["cursor"] = cursor });
        var last = second.GetProperty("results").EnumerateArray().Single();
        Assert.AreEqual("int Count { get; }", last.GetProperty("signature").GetString(), "the second page resumes at the third member");
        Assert.IsFalse(second.GetProperty("meta").TryGetProperty("next_cursor", out _));

        var reused = await host.CallAsync("get_type_members",
            new JsonObject { ["symbol_fqn"] = ChannelStore, ["include_inherited"] = true, ["limit"] = 2, ["cursor"] = cursor });
        Assert.AreEqual(Paging.InvalidCursorCode,
            reused.GetProperty("meta").GetProperty("error").GetProperty("code").GetString(), "the cursor is bound to its arguments");
    }

    // A catalog upgraded to 026 still serves snapshots indexed before it until they are re-indexed: their members
    // have no declaration and keep the legacy signature, next to repository A's declarations.
    [TestMethod]
    public async Task PreDeclarationSnapshot_KeepsTheLegacySignature_NextToANewOne()
    {
        await using var host = await StartAsync();

        var legacy = await host.CallAsync("get_type_members", new JsonObject { ["symbol_fqn"] = LegacyStore }, AgentOutputFixture.RepoB);
        var load = legacy.GetProperty("results").EnumerateArray().Single();
        Assert.AreEqual(LoadLegacy, load.GetProperty("signature").GetString());
        Assert.AreEqual("global::" + LoadLegacy, load.GetProperty("fully_qualified_name").GetString());

        var current = await host.CallAsync("get_type_members", new JsonObject { ["symbol_fqn"] = ChannelStore });
        Assert.AreEqual(CreateDeclaration, current.GetProperty("results")[0].GetProperty("signature").GetString());

        using var localB = host.LocalProvider(AgentOutputFixture.RepoB);
        var byParameters = Parse(FindBySignatureTool.FindBySignature(localB, parameter_type: "CancellationToken", parameter_count: 2));
        Assert.AreEqual(LoadLegacy, byParameters.GetProperty("results").EnumerateArray().Single().GetProperty("signature").GetString(),
            "parameter filters still read a legacy signature");
    }

    [TestMethod]
    public async Task LocalFindBySignature_MatchesTheDeclaration()
    {
        await using var host = await StartAsync();
        using var local = host.LocalProvider();

        var byReturn = Parse(FindBySignatureTool.FindBySignature(local, return_type: "Task<ChannelRecord>"));
        Assert.AreEqual(CreateDeclaration, byReturn.GetProperty("results").EnumerateArray().Single().GetProperty("signature").GetString(),
            "the return type is read from the declaration");

        var byParameters = Parse(FindBySignatureTool.FindBySignature(local, parameter_type: "DateTime", parameter_count: 5));
        Assert.AreEqual(CreateDeclaration, byParameters.GetProperty("results").EnumerateArray().Single().GetProperty("signature").GetString());

        var noParameters = Parse(FindBySignatureTool.FindBySignature(local, return_type: "ChannelRecord?", parameter_count: 2));
        Assert.AreEqual(FindDeclaration, noParameters.GetProperty("results").EnumerateArray().Single().GetProperty("signature").GetString(),
            "a default value is not a parameter type");
    }

    private static JsonElement Parse(string text) => JsonDocument.Parse(text).RootElement.Clone();
}
