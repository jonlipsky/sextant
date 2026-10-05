using Microsoft.Data.Sqlite;
using Sextant.Core;
using Sextant.Indexer;
using Sextant.Service;
using Sextant.Service.Restore;
using Sextant.Service.Sandbox;
using Sextant.Store;

namespace Sextant.Integration.Tests;

/// <summary>
/// Issue #245 end-to-end: a checkout too large to index inside the sandbox's time budget is published PARTIAL,
/// with what was not done recorded as coverage gaps, instead of the evaluation being aborted with nothing.
/// Driven through the REAL <see cref="PersistentVolumeCheckoutProvider"/>, the REAL
/// <see cref="LocalIndexerSnapshotWorker"/> and the REAL <see cref="MultiSolutionLoader"/> over a real MSBuild
/// toolchain. Only the plan's time is simulated: a hand-driven clock moves when the worker logs a project load or
/// a project's symbol extraction, so the deadlines pass at exactly the same point on every machine. The hard
/// abort is exercised with the REAL <see cref="EvaluationSandbox"/> in real time.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public sealed class TimeBudgetSnapshotIntegrationTests : IDisposable
{
    private const string RemoteUrl = "https://example.invalid/org/budget-monorepo.git";

    private readonly string _dataRoot;

    public TimeBudgetSnapshotIntegrationTests()
    {
        _ = IntegrationFixture.Instance; // ensure MSBuildLocator is registered before any Roslyn type loads
        _dataRoot = Path.Combine(Path.GetTempPath(), $"sextant_budget_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataRoot);
    }

    public void Dispose() => SqliteTestDatabase.DeleteDirectory(_dataRoot);

    [TestMethod]
    public async Task BudgetRunsOut_DuringTheLoadAndTheSymbols_PublishesPartial_NamingTheUnfinishedSolutions()
    {
        // A 1000 s budget: no restore here, so the load may run until +405 s (45% of the 900 s before the
        // extraction deadline). Each project load takes 150 s, so the fourth is never opened. Symbols start at
        // +450 s with a deadline of +630 s (40% of the 450 s left); P2's symbols take 200 s, so P3 gets none.
        var (result, db, log) = await Produce(advance: line =>
            line.StartsWith("Loading project ", StringComparison.Ordinal) ? 150
            : line == "  P2..." ? 200
            : 0);
        using (db)
        {
            Assert.AreEqual(SnapshotJobStatus.Partial, result.Status, result.Error + "\n" + string.Join("\n", log));
            Assert.IsNotNull(result.SnapshotId, "what was indexed is published");
            StringAssert.StartsWith(result.Error, "snapshot coverage is partial: The indexing time budget (1000 s) ran out");

            var coverage = new SnapshotCoverageStore(db.GetConnection()).Get(result.SnapshotId!.Value);
            Assert.IsNotNull(coverage);
            Assert.AreEqual(SnapshotCoverageVerdict.Partial, coverage.Verdict);
            Assert.IsNotNull(coverage.TimeBudget, "the gap is recorded durably with the snapshot");
            Assert.AreEqual(1000, coverage.TimeBudget.BudgetSeconds);
            Assert.AreEqual(1, coverage.TimeBudget.ProjectsNotLoaded);
            Assert.AreEqual(1, coverage.TimeBudget.ProjectsNotIndexed);
            Assert.AreEqual(0, coverage.TimeBudget.ProjectsNotFullyExtracted);
            CollectionAssert.AreEqual(new[] { "S3.slnx", "S4.slnx" }, coverage.TimeBudget.UnfinishedSolutions.ToArray());
            StringAssert.StartsWith(coverage.Reasons[0], "The indexing time budget (1000 s) ran out before the whole checkout was indexed: 2 of 4 selected solution(s) are unfinished (S3.slnx, S4.slnx)");
            Assert.AreEqual(0, coverage.ProjectsSkipped, "a project the budget left out is not reported as unloadable");

            var exhausted = result.Projects.Single(p => p.Code == LocalIndexerSnapshotWorker.TimeBudgetExhaustedCode);
            Assert.AreEqual(JobDiagnosticSeverity.Warning, exhausted.Severity);
            CollectionAssert.AreEqual(
                new[] { "S3.slnx", "S4.slnx" },
                result.Projects.Where(p => p.Code == LocalIndexerSnapshotWorker.SolutionDeferredCode).Select(p => p.ProjectPath).ToArray());

            var conn = db.GetConnection();
            var snapshotId = result.SnapshotId.Value;
            Assert.IsTrue(SymbolCount(conn, snapshotId, "P1") > 0);
            Assert.IsTrue(SymbolCount(conn, snapshotId, "P2") > 0);
            Assert.AreEqual(0, SymbolCount(conn, snapshotId, "P3"), "P3 is registered but has no symbols");
            Assert.AreEqual(1, ProjectCount(conn, snapshotId, "P3"));
            Assert.AreEqual(0, ProjectCount(conn, snapshotId, "P4"), "P4 was never loaded");
        }
    }

    [TestMethod]
    public async Task BudgetNotReached_IsComplete_WithNoTimeBudgetGap()
    {
        var (result, db, log) = await Produce(advance: _ => 0);
        using (db)
        {
            Assert.AreEqual(SnapshotJobStatus.Complete, result.Status, result.Error + "\n" + string.Join("\n", log));
            var coverage = new SnapshotCoverageStore(db.GetConnection()).Get(result.SnapshotId!.Value);
            Assert.IsNotNull(coverage);
            Assert.AreEqual(SnapshotCoverageVerdict.Complete, coverage.Verdict, string.Join(" ", coverage.Reasons));
            Assert.IsNull(coverage.TimeBudget);
            Assert.IsFalse(result.Projects.Any(p => p.Code is LocalIndexerSnapshotWorker.TimeBudgetExhaustedCode
                or LocalIndexerSnapshotWorker.SolutionDeferredCode));
            for (var i = 1; i <= 4; i++)
                Assert.IsTrue(SymbolCount(db.GetConnection(), result.SnapshotId.Value, $"P{i}") > 0, $"P{i}");
        }
    }

    [TestMethod]
    public async Task OneStepRunsPastTheWholeBudget_TheSandboxStillAborts_AndNothingIsPublished()
    {
        // The REAL sandbox enforces a 10 s budget in real time, while the plan's clock stays frozen, so no phase
        // deadline ever passes. P1's symbol extraction then blocks for longer than the whole budget, like one
        // project that takes too long to compile: the phase deadlines cannot interrupt a step already running,
        // so the sandbox's abort is what bounds it.
        var budget = TimeSpan.FromSeconds(10);
        var policy = SandboxPolicy.Enforced with { TimeBudget = budget, MemoryBudgetBytes = 0 };
        var (result, db, log) = await Produce(
            advance: line =>
            {
                if (line == "  P1...")
                    Thread.Sleep(budget + TimeSpan.FromSeconds(2));
                return 0;
            },
            sandbox: (paths, logLine) => new EvaluationSandbox(policy, paths, logLine));
        using (db)
        {
            Assert.AreEqual(SnapshotJobStatus.Failed, result.Status, result.Error + "\n" + string.Join("\n", log));
            Assert.AreEqual("untrusted repository evaluation exceeded its time budget and was aborted.", result.Error);
            Assert.IsNull(result.SnapshotId);
            Assert.AreEqual(EvaluationBudgetPolicy.Token(policy),
                result.Projects.Single(p => p.Code == EvaluationBudgetPolicy.PolicyCode).Message,
                "the aborting policy is recorded, so the job is retried only after the policy changes");
            Assert.AreEqual(JobDiagnosticSeverity.Error,
                result.Projects.Single(p => p.Code == EvaluationBudgetPolicy.ExceededCode).Severity);
            Assert.IsFalse(result.Projects.Any(p => p.Code == LocalIndexerSnapshotWorker.TimeBudgetExhaustedCode),
                "a hard abort is never reported as a partial snapshot");
            Assert.AreEqual(0, ScalarInt(db.GetConnection(),
                "SELECT COUNT(*) FROM snapshots WHERE status = 'complete';"),
                "an aborted evaluation publishes nothing");
        }
    }

    [TestMethod]
    public async Task ThePlansRestoreShare_BoundsTheRestore()
    {
        // A 4-tick budget leaves the restore no time (20% of 4 ticks rounds to zero), while the frozen clock keeps
        // every later deadline ahead. The worker must hand the restore that limit: no solution is restored, and
        // the restore is reported stopped. The host is missing, so a restore that WAS started would instead be
        // reported as not started (what an ignored limit looks like), never run.
        var restore = new PackageRestoreRunner(timeout: TimeSpan.FromSeconds(300))
        {
            DotnetPath = Path.Combine(_dataRoot, "no-dotnet", "dotnet")
        };
        var (result, db, log) = await Produce(
            advance: _ => 0,
            budget: TimeSpan.FromTicks(4),
            packageRestore: restore);
        using (db)
        {
            var notes = result.Projects
                .Where(p => p.Code == LocalIndexerSnapshotWorker.PackageRestoreIncompleteCode)
                .Select(p => p.Message)
                .ToList();
            Assert.AreEqual(1, notes.Count, string.Join("\n", notes) + "\n" + string.Join("\n", log));
            StringAssert.StartsWith(notes[0], "Package restore did not finish within 0s and was stopped");
            Assert.IsNotNull(result.SnapshotId, result.Error + "\n" + string.Join("\n", log));
        }
    }

    // Runs the real worker over a four-solution checkout under a 1000 s budget (or the given sandbox), advancing
    // the plan's clock by advance(line) seconds whenever the worker logs a line.
    private async Task<(SnapshotWorkResult Result, IndexDatabase Db, List<string> Log)> Produce(
        Func<string, int> advance,
        Func<ServicePaths, Action<string>, IEvaluationSandbox>? sandbox = null,
        TimeSpan? budget = null,
        PackageRestoreRunner? packageRestore = null)
    {
        var paths = new ServicePaths(ServiceVolumes.Rooted(_dataRoot));
        CreateCheckout(paths);
        var provider = new PersistentVolumeCheckoutProvider(paths);
        var request = new EnsureSnapshotRequest
        {
            RepositoryRemoteUrl = RemoteUrl,
            CommitSha = new string('c', 40),
            TreeSha = new string('d', 40)
        };
        var clock = new ManualClock();
        var log = new List<string>();
        void Log(string line)
        {
            lock (log)
                log.Add(line);
            if (advance(line) is var seconds and > 0)
                clock.Advance(TimeSpan.FromSeconds(seconds));
        }

        var db = new IndexDatabase(Path.Combine(_dataRoot, "catalog.db"), IndexWriteOptions.Default);
        db.RunMigrations();
        var config = new SextantConfiguration();
        packageRestore ??= new PackageRestoreRunner(enabled: false);
        var identityHash = request.ToIdentity(
            IndexProfileDescriptor.FromConfiguration(config).ConfigurationHash, null,
            restorePolicy: packageRestore.IdentityComponent).Hash;
        var worker = new LocalIndexerSnapshotWorker(
            db, config, provider, Log, capability: null,
            sandbox: sandbox?.Invoke(paths, Log) ?? new BudgetOnlySandbox(budget ?? TimeSpan.FromSeconds(1000)),
            packageRestore: packageRestore, clock: clock);
        var result = await worker.ProduceAsync(request, identityHash, paths.AllocateScratch("job-1"), CancellationToken.None);
        return (result, db, log);
    }

    private static int SymbolCount(SqliteConnection conn, long snapshotId, string project) => ScalarInt(conn, $"""
        SELECT COUNT(*) FROM symbols s
        JOIN snapshot_projects sp ON sp.project_id = s.project_id
        JOIN projects p ON p.id = s.project_id
        WHERE sp.snapshot_id = {snapshotId} AND p.repo_relative_path LIKE '%{project}.csproj';
        """);

    private static int ProjectCount(SqliteConnection conn, long snapshotId, string project) => ScalarInt(conn, $"""
        SELECT COUNT(*) FROM snapshot_projects sp
        JOIN projects p ON p.id = sp.project_id
        WHERE sp.snapshot_id = {snapshotId} AND p.repo_relative_path LIKE '%{project}.csproj';
        """);

    private static int ScalarInt(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    // Four solutions at the checkout root, each declaring one project of its own: S1 = {P1} ... S4 = {P4}.
    private static void CreateCheckout(ServicePaths paths)
    {
        var root = Path.Combine(paths.CheckoutRoot, ServicePaths.RepoDirectoryName(RemoteUrl));
        Directory.CreateDirectory(root);
        for (var i = 1; i <= 4; i++)
        {
            Write(root, $"P{i}/P{i}.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
                  </PropertyGroup>
                </Project>
                """);
            Write(root, $"P{i}/Type{i}.cs", $"namespace Budget;\npublic class Type{i} {{ public int Value() => {i}; }}\n");
            Write(root, $"S{i}.slnx", $"<Solution>\n  <Project Path=\"P{i}/P{i}.csproj\" />\n</Solution>\n");
            var restore = BoundedProcess.DotnetRestore(Path.Combine(root, $"P{i}", $"P{i}.csproj"));
            if (!restore.Succeeded)
                Assert.Inconclusive("dotnet restore failed (offline machine?): " + restore.Describe());
        }
    }

    private static void Write(string root, string relative, string content)
    {
        var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    // Reports a time budget, so the worker plans inside it, and runs the evaluation directly; the clock never
    // reaches the budget, so nothing here would abort it.
    private sealed class BudgetOnlySandbox(TimeSpan budget) : IEvaluationSandbox
    {
        public TimeSpan? TimeBudget => budget;

        public string? BudgetPolicyToken => null;

        public Task<T> RunAsync<T>(
            string checkoutDir, string scratchDir, Func<CancellationToken, Task<T>> evaluate, CancellationToken cancellationToken) =>
            evaluate(cancellationToken);
    }
}
