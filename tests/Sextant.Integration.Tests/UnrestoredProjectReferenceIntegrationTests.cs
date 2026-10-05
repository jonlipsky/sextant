using Microsoft.CodeAnalysis;
using Sextant.Core;
using Sextant.Indexer;
using Sextant.Store;

namespace Sextant.Integration.Tests;

/// <summary>
/// The live incident's load shape through the REAL MSBuild loaders, with no <c>dotnet restore</c>: an unrestored
/// SDK project gets only its DIRECT ProjectReferences from the design-time build, so a type it reaches through
/// another project (here an optional parameter's enum) does not bind and every call to that method would be
/// dropped. Both loaders must close the transitive ProjectReference graph so the calls bind exactly.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public sealed class UnrestoredProjectReferenceIntegrationTests : IDisposable
{
    private const string EnsureKey = "M:Core.IStore.EnsureAsync(System.String,System.Threading.CancellationToken,Abs.OwnerKind)";

    private readonly string _tempDir;

    public UnrestoredProjectReferenceIntegrationTests()
    {
        _ = IntegrationFixture.Instance; // MSBuildLocator registered before any Roslyn type loads
        _tempDir = Path.Combine(Path.GetTempPath(), $"sextant_unrestored_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => Sextant.TestSupport.SqliteTestDatabase.DeleteDirectory(_tempDir);

    [TestMethod]
    public async Task SolutionLoader_ClosesTransitiveReferences_OfAnUnrestoredProject_AndItsCallsBindExactly()
    {
        var solutionPath = WriteChain();

        var solution = await SolutionLoader.LoadSolutionAsync(solutionPath);

        await AssertAppBinds(solution);

        var dbPath = Path.Combine(_tempDir, "index.db");
        using var db = new IndexDatabase(dbPath);
        db.RunMigrations();
        await new IndexOrchestrator(db, useDocumentExtractor: true).IndexSolutionAsync(solution);
        var conn = db.GetConnection();
        var ensure = new SymbolStore(conn).GetBySymbolKeyInScope(EnsureKey).Single();
        var refs = new ReferenceStore(conn).GetBySymbolId(ensure.Id);
        Assert.AreEqual(2, refs.Count, "both call sites are indexed");
        Assert.IsFalse(refs.Any(r => r.IsCandidate), "and they bound exactly, not as candidates");
    }

    [TestMethod]
    public async Task MultiSolutionLoader_ClosesTransitiveReferences_OfAnUnrestoredProject()
    {
        var solutionPath = WriteChain();

        var load = await MultiSolutionLoader.LoadAsync([solutionPath], onDiagnostic: null);

        await AssertAppBinds(load.Solution);
    }

    private static async Task AssertAppBinds(Solution solution)
    {
        var app = solution.Projects.Single(p => p.Name == "App");
        var abs = solution.Projects.Single(p => p.Name == "Abs");
        Assert.IsFalse(File.Exists(Path.Combine(Path.GetDirectoryName(app.FilePath)!, "obj", "project.assets.json")),
            "the fixture must stay unrestored, or the SDK would add the transitive reference itself");
        Assert.IsTrue(app.ProjectReferences.Any(r => r.ProjectId == abs.Id),
            "App reaches Abs only through Core, so the loader must add the transitive reference");
        var errors = (await app.GetCompilationAsync())!.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.Id).ToList();
        CollectionAssert.DoesNotContain(errors, "CS0012", string.Join(", ", errors));
    }

    // Abs <- Core <- App on disk, App references only Core, nothing restored.
    private string WriteChain()
    {
        var root = Path.Combine(_tempDir, "repo");
        WriteProject(root, "Abs", [], "namespace Abs { public enum OwnerKind { User, Team } }");
        WriteProject(root, "Core", ["Abs"], """
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
            """);
        WriteProject(root, "App", ["Core"], """
            using System.Threading;
            using System.Threading.Tasks;
            namespace App
            {
                public class Controller
                {
                    private readonly Core.IStore _store;
                    public Controller(Core.IStore store) { _store = store; }
                    public async Task PublishAsync(string name, CancellationToken ct)
                    {
                        var id = await _store.EnsureAsync(name, ct);
                        await _store.PublishAsync(id, ct);
                    }
                    public Task<string> EnsureAgainAsync(CancellationToken ct) => _store.EnsureAsync("x", ct);
                }
            }
            """);
        var solutionPath = Path.Combine(root, "Chain.slnx");
        File.WriteAllText(solutionPath, """
            <Solution>
              <Project Path="src/Abs/Abs.csproj" />
              <Project Path="src/Core/Core.csproj" />
              <Project Path="src/App/App.csproj" />
            </Solution>
            """);
        return solutionPath;
    }

    private static void WriteProject(string root, string name, string[] references, string source)
    {
        var dir = Path.Combine(root, "src", name);
        Directory.CreateDirectory(dir);
        var items = string.Concat(references.Select(r =>
            $"    <ProjectReference Include=\"../{r}/{r}.csproj\" />\n"));
        File.WriteAllText(Path.Combine(dir, name + ".csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
            {items}  </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(dir, name + ".cs"), source);
    }
}
