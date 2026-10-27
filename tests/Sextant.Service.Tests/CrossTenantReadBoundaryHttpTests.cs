using System.Net;
using System.Text.Json;
using Sextant.Mcp;
using Sextant.Service.Host;
using static Sextant.Service.Tests.CallerAssertionHttpTests;

namespace Sextant.Service.Tests;

/// <summary>
/// Issue #181 at the whole read surface. Tenant A holds the only grant on Widgets; tenant B reaches the same service
/// (and the same content-addressed snapshot) with every kind of caller it can have. Every tool the live <c>/mcp</c>
/// registry lists and every <c>/query</c> route the endpoint data source maps is discovered at run time and run as
/// tenant B, so a tool or route added later is covered without editing this file. Each must answer with the uniform
/// not-found / a denial and no Widgets data, including with a cursor tenant A minted.
/// </summary>
[TestClass]
public class CrossTenantReadBoundaryHttpTests
{
    private const string Gadgets = "https://github.com/acme/gadgets";
    private const string Secret = "WidgetsSecret";
    private const string SecretSymbol = "global::App.WidgetsSecret0";
    private static readonly string[] Markers = [Secret, "commit-w1", "acme/widgets"];
    private static readonly string NotFound = WithoutTimestamp(JsonDocument.Parse(ResponseBuilder.BuildNotFound()).RootElement);

    private sealed record ToolInfo(string Name, HashSet<string> Properties, IReadOnlyDictionary<string, string> Types, HashSet<string> Required);

    private static async Task<Harness> StartAsync() =>
        await Harness.StartAsync(seed: db =>
        {
            GrantServiceTests.PublishOnBranch(db, Gadgets, "commit-g1", "main", isDefault: true);
            using var cmd = db.GetConnection().CreateCommand();
            cmd.CommandText = """
                WITH RECURSIVE n(i) AS (SELECT 0 UNION ALL SELECT i + 1 FROM n WHERE i < 5)
                INSERT INTO symbols
                    (project_id, symbol_key, fully_qualified_name, display_name, kind, accessibility,
                     file_version_id, line_start, line_end, last_indexed_at)
                SELECT s.project_id, 'global::App.WidgetsSecret' || n.i, 'global::App.WidgetsSecret' || n.i,
                       'WidgetsSecret' || n.i, s.kind, s.accessibility, s.file_version_id, 1, 10, s.last_indexed_at
                FROM n, symbols s
                JOIN projects p ON p.id = s.project_id
                WHERE p.git_remote_url = @repo AND s.fully_qualified_name = 'global::App.Type0';
                """;
            cmd.Parameters.AddWithValue("@repo", Widgets);
            Assert.AreEqual(6, cmd.ExecuteNonQuery(), "the Widgets file holds symbols only tenant A may read");
        });

    private static async Task GrantSelfAsync(Harness host, string assertion, string repository)
    {
        using var response = await host.ControlAsync(HttpMethod.Put, "/control/grants/self", ControlToken, assertion,
            JsonSerializer.Serialize(new { repository }));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<IReadOnlyList<ToolInfo>> ListToolsAsync(Harness host)
    {
        var list = await host.RpcAsync("tools/list", "{}", DelegateToken);
        Assert.AreEqual(HttpStatusCode.OK, list.Status, list.Raw);
        var tools = new List<ToolInfo>();
        foreach (var tool in list.Result!.Value.GetProperty("tools").EnumerateArray())
        {
            var schema = tool.GetProperty("inputSchema");
            var properties = schema.TryGetProperty("properties", out var props) ? props.EnumerateObject().ToList() : [];
            var types = properties.ToDictionary(p => p.Name, p => p.Value.TryGetProperty("type", out var t) ? t.ToString() : "string");
            var required = schema.TryGetProperty("required", out var req)
                ? req.EnumerateArray().Select(r => r.GetString()!).ToHashSet()
                : [];
            tools.Add(new ToolInfo(tool.GetProperty("name").GetString()!, properties.Select(p => p.Name).ToHashSet(), types, required));
        }
        return tools;
    }

    // The arguments that make the tool read the Widgets secrets: a value per required parameter, a page size of one so a
    // second page (a cursor) exists, and the selection when the tool takes one.
    private static string ArgumentsFor(ToolInfo tool, string? repository, string? cursor)
    {
        var args = new Dictionary<string, object?>();
        foreach (var property in tool.Properties)
        {
            if (property is "repository" or "branch" or "cursor")
                continue;
            if (property == "limit")
                args[property] = 1;
            else if (tool.Required.Contains(property))
                args[property] = property switch
                {
                    "file_path" => "src/App/App.cs",
                    "direction" => "callers",
                    _ => tool.Types[property] switch
                    {
                        "integer" or "number" => 1,
                        "boolean" => false,
                        _ => SecretSymbol
                    }
                };
        }
        if (repository is not null && tool.Properties.Contains("repository"))
            args["repository"] = repository;
        if (cursor is not null)
            args["cursor"] = cursor;
        return JsonSerializer.Serialize(args);
    }

    private static void AssertNoWidgetsData(string text, string context)
    {
        // A symbol name the caller sent may be echoed back in its own not-found message; anything else is data.
        var data = text.Replace(SecretSymbol, "", StringComparison.Ordinal);
        foreach (var marker in Markers)
            Assert.IsFalse(data.Contains(marker, StringComparison.OrdinalIgnoreCase), $"{context} leaked '{marker}': {text}");
    }

    [TestMethod]
    public async Task EveryListedTool_AsAnotherTenant_IsDeniedWithNoData_IncludingReplayedCursors()
    {
        await using var host = await StartAsync();
        var userA = host.UserAssertion(tenant: "tenant-a", sub: "user-1");
        await GrantSelfAsync(host, userA, Widgets);
        await GrantSelfAsync(host, host.UserAssertion(tenant: "tenant-b", sub: "user-1"), Gadgets);
        var callersB = new (string Label, string Assertion)[]
        {
            ("tenant-b user with its own grant", host.UserAssertion(tenant: "tenant-b", sub: "user-1")),
            ("tenant-b user without a grant", host.UserAssertion(tenant: "tenant-b", sub: "user-2")),
            ("tenant-b application", host.Sign(CallerAssertionSigner.ApplicationClaims(DateTimeOffset.UtcNow, tenantId: "tenant-b"), "kid-b"))
        };

        var tools = await ListToolsAsync(host);
        CollectionAssert.AreEquivalent(
            ServiceApp.RemoteQueryTools.SelectMany(t => t.GetMethods())
                .Select(m => m.GetCustomAttributes(typeof(ModelContextProtocol.Server.McpServerToolAttribute), false))
                .SelectMany(a => a.Cast<ModelContextProtocol.Server.McpServerToolAttribute>()).Select(a => a.Name!).ToList(),
            tools.Select(t => t.Name).ToList(), "the live registry lists exactly the registered remote tools");
        var scoped = tools.Where(t => t.Properties.Contains("repository")).ToList();
        Assert.IsTrue(scoped.Count >= 7, "the repository-scoped tools are discovered from the registry");

        var cursors = new List<(ToolInfo Tool, string Cursor)>();
        foreach (var tool in tools)
        {
            var own = await host.CallAsync(tool.Name, ArgumentsFor(tool, "acme/widgets", null), DelegateToken, userA);
            // Tool-level errors (a class given to a method-only tool) still prove the read reached tenant A's snapshot.
            var ownText = own.Body.GetRawText();
            Assert.IsTrue(own.Body.TryGetProperty("meta", out var ownMeta) && ownMeta.TryGetProperty("snapshot", out var ownSnapshot)
                    ? ownSnapshot.GetProperty("repository").GetString() == "github.com/acme/widgets"
                    : tool.Name == "list_repositories" && ownText.Contains("acme/widgets", StringComparison.Ordinal),
                $"{tool.Name}: the granting tenant reads its repository (a new tool may need argument rules here): {ownText}");
            if (tool.Name is "find_symbol" or "get_file_symbols")
                Assert.IsFalse(own.IsError, ownText);
            if (own.Body.TryGetProperty("meta", out var meta) && meta.TryGetProperty("next_cursor", out var next))
                cursors.Add((tool, next.GetString()!));

            foreach (var (label, assertion) in callersB)
            {
                if (tool.Properties.Contains("repository"))
                {
                    var named = await host.CallAsync(tool.Name, ArgumentsFor(tool, "acme/widgets", null), DelegateToken, assertion);
                    Assert.IsTrue(named.IsError, $"{tool.Name} as {label}: {named.Body}");
                    Assert.AreEqual(NotFound, WithoutTimestamp(named.Body), $"{tool.Name} as {label}: the uniform not-found");
                }
                // Naming nothing (an implicit or refused selection) must still never reach Widgets.
                var bare = await host.CallAsync(tool.Name, ArgumentsFor(tool, null, null), DelegateToken, assertion);
                AssertNoWidgetsData(bare.Body.GetRawText(), $"{tool.Name} as {label} without a selection");
            }
        }

        Assert.IsTrue(cursors.Count > 0, "at least one paged tool minted a cursor for tenant A to replay");
        foreach (var (tool, cursor) in cursors)
            foreach (var (label, assertion) in callersB)
            {
                var named = await host.CallAsync(tool.Name, ArgumentsFor(tool, "acme/widgets", cursor), DelegateToken, assertion);
                Assert.IsTrue(named.IsError, $"{tool.Name} replay as {label}: {named.Body}");
                Assert.AreEqual(NotFound, WithoutTimestamp(named.Body), $"{tool.Name} replay as {label}: the uniform not-found");
                var bare = await host.CallAsync(tool.Name, ArgumentsFor(tool, null, cursor), DelegateToken, assertion);
                Assert.IsTrue(bare.IsError, $"{tool.Name} replay as {label} without a selection: {bare.Body}");
                AssertNoWidgetsData(bare.Body.GetRawText(), $"{tool.Name} replay as {label} without a selection");
            }
    }

    [TestMethod]
    public async Task EveryQueryRoute_AsAnotherTenant_Is404WithNoData_IncludingReplayedCursors()
    {
        await using var host = await StartAsync();
        var userA = host.UserAssertion(tenant: "tenant-a", sub: "user-1");
        await GrantSelfAsync(host, userA, Widgets);
        await GrantSelfAsync(host, host.UserAssertion(tenant: "tenant-b", sub: "user-1"), Gadgets);
        var callersB = new[]
        {
            host.UserAssertion(tenant: "tenant-b", sub: "user-1"),
            host.UserAssertion(tenant: "tenant-b", sub: "user-2"),
            host.Sign(CallerAssertionSigner.ApplicationClaims(DateTimeOffset.UtcNow, tenantId: "tenant-b"), "kid-b")
        };

        var routes = host.RouteEndpoints()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/query", StringComparison.Ordinal) == true)
            .ToList();
        Assert.IsTrue(routes.Count > 0, "the /query routes are discovered from the endpoint data source");

        foreach (var route in routes)
        {
            var pattern = route.RoutePattern.RawText!;
            var path = pattern.Replace("{identityHash}", host.WidgetsHash, StringComparison.Ordinal);
            Assert.IsFalse(path.Contains('{'), $"{pattern}: a new /query route parameter needs a value here");
            var methods = route.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods ?? ["GET"];
            Assert.IsTrue(methods.All(m => m == "GET"), $"{pattern}: only reads are expected on /query");

            string? cursor = null;
            using (var own = await Send(host, path + "?limit=1", userA))
            {
                var text = await own.Content.ReadAsStringAsync();
                Assert.AreEqual(HttpStatusCode.OK, own.StatusCode, $"{pattern}: the granting tenant reads it: {text}");
                if (JsonDocument.Parse(text).RootElement.TryGetProperty("next_cursor", out var next) && next.ValueKind == JsonValueKind.String)
                    cursor = next.GetString();
            }
            Assert.IsNotNull(cursor, $"{pattern}: a one-row page must have a next page to replay");

            foreach (var assertion in callersB)
                foreach (var query in new[] { "", "?limit=1", $"?cursor={cursor}&limit=1" })
                {
                    using var response = await Send(host, path + query, assertion);
                    var text = await response.Content.ReadAsStringAsync();
                    Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode, $"{pattern}{query}: {text}");
                    AssertNoWidgetsData(text, $"{pattern}{query}");
                }
        }
    }

    private static Task<HttpResponseMessage> Send(Harness host, string pathAndQuery, string assertion) =>
        host.ControlAsync(HttpMethod.Get, pathAndQuery, DelegateToken, assertion);
}
