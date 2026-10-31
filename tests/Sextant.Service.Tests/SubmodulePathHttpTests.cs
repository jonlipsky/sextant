using System.Text.Json;
using Sextant.Core;
using Sextant.Store;
using static Sextant.Service.Tests.CallerAssertionHttpTests;

namespace Sextant.Service.Tests;

/// <summary>
/// Issue #228 (option 2) at the remote MCP boundary: a repository-relative path under a populated submodule of the
/// selected snapshot has no rows there (the submodule is indexed as its own repository), so a file-scoped tool says so
/// and names the provider and the provider-relative path instead of answering with an empty success. A path outside
/// every submodule keeps its ordinary answer.
/// </summary>
[TestClass]
public class SubmodulePathHttpTests
{
    private const string Parent = "https://github.com/acme/parent";
    private const string Provider = "https://github.com/acme/mix";
    private const string User = "user-parent";

    private static Harness _host = null!;

    [ClassInitialize]
    public static async Task StartAsync(TestContext _)
    {
        _host = await Harness.StartAsync(
            configure: o => o with { RequireRepositorySelection = true },
            seed: Seed);
        using var response = await _host.ControlAsync(HttpMethod.Put, "/control/grants/self", ControlToken,
            _host.UserAssertion(sub: User), JsonSerializer.Serialize(new { repository = Parent }));
        Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [ClassCleanup]
    public static async Task StopAsync() => await _host.DisposeAsync();

    [TestMethod]
    public async Task GetFileSymbols_UnderASubmodule_NamesTheProviderAndItsRelativePath()
    {
        var call = await CallAsync("get_file_symbols", """{"file_path":"libs/mix/src/App/App.cs"}""");

        Assert.IsTrue(call.IsError, call.Body.ToString());
        AssertPointsAtProvider(call.Body);
    }

    [TestMethod]
    public async Task FileScope_UnderASubmodule_NamesTheProviderAndItsRelativePath()
    {
        var call = await CallAsync("find_symbol", """{"name":"Type0","scope":"file:libs/mix/src/App/App.cs"}""");

        Assert.IsTrue(call.IsError, call.Body.ToString());
        AssertPointsAtProvider(call.Body);
    }

    [TestMethod]
    public async Task APathOutsideEverySubmodule_KeepsItsOrdinaryAnswer()
    {
        var symbols = await CallAsync("get_file_symbols", """{"file_path":"src/App/App.cs"}""");
        Assert.IsFalse(symbols.IsError, symbols.Body.ToString());
        Assert.AreEqual(1, symbols.Body.GetProperty("results").GetArrayLength(), symbols.Body.ToString());

        var scoped = await CallAsync("find_symbol", """{"name":"Type0","scope":"file:src/App/App.cs"}""");
        Assert.IsFalse(scoped.IsError, scoped.Body.ToString());
        Assert.AreEqual(1, scoped.Body.GetProperty("results").GetArrayLength(), scoped.Body.ToString());

        var missing = await CallAsync("get_file_symbols", """{"file_path":"src/Nowhere/Missing.cs"}""");
        Assert.IsFalse(missing.IsError, missing.Body.ToString());
        Assert.AreEqual(0, missing.Body.GetProperty("results").GetArrayLength(), missing.Body.ToString());

        var sibling = await CallAsync("get_file_symbols", """{"file_path":"libs/mixer/src/App/App.cs"}""");
        Assert.IsFalse(sibling.IsError, sibling.Body.ToString());
    }

    [TestMethod]
    public async Task DotSegments_AreCollapsedBeforeMatching()
    {
        var outside = await CallAsync("get_file_symbols", """{"file_path":"libs/mix/../../src/App/App.cs"}""");
        Assert.IsFalse(outside.IsError, outside.Body.ToString());

        var inside = await CallAsync("get_file_symbols", """{"file_path":"src/../libs/mix/src/App/App.cs"}""");
        Assert.IsTrue(inside.IsError, inside.Body.ToString());
        AssertPointsAtProvider(inside.Body);

        var escaping = await CallAsync("get_file_symbols", """{"file_path":"../libs/mix/src/App/App.cs"}""");
        Assert.IsFalse(escaping.IsError, escaping.Body.ToString());
    }

    [TestMethod]
    public async Task TheSubmodulePathIsMatchedLikePathPresenter()
    {
        var call = await CallAsync("get_file_symbols", """{"file_path":"libs/MIX/src/App/App.cs"}""");

        Assert.AreEqual(OperatingSystem.IsWindows(), call.IsError, call.Body.ToString());
    }

    private static void AssertPointsAtProvider(JsonElement body)
    {
        Assert.AreEqual("path_in_submodule",
            body.GetProperty("meta").GetProperty("error").GetProperty("code").GetString(), body.ToString());
        var message = body.GetProperty("meta").GetProperty("error").GetProperty("message").GetString()!;
        StringAssert.Contains(message, Provider);
        StringAssert.Contains(message, "'src/App/App.cs'");
        StringAssert.Contains(message, "libs/mix");
        Assert.IsFalse(message.Contains("/srv/", StringComparison.Ordinal), "a disk path must never be printed: " + message);
    }

    private static Task<ToolCall> CallAsync(string tool, string arguments) =>
        _host.CallAsync(tool, arguments[..^1] + ",\"repository\":\"acme/parent\"}", DelegateToken, _host.UserAssertion(sub: User));

    // The parent's snapshot pins a submodule at libs/mix, indexed as a provider snapshot of its own.
    private static void Seed(IndexDatabase db)
    {
        var provider = ServiceTestFixtures.PublishComplete(
            db, ServiceTestFixtures.Request(repo: Provider, commit: "mixcommit1234567890"), isProvider: true);
        var parent = GrantServiceTests.PublishOnBranch(db, Parent, "commit-p1", "main", isDefault: true);

        var conn = db.GetConnection();
        var snapshots = new SnapshotStore(conn);
        var providerSnapshot = snapshots.GetById(provider)!;
        var parentProject = ProjectOf(conn, parent);
        var providerProject = ProjectOf(conn, provider);
        SetDiskPath(conn, parentProject, "/srv/checkout/src/App/App.csproj");
        SetDiskPath(conn, providerProject, "/srv/checkout/libs/mix/src/App/App.csproj");
        new SnapshotDependencyStore(conn).Insert(new SnapshotDependencyEdge
        {
            ConsumerSnapshotId = parent,
            ConsumerProjectId = parentProject,
            ProviderSnapshotId = provider,
            ProviderProjectId = providerProject,
            ProviderRepositoryId = providerSnapshot.RepositoryId,
            ProviderCommitSha = "mixcommit1234567890",
            ReferenceKind = "submodule_ref",
            SubmoduleDirty = false,
            CreatedAt = 1
        });
    }

    private static long ProjectOf(Microsoft.Data.Sqlite.SqliteConnection conn, long snapshotId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT project_id FROM snapshot_projects WHERE snapshot_id = @s LIMIT 1;";
        cmd.Parameters.AddWithValue("@s", snapshotId);
        return (long)cmd.ExecuteScalar()!;
    }

    private static void SetDiskPath(Microsoft.Data.Sqlite.SqliteConnection conn, long projectId, string diskPath)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE projects SET disk_path = @d WHERE id = @id;";
        cmd.Parameters.AddWithValue("@d", diskPath);
        cmd.Parameters.AddWithValue("@id", projectId);
        cmd.ExecuteNonQuery();
    }
}
