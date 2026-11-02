using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Sextant.Core;
using Sextant.Indexer;
using Sextant.Store;

namespace Sextant.Indexer.Tests;

/// <summary>
/// Issue #387: the same commit content got different verdicts because submodule discovery sometimes returned nothing
/// (a failed git call is reported as "no submodules"), so the parent registered every submodule project as its own.
/// Real git repositories: a parent with a populated submodule that has its own origin, discovered by the real
/// <see cref="SubmoduleDiscovery.DiscoverAsync"/>.
/// </summary>
[TestClass]
public class SubmoduleDiscoveryFailureTests
{
    private string _root = null!;
    private string _repo = null!;
    private string _dbPath = null!;
    private IndexDatabase _db = null!;

    [TestInitialize]
    public void Init()
    {
        _root = Path.Combine(Path.GetTempPath(), $"sextant_subfail_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _dbPath = Path.Combine(_root, "index.db");
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        _repo = Layout();
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteTestDatabase.Delete(_dbPath, _db);
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [TestMethod]
    public async Task NormalPath_AttributesTheSubmoduleProjectToItsProvider()
    {
        var discovered = await SubmoduleDiscovery.DiscoverAsync(_repo);
        Assert.AreEqual(1, discovered.Count, "fixture: the real discoverer sees the populated submodule");

        await new IndexOrchestrator(_db, useDocumentExtractor: true).IndexSolutionAsync(
            BuildSolution(), snapshotContext: Context());

        AssertSplit();
    }

    [TestMethod]
    public async Task DiscoveryWhoseGitFailsOnce_IsRetried_AndGivesTheNormalAttribution()
    {
        var head = Path.Combine(_repo, ".git", "HEAD");
        var good = File.ReadAllText(head);
        var calls = 0;

        // The first attempt runs the real discoverer against a checkout whose HEAD is unreadable (real git failure),
        // then the HEAD is back for the retry.
        var found = await CheckoutSubmoduleDiscovery.DiscoverVerifiedAsync(async (root, onFailure) =>
        {
            if (++calls > 1)
                return await SubmoduleDiscovery.DiscoverAsync(root, onFailure);
            File.WriteAllText(head, "garbage\n");
            try { return await SubmoduleDiscovery.DiscoverAsync(root, onFailure); }
            finally { File.WriteAllText(head, good); }
        }, _repo, CancellationToken.None);

        Assert.AreEqual(2, calls);
        CollectionAssert.AreEqual(new[] { "libs/mix" }, found.Select(f => f.Path).ToArray());
    }

    [TestMethod]
    public async Task APopulatedSubmoduleWithoutAnOriginRemote_IsSkippedAsBefore_NoRetryNoRefusal()
    {
        Git(Path.Combine(_repo, "libs", "mix"), "remote", "remove", "origin");

        var (found, calls) = await RunVerifiedAsync();

        Assert.AreEqual(1, calls, "no git failure, so no retry");
        Assert.AreEqual(0, found.Count);
    }

    [TestMethod]
    public async Task AStaleGitmodulesEntryForAPopulatedDirectory_IsSkippedAsBefore_NoRetryNoRefusal()
    {
        var stale = Path.Combine(_repo, "libs", "old");
        Directory.CreateDirectory(stale);
        Git(stale, "init", "--quiet", "-b", "main");
        File.AppendAllText(Path.Combine(_repo, ".gitmodules"),
            "[submodule \"old\"]\n\tpath = libs/old\n\turl = https://github.com/org/Old.git\n");

        var (found, calls) = await RunVerifiedAsync();

        Assert.AreEqual(1, calls, "git status lists no such submodule and reports no failure");
        CollectionAssert.AreEqual(new[] { "libs/mix" }, found.Select(f => f.Path).ToArray());
    }

    private async Task<(List<SubmoduleInfo> Found, int Calls)> RunVerifiedAsync()
    {
        var calls = 0;
        var found = await CheckoutSubmoduleDiscovery.DiscoverVerifiedAsync(
            (root, onFailure) => { calls++; return SubmoduleDiscovery.DiscoverAsync(root, onFailure); },
            _repo, CancellationToken.None);
        return (found, calls);
    }

    [TestMethod]
    public async Task DiscoveryThatKeepsFailing_RefusesToIndex_InsteadOfAttributingTheProjectsToTheParent()
    {
        // A real git failure: HEAD of the checkout is unreadable, so every git call in it fails.
        File.WriteAllText(Path.Combine(_repo, ".git", "HEAD"), "garbage\n");

        var exception = await Assert.ThrowsExactlyAsync<SubmoduleDiscoveryException>(() =>
            new IndexOrchestrator(_db, useDocumentExtractor: true).IndexSolutionAsync(
                BuildSolution(), snapshotContext: Context()));

        CollectionAssert.AreEqual(new[] { "libs/mix" }, exception.Missing.ToArray());
        StringAssert.Contains(exception.Message, "git submodule status --recursive: exit code ");
        StringAssert.Contains(exception.Message, "not a git repository");
        Assert.AreEqual(0L, ScalarCount("SELECT COUNT(*) FROM snapshot_projects;"), "nothing was published");
    }

    [TestMethod]
    public async Task ACheckoutWithoutSubmodules_IsNotAffectedByTheCheck()
    {
        var plain = Path.Combine(_root, "plain");
        Directory.CreateDirectory(plain);
        Git(plain, "init", "--quiet", "-b", "main");
        File.WriteAllText(Path.Combine(plain, "README.md"), "x");

        var solution = AddProject(new AdhocWorkspace().CurrentSolution, plain, "src", "App",
            "namespace App { public class Widget { public int Size() => 1; } }");
        await new IndexOrchestrator(_db, useDocumentExtractor: true).IndexSolutionAsync(
            solution, snapshotContext: Context(plain));

        CollectionAssert.AreEqual(new[] { "src/App.csproj" }, ProjectPaths(provider: false));
    }

    private void AssertSplit()
    {
        CollectionAssert.AreEqual(new[] { "src/App/App.csproj" }, ProjectPaths(provider: false),
            "only the repository's own project is in the parent snapshot");
        CollectionAssert.AreEqual(new[] { "Mix/Mix.csproj" }, ProjectPaths(provider: true),
            "the submodule project is in the provider, relative to the submodule");
    }

    private SnapshotContext Context(string? checkout = null) => new()
    {
        RepositoryRemoteUrl = "https://github.com/org/parent",
        CommitSha = "c1",
        BranchName = "main",
        CheckoutDir = checkout ?? _repo
    };

    private string Layout()
    {
        var repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(repo);
        Git(repo, "init", "--quiet", "-b", "main");
        Git(repo, "config", "user.email", "test@example.com");
        Git(repo, "config", "user.name", "Test");
        Git(repo, "config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(repo, "README.md"), "x");

        var sub = Path.Combine(repo, "libs", "mix");
        Directory.CreateDirectory(sub);
        Git(sub, "init", "--quiet", "-b", "main");
        Git(sub, "remote", "add", "origin", "https://github.com/org/MixAndMatch.git");
        File.WriteAllText(Path.Combine(sub, "README.md"), "x");
        Git(sub, "-c", "user.email=t@example.com", "-c", "user.name=T", "-c", "commit.gpgsign=false", "add", "-A");
        Git(sub, "-c", "user.email=t@example.com", "-c", "user.name=T", "-c", "commit.gpgsign=false", "commit", "--quiet", "-m", "sub");
        var pin = GitOut(sub, "rev-parse", "HEAD").Trim();

        File.WriteAllText(Path.Combine(repo, ".gitmodules"), "[submodule \"mix\"]\n\tpath = libs/mix\n\turl = https://github.com/org/MixAndMatch.git\n");
        Git(repo, "add", ".gitmodules", "README.md");
        Git(repo, "update-index", "--add", "--cacheinfo", $"160000,{pin},libs/mix");
        Git(repo, "commit", "--quiet", "-m", "c1");
        return repo;
    }

    private Solution BuildSolution()
    {
        var solution = AddProject(new AdhocWorkspace().CurrentSolution, _repo, Path.Combine("libs", "mix", "Mix"), "Mix",
            "namespace Mix { public class Combiner { public int Combine() => 2; } }");
        return AddProject(solution, _repo, Path.Combine("src", "App"), "App",
            "namespace App { public class Widget { public int Size() => 1; } }");
    }

    private static Solution AddProject(Solution solution, string repo, string relativeDir, string name, string source)
    {
        var projectDir = Path.Combine(repo, relativeDir);
        Directory.CreateDirectory(projectDir);
        var projectPath = Path.Combine(projectDir, $"{name}.csproj");
        var sourcePath = Path.Combine(projectDir, $"{name}.cs");
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(sourcePath, source);
        var projectId = ProjectId.CreateNewId();
        return solution
            .AddProject(ProjectInfo.Create(projectId, VersionStamp.Default, name, name, LanguageNames.CSharp,
                filePath: projectPath,
                metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]))
            .AddDocument(DocumentId.CreateNewId(projectId), $"{name}.cs", SourceText.From(source), filePath: sourcePath);
    }

    private List<string> ProjectPaths(bool provider)
    {
        using var cmd = _db.GetConnection().CreateCommand();
        cmd.CommandText = """
            SELECT lp.repo_relative_path FROM snapshot_projects sp
            JOIN snapshots s ON s.id = sp.snapshot_id
            JOIN projects p ON p.id = sp.project_id
            JOIN logical_projects lp ON lp.id = p.logical_project_id
            WHERE s.is_provider = @p ORDER BY lp.repo_relative_path;
            """;
        cmd.Parameters.AddWithValue("@p", provider ? 1 : 0);
        using var reader = cmd.ExecuteReader();
        var paths = new List<string>();
        while (reader.Read()) paths.Add(reader.GetString(0));
        return paths;
    }

    private long ScalarCount(string sql)
    {
        using var cmd = _db.GetConnection().CreateCommand();
        cmd.CommandText = sql;
        return (long)cmd.ExecuteScalar()!;
    }

    private static void Git(string dir, params string[] args) => GitOut(dir, args);

    private static string GitOut(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        Assert.AreEqual(0, p.ExitCode, $"git {string.Join(' ', args)} failed: {stderr.Result}");
        return stdout.Result;
    }
}
