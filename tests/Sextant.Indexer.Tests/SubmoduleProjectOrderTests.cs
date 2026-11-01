using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Sextant.Core;
using Sextant.Indexer;
using Sextant.Store;

namespace Sextant.Indexer.Tests;

/// <summary>
/// Issue #373: which snapshot a submodule project lands in must not depend on its position in the solution. The
/// repository root used to be the first project's git root, so a submodule project listed first made discovery start
/// inside the submodule and every submodule project was registered in the parent. Real nested git repositories on disk.
/// </summary>
[TestClass]
public class SubmoduleProjectOrderTests
{
    private const string ProviderRemote = "https://github.com/org/MixAndMatch";
    private const string ProviderCommit = "8796341f00000000000000000000000000000000";
    private string _root = null!;
    private string _dbPath = null!;
    private IndexDatabase _db = null!;

    [TestInitialize]
    public void Init()
    {
        _root = Path.Combine(Path.GetTempPath(), $"sextant_suborder_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _dbPath = Path.Combine(_root, "index.db");
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteTestDatabase.Delete(_dbPath, _db);
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task WithCheckoutDir_SubmoduleProjectGoesToItsProvider_WhateverItsPosition(bool submoduleFirst)
    {
        var repo = Layout(withSubmodule: true);
        var context = new SnapshotContext
        {
            RepositoryRemoteUrl = "https://github.com/org/parent",
            CommitSha = "c1",
            BranchName = "main",
            CheckoutDir = repo
        };

        await Orchestrator(withSubmodule: true).IndexSolutionAsync(BuildSolution(repo, submoduleFirst), snapshotContext: context);

        AssertSplit();
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task LocalPathWithoutSnapshotContext_SubmoduleProjectGoesToItsProvider_WhateverItsPosition(bool submoduleFirst)
    {
        var repo = Layout(withSubmodule: true);
        Git(repo, "add", "-A");
        Git(repo, "commit", "--quiet", "-m", "c1");

        await Orchestrator(withSubmodule: true).IndexSolutionAsync(BuildSolution(repo, submoduleFirst));

        AssertSplit();
    }

    [TestMethod]
    public async Task RepositoryWithoutSubmodules_KeepsEveryProjectInTheParent_AtItsOwnPath()
    {
        var repo = Layout(withSubmodule: false);
        Git(repo, "add", "-A");
        Git(repo, "commit", "--quiet", "-m", "c1");

        await Orchestrator(withSubmodule: false).IndexSolutionAsync(BuildSolution(repo, submoduleFirst: true));

        Assert.AreEqual(0, ScalarCount("SELECT COUNT(*) FROM snapshots WHERE is_provider = 1;"));
        CollectionAssert.AreEquivalent(
            new[] { "libs/mix/Mix/Mix.csproj", "src/App/App.csproj" },
            ProjectPaths(provider: false));
    }

    private void AssertSplit()
    {
        CollectionAssert.AreEqual(new[] { "src/App/App.csproj" }, ProjectPaths(provider: false),
            "only the repository's own project is in the parent snapshot");
        CollectionAssert.AreEqual(new[] { "Mix/Mix.csproj" }, ProjectPaths(provider: true),
            "the submodule project is in the provider, relative to the submodule");
    }

    private IndexOrchestrator Orchestrator(bool withSubmodule) => new(_db, useDocumentExtractor: true)
    {
        SubmoduleDiscoverer = root => Task.FromResult(
            withSubmodule && Path.GetFullPath(root) == Path.GetFullPath(Path.Combine(_root, "repo"))
                ? new List<SubmoduleInfo> { new() { Path = "libs/mix", CommitSha = ProviderCommit, RemoteUrl = ProviderRemote } }
                : new List<SubmoduleInfo>())
    };

    private string Layout(bool withSubmodule)
    {
        var repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(repo);
        Git(repo, "init", "--quiet", "-b", "main");
        Git(repo, "config", "user.email", "test@example.com");
        Git(repo, "config", "user.name", "Test");
        Git(repo, "config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(repo, "README.md"), "x");
        if (withSubmodule)
        {
            var sub = Path.Combine(repo, "libs", "mix");
            Directory.CreateDirectory(sub);
            Git(sub, "init", "--quiet", "-b", "main");
            File.WriteAllText(Path.Combine(sub, "README.md"), "x");
            Git(sub, "-c", "user.email=t@example.com", "-c", "user.name=T", "-c", "commit.gpgsign=false", "add", "-A");
            Git(sub, "-c", "user.email=t@example.com", "-c", "user.name=T", "-c", "commit.gpgsign=false", "commit", "--quiet", "-m", "sub");
        }
        return repo;
    }

    private Solution BuildSolution(string repo, bool submoduleFirst)
    {
        var solution = new AdhocWorkspace().CurrentSolution;
        Func<Solution, Solution> app = s => AddProject(s, repo, Path.Combine("src", "App"), "App",
            "namespace App { public class Widget { public int Size() => 1; } }");
        Func<Solution, Solution> mix = s => AddProject(s, repo, Path.Combine("libs", "mix", "Mix"), "Mix",
            "namespace Mix { public class Combiner { public int Combine() => 2; } }");
        return submoduleFirst ? app(mix(solution)) : mix(app(solution));
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

    private static void Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.AreEqual(0, p.ExitCode, $"git {string.Join(' ', args)} failed");
    }
}
