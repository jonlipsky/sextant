using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Sextant.Core;
using Sextant.Store;

namespace Sextant.Indexer.Tests;

/// <summary>
/// The live incident end to end through the real indexer and store: a project that names a type from an assembly it
/// does not reference (an interface method's optional parameter) keeps its call sites as CANDIDATE occurrences, and
/// the snapshot's recorded coverage says which project did not bind instead of claiming full health.
/// Drives <see cref="IndexOrchestrator"/> over an in-memory three-project solution (Abs ← Core ← App, where App
/// references only Core), so no MSBuild or restore is involved.
/// </summary>
[TestClass]
public class UnresolvedBindingIndexingTests
{
    private const string EnsureKey = "M:Core.IStore.EnsureAsync(System.String,System.Threading.CancellationToken,Abs.OwnerKind)";
    private const string PublishKey = "M:Core.IStore.PublishAsync(System.String,System.Threading.CancellationToken)";

    // Enough callers to cross BindingHealthBuilder.DegradedMinUnboundNames (two unbound names per method).
    private const int CallerMethods = 20;

    private string _root = null!;
    private string _dbPath = null!;
    private IndexDatabase _db = null!;

    [TestInitialize]
    public void Init()
    {
        _root = Path.Combine(Path.GetTempPath(), $"sextant_unbound_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
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

    private static SnapshotContext Context(
        string commit, SnapshotCoverage? coverage, IReadOnlyDictionary<string, string>? loadIssues = null) => new()
    {
        RepositoryRemoteUrl = "https://github.com/org/unbound",
        CommitSha = commit,
        BranchName = "main",
        Coverage = coverage,
        ProjectLoadIssues = loadIssues
    };

    private static readonly SnapshotCoverage CompleteCoverage = new() { Verdict = SnapshotCoverageVerdict.Complete };

    [TestMethod]
    public async Task UnboundCallSites_AreStoredAsCandidateReferencesAndCallEdges()
    {
        await new IndexOrchestrator(_db, useDocumentExtractor: true)
            .IndexSolutionAsync(BuildSolution(appReferencesAbs: false), snapshotContext: Context("c1", CompleteCoverage));

        var conn = _db.GetConnection();
        var symbols = new SymbolStore(conn);
        var ensure = symbols.GetBySymbolKeyInScope(EnsureKey).Single();
        var publish = symbols.GetBySymbolKeyInScope(PublishKey).Single();

        var ensureRefs = new ReferenceStore(conn).GetBySymbolId(ensure.Id);
        Assert.AreEqual(CallerMethods, ensureRefs.Count, "every call site of EnsureAsync is kept");
        Assert.IsTrue(ensureRefs.All(r => r.IsCandidate), "and marked candidate, because none bound exactly");

        var publishRefs = new ReferenceStore(conn).GetBySymbolId(publish.Id);
        Assert.AreEqual(CallerMethods + 1, publishRefs.Count, "the calls taking the error-typed result are kept too");
        Assert.AreEqual(1, publishRefs.Count(r => !r.IsCandidate), "the plain-string call still binds exactly");

        var callers = new CallGraphStore(conn).GetByCallee(ensure.Id);
        Assert.AreEqual(CallerMethods, callers.Count, "each caller keeps its call-graph edge");
        Assert.IsTrue(callers.All(e => e.IsCandidate));

        var caller = symbols.GetBySymbolKeyInScope(
            "M:App.Controller.Publish0Async(System.String,System.Threading.CancellationToken)").Single();
        CollectionAssert.AreEquivalent(new[] { ensure.Id, publish.Id },
            new CallGraphStore(conn).GetByCaller(caller.Id).Select(e => e.CalleeSymbolId).ToArray(),
            "get_call_hierarchy callees of the caller are no longer empty");
    }

    [TestMethod]
    public async Task BindingHealth_IsRecorded_AndADegradedProjectMakesTheVerdictPartial()
    {
        await new IndexOrchestrator(_db, useDocumentExtractor: true)
            .IndexSolutionAsync(BuildSolution(appReferencesAbs: false), snapshotContext: Context("c2", CompleteCoverage));

        var coverage = RecordedCoverage();
        Assert.AreEqual(SnapshotCoverageVerdict.Partial, coverage.Verdict,
            "results inside a project whose code did not bind may be missing, so the index must not claim complete");
        var binding = coverage.Binding!;
        Assert.AreEqual(1, binding.ProjectsDegraded);
        var app = binding.Projects.Single(p => p.Degraded);
        Assert.AreEqual("src/App/App.csproj", app.Project);
        Assert.IsNull(app.TargetFramework, "a single-target project has no framework to show");
        Assert.IsTrue(app.UnboundNames >= BindingHealthBuilder.DegradedMinUnboundNames, $"unbound: {app.UnboundNames}");
        Assert.IsTrue(app.UnboundInvocations >= CallerMethods, $"unbound invocations: {app.UnboundInvocations}");
        Assert.AreEqual(app.CandidateOccurrences, binding.CandidateOccurrences);
        Assert.IsTrue(binding.CandidateOccurrences >= 4L * CallerMethods, "two references + two call edges per caller");
        Assert.IsFalse(binding.Projects.Any(p => p.Project != "src/App/App.csproj"), "Abs and Core bind cleanly");

        var reason = coverage.Reasons.Single();
        StringAssert.StartsWith(reason,
            "Code in 1 project(s) did not fully compile on the indexer, so references and calls inside them may be " +
            "missing (src/App/App.csproj: ");
        StringAssert.EndsWith(reason, "Calls that failed to bind are kept as candidate matches.");
    }

    [TestMethod]
    public async Task CleanBinding_KeepsTheVerdict_AndRecordsZeroUnbound()
    {
        await new IndexOrchestrator(_db, useDocumentExtractor: true)
            .IndexSolutionAsync(BuildSolution(appReferencesAbs: true), snapshotContext: Context("c3", CompleteCoverage));

        var coverage = RecordedCoverage();
        Assert.AreEqual(SnapshotCoverageVerdict.Complete, coverage.Verdict);
        Assert.AreEqual(0, coverage.Reasons.Count);
        Assert.AreEqual(0L, coverage.Binding!.UnboundNames);
        Assert.AreEqual(0L, coverage.Binding.CandidateOccurrences);
        Assert.AreEqual(0, coverage.Binding.Projects.Count, "only projects with a symptom are listed");
        Assert.IsTrue(coverage.Binding.NamesExamined > 0);

        var conn = _db.GetConnection();
        var ensure = new SymbolStore(conn).GetBySymbolKeyInScope(EnsureKey).Single();
        var refs = new ReferenceStore(conn).GetBySymbolId(ensure.Id);
        Assert.AreEqual(CallerMethods, refs.Count);
        Assert.IsFalse(refs.Any(r => r.IsCandidate), "with the reference present every call site binds exactly");
    }

    [TestMethod]
    public async Task LoadIssues_AreAttachedToTheirProject_EvenWhenItWasNotIndexed()
    {
        var issues = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["src/App/App.csproj"] = "restore: package(s) not found: Private.Pkg",
            ["tools/Gone/Gone.csproj"] = "restore: a package source was unreachable"
        };
        await new IndexOrchestrator(_db, useDocumentExtractor: true)
            .IndexSolutionAsync(BuildSolution(appReferencesAbs: true), snapshotContext: Context("c4", CompleteCoverage, issues));

        var binding = RecordedCoverage().Binding!;
        Assert.AreEqual("restore: package(s) not found: Private.Pkg",
            binding.Projects.Single(p => p.Project == "src/App/App.csproj").LoadIssue);
        var gone = binding.Projects.Single(p => p.Project == "tools/Gone/Gone.csproj");
        Assert.AreEqual("restore: a package source was unreachable", gone.LoadIssue);
        Assert.AreEqual(0L, gone.NamesExamined, "listed for its load issue although nothing of it was indexed");
    }

    [TestMethod]
    public async Task LocalIndexWithoutCoverage_RecordsNothing()
    {
        await new IndexOrchestrator(_db, useDocumentExtractor: true)
            .IndexSolutionAsync(BuildSolution(appReferencesAbs: false), snapshotContext: Context("c5", coverage: null));

        var conn = _db.GetConnection();
        var selected = new SnapshotStore(conn).GetSelectedSnapshotId();
        Assert.IsNotNull(selected);
        Assert.IsNull(new SnapshotCoverageStore(conn).Get(selected.Value), "the local path computes no coverage");
    }

    private SnapshotCoverage RecordedCoverage()
    {
        var conn = _db.GetConnection();
        var selected = new SnapshotStore(conn).GetSelectedSnapshotId();
        Assert.IsNotNull(selected);
        return new SnapshotCoverageStore(conn).Get(selected.Value)!;
    }

    private Solution BuildSolution(bool appReferencesAbs)
    {
        var app = new System.Text.StringBuilder("""
            using System.Threading;
            using System.Threading.Tasks;
            namespace App
            {
                public class Controller
                {
                    private readonly Core.IStore _store;
                    public Controller(Core.IStore store) { _store = store; }
                    public Task PublishPlainAsync(string id, CancellationToken ct) => _store.PublishAsync(id, ct);

            """);
        for (var i = 0; i < CallerMethods; i++)
        {
            app.Append($$"""
                        public async Task Publish{{i}}Async(string name, CancellationToken ct)
                        {
                            var id = await _store.EnsureAsync(name, ct);
                            await _store.PublishAsync(id, ct);
                        }

                """);
        }
        app.Append("    }\n}\n");

        var framework = Framework();
        var workspace = new AdhocWorkspace();
        var absId = ProjectId.CreateNewId();
        var coreId = ProjectId.CreateNewId();
        var appId = ProjectId.CreateNewId();
        var solution = workspace.CurrentSolution;
        solution = AddProject(solution, absId, "Abs", "src/Abs", "OwnerKind.cs",
            "namespace Abs { public enum OwnerKind { User, Team } }", framework);
        solution = AddProject(solution, coreId, "Core", "src/Core", "IStore.cs", """
            using System.Threading;
            using System.Threading.Tasks;
            namespace Core
            {
                public interface IStore
                {
                    Task<string> EnsureAsync(string name, CancellationToken ct, Abs.OwnerKind owner = Abs.OwnerKind.User);
                    Task PublishAsync(string id, CancellationToken ct);
                }
            }
            """, framework);
        solution = AddProject(solution, appId, "App", "src/App", "Controller.cs", app.ToString(), framework);
        solution = solution.AddProjectReference(coreId, new ProjectReference(absId))
            .AddProjectReference(appId, new ProjectReference(coreId));
        if (appReferencesAbs)
            solution = solution.AddProjectReference(appId, new ProjectReference(absId));
        return solution;
    }

    private Solution AddProject(
        Solution solution, ProjectId id, string name, string relativeDir, string fileName, string source,
        IReadOnlyList<MetadataReference> framework)
    {
        var dir = Path.Combine(_root, relativeDir);
        Directory.CreateDirectory(dir);
        var projectPath = Path.Combine(dir, name + ".csproj");
        var sourcePath = Path.Combine(dir, fileName);
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(sourcePath, source);
        return solution
            .AddProject(ProjectInfo.Create(id, VersionStamp.Default, name, name, LanguageNames.CSharp,
                filePath: projectPath, metadataReferences: framework,
                compilationOptions: new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)))
            .AddDocument(DocumentId.CreateNewId(id), fileName, SourceText.From(source), filePath: sourcePath);
    }

    private static MetadataReference[] Framework()
    {
        var runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        return
        [
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Runtime.dll")),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Threading.Tasks.dll")),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Threading.dll")),
        ];
    }
}
