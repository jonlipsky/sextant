using System.Security.Cryptography;
using System.Text;
using Sextant.Core;
using Sextant.Store;

namespace Sextant.Store.Tests;

/// <summary>Issue #336: the source-text sweep resumes where the previous slice stopped and wraps after the last shard.</summary>
[TestClass]
public class SourceTextSweepCursorTests
{
    private string _root = null!;
    private string _dbPath = null!;
    private IndexDatabase _db = null!;

    [TestInitialize]
    public void Init()
    {
        _root = Path.Combine(Path.GetTempPath(), $"sextant_sweepcur_{Guid.NewGuid():N}");
        _dbPath = Path.Combine(Path.GetTempPath(), $"sextant_sweepcur_{Guid.NewGuid():N}.db");
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteTestDatabase.Delete(_dbPath, _db);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public void EveryBlobAndTempFile_LivesInAShardDirectory_SoTheShardWalkSkipsNothing()
    {
        var store = new SourceTextStore(_root);
        var good = Encoding.UTF8.GetBytes("class Good { }");
        var corrupt = Encoding.UTF8.GetBytes("class Corrupt { }");
        store.Put(SHA256.HashData(good), good);
        store.Put(SHA256.HashData(corrupt), corrupt);
        // A corrupt blob is moved aside to a temp name by TryGet; an abandoned Put temp sits next to its blob.
        File.WriteAllText(Path.Combine(_root, Convert.ToHexStringLower(SHA256.HashData(corrupt))[..2],
            Convert.ToHexStringLower(SHA256.HashData(corrupt)) + ".br"), "not brotli");
        Assert.IsNull(store.TryGet(SHA256.HashData(corrupt)));
        var hex = Convert.ToHexStringLower(SHA256.HashData(good));
        File.WriteAllText(Path.Combine(_root, hex[..2], $"{hex}.{Guid.NewGuid():N}.tmp"), "partial");

        var all = Directory.GetFiles(_root, "*", SearchOption.AllDirectories);
        Assert.IsTrue(all.Length >= 2);
        Assert.IsTrue(all.All(f => Path.GetDirectoryName(f) is { } d && Path.GetDirectoryName(d) == Path.GetFullPath(_root).TrimEnd(Path.DirectorySeparatorChar)
                                   && Path.GetFileName(d).Length == 2));

        var deleted = store.DeleteUnreferenced(new HashSet<UInt128>(), 1000, () => false, out var reason, new SourceTextSweepCursor());
        Assert.IsNull(reason);
        Assert.AreEqual(1, deleted, "the one intact blob; temp files are removed too but not counted");
        Assert.AreEqual(0, Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Length,
            "a shard-only walk reaches every file the recursive walk would");
    }

    [TestMethod]
    public void TinyBudgetSlices_VisitEveryShard_AndDeleteOrphansInLateShards()
    {
        var store = new SourceTextStore(_root);
        var blobs = Seed(store, 60);
        var cursor = new SourceTextSweepCursor();
        var slices = 0;

        string? reason;
        do
        {
            var handled = 0;
            store.DeleteUnreferenced(new HashSet<UInt128>(), 1000, () => handled++ >= 5, out reason, cursor);
            slices++;
            Assert.IsTrue(slices < 100, "the walk must make progress every slice");
        } while (reason != null);

        Assert.IsTrue(slices > 1, "the store is larger than one slice");
        Assert.IsFalse(blobs.Any(File.Exists), "every orphan was deleted, including those in late shards");
        Assert.IsNull(cursor.Position, "a finished cycle wraps the cursor");
    }

    [TestMethod]
    public void Cursor_ResumesAfterTheLastHandledBlob_AndWrapsToTheFirstShard()
    {
        var store = new SourceTextStore(_root);
        var blobs = Seed(store, 20).OrderBy(p => Rel(p), StringComparer.Ordinal).ToList();
        var cursor = new SourceTextSweepCursor();

        var handled = 0;
        store.DeleteUnreferenced(new HashSet<UInt128>(), 1000, () => handled++ >= 8, out var reason, cursor);

        Assert.AreEqual("time_budget", reason);
        Assert.AreEqual(Rel(blobs[7]), cursor.Position, "the cursor is the last blob handled, in ordinal order");
        Assert.IsTrue(blobs.Take(8).All(b => !File.Exists(b)));
        Assert.IsTrue(blobs.Skip(8).All(File.Exists), "nothing past the cursor was touched");

        // A new orphan BEFORE the cursor is left for the next cycle; the wrap then finds it.
        var early = blobs[0];
        File.WriteAllText(early, "x");
        store.DeleteUnreferenced(new HashSet<UInt128>(), 1000, () => false, out reason, cursor);
        Assert.IsNull(reason);
        Assert.IsNull(cursor.Position, "reaching the end wraps");
        Assert.IsTrue(File.Exists(early), "a blob behind the cursor waits for the next cycle");

        store.DeleteUnreferenced(new HashSet<UInt128>(), 1000, () => false, out reason, cursor);
        Assert.IsFalse(File.Exists(early));
    }

    [TestMethod]
    public void Cursor_SurvivesAcrossRetentionServiceInstances_AndCapStopsBeforeTheOrphan()
    {
        var store = new SourceTextStore(_root);
        var blobs = Seed(store, 12).OrderBy(p => Rel(p), StringComparer.Ordinal).ToList();
        var cursor = new SourceTextSweepCursor();
        var keys = new HashSet<UInt128>();

        Assert.AreEqual(5, store.DeleteUnreferenced(keys, 5, () => false, out var reason, cursor));
        Assert.AreEqual("source_text_cap", reason);
        Assert.AreEqual(Rel(blobs[4]), cursor.Position, "the orphan that hit the cap is still ahead of the cursor");

        Assert.AreEqual(7, store.DeleteUnreferenced(keys, 100, () => false, out reason, cursor));
        Assert.IsNull(reason);
    }

    [TestMethod]
    public void FullCleanCycle_ReportsNothingRemaining_AndNoStopReason()
    {
        var store = new SourceTextStore(_root);
        Seed(store, 15);
        var cursor = new SourceTextSweepCursor();
        var service = new RetentionService(_db.GetConnection(), new RetentionPolicy());
        var limit = TimeSpan.FromSeconds(30);

        var first = service.SweepSourceTexts(store, CancellationToken.None, limit, cursor: cursor);
        Assert.AreEqual(15, first.SourceTextsDeleted);
        Assert.IsFalse(first.MoreRemaining);
        Assert.IsNull(first.StopReason);
        Assert.IsTrue(first.SourceTextSweepAdvanced);

        var clean = service.SweepSourceTexts(store, CancellationToken.None, limit, cursor: cursor);
        Assert.AreEqual(0, clean.SourceTextsDeleted);
        Assert.IsFalse(clean.MoreRemaining);
        Assert.IsNull(clean.StopReason);
    }

    [TestMethod]
    public void ABlobStoredAfterTheKeySetWasBuilt_IsNeverDeleted_BecauseEverySliceRebuildsTheKeys()
    {
        var store = new SourceTextStore(_root);
        var blobs = Seed(store, 20).OrderBy(p => Rel(p), StringComparer.Ordinal).ToList();
        var cursor = new SourceTextSweepCursor();
        var handled = 0;
        store.DeleteUnreferenced(new HashSet<UInt128>(), 1000, () => handled++ >= 5, out _, cursor);

        // An index job lands the last blob's file_versions row between two writer turns.
        var late = blobs[^1];
        var conn = _db.GetConnection();
        var projectId = new ProjectStore(conn).Insert(new Sextant.Core.ProjectIdentity
        {
            CanonicalId = "sweepcursor00001", GitRemoteUrl = "https://github.com/o/r", RepoRelativePath = "a.csproj",
            DiskPath = "a.csproj", TargetFramework = "net10.0"
        }, 1);
        new FileStore(conn).ResolveFileVersionId(projectId, "src/Late.cs", Convert.FromHexString(Path.GetFileNameWithoutExtension(late)));

        var report = new RetentionService(conn, new RetentionPolicy())
            .SweepSourceTexts(store, CancellationToken.None, TimeSpan.FromSeconds(30), cursor: cursor);

        Assert.IsFalse(report.MoreRemaining);
        Assert.IsTrue(File.Exists(late), "the referenced blob survives");
        Assert.AreEqual(14, report.SourceTextsDeleted, "the other orphans past the cursor go");
    }

    [TestMethod]
    public void AnInterruptThatNoBudgetAskedFor_PropagatesInsteadOfBeingReportedAsABudgetStop()
    {
        var store = new SourceTextStore(_root);
        Seed(store, 3);
        store.EnumerateOverride = _ => ThrowInterrupt();
        var service = new RetentionService(_db.GetConnection(), new RetentionPolicy());

        var ex = Assert.ThrowsExactly<Microsoft.Data.Sqlite.SqliteException>(
            () => service.SweepSourceTexts(store, CancellationToken.None, TimeSpan.FromSeconds(30), cursor: new SourceTextSweepCursor()));
        Assert.AreEqual(9, ex.SqliteErrorCode);
    }

    private static IEnumerable<string> ThrowInterrupt()
    {
        throw new Microsoft.Data.Sqlite.SqliteException("interrupted", 9);
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    [TestMethod]
    public void AKeyBuildThatRunsOutOfItsBudget_StopsWithItsOwnReason_AndIsNotProgress()
    {
        var store = new SourceTextStore(_root);
        var blobs = Seed(store, 5);
        var cursor = new SourceTextSweepCursor();
        var service = new RetentionService(_db.GetConnection(), new RetentionPolicy());

        var report = service.SweepSourceTexts(store, CancellationToken.None, TimeSpan.FromSeconds(30), TimeSpan.Zero, cursor);

        Assert.AreEqual("source_text_keys_budget", report.StopReason);
        Assert.IsTrue(report.MoreRemaining);
        Assert.IsFalse(report.SourceTextSweepAdvanced);
        Assert.AreEqual(0, report.SourceTextsDeleted ?? 0);
        Assert.IsNull(cursor.Position);
        Assert.IsTrue(blobs.All(File.Exists));
    }

    [TestMethod]
    public void AnIoErrorPartWayThroughAShard_MovesTheCursorPastThatShard_AndKeepsReportingTheError()
    {
        var store = new SourceTextStore(_root);
        var blobs = Seed(store, 40).OrderBy(p => Rel(p), StringComparer.Ordinal).ToList();
        var cursor = new SourceTextSweepCursor();
        var failing = blobs[10];
        var failingShard = Rel(failing).Split('/')[0];
        store.EnumerateOverride = _ => FailAfter(blobs.Take(11));

        store.DeleteUnreferenced(new HashSet<UInt128>(), 1000, () => false, out var reason, cursor);

        Assert.AreEqual("source_text_error", reason);
        Assert.AreEqual(failingShard + "/\uffff", cursor.Position);

        store.EnumerateOverride = null;
        store.DeleteUnreferenced(new HashSet<UInt128>(), 1000, () => false, out reason, cursor);
        Assert.IsNull(reason, "the next slice does not stop at the same blob");
        Assert.IsTrue(blobs.Where(b => string.CompareOrdinal(Rel(b).Split('/')[0], failingShard) > 0).All(b => !File.Exists(b)),
            "every shard after the failing one was swept");
        Assert.IsNull(cursor.Position);
    }

    private static IEnumerable<string> FailAfter(IEnumerable<string> paths)
    {
        foreach (var p in paths) yield return p;
        throw new IOException("disk error");
    }

    private string Rel(string path) => Path.GetRelativePath(_root, path).Replace('\\', '/');

    private List<string> Seed(SourceTextStore store, int count)
    {
        var paths = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var content = Encoding.UTF8.GetBytes($"class Orphan{i} {{ }}");
            var hash = SHA256.HashData(content);
            store.Put(hash, content);
            var hex = Convert.ToHexStringLower(hash);
            paths.Add(Path.Combine(_root, hex[..2], hex + ".br"));
        }
        return paths;
    }
}
