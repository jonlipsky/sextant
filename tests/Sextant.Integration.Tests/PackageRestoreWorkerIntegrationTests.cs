using Sextant.Core;
using Sextant.Indexer;
using Sextant.Service;
using Sextant.Store;
using Sextant.TestSupport;

namespace Sextant.Integration.Tests;

/// <summary>
/// The service worker restores packages before it loads the checkout, through the REAL
/// <see cref="LocalIndexerSnapshotWorker"/> (real <c>dotnet restore</c>, real MSBuild load, real publish). The
/// fixture is the live incident's shape (<c>Abs ← Core ← App</c>, App references only Core) in a git checkout
/// whose App also asks for a package no source has (a private feed the worker cannot read): the restore writes
/// the assets files, so App's calls through Core bind exactly, and the missing package is reported as a coverage
/// note, a per-project load issue and a job diagnostic instead of being silently ignored.
/// Offline: the checkout's <c>nuget.config</c> clears every source and points at an empty local folder.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public sealed class PackageRestoreWorkerIntegrationTests : IDisposable
{
    private const string RemoteUrl = "https://example.invalid/org/restore-fixture.git";
    private const string MissingPackage = "Sextant.Fixture.Private.Package";
    private const string EnsureKey = "M:Core.IStore.EnsureAsync(System.String,System.Threading.CancellationToken,Abs.OwnerKind)";

    private readonly string _root;
    private readonly string _checkout;

    public PackageRestoreWorkerIntegrationTests()
    {
        _ = IntegrationFixture.Instance; // MSBuildLocator registered before any Roslyn type loads
        _root = Path.Combine(Path.GetTempPath(), $"sextant_restore_worker_{Guid.NewGuid():N}");
        _checkout = Path.Combine(_root, "checkouts", "restore-fixture");
        Directory.CreateDirectory(_checkout);
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal); // git object files are read-only on Windows
        }
        catch (IOException)
        {
            // best effort: DeleteDirectory retries
        }
        SqliteTestDatabase.DeleteDirectory(_root);
    }

    [TestMethod]
    public async Task Worker_RestoresBeforeTheLoad_CallsBindExactly_AndTheMissingPackageIsReported()
    {
        var commit = CreateRepo();
        var config = new SextantConfiguration();
        var resolution = new CheckoutResolution
        {
            CheckoutDir = _checkout,
            SelectedSolutions = [Path.Combine(_checkout, "App.slnx")],
            Source = SolutionSelectionSource.DefaultUnion
        };
        var request = new EnsureSnapshotRequest { RepositoryRemoteUrl = RemoteUrl, CommitSha = commit, BranchName = "main" };
        var identity = request.ToIdentity(IndexProfileDescriptor.FromConfiguration(config).ConfigurationHash, null).Hash;
        var scratch = Path.Combine(_root, "scratch");
        Directory.CreateDirectory(scratch);

        var dbPath = Path.Combine(_root, "catalog.db");
        using var db = new IndexDatabase(dbPath);
        db.RunMigrations();
        var result = await new LocalIndexerSnapshotWorker(db, config, new FixedCheckoutProvider(resolution))
            .ProduceAsync(request, identity, scratch, CancellationToken.None);

        Assert.AreEqual(SnapshotJobStatus.Complete, result.Status,
            "a package nothing uses is a load issue, not a binding gap: " + result.Error);
        foreach (var project in new[] { "Abs", "Core", "App" })
        {
            Assert.IsTrue(File.Exists(Path.Combine(_checkout, "src", project, "obj", "project.assets.json")),
                $"the worker restored {project} before loading it");
        }

        var conn = db.GetConnection();
        var ensure = new SymbolStore(conn).GetBySymbolKeyInScope(EnsureKey).Single();
        var refs = new ReferenceStore(conn).GetBySymbolId(ensure.Id);
        Assert.AreEqual(2, refs.Count, "both call sites are indexed");
        Assert.IsFalse(refs.Any(r => r.IsCandidate), "and bound exactly, because App got its transitive reference");

        var coverage = new SnapshotCoverageStore(conn).Get(result.SnapshotId!.Value)!;
        Assert.AreEqual(SnapshotCoverageVerdict.Complete, coverage.Verdict, string.Join(" ", coverage.Reasons));
        Assert.AreEqual(
            $"Package restore could not find 1 package(s) ({MissingPackage}) for 1 project(s); code that uses them may not bind.",
            coverage.Notes!.Single());
        var app = coverage.Binding!.Projects.Single();
        Assert.AreEqual("src/App/App.csproj", app.Project);
        Assert.AreEqual($"restore: package(s) not found: {MissingPackage}", app.LoadIssue);
        Assert.IsFalse(app.Degraded, "App's own code binds");

        var diagnostic = result.Projects.First(p => p.Code == LocalIndexerSnapshotWorker.PackageRestoreIncompleteCode);
        Assert.AreEqual("src/App/App.csproj", diagnostic.ProjectPath);
        Assert.AreEqual(JobDiagnosticSeverity.Warning, diagnostic.Severity);
    }

    // Abs <- Core <- App in a committed git checkout; App references only Core and asks for a missing package.
    private string CreateRepo()
    {
        var feed = Path.Combine(_root, "empty-feed");
        Directory.CreateDirectory(feed);
        Write("nuget.config", $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="local" value="{feed}" />
              </packageSources>
            </configuration>
            """);
        Write(".gitignore", "bin/\nobj/\n");
        Write("App.slnx", """
            <Solution>
              <Project Path="src/Abs/Abs.csproj" />
              <Project Path="src/Core/Core.csproj" />
              <Project Path="src/App/App.csproj" />
            </Solution>
            """);
        WriteProject("Abs", "", "namespace Abs { public enum OwnerKind { User, Team } }");
        WriteProject("Core", "<ProjectReference Include=\"../Abs/Abs.csproj\" />", """
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
        WriteProject("App",
            $"<ProjectReference Include=\"../Core/Core.csproj\" />\n    <PackageReference Include=\"{MissingPackage}\" Version=\"1.0.0\" />",
            """
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

        try
        {
            Git("init", "--quiet", "--initial-branch", "main");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Assert.Inconclusive($"git is not available: {ex.Message}");
        }
        Git("config", "user.email", "test@example.com");
        Git("config", "user.name", "Sextant Test");
        Git("config", "commit.gpgsign", "false");
        Git("config", "core.autocrlf", "false");
        Git("add", "-A");
        Git("commit", "--quiet", "-m", "fixture");
        return Git("rev-parse", "HEAD").Trim();
    }

    private void WriteProject(string name, string items, string source)
    {
        Write($"src/{name}/{name}.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net{Environment.Version.Major}.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                {items}
              </ItemGroup>
            </Project>
            """);
        Write($"src/{name}/{name}.cs", source);
    }

    private void Write(string relative, string content)
    {
        var full = Path.Combine(_checkout, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private string Git(params string[] args)
    {
        var result = BoundedProcess.Run("git", args, TimeSpan.FromMinutes(1), _checkout);
        if (!result.Succeeded)
            throw new InvalidOperationException(result.Describe());
        return result.StandardOutput;
    }

    private sealed class FixedCheckoutProvider(CheckoutResolution resolution) : ICheckoutProvider
    {
        public bool TryResolve(EnsureSnapshotRequest request, out CheckoutResolution resolved)
        {
            resolved = resolution;
            return true;
        }
    }
}
