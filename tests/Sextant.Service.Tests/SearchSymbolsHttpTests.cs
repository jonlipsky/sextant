using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Sextant.Core;
using Sextant.Service.CallerIdentity;
using Sextant.Service.Grants;
using Sextant.Service.Host;
using Sextant.Service.Search;
using Sextant.Store;
using static Sextant.Service.Tests.CallerAssertionHttpTests;

namespace Sextant.Service.Tests;

/// <summary>
/// SVC-F at the HTTP boundary: <c>search_symbols</c> on the service <c>/mcp</c>, through the real host. The targets are
/// the verified caller's visible grants (SVC-4), re-resolved on every page; the cursor can only narrow them. The
/// harness is SVC-3's (<see cref="CallerAssertionHttpTests.Harness"/>): Widgets is published with a complete default
/// <c>main</c> holding one class <c>Type0</c>; the tenants are <c>tenant-a</c> (<c>kid-a</c>) and <c>tenant-b</c>
/// (<c>kid-b</c>).
/// </summary>
[TestClass]
public class SearchSymbolsHttpTests
{
    private const string Gadgets = "https://github.com/acme/gadgets";
    private const string Gizmos = "https://github.com/acme/gizmos";
    private const string SelfPath = "/control/grants/self";
    private const string TenantPath = "/control/grants/tenant";
    private const string Tool = SearchSymbolsTool.ToolName;
    private const string TypePrefix = """{"name_prefix":"Type"}""";

    private static readonly string GadgetsHash = HashOf(Gadgets, "commit-g1");
    private static readonly string GizmosHash = HashOf(Gizmos, "commit-z1");

    // ==== callers and visibility ====================================================================

    [TestMethod]
    public async Task WithoutAVerifiedCaller_IsCallerRequired_AndReadsNothing()
    {
        await using var host = await Harness.StartAsync();
        var reads = RecordReads(host);

        // The legacy query token carries no caller: the tool is new, and without a caller it answers nothing.
        var legacy = await host.CallAsync(Tool, TypePrefix, QueryToken);
        // A delegate token without an assertion is refused by the caller gate before the tool.
        var pooled = await host.CallAsync(Tool, TypePrefix, DelegateToken);

        Assert.IsTrue(legacy.IsError, legacy.Body.ToString());
        Assert.AreEqual(ListRepositoriesTool.CallerRequiredCode, ErrorCode(legacy.Body), legacy.Body.ToString());
        Assert.IsTrue(pooled.IsError, pooled.Body.ToString());
        Assert.AreEqual(CallerAssertionGate.RequiredCode, ErrorCode(pooled.Body), pooled.Body.ToString());
        Assert.AreEqual(0, reads.Count, "no snapshot was read");
        Assert.IsFalse(legacy.Body.ToString().Contains("Type0", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task User_SeesItsOwnAndTenantWideGrants_Only()
    {
        await using var host = await Harness.StartAsync(seed: db =>
        {
            Publish(db, Gadgets, "commit-g1", 1);
            Publish(db, Gizmos, "commit-z1", 1);
        });
        await GrantSelfAsync(host, host.UserAssertion(sub: "user-1"), Widgets);
        await GrantTenantAsync(host, Gadgets);
        await GrantSelfAsync(host, host.UserAssertion(sub: "user-2"), Gizmos);

        var page = await SearchAsync(host, TypePrefix, host.UserAssertion(sub: "user-1"));

        CollectionAssert.AreEquivalent(new[] { Widgets, Gadgets }, Repositories(page));
        Assert.IsFalse(page.ToString().Contains("gizmos", StringComparison.Ordinal), page.ToString());
    }

    [TestMethod]
    public async Task Application_SeesOnlyTenantWideGrants()
    {
        await using var host = await Harness.StartAsync(seed: db => Publish(db, Gadgets, "commit-g1", 1));
        await GrantSelfAsync(host, host.UserAssertion(sub: "user-1"), Widgets);
        await GrantTenantAsync(host, Gadgets);

        var page = await SearchAsync(host, TypePrefix, AppAssertion(host));

        CollectionAssert.AreEqual(new[] { Gadgets }, Repositories(page));
    }

    [TestMethod]
    public async Task CrossTenant_TheSameSubjectElsewhereSeesNothing()
    {
        await using var host = await Harness.StartAsync(seed: db => Publish(db, Gadgets, "commit-g1", 1));
        await GrantSelfAsync(host, host.UserAssertion(tenant: "tenant-a", sub: "user-1"), Widgets);
        await GrantTenantAsync(host, Gadgets, tenant: "tenant-a");
        var reads = RecordReads(host);

        var user = await host.CallAsync(Tool, TypePrefix, DelegateToken, host.UserAssertion(tenant: "tenant-b", sub: "user-1"));
        var application = await host.CallAsync(Tool, TypePrefix, DelegateToken, AppAssertion(host, "tenant-b"));

        foreach (var call in new[] { user, application })
        {
            Assert.IsTrue(call.IsError, call.Body.ToString());
            Assert.AreEqual(SearchSymbolsTool.NoVisibleRepositoriesCode, ErrorCode(call.Body), call.Body.ToString());
            Assert.IsFalse(call.Body.ToString().Contains("acme", StringComparison.Ordinal), call.Body.ToString());
        }
        Assert.AreEqual(0, reads.Count, "no snapshot was read for the other tenant");
    }

    [TestMethod]
    public async Task ExplicitRepository_Narrows_AndAnUngrantedOneIsTheUniformNotFound()
    {
        await using var host = await Harness.StartAsync(seed: db =>
        {
            Publish(db, Gadgets, "commit-g1", 1);
            Publish(db, Gizmos, "commit-z1", 1);
        });
        var assertion = host.UserAssertion();
        await GrantSelfAsync(host, assertion, Widgets);
        await GrantSelfAsync(host, assertion, Gadgets);

        foreach (var (selector, expected) in new[]
                 {
                     ("acme/widgets", Widgets), ("github.com/acme/gadgets", Gadgets), (Widgets + ".git", Widgets)
                 })
        {
            var narrowed = await SearchAsync(host, $$"""{"name_prefix":"Type","repository":"{{selector}}"}""", assertion);
            CollectionAssert.AreEqual(new[] { expected }, Repositories(narrowed), selector);
        }

        var ungranted = await host.CallAsync(Tool, """{"name_prefix":"Type","repository":"acme/gizmos"}""", DelegateToken, assertion);
        var absent = await host.CallAsync(Tool, """{"name_prefix":"Type","repository":"acme/absent"}""", DelegateToken, assertion);
        var noGrants = await host.CallAsync(Tool, TypePrefix, DelegateToken, host.UserAssertion(sub: "user-2"));
        foreach (var call in new[] { ungranted, absent, noGrants })
        {
            Assert.IsTrue(call.IsError, call.Body.ToString());
            Assert.AreEqual(SearchSymbolsTool.NoVisibleRepositoriesCode, ErrorCode(call.Body), call.Body.ToString());
        }
        Assert.AreEqual(WithoutTimestamp(absent.Body), WithoutTimestamp(ungranted.Body), "an ungranted repository is indistinguishable from an absent one");
        Assert.AreEqual(WithoutTimestamp(noGrants.Body), WithoutTimestamp(ungranted.Body));
    }

    [TestMethod]
    public async Task RepositoryHeader_NeitherNarrowsNorWidens()
    {
        await using var host = await Harness.StartAsync(seed: db => Publish(db, Gizmos, "commit-z1", 1));
        await GrantSelfAsync(host, host.UserAssertion(), Widgets);

        // search_symbols is exempt from repository selection: it takes its narrowing from its own arguments only.
        host.RepositoryHeader = Gizmos;
        var page = await SearchAsync(host, TypePrefix, host.UserAssertion());

        CollectionAssert.AreEqual(new[] { Widgets }, Repositories(page));
    }

    [TestMethod]
    public async Task ForgedCursor_NamingAnUngrantedSnapshot_NeverReadsIt_AndIsNoOracle()
    {
        await using var host = await Harness.StartAsync(seed: db => Publish(db, Gadgets, "commit-g1", 3));
        await GrantSelfAsync(host, host.UserAssertion(), Widgets);
        var reads = RecordReads(host);
        var binding = SymbolSearchCursor.Binding(User("tenant-a", "user-1"), "Type", null, null, null);
        // A hash that sorts below Gadgets' and names no snapshot at all.
        var absentHash = new string('0', 64);
        Assert.IsTrue(string.CompareOrdinal(absentHash, GadgetsHash) < 0);

        // Well-formed cursors (the digest is not a secret) that point at Gadgets' real snapshot, or at nothing.
        var namingGadgets = SymbolSearchCursor.Encode(new SymbolSearchCursorState([new(GadgetsHash, 0)], GadgetsHash), binding);
        var namingNothing = SymbolSearchCursor.Encode(new SymbolSearchCursorState([new(absentHash, 0)], GadgetsHash), binding);
        var lowWatermark = SymbolSearchCursor.Encode(new SymbolSearchCursorState([], absentHash), binding);

        var forged = await SearchAsync(host, WithCursor(TypePrefix, namingGadgets), host.UserAssertion());
        var nothing = await SearchAsync(host, WithCursor(TypePrefix, namingNothing), host.UserAssertion());
        var low = await SearchAsync(host, WithCursor(TypePrefix, lowWatermark), host.UserAssertion());

        foreach (var page in new[] { forged, nothing, low })
        {
            Assert.IsFalse(page.ToString().Contains("gadgets", StringComparison.Ordinal), page.ToString());
            Assert.IsFalse(page.ToString().Contains(GadgetsHash, StringComparison.Ordinal), page.ToString());
        }
        Assert.AreEqual(WithoutTimestamp(nothing), WithoutTimestamp(forged), "a forged hash is treated exactly like one that does not exist");
        CollectionAssert.AreEqual(new[] { Widgets }, Repositories(low), "the visible snapshot is still searched");
        CollectionAssert.DoesNotContain(reads.ToList(), GadgetsHash, "the ungranted snapshot was never read");
    }

    [TestMethod]
    public async Task RevokedGrant_DropsItsTargetBetweenPages()
    {
        await using var host = await Harness.StartAsync(seed: db => Publish(db, Gadgets, "commit-g1", 3));
        var assertion = host.UserAssertion();
        await GrantSelfAsync(host, assertion, Widgets);
        await GrantSelfAsync(host, assertion, Gadgets);
        const string arguments = """{"name_prefix":"Type","limit":1}""";

        var first = await SearchAsync(host, arguments, assertion);
        CollectionAssert.AreEquivalent(new[] { Widgets, Gadgets }, Repositories(first));
        var cursor = first.GetProperty("next_cursor").GetString();
        Assert.IsNotNull(cursor, "Gadgets has more symbols");

        await RevokeSelfAsync(host, assertion, Gadgets);
        var reads = RecordReads(host);
        var second = await SearchAsync(host, WithCursor(arguments, cursor), assertion);

        Assert.AreEqual(0, second.GetProperty("symbols").GetArrayLength(), second.ToString());
        Assert.IsFalse(second.ToString().Contains("gadgets", StringComparison.Ordinal), "the revoked repository is not even listed");
        Assert.AreEqual(JsonValueKind.Null, second.GetProperty("next_cursor").ValueKind, "nothing visible is left to page");
        CollectionAssert.DoesNotContain(reads.ToList(), GadgetsHash);

        // Revoking the last grant leaves nothing to search, so the same cursor is the uniform not-found.
        await RevokeSelfAsync(host, assertion, Widgets);
        var none = await host.CallAsync(Tool, WithCursor(arguments, cursor), DelegateToken, assertion);
        Assert.AreEqual(SearchSymbolsTool.NoVisibleRepositoriesCode, ErrorCode(none.Body), none.Body.ToString());
    }

    [TestMethod]
    public async Task VisibilityStoreFailure_FailsClosed()
    {
        await using var host = await Harness.StartAsync();
        await GrantSelfAsync(host, host.UserAssertion(), Widgets);
        Assert.AreEqual(1, (await SearchAsync(host, TypePrefix, host.UserAssertion())).GetProperty("symbols").GetArrayLength());

        // The grant catalog becomes unreadable: the visibility read throws, and the search must not proceed.
        host.ExecuteOnCatalog("ALTER TABLE repository_grants RENAME TO repository_grants_unreadable;");
        var reads = RecordReads(host);

        var (status, raw, _, payload) = await host.SendRpcAsync("tools/call",
            $$"""{"name":"{{Tool}}","arguments":{{TypePrefix}}}""", DelegateToken, host.UserAssertion());

        Assert.IsTrue(status != HttpStatusCode.OK || IsFailure(payload), raw);
        Assert.IsFalse(payload.Contains("Type0", StringComparison.Ordinal) || payload.Contains("acme", StringComparison.OrdinalIgnoreCase), raw);
        Assert.IsFalse(payload.Contains(SearchSymbolsTool.NoVisibleRepositoriesCode, StringComparison.Ordinal),
            "a failed visibility read is never presented as an empty grant set");
        Assert.AreEqual(0, reads.Count);

        static bool IsFailure(string payload)
        {
            using var rpc = JsonDocument.Parse(payload);
            return rpc.RootElement.TryGetProperty("error", out _)
                || (rpc.RootElement.TryGetProperty("result", out var result)
                    && result.TryGetProperty("isError", out var isError) && isError.GetBoolean());
        }
    }

    // ==== pagination ================================================================================

    [TestMethod]
    public async Task Pagination_IsCompleteAndStable_WithoutDuplicatesOrGaps()
    {
        await using var host = await Harness.StartAsync(seed: db =>
        {
            AddSymbols(db, HashOf(Widgets, "commit-w1"),
                ("TypeA", SymbolKind.Class), ("TypeB", SymbolKind.Interface), ("typeC", SymbolKind.Struct),
                ("TypeD", SymbolKind.Enum), ("TypeE", SymbolKind.Record), ("Other1", SymbolKind.Class), ("Other2", SymbolKind.Class));
            Publish(db, Gadgets, "commit-g1", 4);
            Publish(db, Gizmos, "commit-z1", 3);
        });
        var assertion = host.UserAssertion();
        await GrantSelfAsync(host, assertion, Widgets);
        await GrantSelfAsync(host, assertion, Gadgets);
        await GrantTenantAsync(host, Gizmos);
        const string arguments = """{"name_prefix":"type","limit":2}""";

        var pages = await WalkAsync(host, arguments, assertion);
        var again = await WalkAsync(host, arguments, assertion);

        var hits = pages.SelectMany(p => p.GetProperty("symbols").EnumerateArray())
            .Select(s => (Hash: s.GetProperty("identity_hash").GetString()!, Key: s.GetProperty("symbol_key").GetString()!,
                Name: s.GetProperty("name").GetString()!))
            .ToList();
        Assert.AreEqual(hits.Count, hits.Select(h => (h.Hash, h.Key)).Distinct().Count(), "no symbol is returned twice");
        Assert.AreEqual(6, hits.Count(h => h.Hash == host.WidgetsHash), "Widgets: Type0 and TypeA..TypeE, case-insensitively");
        Assert.AreEqual(4, hits.Count(h => h.Hash == GadgetsHash));
        Assert.AreEqual(3, hits.Count(h => h.Hash == GizmosHash));
        Assert.AreEqual(13, hits.Count, "every match and nothing else");
        Assert.IsFalse(hits.Any(h => h.Name.StartsWith("Other", StringComparison.Ordinal)));
        CollectionAssert.AreEqual(pages.Select(WithoutTimestamp).ToList(), again.Select(WithoutTimestamp).ToList(),
            "the same search pages identically");
        foreach (var page in pages)
        {
            var order = page.GetProperty("symbols").EnumerateArray()
                .Select(s => (s.GetProperty("name").GetString()!, s.GetProperty("identity_hash").GetString()!))
                .ToList();
            CollectionAssert.AreEqual(
                order.OrderBy(o => o.Item1, StringComparer.Ordinal).ThenBy(o => o.Item2, StringComparer.Ordinal).ToList(), order,
                "a page is merged by name, then identity hash");
            Assert.IsTrue(order.GroupBy(o => o.Item2).All(g => g.Count() <= 2), "at most `limit` symbols per snapshot per page");
        }
    }

    [TestMethod]
    public async Task WidthCap_DefersTheRestToTruncated_AndSearchesItOnLaterPages()
    {
        await using var host = await Harness.StartAsync(
            configure: o => o with { SearchMaxWidth = 2 },
            seed: db =>
            {
                Publish(db, Gadgets, "commit-g1", 3);
                Publish(db, Gizmos, "commit-z1", 2);
            });
        var assertion = host.UserAssertion();
        foreach (var repository in new[] { Widgets, Gadgets, Gizmos })
            await GrantSelfAsync(host, assertion, repository);
        var byHash = new Dictionary<string, string>
        {
            [host.WidgetsHash] = Widgets, [GadgetsHash] = Gadgets, [GizmosHash] = Gizmos
        };
        var ordered = byHash.Keys.Order(StringComparer.Ordinal).ToList();
        var reads = RecordReads(host);
        const string arguments = """{"name_prefix":"Type","limit":1}""";

        var first = await SearchAsync(host, arguments, assertion);

        CollectionAssert.AreEquivalent(ordered.Take(2).ToList(), reads.ToList(), "the two lowest identity hashes are searched first");
        CollectionAssert.AreEqual(new[] { byHash[ordered[2]] },
            first.GetProperty("truncated").EnumerateArray().Select(t => t.GetProperty("repository").GetString()).ToList());
        Assert.AreEqual("main", first.GetProperty("truncated")[0].GetProperty("branch").GetString());

        var cursor = first.GetProperty("next_cursor").GetString();
        Assert.IsNotNull(cursor, "the truncated snapshot is searched on a later page");
        var pages = await FollowAsync(host, arguments, assertion, cursor, reads, maxReadsPerPage: 2);
        var hashes = pages.Prepend(first).SelectMany(p => p.GetProperty("symbols").EnumerateArray())
            .Select(s => s.GetProperty("identity_hash").GetString()!)
            .ToList();
        Assert.AreEqual(1, hashes.Count(h => h == host.WidgetsHash));
        Assert.AreEqual(3, hashes.Count(h => h == GadgetsHash));
        Assert.AreEqual(2, hashes.Count(h => h == GizmosHash));
        Assert.AreEqual(0, pages[^1].GetProperty("truncated").GetArrayLength());
    }

    [TestMethod]
    [DataRow(0, 1)]
    [DataRow(-3, 1)]
    [DataRow(7, 7)]
    [DataRow(1000, 200)]
    public async Task Limit_IsClampedToOneThroughTwoHundred(int limit, int expected)
    {
        await using var host = await Harness.StartAsync(seed: db => Publish(db, Gadgets, "commit-g1", 250));
        await GrantSelfAsync(host, host.UserAssertion(), Gadgets);

        var page = await SearchAsync(host, $$"""{"name_prefix":"Type","limit":{{limit}}}""", host.UserAssertion());

        Assert.AreEqual(expected, page.GetProperty("symbols").GetArrayLength());
        Assert.AreEqual(expected, page.GetProperty("meta").GetProperty("result_count").GetInt32());
        Assert.AreEqual(JsonValueKind.String, page.GetProperty("next_cursor").ValueKind);
    }

    [TestMethod]
    public async Task GrantWithoutACompleteSnapshot_IsPending()
    {
        await using var host = await Harness.StartAsync();
        var assertion = host.UserAssertion();
        await GrantSelfAsync(host, assertion, Widgets);
        await GrantSelfAsync(host, assertion, Widgets, "release");
        await GrantSelfAsync(host, assertion, Gizmos);

        var page = await SearchAsync(host, TypePrefix, assertion);

        CollectionAssert.AreEqual(new[] { Widgets }, Repositories(page));
        CollectionAssert.AreEqual(new[] { (Gizmos, ""), (Widgets, "release") }, Targets(page, "pending"));
        Assert.AreEqual(0, page.GetProperty("unavailable").GetArrayLength());
        Assert.AreEqual(JsonValueKind.Null, page.GetProperty("next_cursor").ValueKind, "a pending target is not paged");
    }

    [TestMethod]
    public async Task UnreadableSnapshot_IsUnavailable_AndKeepsItsPosition()
    {
        await using var host = await Harness.StartAsync(seed: db => Publish(db, Gadgets, "commit-g1", 3));
        var assertion = host.UserAssertion();
        await GrantSelfAsync(host, assertion, Widgets);
        await GrantSelfAsync(host, assertion, Gadgets);
        host.Service.SearchSnapshotReadHook = hash =>
        {
            if (hash == GadgetsHash)
                throw new SqliteException("injected read failure", 1);
        };

        var first = await SearchAsync(host, TypePrefix, assertion);

        CollectionAssert.AreEqual(new[] { Widgets }, Repositories(first));
        CollectionAssert.AreEqual(new[] { (Gadgets, "main") }, Targets(first, "unavailable"));
        var cursor = first.GetProperty("next_cursor").GetString();
        Assert.IsNotNull(cursor, "the unavailable snapshot is retried on the next page");

        host.Service.SearchSnapshotReadHook = null;
        var second = await SearchAsync(host, WithCursor(TypePrefix, cursor), assertion);

        CollectionAssert.AreEqual(new[] { "Type0", "Type1", "Type2" }, Names(second), "Gadgets resumes from its start");
        CollectionAssert.AreEqual(new[] { Gadgets }, Repositories(second));
        Assert.AreEqual(0, second.GetProperty("unavailable").GetArrayLength());
        Assert.AreEqual(JsonValueKind.Null, second.GetProperty("next_cursor").ValueKind);
    }

    [TestMethod]
    public async Task SharedSnapshot_IsSearchedOnce()
    {
        await using var host = await Harness.StartAsync(seed: db =>
        {
            // `develop` points at the same snapshot as `main`.
            var snapshots = new SnapshotStore(db.GetConnection());
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var repositoryId = snapshots.GetRepositoryId(Widgets)!.Value;
            var snapshotId = snapshots.GetByIdentityHash(HashOf(Widgets, "commit-w1"))!.Id;
            snapshots.SetBranchPointer(snapshots.EnsureBranch(repositoryId, "develop", false, now), snapshotId, now);
        });
        var assertion = host.UserAssertion();
        await GrantSelfAsync(host, assertion, Widgets);
        await GrantSelfAsync(host, assertion, Widgets, "develop");

        var page = await SearchAsync(host, TypePrefix, assertion);

        var symbol = page.GetProperty("symbols").EnumerateArray().Single();
        Assert.AreEqual(host.WidgetsHash, symbol.GetProperty("identity_hash").GetString());
        Assert.AreEqual("develop", symbol.GetProperty("branch").GetString(), "the first target in (repository, branch) order labels it");
    }

    // ==== filters and output ========================================================================

    [TestMethod]
    public async Task BranchFilter_NarrowsToThatBranch()
    {
        await using var host = await Harness.StartAsync(seed: db => GrantServiceTests.PublishOnBranch(db, Widgets, "commit-w2", "release", isDefault: false));
        var assertion = host.UserAssertion();
        await GrantSelfAsync(host, assertion, Widgets);
        await GrantSelfAsync(host, assertion, Widgets, "release");
        var releaseHash = HashOf(Widgets, "commit-w2");

        var all = await SearchAsync(host, TypePrefix, assertion);
        var release = await SearchAsync(host, """{"name_prefix":"Type","branch":"release"}""", assertion);
        var main = await SearchAsync(host, """{"name_prefix":"Type","branch":"main"}""", assertion);
        var unknown = await host.CallAsync(Tool, """{"name_prefix":"Type","branch":"nope"}""", DelegateToken, assertion);

        CollectionAssert.AreEquivalent(new[] { "main", "release" }, Branches(all), "no branch argument searches every granted branch");
        CollectionAssert.AreEqual(new[] { "release" }, Branches(release));
        Assert.AreEqual(releaseHash, release.GetProperty("symbols")[0].GetProperty("identity_hash").GetString());
        CollectionAssert.AreEqual(new[] { "main" }, Branches(main), "a default-branch grant matches its branch's name");
        Assert.AreEqual(SearchSymbolsTool.NoVisibleRepositoriesCode, ErrorCode(unknown.Body), unknown.Body.ToString());
    }

    [TestMethod]
    public async Task BranchFilter_ReachesAnIndexedBranchOfAVisibleRepository()
    {
        // Visibility is repository-level (SVC-4): a default-branch grant makes every indexed branch readable.
        await using var host = await Harness.StartAsync(seed: db => GrantServiceTests.PublishOnBranch(db, Widgets, "commit-w2", "release", isDefault: false));
        await GrantSelfAsync(host, host.UserAssertion(), Widgets);

        var all = await SearchAsync(host, TypePrefix, host.UserAssertion());
        var release = await SearchAsync(host, """{"name_prefix":"Type","branch":"release"}""", host.UserAssertion());

        CollectionAssert.AreEqual(new[] { "main" }, Branches(all), "without a branch, only granted branches are searched");
        CollectionAssert.AreEqual(new[] { "release" }, Branches(release));
    }

    [TestMethod]
    public async Task KindFilter_AndKindAndAccessibilityAreNames()
    {
        await using var host = await Harness.StartAsync(seed: db => AddSymbols(db, HashOf(Widgets, "commit-w1"),
            ("TypeRun", SymbolKind.Method, Accessibility.Public), ("TypeCount", SymbolKind.Property, Accessibility.Internal),
            ("TypeParameterT", SymbolKind.TypeParameter, Accessibility.Public)));
        var assertion = host.UserAssertion();
        await GrantSelfAsync(host, assertion, Widgets);

        var all = await SearchAsync(host, TypePrefix, assertion);
        var methods = await SearchAsync(host, """{"name_prefix":"Type","kind":"method"}""", assertion);
        var properties = await SearchAsync(host, """{"name_prefix":"Type","kind":"Property"}""", assertion);
        var bogus = await host.CallAsync(Tool, """{"name_prefix":"Type","kind":"bogus"}""", DelegateToken, assertion);

        var kinds = all.GetProperty("symbols").EnumerateArray()
            .ToDictionary(s => s.GetProperty("name").GetString()!, s => s.GetProperty("kind"));
        Assert.AreEqual("class", kinds["Type0"].GetString());
        Assert.AreEqual("typeparameter", kinds["TypeParameterT"].GetString(), "the same lowercase name the local tools emit");
        Assert.IsTrue(kinds.Values.All(k => k.ValueKind == JsonValueKind.String), "kind is a string, never an ordinal");
        CollectionAssert.AreEqual(new[] { "TypeRun" }, Names(methods));
        Assert.AreEqual("method", methods.GetProperty("symbols")[0].GetProperty("kind").GetString());
        Assert.AreEqual("public", methods.GetProperty("symbols")[0].GetProperty("accessibility").GetString());
        CollectionAssert.AreEqual(new[] { "TypeCount" }, Names(properties));
        Assert.AreEqual("internal", properties.GetProperty("symbols")[0].GetProperty("accessibility").GetString());
        Assert.AreEqual(SearchSymbolsTool.InvalidArgumentsCode, ErrorCode(bogus.Body), bogus.Body.ToString());
    }

    [TestMethod]
    public async Task NamePrefix_IsALiteralPrefix()
    {
        await using var host = await Harness.StartAsync(seed: db => AddSymbols(db, HashOf(Widgets, "commit-w1"),
            ("Type_1", SymbolKind.Class), ("Type%2", SymbolKind.Class), ("Type\\3", SymbolKind.Class), ("ATypeX", SymbolKind.Class)));
        var assertion = host.UserAssertion();
        await GrantSelfAsync(host, assertion, Widgets);

        CollectionAssert.AreEqual(new[] { "Type_1" }, Names(await SearchAsync(host, """{"name_prefix":"Type_"}""", assertion)));
        CollectionAssert.AreEqual(new[] { "Type%2" }, Names(await SearchAsync(host, """{"name_prefix":"Type%"}""", assertion)));
        CollectionAssert.AreEqual(new[] { "Type\\3" }, Names(await SearchAsync(host, """{"name_prefix":"Type\\"}""", assertion)));
        CollectionAssert.AreEqual(new[] { "Type%2", "Type0", "Type\\3", "Type_1" }, Names(await SearchAsync(host, """{"name_prefix":" tYPE "}""", assertion)),
            "the prefix is trimmed and ASCII case-insensitive, and never matches mid-name");
    }

    [TestMethod]
    public async Task Result_IsATextBlockAndTheSameStructuredContent()
    {
        await using var host = await Harness.StartAsync();
        await GrantSelfAsync(host, host.UserAssertion(), Widgets);

        var response = await host.RpcAsync("tools/call", $$"""{"name":"{{Tool}}","arguments":{{TypePrefix}}}""", DelegateToken, host.UserAssertion());

        var result = response.Result!.Value;
        var text = JsonNode.Parse(result.GetProperty("content")[0].GetProperty("text").GetString()!)!;
        Assert.IsTrue(JsonNode.DeepEquals(text, JsonNode.Parse(result.GetProperty("structuredContent").GetRawText())), result.ToString());
        CollectionAssert.AreEquivalent(new[] { "symbols", "next_cursor", "pending", "unavailable", "truncated", "meta" },
            text.AsObject().Select(p => p.Key).ToList());
        var symbol = text["symbols"]!.AsArray().Single()!.AsObject();
        CollectionAssert.AreEquivalent(
            new[] { "repository", "branch", "identity_hash", "symbol_key", "name", "fully_qualified_name", "kind", "accessibility", "project" },
            symbol.Select(p => p.Key).ToList());
        Assert.AreEqual(Widgets, (string?)symbol["repository"]);
        Assert.AreEqual("main", (string?)symbol["branch"]);
        Assert.AreEqual(host.WidgetsHash, (string?)symbol["identity_hash"]);
        Assert.AreEqual("global::App.Type0", (string?)symbol["fully_qualified_name"]);
        Assert.AreEqual(1, (int)text["meta"]!["result_count"]!);
        Assert.IsTrue((long)text["meta"]!["index_freshness"]! > 0, "the newest published_at among the searched snapshots");
        Assert.IsNull(text["next_cursor"]);
    }

    // ==== invalid input =============================================================================

    [TestMethod]
    public async Task InvalidCursor_TamperedOversizedOrForeign()
    {
        await using var host = await Harness.StartAsync(seed: db => Publish(db, Gadgets, "commit-g1", 3));
        foreach (var sub in new[] { "user-1", "user-2" })
        {
            await GrantSelfAsync(host, host.UserAssertion(sub: sub), Widgets);
            await GrantSelfAsync(host, host.UserAssertion(sub: sub), Gadgets);
        }
        await GrantSelfAsync(host, host.UserAssertion(tenant: "tenant-b", sub: "user-1"), Widgets);
        await GrantTenantAsync(host, Widgets);
        const string arguments = """{"name_prefix":"Type","limit":1}""";
        var cursor = (await SearchAsync(host, arguments, host.UserAssertion())).GetProperty("next_cursor").GetString()!;
        var middle = cursor.Length / 2;
        var tampered = cursor[..middle] + (cursor[middle] == 'A' ? 'B' : 'A') + cursor[(middle + 1)..];
        var reads = RecordReads(host);

        foreach (var (label, args, assertion) in new[]
                 {
                     ("tampered", WithCursor(arguments, tampered), host.UserAssertion()),
                     ("oversized", WithCursor(arguments, new string('A', SymbolSearchCursor.MaxLength + 1)), host.UserAssertion()),
                     ("not base64url", WithCursor(arguments, "!!not-a-cursor!!"), host.UserAssertion()),
                     ("another user", WithCursor(arguments, cursor), host.UserAssertion(sub: "user-2")),
                     ("another tenant", WithCursor(arguments, cursor), host.UserAssertion(tenant: "tenant-b", sub: "user-1")),
                     ("an application", WithCursor(arguments, cursor), AppAssertion(host)),
                     ("another prefix", WithCursor("""{"name_prefix":"Typ","limit":1}""", cursor), host.UserAssertion()),
                     ("another kind", WithCursor("""{"name_prefix":"Type","kind":"class","limit":1}""", cursor), host.UserAssertion()),
                     ("another repository", WithCursor("""{"name_prefix":"Type","repository":"acme/widgets","limit":1}""", cursor), host.UserAssertion()),
                     ("another branch", WithCursor("""{"name_prefix":"Type","branch":"main","limit":1}""", cursor), host.UserAssertion())
                 })
        {
            var call = await host.CallAsync(Tool, args, DelegateToken, assertion);
            Assert.IsTrue(call.IsError, $"{label}: {call.Body}");
            Assert.AreEqual(SearchSymbolsTool.InvalidCursorCode, ErrorCode(call.Body), $"{label}: {call.Body}");
        }
        Assert.AreEqual(0, reads.Count, "a refused cursor reads nothing");

        // The genuine cursor still works for its own caller, and the limit may change between pages.
        var resumed = await SearchAsync(host, WithCursor("""{"name_prefix":"Type","limit":5}""", cursor), host.UserAssertion());
        Assert.AreEqual(2, resumed.GetProperty("symbols").GetArrayLength(), resumed.ToString());
    }

    [TestMethod]
    [DataRow("""{}""")]
    [DataRow("""{"name_prefix":"   "}""")]
    [DataRow("""{"name_prefix":7}""")]
    [DataRow("""{"name_prefix":"Type","cursor":5}""")]
    [DataRow("""{"name_prefix":"Type","cursor":{"v":1}}""")]
    [DataRow("""{"name_prefix":"Type","repository":["acme/widgets"]}""")]
    [DataRow("""{"name_prefix":"Type","branch":true}""")]
    [DataRow("""{"name_prefix":"Type","limit":"5"}""")]
    [DataRow("""{"name_prefix":"Type","limit":1.5}""")]
    [DataRow("""{"name_prefix":"Type","repo":"acme/widgets"}""")]
    [DataRow("""{"name_prefix":"Type","caller":{"tenant_id":"tenant-b"}}""")]
    [DataRow("""{"name_prefix":"Type","kind":"bogus"}""")]
    public async Task MalformedArguments_AreInvalidArguments(string arguments)
    {
        await using var host = await Harness.StartAsync();
        await GrantSelfAsync(host, host.UserAssertion(), Widgets);

        var call = await host.CallAsync(Tool, arguments, DelegateToken, host.UserAssertion());

        Assert.IsTrue(call.IsError, call.Body.ToString());
        Assert.AreEqual(SearchSymbolsTool.InvalidArgumentsCode, ErrorCode(call.Body), call.Body.ToString());
    }

    [TestMethod]
    public async Task OverlongNamePrefix_IsInvalidArguments()
    {
        await using var host = await Harness.StartAsync();
        await GrantSelfAsync(host, host.UserAssertion(), Widgets);
        var prefix = new string('T', SearchSymbolsTool.MaxNamePrefixLength + 1);

        var call = await host.CallAsync(Tool, $$"""{"name_prefix":"{{prefix}}"}""", DelegateToken, host.UserAssertion());

        Assert.AreEqual(SearchSymbolsTool.InvalidArgumentsCode, ErrorCode(call.Body), call.Body.ToString());
    }

    [TestMethod]
    [DataRow("http://github.com/acme/widgets")]
    [DataRow("https://127.0.0.1/acme/widgets")]
    [DataRow("not a repository")]
    [DataRow("https://github.com/acme/widgets/tree/main")]
    public async Task RefusedRepositorySelector_IsInvalidSelector(string selector)
    {
        await using var host = await Harness.StartAsync();
        await GrantSelfAsync(host, host.UserAssertion(), Widgets);

        var call = await host.CallAsync(Tool, $$"""{"name_prefix":"Type","repository":"{{selector}}"}""", DelegateToken, host.UserAssertion());

        Assert.IsTrue(call.IsError, call.Body.ToString());
        Assert.AreEqual(SearchSymbolsTool.InvalidSelectorCode, ErrorCode(call.Body), call.Body.ToString());
    }

    [TestMethod]
    public async Task ToolsList_AdvertisesItsOwnStrictSchema()
    {
        await using var host = await Harness.StartAsync();

        var list = await host.RpcAsync("tools/list", "{}", DelegateToken);

        var tool = list.Result!.Value.GetProperty("tools").EnumerateArray().Single(t => t.GetProperty("name").GetString() == Tool);
        var schema = tool.GetProperty("inputSchema");
        Assert.IsFalse(schema.GetProperty("additionalProperties").GetBoolean());
        CollectionAssert.AreEqual(new[] { "name_prefix" }, schema.GetProperty("required").EnumerateArray().Select(r => r.GetString()).ToList());
        CollectionAssert.AreEquivalent(new[] { "name_prefix", "repository", "branch", "kind", "cursor", "limit" },
            schema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToList());
        Assert.AreEqual("integer", schema.GetProperty("properties").GetProperty("limit").GetProperty("type").GetString());
        Assert.AreEqual(SymbolSearchCursor.MaxLength, schema.GetProperty("properties").GetProperty("cursor").GetProperty("maxLength").GetInt32());
        CollectionAssert.Contains(schema.GetProperty("properties").GetProperty("kind").GetProperty("enum").EnumerateArray()
            .Select(k => k.GetString()).ToList(), "typeparameter");
        CollectionAssert.Contains(ToolSelectionFilters.SelectionExemptTools.ToList(), Tool);
    }

    // ==== helpers ===================================================================================

    private static string HashOf(string repository, string commit) =>
        ServiceTestFixtures.Request(repo: repository, commit: commit).ToIdentity().Hash;

    // Publishes `repository` at `commit` as the complete head of its default branch `main`, with Type0..Type{count-1}.
    private static void Publish(IndexDatabase db, string repository, string commit, int count)
    {
        var snapshotId = ServiceTestFixtures.PublishComplete(db, ServiceTestFixtures.Request(repo: repository, commit: commit), symbolCount: count);
        var snapshots = new SnapshotStore(db.GetConnection());
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        snapshots.SetBranchPointer(snapshots.EnsureBranch(snapshots.GetRepositoryId(repository)!.Value, "main", true, now), snapshotId, now);
    }

    private static void AddSymbols(IndexDatabase db, string identityHash, params (string Name, SymbolKind Kind)[] symbols) =>
        AddSymbols(db, identityHash, symbols.Select(s => (s.Name, s.Kind, Accessibility.Public)).ToArray());

    // Adds symbols to the (single) project of a published snapshot, next to its existing ones.
    private static void AddSymbols(IndexDatabase db, string identityHash, params (string Name, SymbolKind Kind, Accessibility Access)[] symbols)
    {
        var conn = db.GetConnection();
        var snapshotId = new SnapshotStore(conn).GetByIdentityHash(identityHash)!.Id;
        long projectId;
        long fileVersionId;
        using (var find = conn.CreateCommand())
        {
            find.CommandText = """
                SELECT s.project_id, s.file_version_id
                FROM snapshot_projects sp JOIN symbols s ON s.project_id = sp.project_id
                WHERE sp.snapshot_id = @snapshot LIMIT 1;
                """;
            find.Parameters.AddWithValue("@snapshot", snapshotId);
            using var reader = find.ExecuteReader();
            Assert.IsTrue(reader.Read());
            projectId = reader.GetInt64(0);
            fileVersionId = reader.GetInt64(1);
        }

        foreach (var (name, kind, access) in symbols)
        {
            using var insert = conn.CreateCommand();
            insert.CommandText = """
                INSERT INTO symbols
                    (project_id, symbol_key, fully_qualified_name, display_name, kind, accessibility,
                     file_version_id, line_start, line_end, last_indexed_at)
                VALUES (@p, @key, @fqn, @name, @kind, @access, @fv, 1, 10, 0);
                """;
            insert.Parameters.AddWithValue("@p", projectId);
            insert.Parameters.AddWithValue("@key", $"global::App.{name}:{snapshotId}");
            insert.Parameters.AddWithValue("@fqn", $"global::App.{name}");
            insert.Parameters.AddWithValue("@name", name);
            insert.Parameters.AddWithValue("@kind", (int)kind);
            insert.Parameters.AddWithValue("@access", (int)access);
            insert.Parameters.AddWithValue("@fv", fileVersionId);
            insert.ExecuteNonQuery();
        }
    }

    private static CallerPrincipal User(string tenant, string sub) => new()
    {
        TenantId = tenant,
        TenantSlug = tenant,
        Actor = CallerActor.User,
        UserId = sub,
        App = "app",
        Connection = "conn",
        Via = "mcp-surface",
        KeyId = "kid-a",
        Jti = "jti"
    };

    private static System.Collections.Concurrent.ConcurrentQueue<string> RecordReads(Harness host)
    {
        var reads = new System.Collections.Concurrent.ConcurrentQueue<string>();
        host.Service.SearchSnapshotReadHook = reads.Enqueue;
        return reads;
    }

    private static async Task<JsonElement> SearchAsync(Harness host, string arguments, string assertion)
    {
        var call = await host.CallAsync(Tool, arguments, DelegateToken, assertion);
        Assert.IsFalse(call.IsError, call.Body.ToString());
        return call.Body;
    }

    // Every page of a search from its start, following next_cursor to the end.
    private static Task<List<JsonElement>> WalkAsync(Harness host, string arguments, string assertion) =>
        FollowAsync(host, arguments, assertion, cursor: null, reads: null, maxReadsPerPage: int.MaxValue);

    // Every page after `cursor` (from the start when it is null), following next_cursor to the end. When `reads` is
    // given, each page must read at most `maxReadsPerPage` snapshots.
    private static async Task<List<JsonElement>> FollowAsync(Harness host, string arguments, string assertion, string? cursor,
        System.Collections.Concurrent.ConcurrentQueue<string>? reads, int maxReadsPerPage)
    {
        var pages = new List<JsonElement>();
        for (var i = 0; i < 100; i++)
        {
            reads?.Clear();
            var page = await SearchAsync(host, cursor is null ? arguments : WithCursor(arguments, cursor), assertion);
            if (reads is not null)
                Assert.IsTrue(reads.Count <= maxReadsPerPage, $"page {i} read {reads.Count} snapshots");
            pages.Add(page);
            cursor = page.GetProperty("next_cursor").GetString();
            if (cursor is null)
                return pages;
        }
        Assert.Fail("the search did not terminate");
        return pages;
    }

    private static string WithCursor(string arguments, string? cursor)
    {
        var node = JsonNode.Parse(arguments)!.AsObject();
        node["cursor"] = cursor;
        return node.ToJsonString();
    }

    private static List<string?> Repositories(JsonElement page) =>
        page.GetProperty("symbols").EnumerateArray().Select(s => s.GetProperty("repository").GetString()).Distinct().ToList();

    private static List<string?> Branches(JsonElement page) =>
        page.GetProperty("symbols").EnumerateArray().Select(s => s.GetProperty("branch").GetString()).ToList();

    private static List<string?> Names(JsonElement page) =>
        page.GetProperty("symbols").EnumerateArray().Select(s => s.GetProperty("name").GetString()).ToList();

    private static List<(string?, string?)> Targets(JsonElement page, string list) =>
        page.GetProperty(list).EnumerateArray()
            .Select(t => (t.GetProperty("repository").GetString(), t.GetProperty("branch").GetString()))
            .ToList();

    private static string AppAssertion(Harness host, string tenant = "tenant-a") =>
        host.Sign(CallerAssertionSigner.ApplicationClaims(DateTimeOffset.UtcNow, tenantId: tenant), tenant == "tenant-b" ? "kid-b" : "kid-a");

    private static string Body(string repository, string? branch) =>
        branch is null ? JsonSerializer.Serialize(new { repository }) : JsonSerializer.Serialize(new { repository, branch });

    private static async Task GrantSelfAsync(Harness host, string assertion, string repository, string? branch = null)
    {
        using var response = await host.ControlAsync(HttpMethod.Put, SelfPath, ControlToken, assertion, Body(repository, branch));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task GrantTenantAsync(Harness host, string repository, string tenant = "tenant-a")
    {
        using var response = await host.ControlAsync(HttpMethod.Put, TenantPath, ControlToken, AppAssertion(host, tenant), Body(repository, null));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task RevokeSelfAsync(Harness host, string assertion, string repository)
    {
        using var response = await host.ControlAsync(HttpMethod.Delete, $"{SelfPath}?repository={Uri.EscapeDataString(repository)}", ControlToken, assertion);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
    }
}
