using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Sextant.Service.Host;
using Sextant.Store;
using Sextant.TestSupport;

namespace Sextant.Service.Tests;

/// <summary>
/// Issue #158 at the HTTP boundary: a production holds the single writer for its whole run, so a control write
/// that needs the writer must still answer within <see cref="ServiceOptions.ControlWriteWait"/>, well under the
/// ProcessStack app's 30 s client timeout. <c>POST /control/ensure?wait=false</c> for a new identity answers
/// <c>202 {status:"queued", job_id:&lt;integer&gt;}</c> with the id its row is later inserted under, and
/// <c>POST /control/branches/retire</c> answers <c>202 {"status":"accepted"}</c> and applies once the writer frees,
/// with its guards evaluated then. Both are service-owned, survive the caller going away, and apply in submission
/// order. A blocking test worker holds the writer: every commit of <see cref="Repo"/> is pre-published (its ensures
/// are service-side branch decisions), while the blocker repository and any new commit go through the worker.
/// </summary>
[TestClass]
public class QueuedControlWriteHttpTests
{
    private const string ControlToken = "control-secret";
    private const string Repo = "https://github.com/acme/widgets";
    private const string BlockerRepo = "https://github.com/acme/blocker";

    private static readonly TimeSpan Bound = TimeSpan.FromMilliseconds(400);
    // The bound plus generous scheduling slack: a write that still waits for the writer fails this, never hangs.
    private static readonly TimeSpan Answered = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(20);

    [TestMethod]
    public async Task EnsureWaitFalse_NewIdentityWhileTheWriterIsBusy_Is202Queued_WithAnIntegerJobId_ThatStatusResolves()
    {
        var clock = new ManualClock();
        await using var host = await Harness.StartAsync(clock);
        await host.HoldWriterAsync();
        var request = Ensure("commit-new");
        var hash = request.ToIdentity().Hash;

        var controlWaitStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Service.ControlWaitStartedForTesting = () => controlWaitStarted.TrySetResult();
        var post = host.EnsureAsync(request, wait: false);
        await controlWaitStarted.Task.WaitAsync(Answered);
        Assert.IsFalse(post.IsCompleted, "the queued response waits through its configured bound");
        clock.Advance(Bound);
        var response = await post.WaitAsync(Answered);

        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);
        long jobId;
        using (var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
        {
            var root = json.RootElement;
            CollectionAssert.AreEquivalent(new[] { "job_id", "identity_hash", "status", "attached" },
                root.EnumerateObject().Select(p => p.Name).ToArray());
            Assert.AreEqual(JsonValueKind.Number, root.GetProperty("job_id").ValueKind, "job_id stays a JSON integer");
            jobId = root.GetProperty("job_id").GetInt64();
            Assert.IsTrue(jobId > 0);
            Assert.AreEqual(SnapshotJobStatus.Queued, root.GetProperty("status").GetString());
            Assert.AreEqual(hash, root.GetProperty("identity_hash").GetString());
            Assert.IsFalse(root.GetProperty("attached").GetBoolean());
        }

        // The id resolves at once, before its row exists.
        var status = await host.StatusAsync(jobId).WaitAsync(Answered);
        Assert.IsNotNull(status);
        Assert.AreEqual(jobId, status.Value.GetProperty("job").GetProperty("id").GetInt64());
        Assert.AreEqual(SnapshotJobStatus.Queued, status.Value.GetProperty("job").GetProperty("status").GetString());
        Assert.AreEqual(hash, status.Value.GetProperty("job").GetProperty("identity_hash").GetString());
        Assert.AreEqual(Repo, status.Value.GetProperty("job").GetProperty("repository_url").GetString());

        // A second ensure of the pending identity attaches to the same id.
        var duplicateWaitStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Service.ControlWaitStartedForTesting = () => duplicateWaitStarted.TrySetResult();
        var duplicatePost = host.EnsureAsync(request, wait: false);
        await duplicateWaitStarted.Task.WaitAsync(Answered);
        Assert.IsFalse(duplicatePost.IsCompleted);
        clock.Advance(Bound);
        var again = await duplicatePost.WaitAsync(Answered);
        Assert.AreEqual(HttpStatusCode.Accepted, again.StatusCode);
        using (var json = JsonDocument.Parse(await again.Content.ReadAsStringAsync()))
        {
            Assert.AreEqual(jobId, json.RootElement.GetProperty("job_id").GetInt64());
            Assert.IsTrue(json.RootElement.GetProperty("attached").GetBoolean());
        }
        Assert.AreEqual(1, host.Worker.Calls, "nothing but the blocker has run");

        host.ReleaseWriter();
        await WaitUntilAsync(async () =>
            (await host.StatusAsync(jobId))?.GetProperty("job").GetProperty("status").GetString() == SnapshotJobStatus.Complete);
        var produced = (await host.StatusAsync(jobId))!.Value.GetProperty("job");
        Assert.AreEqual(hash, produced.GetProperty("identity_hash").GetString(), "the durable row took the reserved id");
        Assert.AreEqual(2, host.Worker.Calls, "the identity produced exactly once");
        Assert.AreNotEqual(host.BlockerJobId, jobId);
    }

    [TestMethod]
    public async Task EnsureWaitFalse_DurableAdmissionBlockedByCatalogWriter_ReturnsUnavailableAtResponseBound()
    {
        // This deterministic reproduction holds SQLite's BEGIN IMMEDIATE lock while the service-owned queued_ensures
        // write runs. The older writer-gate tests use a fake worker that does not hold a SQLite transaction, so this
        // proves a distinct delay path, not the historical flake's cause.
        var clock = new ManualClock();
        await using var host = await Harness.StartAsync(clock, new Harness.ConfirmingRemote());
        using var catalogLock = host.HoldCatalogWriteLock();
        var request = Ensure("refresh-head", "feature");
        var admitted = host.Service.WriteAdmissions;
        var post = host.EnsureAsync(request, wait: false);

        await WaitUntilAsync(() => Task.FromResult(
            host.Service.WriteAdmissions > admitted && host.Service.WriteLockMetrics.WaitingWriters > 0));
        Assert.IsFalse(post.IsCompleted, "durable admission is blocked on SQLite, not the service write gate");
        clock.Advance(Bound);

        var response = await post.WaitAsync(Answered);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode,
            "wait=false must not overrun its response bound while queued_ensures is not durable");
        string? admissionId;
        using (var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
        {
            admissionId = json.RootElement.GetProperty("admission_id").GetString();
            Assert.IsFalse(string.IsNullOrEmpty(admissionId));
        }

        catalogLock.Dispose();
        host.ReleaseWriter();
        var hash = request.ToIdentity().Hash;
        await WaitUntilAsync(() =>
            Task.FromResult(host.Service.GetStatusByIdentity(hash)?.Job.Status == SnapshotJobStatus.Complete));
        var admission = await host.AdmissionStatusAsync(admissionId!);
        Assert.AreEqual(admissionId, admission.GetProperty("admission_id").GetString());
        Assert.AreEqual("ensure", admission.GetProperty("kind").GetString());
        Assert.AreEqual(1, host.Worker.Calls, "the service-owned admitted ensure continues after this caller receives 503");
    }

    [TestMethod]
    [DataRow(true, false)]
    [DataRow(true, true)]
    [DataRow(false, false)]
    [DataRow(false, true)]
    public async Task EnsureWaitFalse_NonConvergenceAdmissionCanOutlastResponseBound(bool locate, bool failWrite)
    {
        var clock = new ManualClock();
        await using var host = await Harness.StartAsync(clock, locate ? null : new Harness.ConfirmingRemote());
        if (failWrite)
            host.Service.BeforeQueuedEnsureWriteForTesting =
                () => throw new InvalidOperationException("injected admission write failure");
        using var catalogLock = host.HoldCatalogWriteLock();
        var request = Ensure("refresh-head", "feature") with
        {
            BranchUpdate = locate ? BranchUpdateMode.Advance : BranchUpdateMode.None
        };
        var post = host.EnsureAsync(request, wait: false);
        await WaitUntilAsync(() => Task.FromResult(host.Service.WriteLockMetrics.WaitingWriters > 0));
        clock.Advance(Bound);
        Assert.IsFalse(post.IsCompleted, "best-effort admission is not subject to the response bound");

        catalogLock.Dispose();
        host.ReleaseWriter();
        using var response = await post.WaitAsync(Answered);
        Assert.AreNotEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var hash = request.ToIdentity().Hash;
        await WaitUntilAsync(() =>
            Task.FromResult(host.Service.GetStatusByIdentity(hash)?.Job.Status == SnapshotJobStatus.Complete));
        Assert.AreEqual(1, host.Worker.Calls);
    }

    [TestMethod]
    public async Task EnsureWaitFalse_RemoteConvergenceAdmissionWriteFailure_ReturnsUnavailable()
    {
        var clock = new ManualClock();
        await using var host = await Harness.StartAsync(clock, new Harness.ConfirmingRemote());
        host.Service.BeforeQueuedEnsureWriteForTesting =
            () => throw new InvalidOperationException("injected admission write failure");
        var request = Ensure("commit-new", "feature");

        using var response = await host.EnsureAsync(request, wait: false).WaitAsync(Answered);

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.AreEqual(0, host.Worker.Calls, "a branch-moving ensure never runs without a durable convergence marker");
        Assert.IsNull(host.Service.GetStatusByIdentity(request.ToIdentity().Hash));
    }

    // Issue #273 (B2): a CAS ensure that would move a branch onto an already-published snapshot is a durable admission,
    // so it is bound by the response wait (503 while the catalog writer is blocked). Its service-owned operation still
    // commits and runs afterwards, and the retry attaches: commit-C is never produced.
    [TestMethod]
    public async Task EnsureWaitFalse_BranchMovingTerminalAttach_IsDurablyAdmitted_503RetryAttachesWithoutWorker()
    {
        var clock = new ManualClock();
        await using var host = await Harness.StartAsync(clock, new Harness.ConfirmingRemote(), recordWorkerCommit: true);
        host.ReleaseWriter();
        foreach (var (commit, branch) in new[] { ("commit-A", "main"), ("commit-B", "feature") })
        {
            using var seeded = await host.EnsureAsync(Ensure(commit, branch, expected: ""), wait: true).WaitAsync(Settle);
            Assert.AreEqual(HttpStatusCode.OK, seeded.StatusCode, await seeded.Content.ReadAsStringAsync());
        }
        await WaitUntilAsync(() => Task.FromResult(host.OpenWorkCount == 0));
        Assert.AreEqual("refresh-head", await host.ResolveCommitAsync("feature"));
        var calls = host.Worker.Calls;

        var move = Ensure("commit-C", "feature", expected: "refresh-head");
        string? admissionId;
        using (host.HoldCatalogWriteLock())
        {
            var post = host.EnsureAsync(move, wait: false);
            await WaitUntilAsync(() => Task.FromResult(host.Service.WriteLockMetrics.WaitingWriters > 0));
            clock.Advance(Bound);
            using var unavailable = await post.WaitAsync(Answered);
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode,
                "a branch-moving attach is acknowledged only once its intent is durable");
            using var unavailableBody = JsonDocument.Parse(await unavailable.Content.ReadAsStringAsync());
            admissionId = unavailableBody.RootElement.GetProperty("admission_id").GetString();
            Assert.IsFalse(string.IsNullOrEmpty(admissionId));
        }

        await WaitUntilAsync(async () =>
            (await host.AdmissionStatusAsync(admissionId!)).TryGetProperty("kind", out _));
        var admission = await host.AdmissionStatusAsync(admissionId!);
        Assert.AreEqual(admissionId, admission.GetProperty("admission_id").GetString());
        Assert.AreEqual("ensure", admission.GetProperty("kind").GetString());
        await WaitUntilAsync(() => Task.FromResult(host.OpenWorkCount == 0));
        Assert.IsTrue(host.SettledEnsureIntents("feature") >= 1, "the 503'd admission committed and ran in order");
        using var retry = await host.EnsureAsync(move, wait: false).WaitAsync(Answered);
        Assert.IsTrue(retry.StatusCode is HttpStatusCode.OK or HttpStatusCode.Accepted,
            await retry.Content.ReadAsStringAsync());
        using (var body = JsonDocument.Parse(await retry.Content.ReadAsStringAsync()))
        {
            Assert.IsTrue(body.RootElement.GetProperty("attached").GetBoolean());
            if (body.RootElement.TryGetProperty("admission_id", out var responseAdmissionId))
            {
                var status = await host.AdmissionStatusAsync(responseAdmissionId.GetString()!);
                Assert.AreEqual("ensure", status.GetProperty("kind").GetString());
            }
        }
        await WaitUntilAsync(() => Task.FromResult(host.OpenWorkCount == 0));
        Assert.AreEqual(calls, host.Worker.Calls, "commit-C was attached, never produced");
        Assert.AreEqual("refresh-head", await host.ResolveCommitAsync("feature"), "convergence follows the move");
    }

    // Issue #273 (D2): E's caller got an ambiguous 503 while its admission was still being written; the admission then
    // committed and a retirement of the branch was admitted. The identical retry must not share E across that
    // retirement: it gets its own intent after the retirement and recreates the branch at E's snapshot.
    [TestMethod]
    public async Task Coalesce_RetryAfter503AndAdmittedRetire_IsAdmittedAfterTheRetirement()
    {
        var clock = new ManualClock();
        await using var host = await Harness.StartAsync(clock, new Harness.ConfirmingRemote(),
            publishedCommits: ["refresh-head"]);
        using (var seeded = await host.EnsureAsync(Ensure("commit-A", "main", expected: ""), wait: true).WaitAsync(Settle))
            Assert.AreEqual(HttpStatusCode.OK, seeded.StatusCode, await seeded.Content.ReadAsStringAsync());
        await WaitUntilAsync(() => Task.FromResult(host.OpenWorkCount == 0));
        // A historical production of another repository holds the writer (it moves no branch, so it has no refresh).
        using (var hold = await host.EnsureAsync(new EnsureSnapshotRequest
               {
                   RepositoryRemoteUrl = BlockerRepo, CommitSha = "blocker-1", BranchUpdate = BranchUpdateMode.None
               }, wait: false).WaitAsync(Answered))
            Assert.AreEqual(HttpStatusCode.Accepted, hold.StatusCode);
        await WaitUntilAsync(() => Task.FromResult(host.Worker.Calls == 1));
        var request = Ensure("refresh-head", "feature");

        string firstAdmission;
        using (host.HoldCatalogWriteLock())
        {
            var post = host.EnsureAsync(request, wait: false);
            await WaitUntilAsync(() => Task.FromResult(host.Service.WriteLockMetrics.WaitingWriters > 0));
            clock.Advance(Bound);
            using var unavailable = await post.WaitAsync(Answered);
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
            using var body = JsonDocument.Parse(await unavailable.Content.ReadAsStringAsync());
            firstAdmission = body.RootElement.GetProperty("admission_id").GetString()!;
        }
        await WaitUntilAsync(async () =>
            (await host.AdmissionStatusAsync(firstAdmission)).TryGetProperty("kind", out _));

        var retire = host.Service.RetireBranchAsync(new RetireBranchRequest { Repository = Repo, Branch = "feature" });
        await WaitUntilAsync(() => Task.FromResult(host.IntentCount("retire") == 1));

        using var retry = await EnsureQueuedAsync(host, clock, request);
        Assert.AreEqual(HttpStatusCode.Accepted, retry.StatusCode, await retry.Content.ReadAsStringAsync());
        string retryAdmission;
        using (var body = JsonDocument.Parse(await retry.Content.ReadAsStringAsync()))
            retryAdmission = body.RootElement.GetProperty("admission_id").GetString()!;
        Assert.AreNotEqual(firstAdmission, retryAdmission, "the retry is a new intent after the retirement");
        Assert.AreEqual("ensure", (await host.AdmissionStatusAsync(retryAdmission)).GetProperty("kind").GetString());

        host.ReleaseWriter();
        Assert.IsTrue((await retire.WaitAsync(Settle)).Retired, "E created the branch before the retirement applied");
        await WaitUntilAsync(() => Task.FromResult(host.OpenWorkCount == 0));
        var audit = host.Service.RecentAudit(action: AuditAction.Retire, repositoryScope: Repo);
        Assert.AreEqual("retired", audit.Single().Detail);
        Assert.AreEqual("refresh-head", await host.ResolveCommitAsync("feature"), "the retry recreated the branch at E's snapshot");
        Assert.AreEqual(1, host.Worker.Calls, "only the blocker ran: E and its retry attached the published snapshot");
    }

    // Issue #273 (D2): an identical wait=false ensure that shares E while E still waits for the writer answers its
    // queued 202 with E's admission id (the one its admission transaction resolved), which status resolves.
    [TestMethod]
    public async Task Coalesce_SharedQueuedEnsure_ReturnsTheSharedAdmissionId()
    {
        var clock = new ManualClock();
        await using var host = await Harness.StartAsync(clock, new Harness.ConfirmingRemote());
        // An unrelated repository's historical production holds the writer.
        using (var hold = await host.EnsureAsync(new EnsureSnapshotRequest
               {
                   RepositoryRemoteUrl = BlockerRepo, CommitSha = "blocker-1", BranchUpdate = BranchUpdateMode.None
               }, wait: false).WaitAsync(Answered))
            Assert.AreEqual(HttpStatusCode.Accepted, hold.StatusCode);
        await WaitUntilAsync(() => Task.FromResult(host.Worker.Calls == 1));
        var request = Ensure("commit-C", "feature");

        string first;
        using (var admitted = await EnsureQueuedAsync(host, clock, request))
        {
            Assert.AreEqual(HttpStatusCode.Accepted, admitted.StatusCode, await admitted.Content.ReadAsStringAsync());
            using var body = JsonDocument.Parse(await admitted.Content.ReadAsStringAsync());
            first = body.RootElement.GetProperty("admission_id").GetString()!;
        }
        string shared;
        using (var again = await EnsureQueuedAsync(host, clock, request))
        {
            Assert.AreEqual(HttpStatusCode.Accepted, again.StatusCode, await again.Content.ReadAsStringAsync());
            using var body = JsonDocument.Parse(await again.Content.ReadAsStringAsync());
            shared = body.RootElement.GetProperty("admission_id").GetString()!;
        }

        Assert.AreEqual(first, shared, "the shared request answers with E's admitted id, not its unrecorded candidate");
        var status = await host.AdmissionStatusAsync(shared);
        Assert.AreEqual("ensure", status.GetProperty("kind").GetString());
        Assert.AreEqual(1, host.IntentCount("ensure"), "the identical request shared E's intent");
        host.ReleaseWriter();
    }

    [TestMethod]
    [DataRow(false, false, false)]
    [DataRow(false, false, true)]
    public async Task ExplicitRetryAfter503WithRepositoryAliasAttachesOriginalTerminalResult(
        bool locate, bool suppress, bool unsupported)
    {
        var clock = new ManualClock();
        await using var host = await Harness.StartAsync(clock, locate ? null : new Harness.ConfirmingRemote(),
            terminalStatus: unsupported ? SnapshotJobStatus.Unsupported : SnapshotJobStatus.Failed);
        using var catalogLock = host.HoldCatalogWriteLock();
        var request = Ensure("refresh-head", "feature") with
        {
            BranchUpdate = suppress ? BranchUpdateMode.None : BranchUpdateMode.Advance
        };
        var post = host.EnsureAsync(request, wait: false);
        await WaitUntilAsync(() => Task.FromResult(host.Service.WriteLockMetrics.WaitingWriters > 0));
        clock.Advance(Bound);
        using var unavailable = await post.WaitAsync(Answered);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);

        catalogLock.Dispose();
        host.ReleaseWriter();
        var hash = request.ToIdentity().Hash;
        var expected = unsupported ? SnapshotJobStatus.Unsupported : SnapshotJobStatus.Failed;
        await WaitUntilAsync(() => Task.FromResult(host.Service.GetStatusByIdentity(hash)?.Job.Status == expected));
        var aliasRequest = request with
        {
            RepositoryRemoteUrl = "HTTPS://GITHUB.COM/acme/widgets.git"
        };
        Assert.AreNotEqual(hash, aliasRequest.ToIdentity().Hash,
            "the submitted spelling differs; admission must pin it to the catalog's first spelling");
        using var retry = await host.EnsureAsync(aliasRequest, wait: false).WaitAsync(Answered);
        Assert.IsTrue(retry.StatusCode is HttpStatusCode.OK or HttpStatusCode.Accepted,
            await retry.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await retry.Content.ReadAsStringAsync());
        if (retry.StatusCode == HttpStatusCode.OK)
            Assert.AreEqual(expected, body.RootElement.GetProperty("status").GetString());
        else
            Assert.IsTrue(body.RootElement.GetProperty("attached").GetBoolean());
        await WaitUntilAsync(() => Task.FromResult(host.Service.GetStatusByIdentity(hash)?.Job.Status == expected));
        Assert.AreEqual(1, host.Worker.Calls, "the retry attaches instead of launching another production");

        // On another branch: the failed ensure's following refresh holds "feature" until its retry (rule 3: admission
        // order within a branch), and branch order is not what this checks.
        var incompatibleConfig = aliasRequest with { ConfigHash = "different-config", BranchName = "feature-2" };
        var incompatibleIdentity = request with { ConfigHash = incompatibleConfig.ConfigHash, BranchName = "feature-2" };
        using (var response = await host.EnsureAsync(incompatibleConfig, wait: false).WaitAsync(Answered))
            Assert.IsTrue(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Accepted);
        await WaitUntilAsync(() =>
            Task.FromResult(host.Service.GetStatusByIdentity(incompatibleIdentity.ToIdentity().Hash)?.Job.Status == expected));
        Assert.AreEqual(2, host.Worker.Calls, "an alias with a different config is a distinct identity");

        var otherRepository = aliasRequest with { RepositoryRemoteUrl = "https://github.com/acme/other" };
        using (var response = await host.EnsureAsync(otherRepository, wait: false).WaitAsync(Answered))
            Assert.IsTrue(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Accepted);
        await WaitUntilAsync(() =>
            Task.FromResult(host.Service.GetStatusByIdentity(otherRepository.ToIdentity().Hash)?.Job.Status == expected));
        Assert.AreEqual(3, host.Worker.Calls, "a different repository never attaches through the spelling map");
    }

    [TestMethod]
    public async Task EnsureWaitFalse_NonConvergenceUnwritableJobIdFloor_WaitsForResult()
    {
        var clock = new ManualClock();
        await using var host = await Harness.StartAsync(clock);
        await host.HoldWriterAsync();
        host.BlockJobIdFloor();
        try
        {
            var controlWaitStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            host.Service.ControlWaitStartedForTesting = () => controlWaitStarted.TrySetResult();
            var post = host.EnsureAsync(Ensure("commit-new"), wait: false);
            await controlWaitStarted.Task.WaitAsync(Answered);
            Assert.IsFalse(post.IsCompleted);
            clock.Advance(Bound);

            Assert.IsFalse(post.IsCompleted, "a missing safe id leaves the main-compatible request waiting");

            host.ClearJobIdFloor();
            host.ReleaseWriter();
            using var response = await post.WaitAsync(Answered);
            Assert.IsTrue(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Accepted);
            var hash = Ensure("commit-new").ToIdentity().Hash;
            await WaitUntilAsync(() =>
                Task.FromResult(host.Service.GetStatusByIdentity(hash)?.Job.Status == SnapshotJobStatus.Complete));
            Assert.AreEqual(2, host.Worker.Calls, "the service-owned operation can still finish after its 503 response");
        }
        finally
        {
            host.ClearJobIdFloor();
            host.ReleaseWriter();
        }
    }

    [TestMethod]
    public async Task EnsureWaitFalse_RefreshAdmissionBlockedByCatalogWriter_ReturnsUnavailableAtResponseBound()
    {
        var clock = new ManualClock();
        await using var host = await Harness.StartAsync(clock, new Harness.ConfirmingRemote());
        using var catalogLock = host.HoldCatalogWriteLock();
        var request = new EnsureSnapshotRequest
        {
            RepositoryRemoteUrl = Repo,
            CommitSha = "refresh-head",
            BranchName = "feature",
            Refresh = true
        };
        var post = host.EnsureAsync(request, wait: false);

        await WaitUntilAsync(() => Task.FromResult(host.Service.WriteLockMetrics.WaitingWriters > 0));
        Assert.IsFalse(post.IsCompleted, "refresh confirmation passed; durable marker admission is blocked on SQLite");
        clock.Advance(Bound);

        var response = await post.WaitAsync(Answered);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode,
            "a refresh is not acknowledged until its marker is durable");

        catalogLock.Dispose();
        await WaitUntilAsync(() => Task.FromResult(host.PendingRefreshCount > 0));
    }

    [TestMethod]
    public async Task RefreshRetryWithoutKey_AfterUnavailableAndClaim_CreatesAnotherAdmission()
    {
        var clock = new ManualClock();
        await using var host = await Harness.StartAsync(clock, new Harness.ConfirmingRemote(), recordWorkerCommit: true);
        using var catalogLock = host.HoldCatalogWriteLock();
        var request = new EnsureSnapshotRequest
        {
            RepositoryRemoteUrl = Repo,
            CommitSha = "refresh-head",
            BranchName = "feature",
            Refresh = true
        };
        var post = host.EnsureAsync(request, wait: false);
        await WaitUntilAsync(() => Task.FromResult(host.Service.WriteLockMetrics.WaitingWriters > 0));
        clock.Advance(Bound);
        using var unavailable = await post.WaitAsync(Answered);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);

        catalogLock.Dispose();
        await WaitUntilAsync(() => Task.FromResult(host.ClaimedRefreshCount == 1));
        using var retry = await host.EnsureAsync(request, wait: false).WaitAsync(Answered);
        Assert.AreEqual(HttpStatusCode.Accepted, retry.StatusCode);
        Assert.AreEqual(2, host.PendingRefreshCount, "a retry without a key after claim is a new admission");
        host.ReleaseWriter();
        await WaitUntilAsync(() => Task.FromResult(host.CompletedRefreshCount == 2));
    }

    [TestMethod]
    public async Task EnsureWaitFalse_CallerGoneWhileQueued_TheEnsureStillRegistersAndProduces()
    {
        await using var host = await Harness.StartAsync();
        await host.HoldWriterAsync();
        var request = Ensure("commit-new");
        var hash = request.ToIdentity().Hash;

        using var gone = new CancellationTokenSource();
        var admitted = host.Service.WriteAdmissions;
        var post = host.EnsureAsync(request, wait: false, gone.Token);
        await WaitUntilAsync(() => Task.FromResult(host.Service.WriteAdmissions > admitted));
        gone.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => post.WaitAsync(Answered));

        host.ReleaseWriter();
        await WaitUntilAsync(() => Task.FromResult(host.Service.GetStatusByIdentity(hash)?.Job.Status == SnapshotJobStatus.Complete));
        Assert.AreEqual(2, host.Worker.Calls);
    }

    [TestMethod]
    public async Task EnsureWaitFalse_RegistrationThatNeverCommits_LeavesTheReservedIdUnknown_NotQueuedForever()
    {
        var clock = new ManualClock();
        await using var host = await Harness.StartAsync(clock);
        await host.HoldWriterAsync();
        var response = await EnsureQueuedAsync(host, clock, Ensure("commit-new"));
        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);
        long jobId;
        using (var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
            jobId = json.RootElement.GetProperty("job_id").GetInt64();

        // The service loses the writer lease before the queued registration's turn, so its row is never written.
        await host.StealLeaseAsync();
        host.ReleaseWriter();

        // The reservation goes with the failed registration: the id reads 404 (ensure again), never queued forever.
        await WaitUntilAsync(async () => await host.StatusAsync(jobId) is null);
        Assert.IsNull(host.Service.GetStatusByIdentity(Ensure("commit-new").ToIdentity().Hash));
    }

    [TestMethod]
    public async Task EnsureWaitTrue_WhileTheWriterIsBusy_StillWaitsForItsResult()
    {
        await using var host = await Harness.StartAsync();
        await host.HoldWriterAsync();

        var post = host.EnsureAsync(Ensure("commit-new"), wait: true);
        await Task.Delay(Bound * 2);
        Assert.IsFalse(post.IsCompleted, "the blocking ensure keeps its contract");

        host.ReleaseWriter();
        var response = await post.WaitAsync(Settle);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual(SnapshotJobStatus.Complete, json.RootElement.GetProperty("status").GetString());
    }

    [TestMethod]
    public async Task Retire_WithTheWriterFree_KeepsTheSynchronous200Body()
    {
        await using var host = await Harness.StartAsync();
        await host.SeedAsync();

        var response = await host.RetireAsync("feature", expected: "commit-B").WaitAsync(Answered);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("{\"retired\":true}", await response.Content.ReadAsStringAsync());
        Assert.IsNull(await host.ResolveCommitAsync("feature"));
    }

    [TestMethod]
    public async Task Retire_WhileTheWriterIsBusy_Is202Accepted_AndAppliesOnceItFrees()
    {
        var clock = new ManualClock();
        await using var host = await Harness.StartAsync(clock);
        await host.SeedAsync();
        await host.HoldWriterAsync();

        var waitStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Service.RetireWaitStartedForTesting = () => waitStarted.TrySetResult();
        var post = host.RetireAsync("feature", expected: "commit-B");
        await waitStarted.Task.WaitAsync(Answered);
        Assert.IsFalse(post.IsCompleted, "the retire turn is queued behind the production");
        clock.Advance(Bound);
        var response = await post.WaitAsync(Answered);

        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);
        Assert.AreEqual("{\"status\":\"accepted\"}", await response.Content.ReadAsStringAsync());
        Assert.AreEqual("commit-B", await host.ResolveCommitAsync("feature"), "not applied while the writer is busy");
        Assert.AreEqual(0, host.Service.RecentAudit(action: AuditAction.Retire).Count);

        host.ReleaseWriter();
        await WaitUntilAsync(() => Task.FromResult(host.Service.RecentAudit(action: AuditAction.Retire).Count == 1));
        Assert.IsNull(await host.ResolveCommitAsync("feature"));
        var audit = host.Service.RecentAudit(action: AuditAction.Retire)[0];
        Assert.AreEqual(AuditOutcome.Complete, audit.Outcome);
        Assert.AreEqual("retired", audit.Detail);
        Assert.AreEqual(Repo, audit.RepositoryScope);
        Assert.AreEqual(AuditLogStore.HashActor(ControlToken), audit.Actor);
    }

    [TestMethod]
    public async Task RetireUnavailableResponseId_ResolvesWhenAdmissionWasCommitted()
    {
        var clock = new ManualClock();
        await using var host = await Harness.StartAsync(clock, new Harness.ConfirmingRemote());
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseAdmission = new ManualResetEventSlim();
        host.Service.AfterRetireAdmissionCommitForTesting = () =>
        {
            committed.TrySetResult();
            releaseAdmission.Wait();
        };
        // The clock may only move once the handler's admission timer exists, or that timer would wait for a later advance.
        var admissionWaitStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Service.RetireWaitStartedForTesting = () => admissionWaitStarted.TrySetResult();
        try
        {
            var post = host.RetireAsync("feature");
            await committed.Task.WaitAsync(Answered);
            await admissionWaitStarted.Task.WaitAsync(Answered);
            clock.Advance(Bound);

            using var response = await post.WaitAsync(Answered);
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var admissionId = body.RootElement.GetProperty("admission_id").GetString();
            Assert.IsFalse(string.IsNullOrEmpty(admissionId));
            var admission = await host.AdmissionStatusAsync(admissionId!);
            Assert.AreEqual(admissionId, admission.GetProperty("admission_id").GetString());
            Assert.AreEqual("retire", admission.GetProperty("kind").GetString());
        }
        finally
        {
            releaseAdmission.Set();
            host.Service.AfterRetireAdmissionCommitForTesting = null;
            host.Service.RetireWaitStartedForTesting = null;
        }
    }

    [TestMethod]
    public async Task RetireAdmissionResponseId_ResolvesAsRetireIntent()
    {
        await using var host = await Harness.StartAsync(remoteDefaults: new Harness.ConfirmingRemote());
        using var response = await host.RetireAsync("feature").WaitAsync(Answered);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var admissionId = body.RootElement.GetProperty("admission_id").GetString();
        Assert.IsFalse(string.IsNullOrEmpty(admissionId));
        var status = await host.AdmissionStatusAsync(admissionId!);
        Assert.AreEqual(admissionId, status.GetProperty("admission_id").GetString());
        Assert.AreEqual("retire", status.GetProperty("kind").GetString());
    }

    [TestMethod]
    public async Task EnsureAdmissionResponseId_ResolvesAsEnsureIntent()
    {
        var clock = new ManualClock();
        await using var host = await Harness.StartAsync(clock, new Harness.ConfirmingRemote());
        await host.HoldWriterAsync();
        var request = Ensure("commit-new", "feature", expected: "") with { IsDefaultBranch = false };
        var waitStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Service.ControlWaitStartedForTesting = () => waitStarted.TrySetResult();

        var post = host.EnsureAsync(request, wait: false);
        await waitStarted.Task.WaitAsync(Answered);
        clock.Advance(Bound);
        using var response = await post.WaitAsync(Answered);

        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var admissionId = body.RootElement.GetProperty("admission_id").GetString();
        Assert.IsFalse(string.IsNullOrEmpty(admissionId));
        var pending = await host.AdmissionStatusAsync(admissionId!);
        Assert.AreEqual(admissionId, pending.GetProperty("admission_id").GetString());
        Assert.AreEqual("ensure", pending.GetProperty("kind").GetString());
        host.ReleaseWriter();
    }

    [TestMethod]
    public async Task Retire_CallerGoneWhileQueued_StillApplies()
    {
        await using var host = await Harness.StartAsync();
        await host.SeedAsync();
        await host.HoldWriterAsync();

        using var gone = new CancellationTokenSource();
        var admitted = host.Service.WriteAdmissions;
        var post = host.RetireAsync("feature", expected: "commit-B", gone.Token);
        await WaitUntilAsync(() => Task.FromResult(host.Service.WriteAdmissions > admitted));
        gone.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => post.WaitAsync(Answered));

        host.ReleaseWriter();
        await WaitUntilAsync(() => Task.FromResult(host.Service.RecentAudit(action: AuditAction.Retire).Count == 1));
        Assert.IsNull(await host.ResolveCommitAsync("feature"));
    }

    [TestMethod]
    public async Task Retire_QueuedBehindAnAdvance_EvaluatesItsCasWhenItApplies()
    {
        // When it was submitted, feature was at B and the CAS would have passed; by the time it applies, the push
        // queued before it has moved feature to C, so it must not retire the branch.
        var clock = new ManualClock();
        await using var host = await Harness.StartAsync(clock);
        await host.SeedAsync();
        await host.HoldWriterAsync();

        Assert.AreEqual(HttpStatusCode.Accepted,
            (await EnsureQueuedAsync(host, clock, Ensure("commit-C", "feature", expected: "commit-B"))).StatusCode);
        var retireWait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Service.RetireWaitStartedForTesting = () => retireWait.TrySetResult();
        var retire = host.RetireAsync("feature", expected: "commit-B");
        await retireWait.Task.WaitAsync(Answered);
        clock.Advance(Bound);
        Assert.AreEqual(HttpStatusCode.Accepted,
            (await retire.WaitAsync(Answered)).StatusCode);

        host.ReleaseWriter();
        await WaitUntilAsync(() => Task.FromResult(host.Service.RecentAudit(action: AuditAction.Retire).Count == 1));
        Assert.AreEqual("commit-C", await host.ResolveCommitAsync("feature"));
        var audit = host.Service.RecentAudit(action: AuditAction.Retire)[0];
        Assert.AreEqual(AuditOutcome.Denied, audit.Outcome);
        Assert.AreEqual(BranchGuardReason.HeadMismatch, audit.Detail);
    }

    [TestMethod]
    public async Task Retire_OfTheDefaultBranch_QueuedWhileBusy_IsNeverApplied()
    {
        await using var host = await Harness.StartAsync();
        await host.SeedAsync();
        await host.HoldWriterAsync();

        Assert.AreEqual(HttpStatusCode.Accepted,
            (await host.RetireAsync("main", expected: "commit-A").WaitAsync(Answered)).StatusCode);

        host.ReleaseWriter();
        await WaitUntilAsync(() => Task.FromResult(host.Service.RecentAudit(action: AuditAction.Retire).Count == 1));
        Assert.AreEqual("commit-A", await host.ResolveCommitAsync("main"));
        var audit = host.Service.RecentAudit(action: AuditAction.Retire)[0];
        Assert.AreEqual(AuditOutcome.Denied, audit.Outcome);
        Assert.AreEqual(BranchGuardReason.DefaultBranch, audit.Detail);
    }

    [TestMethod]
    public async Task PushRetireRecreate_QueuedWhileBusy_ApplyInSubmissionOrder()
    {
        // A push (B -> C), the branch's deletion (before = C) and its re-creation (before = zeros) at D, all while a
        // production holds the writer. Only submission order ends with feature at D: every other order leaves it at
        // C (the retire's CAS fails against B) or absent (the re-create's CAS fails against a still-present branch).
        var clock = new ManualClock();
        await using var host = await Harness.StartAsync(clock);
        await host.SeedAsync();
        await host.HoldWriterAsync();

        Assert.AreEqual(HttpStatusCode.Accepted,
            (await EnsureQueuedAsync(host, clock, Ensure("commit-C", "feature", expected: "commit-B"))).StatusCode);
        var retireWait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Service.RetireWaitStartedForTesting = () => retireWait.TrySetResult();
        var retire = host.RetireAsync("feature", expected: "commit-C");
        await retireWait.Task.WaitAsync(Answered);
        clock.Advance(Bound);
        Assert.AreEqual(HttpStatusCode.Accepted, (await retire.WaitAsync(Answered)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Accepted,
            (await EnsureQueuedAsync(host, clock, Ensure("commit-D", "feature", expected: ""))).StatusCode);

        host.ReleaseWriter();
        await host.QuiesceAsync();
        var recreate = Ensure("commit-D", "feature").ToIdentity().Hash;
        Assert.AreEqual(SnapshotJobStatus.Complete, host.Service.GetStatusByIdentity(recreate)?.Job.Status);
        Assert.AreEqual("commit-D", await host.ResolveCommitAsync("feature"));
        var audit = host.Service.RecentAudit(action: AuditAction.Retire, repositoryScope: Repo);
        Assert.AreEqual(1, audit.Count);
        Assert.AreEqual(AuditOutcome.Complete, audit[0].Outcome);
        Assert.AreEqual("retired", audit[0].Detail, "the retire applied between the push and the re-create");
    }

    [TestMethod]
    public async Task EachQueuedRetireGetsItsOwnIntentAndAudit()
    {
        await using var host = await Harness.StartAsync();
        await host.SeedAsync();
        await host.HoldWriterAsync();

        for (var i = 0; i < 2; i++)
            Assert.AreEqual(HttpStatusCode.Accepted,
                (await host.RetireAsync("feature", expected: "commit-B").WaitAsync(Answered)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Accepted, (await host.RetireAsync("feature").WaitAsync(Answered)).StatusCode);

        host.ReleaseWriter();
        await WaitUntilAsync(() => Task.FromResult(host.Service.RecentAudit(action: AuditAction.Retire, repositoryScope: Repo).Count >= 3));
        await host.QuiesceAsync();
        var audit = host.Service.RecentAudit(action: AuditAction.Retire, repositoryScope: Repo);
        CollectionAssert.AreEqual(new[] { "absent", "absent", "retired" }, audit.Select(a => a.Detail).ToArray());
    }

    [TestMethod]
    public async Task Retire_AfterShutdownBegan_Is503_AndNothingIsQueued()
    {
        await using var host = await Harness.StartAsync();
        await host.SeedAsync();
        host.Service.StopProduction();

        var response = await host.RetireAsync("feature").WaitAsync(Answered);

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using (var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
            Assert.AreEqual("unavailable", json.RootElement.GetProperty("status").GetString());
        Assert.AreEqual("commit-B", await host.ResolveCommitAsync("feature"));
    }

    [TestMethod]
    public async Task QueuedRegistrationAndRetire_LandDuringTheShutdownDrain_TheIdSurvivesARestart()
    {
        await using var host = await Harness.StartAsync();
        await host.SeedAsync();
        await host.HoldWriterAsync();
        var request = Ensure("commit-new");
        var response = await host.EnsureAsync(request, wait: false).WaitAsync(Answered);
        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);
        long jobId;
        using (var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
            jobId = json.RootElement.GetProperty("job_id").GetInt64();
        Assert.AreEqual(HttpStatusCode.Accepted,
            (await host.RetireAsync("feature", expected: "commit-B").WaitAsync(Answered)).StatusCode);

        // Graceful shutdown: the blocker is cancelled and requeued, then the drain lets the queued registration and
        // the queued retire take their turns before the lease is released. Nothing new is produced.
        host.Service.Dispose();
        Assert.IsTrue(host.Service.ProductionDrained);

        using var restarted = SnapshotService.Start(host.Options, host.Worker, host.Db);
        var status = restarted.GetStatus(jobId);
        Assert.IsNotNull(status, "the id handed out while queued is the durable job's id");
        Assert.AreEqual(request.ToIdentity().Hash, status.Job.IdentityHash);
        // Issue #406: the admitted ensure is durable, so the restarted service produces it under the same job id.
        host.ReleaseWriter();
        await WaitUntilAsync(() => Task.FromResult(restarted.GetStatus(jobId)!.Job.Status == SnapshotJobStatus.Complete));
        Assert.IsNull(restarted.ResolveBranch(Repo, "feature"), "the queued retire applied during the drain");
        Assert.IsGreaterThanOrEqualTo(2, host.Worker.Calls);
    }

    private static EnsureSnapshotRequest Ensure(string commit, string? branch = null, string? expected = null) => new()
    {
        RepositoryRemoteUrl = Repo,
        CommitSha = commit,
        BranchName = branch,
        ExpectedHeadCommit = expected
    };

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = Stopwatch.StartNew();
        while (!await Task.Run(condition).WaitAsync(TimeSpan.FromSeconds(10)))
        {
            if (deadline.Elapsed > Settle)
                Assert.Fail("condition not reached within the settle bound");
            await Task.Delay(20);
        }
    }

    private static async Task<HttpResponseMessage> EnsureQueuedAsync(
        Harness host, ManualClock clock, EnsureSnapshotRequest request)
    {
        var waitStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Service.ControlWaitStartedForTesting = () => waitStarted.TrySetResult();
        var post = host.EnsureAsync(request, wait: false);
        try
        {
            await waitStarted.Task.WaitAsync(Answered);
        }
        catch (TimeoutException)
        {
            Assert.Fail($"ensure did not reach control wait; response_completed={post.IsCompleted}, " +
                $"admissions={host.Service.WriteAdmissions}, sqlite_waiters={host.Service.WriteLockMetrics.WaitingWriters}");
        }
        clock.Advance(Bound);
        return await post.WaitAsync(Answered);
    }

    private sealed class Harness : IAsyncDisposable
    {
        public HttpClient Client { get; private init; } = null!;
        public SnapshotService Service { get; private init; } = null!;
        public FakeSnapshotWorker Worker { get; private init; } = null!;
        public IndexDatabase Db { get; private init; } = null!;
        public ServiceOptions Options { get; private init; } = null!;
        public long BlockerJobId { get; private set; }
        private WebApplication App { get; init; } = null!;
        private string DbPath { get; init; } = "";

        public static async Task<Harness> StartAsync(
            ManualClock? clock = null, IRemoteDefaultBranchResolver? remoteDefaults = null, bool recordWorkerCommit = false,
            string? terminalStatus = null, string[]? publishedCommits = null)
        {
            var dbPath = ServiceTestFixtures.NewDbPath();
            var db = new IndexDatabase(dbPath);
            db.RunMigrations();
            foreach (var commit in new[] { "commit-A", "commit-B", "commit-C", "commit-D" }.Concat(publishedCommits ?? []))
                ServiceTestFixtures.PublishComplete(db, Ensure(commit), recordCommit: true);

            var options = ServiceTestFixtures.NewOptions(dbPath, controlToken: ControlToken) with { ControlWriteWait = Bound };
            Func<FakeSnapshotWorker, EnsureSnapshotRequest, SnapshotWorkResult>? produce = terminalStatus switch
            {
                SnapshotJobStatus.Failed => (_, _) => SnapshotWorkResult.Failed("test failure"),
                SnapshotJobStatus.Unsupported => (_, _) => SnapshotWorkResult.Unsupported("test unsupported"),
                _ when recordWorkerCommit => (self, request) => SnapshotWorkResult.Complete(
                    ServiceTestFixtures.PublishComplete(self.Database, request, recordCommit: true)),
                _ => null
            };
            var worker = new FakeSnapshotWorker(db, produce) { UseGate = true };
            var service = SnapshotService.Start(options, worker, db, remoteDefaults: remoteDefaults, timeProvider: clock);
            service.EnableWriterTurnGuardForTesting();

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            ServiceApp.RegisterServices(builder, options, service);
            var app = builder.Build();
            ServiceApp.MapEndpoints(app, options);
            await app.StartAsync();

            return new Harness
            {
                Client = app.GetTestClient(), App = app, Service = service, Worker = worker, Db = db, Options = options, DbPath = dbPath
            };
        }

        // main at A (the repository's first branch, so its default) and feature at B, while the writer is free.
        public async Task SeedAsync()
        {
            foreach (var (commit, branch) in new[] { ("commit-A", "main"), ("commit-B", "feature") })
            {
                var response = await EnsureAsync(Ensure(commit, branch, expected: ""), wait: true);
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            }
            Assert.AreEqual("commit-B", await ResolveCommitAsync("feature"));
        }

        // Starts a production of another repository and leaves it holding the single writer until ReleaseWriter.
        public async Task HoldWriterAsync()
        {
            var response = await EnsureAsync(
                new EnsureSnapshotRequest { RepositoryRemoteUrl = BlockerRepo, CommitSha = "blocker-1" }, wait: false);
            Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);
            using (var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
                BlockerJobId = json.RootElement.GetProperty("job_id").GetInt64();
            await WaitUntilAsync(() => Task.FromResult(Worker.Calls == 1));
        }

        public void ReleaseWriter() => Worker.Gate.TrySetResult();

        public SqliteConnection HoldCatalogWriteLock()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = DbPath,
                Pooling = false
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "BEGIN IMMEDIATE;";
            command.ExecuteNonQuery();
            return connection;
        }

        public void BlockJobIdFloor() =>
            Directory.CreateDirectory(JobIdReservations.FloorPathFor(DbPath));

        public void ClearJobIdFloor()
        {
            var path = JobIdReservations.FloorPathFor(DbPath);
            if (Directory.Exists(path))
                Directory.Delete(path);
        }

        public int PendingRefreshCount
        {
            get
            {
                using var connection = Db.OpenReadConnection();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT (SELECT COUNT(*) FROM pending_refreshes)
                         + (SELECT COUNT(*) FROM completed_refresh_admissions);
                    """;
                return Convert.ToInt32(command.ExecuteScalar());
            }
        }

        // Unsettled branch intents plus pending markers and queued ensures: zero once every admission has run.
        public int OpenWorkCount => RefreshCount("""
            SELECT (SELECT COUNT(*) FROM pending_refreshes)
                 + (SELECT COUNT(*) FROM queued_ensures)
                 + (SELECT COUNT(*) FROM branch_intents WHERE state <> 'settled');
            """);

        public int SettledEnsureIntents(string branch) => RefreshCount(
            $"SELECT COUNT(*) FROM branch_intents WHERE kind = 'ensure' AND state = 'settled' AND branch = '{branch}';");

        public int IntentCount(string kind) => RefreshCount(
            $"SELECT COUNT(*) FROM branch_intents WHERE kind = '{kind}';");

        public int ClaimedRefreshCount => RefreshCount(
            "SELECT COUNT(*) FROM pending_refreshes WHERE claimed_at IS NOT NULL;");

        public int CompletedRefreshCount => RefreshCount(
            "SELECT COUNT(*) FROM completed_refresh_admissions;");

        private int RefreshCount(string sql)
        {
            using var connection = Db.OpenReadConnection();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt32(command.ExecuteScalar());
        }

        /// <summary>Takes the single-writer lease from this service (another owner claims it) and waits until it notices.</summary>
        public async Task StealLeaseAsync()
        {
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                   {
                       DataSource = DbPath,
                       Pooling = false
                   }.ToString()))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE writer_lease SET owner_token = 'thief' WHERE id = 1;";
                cmd.ExecuteNonQuery();
            }
            await WaitUntilAsync(() => Task.FromResult(Service.LeaseLost));
        }

        // Waits until every write admitted so far has applied: a retire of an unknown branch takes the next turn.
        public async Task QuiesceAsync()
        {
            var probe = await Service.RetireBranchAsync(
                new RetireBranchRequest { Repository = "https://github.com/acme/none", Branch = "none" }).WaitAsync(Settle);
            Assert.IsFalse(probe.Retired);
        }

        public Task<HttpResponseMessage> EnsureAsync(
            EnsureSnapshotRequest request, bool wait, CancellationToken cancellationToken = default) =>
            PostJsonAsync(wait ? "/control/ensure" : "/control/ensure?wait=false",
                JsonSerializer.Serialize(request, ServiceJson.Options), cancellationToken);

        public Task<HttpResponseMessage> RetireAsync(
            string branch, string? expected = null, CancellationToken cancellationToken = default) =>
            PostJsonAsync("/control/branches/retire", JsonSerializer.Serialize(
                new RetireBranchRequest { Repository = Repo, Branch = branch, ExpectedHeadCommit = expected },
                ServiceJson.Options), cancellationToken);

        public async Task<JsonElement?> StatusAsync(long jobId)
        {
            var response = await GetAsync($"/control/status/{jobId}");
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return json.RootElement.Clone();
        }

        public async Task<JsonElement> AdmissionStatusAsync(string admissionId)
        {
            var response = await GetAsync($"/control/refresh-status/{admissionId}");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return json.RootElement.Clone();
        }

        public async Task<string?> ResolveCommitAsync(string branch)
        {
            var response = await GetAsync(
                $"/control/resolve?repository={Uri.EscapeDataString(Repo)}&branch={Uri.EscapeDataString(branch)}");
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return json.RootElement.GetProperty("commit_sha").GetString();
        }

        private async Task<HttpResponseMessage> GetAsync(string url)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ControlToken);
            return await Client.SendAsync(request);
        }

        private async Task<HttpResponseMessage> PostJsonAsync(string url, string json, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ControlToken);
            return await Client.SendAsync(request, cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            ReleaseWriter();
            Client.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
            Service.Dispose();
            Assert.AreEqual(0, Service.WriterTraceViolationsForTesting,
                "all writer-connection access must stay inside its admitted writer turn");
            SqliteTestDatabase.Delete(DbPath, Db);
        }

        public sealed class ConfirmingRemote : IRemoteDefaultBranchResolver, IRemoteBranchHeadResolver
        {
            public RemoteBranchHeadObservation? ObserveBranchHead(
                string repositoryRemoteUrl, string branch, CancellationToken cancellationToken = default) =>
                ResolveBranchHead(repositoryRemoteUrl, branch, cancellationToken) is { } observed && !string.IsNullOrWhiteSpace(observed)
                    ? new RemoteBranchHeadObservation(observed)
                    : null;

            public string? ResolveDefaultBranch(string repositoryRemoteUrl, CancellationToken cancellationToken) =>
                ResolveDefaultBranch(repositoryRemoteUrl);

            public string? ResolveDefaultBranch(string repositoryRemoteUrl) => "main";

            public string? ResolveBranchHead(
                string repositoryRemoteUrl, string branch, CancellationToken cancellationToken = default) => "refresh-head";
            public RemoteBranchStateObservation? ObserveBranchState(
                string repositoryRemoteUrl, string branch, CancellationToken cancellationToken = default) => new("main", "refresh-head");
        }
    }
}
