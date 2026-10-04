using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Sextant.Core;
using Sextant.Store;

namespace Sextant.Service.Tests;

/// <summary>
/// A realistic service catalog for the agent-sized-output tests: one repository published from a worker
/// checkout under a temp data root (absolute project disk paths, like <c>/data/service/checkouts/app-…</c>),
/// with a submodule project, an interface referenced 259 times across 40 real source files (so
/// hash-gated snippets are emitted, as on the live service), 120 handler classes that implement it and
/// call its method, TODO comments, and a solution mapped to two of its projects. A second, small
/// repository carries a partial coverage verdict.
/// </summary>
internal sealed class AgentOutputFixture : IDisposable
{
    public const string RepoA = "https://github.com/org/app";
    public const string RepoB = "https://github.com/org/other";
    public const string LibRepo = "https://github.com/org/lib";
    public const string CommitA = "0123456789abcdef0123456789abcdef01234567";
    public const string TargetInterface = "global::App.Core.Versioning.IStore";
    public const string TargetMethod = "global::App.Core.Versioning.IStore.Get";
    public const int ReferenceCount = 259;
    public const int FileCount = 40;
    public const int HandlerCount = 120;
    public const string SolutionRelative = "App.slnx";
    public const string OtherCheckoutDirectory = "other-01jz7k3m9q2w8e4r6t5y1u0i9p";
    public const string OtherSolutionRelative = "Other.slnx";

    public required string DataRoot { get; init; }
    public required string CheckoutRoot { get; init; }
    public required long SnapshotA { get; init; }
    public required long SnapshotB { get; init; }

    /// <summary>Repository-relative path of every reference file, in fixture order.</summary>
    public required IReadOnlyList<string> ReferenceFiles { get; init; }

    public static AgentOutputFixture Create(IndexDatabase db)
    {
        var dataRoot = ServiceTestFixtures.NewDataRoot();
        var checkoutRoot = Path.Combine(dataRoot, "checkouts", "app-01jz7k3m9q2w8e4r6t5y1u0i9o");
        Directory.CreateDirectory(checkoutRoot);
        var conn = db.GetConnection();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var snapshots = new SnapshotStore(conn);

        var request = ServiceTestFixtures.Request(repo: RepoA, commit: CommitA);
        var identity = request.ToIdentity();
        var repoId = snapshots.EnsureRepository(RepoA, now);
        var commitId = snapshots.EnsureCommit(repoId, CommitA, null, now);
        var runStore = new IndexRunStore(conn);
        var runId = runStore.BeginRun("full", now,
            IndexProfileDescriptor.Full.ConfigurationHash, IndexProfiles.Deep, (long)IndexFeature.Deep);
        runStore.MarkComplete(runId, now, 1);
        var (snapId, _, _) = snapshots.BeginPending(identity, repoId, commitId, runId, now);

        var core = Project(conn, snapshots, snapId, checkoutRoot, RepoA, "src/App.Core/App.Core.csproj", "src/App.Core/App.Core.csproj", false, now);
        var web = Project(conn, snapshots, snapId, checkoutRoot, RepoA, "src/App.Web/App.Web.csproj", "src/App.Web/App.Web.csproj", false, now);
        var tests = Project(conn, snapshots, snapId, checkoutRoot, RepoA, "tests/App.Tests/App.Tests.csproj", "tests/App.Tests/App.Tests.csproj", true, now);
        // A submodule project: its own remote and repo-relative path, checked out under external/lib.
        var lib = Project(conn, snapshots, snapId, checkoutRoot, LibRepo, "src/Lib/Lib.csproj", "external/lib/src/Lib/Lib.csproj", false, now);

        var ifaceFile = SourceFile(conn, checkoutRoot, core, "src/App.Core/Versioning/IStore.cs", "src/App.Core/Versioning/IStore.cs",
            ["namespace App.Core.Versioning;", "", "public interface IStore", "{", "    object Get(string key);", "}"], now);
        var iface = Symbol(conn, core, TargetInterface, "IStore", SymbolKind.Interface, ifaceFile, 3, 6, now);
        var get = Symbol(conn, core, TargetMethod, "Get", SymbolKind.Method, ifaceFile, 5, 5, now);

        // 40 files across the four projects; 259 references (6 or 7 per file), 3 handlers per file.
        var owners = new[] { (core, "src/App.Core/Stores", "src/App.Core/Stores"), (web, "src/App.Web/Features", "src/App.Web/Features"),
            (tests, "tests/App.Tests/Stores", "tests/App.Tests/Stores"), (lib, "src/Lib/Adapters", "external/lib/src/Lib/Adapters") };
        var files = new List<string>();
        var refsLeft = ReferenceCount;
        var handler = 0;
        for (var f = 0; f < FileCount; f++)
        {
            var (project, storedDir, checkoutDir) = f < 10 ? owners[0] : f < 30 ? owners[1] : f < 38 ? owners[2] : owners[3];
            var refsHere = Math.Min(refsLeft, f < ReferenceCount % FileCount ? ReferenceCount / FileCount + 1 : ReferenceCount / FileCount);
            refsLeft -= refsHere;
            var name = $"VersionedAssetStoreConsumer{f:D2}.cs";
            var lines = new List<string> { "namespace App.Feature;", "" };
            var refLines = new List<int>();
            for (var r = 0; r < refsHere; r++)
            {
                lines.Add($"    private readonly global::App.Core.Versioning.IStore _versionedAssetStore{r} = default!; // dependency {r}");
                refLines.Add(lines.Count);
            }
            var handlerLines = new List<int>();
            for (var h = 0; h < 3; h++)
            {
                lines.Add($"public sealed class Handler{handler + h} : IStore {{ public object Get(string key) => _versionedAssetStore0.Get(key); }}");
                handlerLines.Add(lines.Count);
            }
            lines.Add("// TODO: retire the legacy store adapter");
            var todoLine = lines.Count;
            lines.Add("// HACK: cache the store lookup");
            var hackLine = lines.Count;

            var fv = SourceFile(conn, checkoutRoot, project, $"{storedDir}/{name}", $"{checkoutDir}/{name}", lines, now);
            files.Add($"{checkoutDir}/{name}");
            foreach (var line in refLines)
                Occurrence(conn, project, iface, null, fv, line, ReferenceKind.TypeRef, now);
            foreach (var line in handlerLines)
            {
                var cls = Symbol(conn, project, $"global::App.Feature.Handler{handler}", $"Handler{handler}", SymbolKind.Class, fv, line, line, now);
                var run = Symbol(conn, project, $"global::App.Feature.Handler{handler}.Get", "Get", SymbolKind.Method, fv, line, line, now);
                Relationship(conn, cls, iface, RelationshipKind.Implements, now);
                Occurrence(conn, project, get, run, fv, line, ReferenceKind.Invocation, now);
                handler++;
            }
            Comment(conn, project, fv, todoLine, "TODO", "retire the legacy store adapter", now);
            Comment(conn, project, fv, hackLine, "HACK", "cache the store lookup", now);
        }

        var solutions = new SolutionStore(conn);
        var solutionId = solutions.Upsert(Path.Combine(checkoutRoot, SolutionRelative), "App", now);
        solutions.AddProjectMapping(solutionId, core);
        solutions.AddProjectMapping(solutionId, web);
        File.WriteAllText(Path.Combine(checkoutRoot, SolutionRelative), "<Solution />");

        snapshots.MarkComplete(snapId, now);
        SetDefaultBranch(snapshots, RepoA, snapId, now);

        var snapB = ServiceTestFixtures.PublishComplete(db, ServiceTestFixtures.Request(repo: RepoB, commit: "fedcba9876543210fedcba9876543210fedcba98"), symbolCount: 2);
        new SnapshotCoverageStore(conn).Record(snapB, new SnapshotCoverage
        {
            Verdict = SnapshotCoverageVerdict.Partial,
            Reasons = ["submodule_unpopulated: external/tools"],
            SubmodulesDeclared = 1,
            SubmodulesUnpopulated = 1
        }, now);
        SetDefaultBranch(snapshots, RepoB, snapB, now);

        // RepoB implements RepoA's interface row (another consumer bound to the same target): a read of RepoA
        // must neither list nor count it.
        var otherCheckout = Path.Combine(dataRoot, "checkouts", OtherCheckoutDirectory);
        var other = Project(conn, snapshots, snapB, otherCheckout, RepoB, "src/Other/Other.csproj", "src/Other/Other.csproj", false, now);
        var otherFile = SourceFile(conn, otherCheckout, other, "src/Other/OtherStore.cs", "src/Other/OtherStore.cs",
            ["public sealed class OtherStore : global::App.Core.Versioning.IStore { }"], now);
        var otherStore = Symbol(conn, other, "global::Other.OtherStore", "OtherStore", SymbolKind.Class, otherFile, 1, 1, now);
        Relationship(conn, otherStore, iface, RelationshipKind.Implements, now);
        // RepoB's own solution, a sibling of RepoA's checkout on the worker: a read of RepoA must not be able to name it.
        var otherSolution = solutions.Upsert(Path.Combine(otherCheckout, OtherSolutionRelative), "Other", now);
        solutions.AddProjectMapping(otherSolution, other);

        return new AgentOutputFixture
        {
            DataRoot = dataRoot,
            CheckoutRoot = checkoutRoot,
            SnapshotA = snapId,
            SnapshotB = snapB,
            ReferenceFiles = files
        };
    }

    private static void SetDefaultBranch(SnapshotStore snapshots, string repo, long snapshotId, long now)
    {
        var branch = snapshots.EnsureBranch(snapshots.GetRepositoryId(repo)!.Value, "main", isDefault: true, now);
        snapshots.SetBranchPointer(branch, snapshotId, now);
    }

    private static long Project(
        SqliteConnection conn, SnapshotStore snapshots, long snapshotId, string checkoutRoot, string remote,
        string repoRelative, string checkoutRelative, bool isTest, long now)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO projects (canonical_id, git_remote_url, repo_relative_path, disk_path, assembly_name,
                                  target_framework, is_test_project, last_indexed_at, snapshot_id)
            VALUES (@c, @g, @p, @d, @a, 'net10.0', @t, @now, @snap) RETURNING id;
            """;
        var assembly = Path.GetFileNameWithoutExtension(repoRelative);
        cmd.Parameters.AddWithValue("@c", $"{assembly}:{snapshotId}");
        cmd.Parameters.AddWithValue("@g", remote);
        cmd.Parameters.AddWithValue("@p", repoRelative);
        cmd.Parameters.AddWithValue("@d", Path.Combine(checkoutRoot, checkoutRelative.Replace('/', Path.DirectorySeparatorChar)));
        cmd.Parameters.AddWithValue("@a", assembly);
        cmd.Parameters.AddWithValue("@t", isTest ? 1 : 0);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.Parameters.AddWithValue("@snap", snapshotId);
        var id = (long)cmd.ExecuteScalar()!;
        snapshots.MapProject(snapshotId, id);
        return id;
    }

    // Writes the file into the checkout and records its version with the real SHA-256, so the service's
    // hash-gated SourceContextRetriever serves snippets exactly as it does on a worker.
    private static long SourceFile(
        SqliteConnection conn, string checkoutRoot, long projectId, string storedRelative, string checkoutRelative,
        IEnumerable<string> lines, long now)
    {
        var absolute = Path.Combine(checkoutRoot, checkoutRelative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        var bytes = Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n");
        File.WriteAllBytes(absolute, bytes);

        using var f = conn.CreateCommand();
        f.CommandText = "INSERT INTO files (project_id, repo_relative_path) VALUES (@p, @path) RETURNING id;";
        f.Parameters.AddWithValue("@p", projectId);
        f.Parameters.AddWithValue("@path", storedRelative);
        var fileId = (long)f.ExecuteScalar()!;

        using var fv = conn.CreateCommand();
        fv.CommandText = "INSERT INTO file_versions (file_id, content_hash, last_indexed_at) VALUES (@f, @h, @now) RETURNING id;";
        fv.Parameters.AddWithValue("@f", fileId);
        fv.Parameters.AddWithValue("@h", SHA256.HashData(bytes));
        fv.Parameters.AddWithValue("@now", now);
        return (long)fv.ExecuteScalar()!;
    }

    // The symbol key the indexer would store: a Roslyn documentation ID (every fixture method is Get(string key)).
    private static string DocumentationId(string fqn, SymbolKind kind)
    {
        var name = fqn.StartsWith("global::", StringComparison.Ordinal) ? fqn["global::".Length..] : fqn;
        return kind == SymbolKind.Method ? $"M:{name}(System.String)" : $"T:{name}";
    }

    private static long Symbol(
        SqliteConnection conn, long projectId, string fqn, string name, SymbolKind kind, long fileVersionId,
        int lineStart, int lineEnd, long now)
    {
        using var s = conn.CreateCommand();
        s.CommandText = """
            INSERT INTO symbols (project_id, symbol_key, fully_qualified_name, display_name, kind, accessibility,
                                 file_version_id, line_start, line_end, last_indexed_at)
            VALUES (@p, @key, @fqn, @name, @kind, 0, @fv, @ls, @le, @now) RETURNING id;
            """;
        s.Parameters.AddWithValue("@p", projectId);
        s.Parameters.AddWithValue("@key", DocumentationId(fqn, kind));
        s.Parameters.AddWithValue("@fqn", fqn);
        s.Parameters.AddWithValue("@name", name);
        s.Parameters.AddWithValue("@kind", (int)kind);
        s.Parameters.AddWithValue("@fv", fileVersionId);
        s.Parameters.AddWithValue("@ls", lineStart);
        s.Parameters.AddWithValue("@le", lineEnd);
        s.Parameters.AddWithValue("@now", now);
        return (long)s.ExecuteScalar()!;
    }

    private static void Occurrence(
        SqliteConnection conn, long inProjectId, long targetId, long? sourceId, long fileVersionId, int line,
        ReferenceKind kind, long now)
    {
        using var o = conn.CreateCommand();
        o.CommandText = """
            INSERT INTO occurrences (in_project_id, target_symbol_id, source_symbol_id, file_version_id, line, col, kind, flags, last_indexed_at)
            VALUES (@p, @t, @s, @fv, @line, 5, @kind, 0, @now);
            """;
        o.Parameters.AddWithValue("@p", inProjectId);
        o.Parameters.AddWithValue("@t", targetId);
        o.Parameters.AddWithValue("@s", (object?)sourceId ?? DBNull.Value);
        o.Parameters.AddWithValue("@fv", fileVersionId);
        o.Parameters.AddWithValue("@line", line);
        o.Parameters.AddWithValue("@kind", (int)kind);
        o.Parameters.AddWithValue("@now", now);
        o.ExecuteNonQuery();
    }

    private static void Relationship(SqliteConnection conn, long from, long to, RelationshipKind kind, long now)
    {
        using var r = conn.CreateCommand();
        r.CommandText = "INSERT INTO relationships (from_symbol_id, to_symbol_id, kind, last_indexed_at) VALUES (@f, @t, @k, @now);";
        r.Parameters.AddWithValue("@f", from);
        r.Parameters.AddWithValue("@t", to);
        r.Parameters.AddWithValue("@k", (int)kind);
        r.Parameters.AddWithValue("@now", now);
        r.ExecuteNonQuery();
    }

    private static void Comment(SqliteConnection conn, long projectId, long fileVersionId, int line, string tag, string text, long now)
    {
        using var c = conn.CreateCommand();
        c.CommandText = """
            INSERT INTO comments (project_id, file_version_id, line, tag, text, last_indexed_at)
            VALUES (@p, @fv, @line, @tag, @text, @now);
            """;
        c.Parameters.AddWithValue("@p", projectId);
        c.Parameters.AddWithValue("@fv", fileVersionId);
        c.Parameters.AddWithValue("@line", line);
        c.Parameters.AddWithValue("@tag", tag);
        c.Parameters.AddWithValue("@text", text);
        c.Parameters.AddWithValue("@now", now);
        c.ExecuteNonQuery();
    }

    public void Dispose()
    {
        try { Directory.Delete(DataRoot, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
