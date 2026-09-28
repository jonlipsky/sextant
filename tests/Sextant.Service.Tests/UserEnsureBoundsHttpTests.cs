using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Sextant.Service.Grants;
using Sextant.Service.Host;
using Sextant.Store;
using static Sextant.Service.Tests.CallerAssertionHttpTests;

namespace Sextant.Service.Tests;

/// <summary>
/// SX-6d (issue #198) at the HTTP boundary. A user caller (a verified <c>act=user</c> assertion) that can see a
/// repository may ensure it, but may not change shared branch state beyond a guarded advance: <c>default_branch:
/// true</c>, any <c>branch_head_sequence</c>, and an ensure with neither the <c>expected_head_commit</c> CAS nor
/// <c>branch_update: none</c> (or a CAS that names no branch) are a 400 before any job exists, audited as denied.
/// A user that cannot see the repository gets the SVC-4 <c>not_granted</c> whatever its body asks for. Application
/// callers and assertion-less control calls keep full branch control. Also: the host refuses to start without a
/// control token unless the insecure opt-out is set, which it logs.
/// </summary>
[TestClass]
public class UserEnsureBoundsHttpTests
{
    private const string Suffix = ";idp=processstack;kid=kid-a;via=mcp-surface;cid=conn-1;dep=dep-1;jti=jti-1";
    private const string Ensure = "/control/ensure";
    private const string Absent = "https://github.com/acme/absent";

    // ==== user bounds ==============================================================================

    [TestMethod]
    [DataRow("""{"branch_name":"feature","default_branch":true,"expected_head_commit":""}""", BranchGuardReason.DefaultBranchNotAllowed)]
    [DataRow("""{"branch_name":"feature","default_branch":true,"branch_update":"none"}""", BranchGuardReason.DefaultBranchNotAllowed)]
    [DataRow("""{"default_branch":true}""", BranchGuardReason.DefaultBranchNotAllowed)]
    [DataRow("""{"branch_name":"feature","branch_head_sequence":9223372036854775807}""", BranchGuardReason.HeadSequenceNotAllowed)]
    [DataRow("""{"branch_name":"feature","branch_head_sequence":1,"branch_update":"none"}""", BranchGuardReason.HeadSequenceNotAllowed)]
    [DataRow("""{"branch_name":"feature"}""", BranchGuardReason.BranchGuardRequired)]
    [DataRow("""{"branch_name":"feature","branch_update":"advance","default_branch":false}""", BranchGuardReason.BranchGuardRequired)]
    [DataRow("""{}""", BranchGuardReason.BranchGuardRequired)]
    [DataRow("""{"expected_head_commit":""}""", BranchGuardReason.BranchRequired)]
    [DataRow("""{"branch_name":" ","expected_head_commit":"commit-w1"}""", BranchGuardReason.BranchRequired)]
    public async Task UserEnsure_BeyondTheBounds_Is400_BeforeAnyJob_AndAudited(string fields, string reason)
    {
        await using var host = await Harness.StartAsync(seed: Seed);
        await GrantAsync(host, Widgets);
        var branchesBefore = host.BranchRows();

        using var response = await host.ControlAsync(HttpMethod.Post, Ensure, ControlToken, host.UserAssertion(),
            EnsureOf(Widgets, "commit-w2", fields));

        await AssertRejectedAsync(response, HttpStatusCode.BadRequest, reason);
        Assert.AreEqual(0L, host.JobRows(), "the refusal precedes any job row");
        Assert.AreEqual(0, host.Worker.Calls);
        Assert.AreEqual(branchesBefore, host.BranchRows(), "no branch row is created");
        var main = host.Service.ResolveBranchHead(Widgets, null)!;
        Assert.AreEqual("main", main.Branch, "the default branch is untouched");
        var audit = host.Service.RecentAudit(action: AuditAction.Ensure).Single();
        Assert.AreEqual(AuditOutcome.Denied, audit.Outcome);
        Assert.AreEqual(reason + Suffix, audit.Detail);
        Assert.AreEqual(RepositoryGrantKey.Of(Widgets), audit.RepositoryScope);
        Assert.AreEqual(AuditLogStore.HashActor("tenant-a/user-1"), audit.Actor);
    }

    [TestMethod]
    public async Task UserEnsure_TheRefusalReadsOnlyTheBody_NotTheCatalog()
    {
        // Widgets is indexed, the absent repository is not: a visible user gets the same answer for both.
        await using var host = await Harness.StartAsync(seed: Seed);
        await GrantAsync(host, Widgets);
        await GrantAsync(host, Absent);

        foreach (var fields in ForbiddenBodies)
        {
            using var known = await host.ControlAsync(HttpMethod.Post, Ensure, ControlToken, host.UserAssertion(),
                EnsureOf(Widgets, "commit-w2", fields));
            using var unknown = await host.ControlAsync(HttpMethod.Post, Ensure, ControlToken, host.UserAssertion(),
                EnsureOf(Absent, "commit-a1", fields));
            Assert.AreEqual(HttpStatusCode.BadRequest, known.StatusCode, fields);
            Assert.AreEqual(await known.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync(), fields);
        }
        Assert.AreEqual(0L, host.JobRows());
    }

    [TestMethod]
    public async Task UserEnsure_WithoutVisibility_IsNotGranted_WhateverTheBody()
    {
        await using var host = await Harness.StartAsync(seed: Seed);
        string[] bodies = [.. ForbiddenBodies, """{"branch_name":"feature","expected_head_commit":""}""", """{"branch_update":"none"}"""];
        string expected = "";

        foreach (var fields in bodies)
        {
            foreach (var repository in new[] { Widgets, Absent })
            {
                using var response = await host.ControlAsync(HttpMethod.Post, Ensure, ControlToken, host.UserAssertion(),
                    EnsureOf(repository, "commit-w2", fields));
                var raw = await AssertRejectedAsync(response, HttpStatusCode.Forbidden, GrantReason.NotGranted);
                if (expected.Length == 0)
                    expected = raw;
                Assert.AreEqual(expected, raw, $"{repository} {fields}: an ungranted ensure never reveals which body rule it breaks");
            }
        }

        Assert.AreEqual(0L, host.JobRows());
        var rows = host.Service.RecentAudit(action: AuditAction.Ensure);
        Assert.AreEqual(bodies.Length * 2, rows.Count);
        Assert.IsTrue(rows.All(r => r.Detail == GrantReason.NotGranted + Suffix), "every refusal is audited as not_granted");
    }

    [TestMethod]
    public async Task UserEnsure_ConflictingGuards_StayTheExistingIntakeError()
    {
        // The SVC-6/7 malformed-guard check runs before the user bounds, for every caller alike.
        await using var host = await Harness.StartAsync(seed: Seed);
        await GrantAsync(host, Widgets);

        using var response = await host.ControlAsync(HttpMethod.Post, Ensure, ControlToken, host.UserAssertion(),
            EnsureOf(Widgets, "commit-w2", """{"branch_name":"feature","expected_head_commit":"","branch_head_sequence":3}"""));

        await AssertRejectedAsync(response, HttpStatusCode.BadRequest, BranchGuardReason.ConflictingBranchGuards);
    }

    [TestMethod]
    public async Task UserEnsure_BeyondTheBounds_WhoseRefusalCannotBeRecorded_Is503_AndWritesNothing()
    {
        // Fail closed like not_granted: a refusal that cannot be audited (the writer lease is lost) is 503.
        await using var host = await Harness.StartAsync(seed: Seed, configure: o => o with { LeaseTtl = TimeSpan.FromSeconds(3) });
        await GrantAsync(host, Widgets);
        await host.StealLeaseAsync();

        using var response = await host.ControlAsync(HttpMethod.Post, Ensure, ControlToken, host.UserAssertion(),
            EnsureOf(Widgets, "commit-w2", """{"branch_name":"feature","default_branch":true,"expected_head_commit":""}"""));

        var raw = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode, raw);
        Assert.AreEqual("unavailable", JsonDocument.Parse(raw).RootElement.GetProperty("status").GetString(), raw);
        Assert.AreEqual(0, host.Service.RecentAudit(action: AuditAction.Ensure).Count, "no ensure row was written");
        Assert.AreEqual(0L, host.JobRows());
        Assert.AreEqual(0, host.Worker.Calls);
    }

    [TestMethod]
    public async Task UserEnsure_WithTheHeadCas_AdvancesItsBranch_AndNeverTheDefault()
    {
        await using var host = await Harness.StartAsync(seed: Seed);
        await GrantAsync(host, Widgets);

        var created = await EnsureOkAsync(host, host.UserAssertion(), Widgets, "commit-w2",
            """{"branch_name":"feature","expected_head_commit":""}""");
        var advanced = await EnsureOkAsync(host, host.UserAssertion(), Widgets, "commit-w3",
            """{"branch_name":"feature","expected_head_commit":"commit-w2","forced":true}""");
        var stale = await EnsureOkAsync(host, host.UserAssertion(), Widgets, "commit-w2",
            """{"branch_name":"feature","expected_head_commit":"commit-w2"}""");

        Assert.IsTrue(created.GetProperty("branch_advanced").GetBoolean());
        Assert.IsTrue(advanced.GetProperty("branch_advanced").GetBoolean());
        Assert.IsFalse(stale.GetProperty("branch_advanced").GetBoolean(), "a stale CAS attaches only");
        var feature = host.Service.ResolveBranchHead(Widgets, "feature")!;
        Assert.AreEqual("commit-w3", feature.CommitSha);
        Assert.IsFalse(feature.IsDefault, "a user-created branch never becomes the default");
        Assert.AreEqual("main", host.Service.ResolveBranchHead(Widgets, null)!.Branch);
    }

    [TestMethod]
    public async Task UserEnsure_BranchUpdateNone_IndexesWithoutMovingAnyPointer()
    {
        await using var host = await Harness.StartAsync(seed: Seed);
        await GrantAsync(host, Widgets);
        var branchesBefore = host.BranchRows();

        var named = await EnsureOkAsync(host, host.UserAssertion(), Widgets, "commit-w2",
            """{"branch_name":"feature","branch_update":"none"}""");
        var unnamed = await EnsureOkAsync(host, host.UserAssertion(), Widgets, "commit-w3", """{"branch_update":"none"}""");

        Assert.IsFalse(named.GetProperty("branch_advanced").GetBoolean());
        Assert.IsFalse(unnamed.GetProperty("branch_advanced").GetBoolean());
        Assert.AreEqual(branchesBefore, host.BranchRows(), "branch_update: none creates no branch row");
        Assert.IsNull(host.Service.ResolveBranchHead(Widgets, "feature"));
        Assert.AreEqual("main", host.Service.ResolveBranchHead(Widgets, null)!.Branch);
    }

    [TestMethod]
    public async Task UserEnsure_ANewCommitWithTheCas_ReachesTheWorker()
    {
        await using var host = await Harness.StartAsync(seed: Seed);
        await GrantAsync(host, Widgets);

        await EnsureOkAsync(host, host.UserAssertion(), Widgets, "commit-new",
            """{"branch_name":"feature","expected_head_commit":""}""");

        Assert.AreEqual(1, host.Worker.Calls, "a guarded user ensure of an unbuilt commit is indexed");
    }

    // ==== application and assertion-less callers are unchanged =====================================

    [TestMethod]
    public async Task ApplicationEnsure_KeepsFullBranchControl()
    {
        await using var host = await Harness.StartAsync(seed: Seed);
        var application = host.Sign(CallerAssertionSigner.ApplicationClaims(DateTimeOffset.UtcNow));

        var promoted = await EnsureOkAsync(host, application, Widgets, "commit-w2",
            """{"branch_name":"release","default_branch":true,"expected_head_commit":""}""");
        var sequenced = await EnsureOkAsync(host, application, Widgets, "commit-w3",
            """{"branch_name":"seq","branch_head_sequence":5}""");
        var unguarded = await EnsureOkAsync(host, application, Widgets, "commit-w2", """{"branch_name":"plain"}""");

        Assert.IsTrue(promoted.GetProperty("branch_advanced").GetBoolean());
        Assert.AreEqual("release", host.Service.ResolveBranchHead(Widgets, null)!.Branch, "the application may move the default");
        Assert.IsTrue(sequenced.GetProperty("branch_advanced").GetBoolean());
        Assert.AreEqual(5L, host.Service.ResolveBranchHead(Widgets, "seq")!.HeadSequence);
        Assert.IsTrue(unguarded.GetProperty("branch_advanced").GetBoolean());
        Assert.AreEqual("commit-w2", host.Service.ResolveBranchHead(Widgets, "plain")!.CommitSha);
    }

    [TestMethod]
    public async Task AssertionLessEnsure_KeepsFullBranchControl()
    {
        await using var host = await Harness.StartAsync(seed: Seed);

        var promoted = await EnsureOkAsync(host, null, Widgets, "commit-w2",
            """{"branch_name":"release","default_branch":true,"expected_head_commit":""}""");
        var sequenced = await EnsureOkAsync(host, null, Widgets, "commit-w3",
            """{"branch_name":"seq","branch_head_sequence":9223372036854775807}""");
        var unguarded = await EnsureOkAsync(host, null, Widgets, "commit-w2", """{"branch_name":"plain"}""");

        Assert.IsTrue(promoted.GetProperty("branch_advanced").GetBoolean());
        Assert.AreEqual("release", host.Service.ResolveBranchHead(Widgets, null)!.Branch);
        Assert.IsTrue(sequenced.GetProperty("branch_advanced").GetBoolean());
        Assert.AreEqual(long.MaxValue, host.Service.ResolveBranchHead(Widgets, "seq")!.HeadSequence);
        Assert.IsTrue(unguarded.GetProperty("branch_advanced").GetBoolean());
        Assert.AreEqual(0, host.Service.RecentAudit(action: AuditAction.Ensure).Count(r => r.Outcome == AuditOutcome.Denied));
    }

    // ==== startup guard ============================================================================

    [TestMethod]
    public void Host_WithoutAControlToken_RefusesToStart()
    {
        var dbPath = ServiceTestFixtures.NewDbPath();
        var db = new IndexDatabase(dbPath);
        db.RunMigrations();
        var options = ServiceTestFixtures.NewOptions(dbPath) with { InsecureOpenControlPlane = false };
        var service = SnapshotService.Start(options, new FakeSnapshotWorker(db), db);
        try
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            var refused = Assert.ThrowsExactly<InvalidOperationException>(() => ServiceApp.RegisterServices(builder, options, service));
            StringAssert.Contains(refused.Message, "SEXTANT_SERVICE_CONTROL_TOKEN is not set");
        }
        finally
        {
            service.Dispose();
            SqliteTestDatabase.Delete(dbPath, db);
        }
    }

    [TestMethod]
    [DataRow(null, HttpStatusCode.OK, true)]
    [DataRow(ControlToken, HttpStatusCode.Unauthorized, false)]
    public async Task Host_TheInsecureOptOut_OpensTheControlPlane_OnlyWithoutAToken_AndLogsIt(
        string? controlToken, HttpStatusCode tokenlessRetention, bool warned)
    {
        var logs = new AllCategoriesLoggerProvider();
        var dbPath = ServiceTestFixtures.NewDbPath();
        var db = new IndexDatabase(dbPath);
        db.RunMigrations();
        var options = ServiceTestFixtures.NewOptions(dbPath, controlToken: controlToken) with { InsecureOpenControlPlane = true };
        var service = SnapshotService.Start(options, new FakeSnapshotWorker(db), db);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        ServiceApp.RegisterServices(builder, options, service);
        var app = builder.Build();
        try
        {
            ServiceApp.MapEndpoints(app, options);
            await app.StartAsync();
            using var client = app.GetTestClient();

            using var retention = await client.PostAsync("/control/retention", null);

            Assert.AreEqual(tokenlessRetention, retention.StatusCode, "a configured control token always wins over the opt-out");
            Assert.AreEqual(warned,
                logs.Entries.Any(e => e.Level == LogLevel.Warning && e.Message == ServiceOptions.OpenControlPlaneWarning),
                string.Join('\n', logs.Entries.Select(e => e.Message)));
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            service.Dispose();
            SqliteTestDatabase.Delete(dbPath, db);
        }
    }

    // ==== helpers ==================================================================================

    private static readonly string[] ForbiddenBodies =
    [
        """{"branch_name":"feature","default_branch":true,"expected_head_commit":""}""",
        """{"branch_name":"feature","branch_head_sequence":9223372036854775807}""",
        """{"branch_name":"feature"}""",
        """{"expected_head_commit":""}"""
    ];

    private static void Seed(IndexDatabase db)
    {
        foreach (var commit in new[] { "commit-w2", "commit-w3" })
            ServiceTestFixtures.PublishComplete(db, ServiceTestFixtures.Request(repo: Widgets, commit: commit), symbolCount: 1, recordCommit: true);
    }

    private static async Task GrantAsync(Harness host, string repository)
    {
        using var grant = await host.ControlAsync(HttpMethod.Put, "/control/grants/self", ControlToken, host.UserAssertion(),
            JsonSerializer.Serialize(new { repository }));
        Assert.AreEqual(HttpStatusCode.OK, grant.StatusCode, await grant.Content.ReadAsStringAsync());
    }

    private static string EnsureOf(string repository, string commit, string fields)
    {
        var body = JsonSerializer.SerializeToNode(
            new EnsureSnapshotRequest { RepositoryRemoteUrl = repository, CommitSha = commit }, ServiceJson.Options)!.AsObject();
        foreach (var (key, value) in JsonNode.Parse(fields)!.AsObject())
            body[key] = value?.DeepClone();
        return body.ToJsonString();
    }

    private static async Task<JsonElement> EnsureOkAsync(Harness host, string? assertion, string repository, string commit, string fields)
    {
        using var response = await host.ControlAsync(HttpMethod.Post, Ensure, ControlToken, assertion, EnsureOf(repository, commit, fields));
        var raw = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, raw);
        return JsonDocument.Parse(raw).RootElement.Clone();
    }

    private static async Task<string> AssertRejectedAsync(HttpResponseMessage response, HttpStatusCode status, string reason)
    {
        var raw = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(status, response.StatusCode, raw);
        Assert.AreEqual($$"""{"status":"rejected","reason":"{{reason}}"}""", raw);
        return raw;
    }

    private sealed class AllCategoriesLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(Entries);

        public void Dispose()
        {
        }

        private sealed class Logger(ConcurrentQueue<(LogLevel Level, string Message)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Enqueue((logLevel, formatter(state, exception)));
        }
    }
}
