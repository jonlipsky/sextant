using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Sextant.Core;
using Sextant.Indexer;
using Sextant.Service;
using Sextant.Store;

namespace Sextant.Integration.Tests;

/// <summary>
/// Issue #125 end-to-end: a <c>clone</c>-mode service ensure of a parent repository whose project
/// <c>ProjectReference</c>s into a git SUBMODULE. <see cref="CloningCheckoutProvider"/> provisions the parent
/// AND recursively initializes the submodule at its pinned gitlink; the real
/// <see cref="LocalIndexerSnapshotWorker"/> then indexes the checkout, and — because the submodule is now
/// populated at its pin with its own remote — the Phase-12 submodule path fires: the submodule's project is
/// indexed into a deduplicated PROVIDER snapshot, the parent records a <c>snapshot_dependencies</c> edge, and
/// the parent's call into the provider is a cross-repository usage. With every submodule fetchable the
/// checkout's coverage is COMPLETE. Before #125 the submodule stayed empty: the provider project was never
/// indexed and coverage was partial (<c>submodule_unpopulated</c>).
/// <para>No network: sibling local repositories over <c>file://</c> under the test-only
/// <see cref="CloningCheckoutProvider.AllowFileTransportForTesting"/> opt-in. Skipped (inconclusive) when
/// git or the SDK restore is unavailable, so it never produces a false failure.</para>
/// </summary>
[TestClass]
[TestCategory("Integration")]
public class SubmoduleCheckoutIntegrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<string> _log = [];

    public SubmoduleCheckoutIntegrationTests()
    {
        _ = IntegrationFixture.Instance; // MSBuildLocator registered before any Roslyn type loads
        _tempDir = Path.Combine(Path.GetTempPath(), $"sextant_subcheckout_int_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => Sextant.TestSupport.SqliteTestDatabase.DeleteDirectory(_tempDir);

    [TestMethod]
    public async Task CloneModeEnsure_ParentWithSubmodule_IndexesTheSubmoduleProjectAsAProviderSnapshot()
    {
        var (providerDir, providerCommit) = CreateProviderRepo();
        var (parentUrl, parentCommit) = CreateParentRepo(providerDir, providerCommit);

        var paths = new ServicePaths(ServiceVolumes.Rooted(Path.Combine(_tempDir, "service")));
        var checkouts = new CloningCheckoutProvider(new PersistentVolumeCheckoutProvider(paths), paths, log: _log.Add)
        {
            AllowFileTransportForTesting = true
        };
        var request = new EnsureSnapshotRequest { RepositoryRemoteUrl = parentUrl, CommitSha = parentCommit };

        // Provision (clone + recursive submodule init at the pin), then restore the published checkout so
        // MSBuild can evaluate it. The worker's own TryResolve below is a marker'd cache hit on this checkout.
        Assert.IsTrue(checkouts.TryResolve(request, out var resolution), string.Join("\n", _log));
        var submodule = resolution.SubmoduleProvisioning.Single();
        Assert.IsTrue(submodule.IsPopulated, $"{submodule.Status}: {submodule.Reason}");
        Assert.AreEqual("libs/mix", submodule.Path);
        Assert.AreEqual(providerCommit, submodule.Commit);
        RestoreSolution(Path.Combine(resolution.CheckoutDir, "App.slnx"));

        var config = new SextantConfiguration();
        using var db = new IndexDatabase(Path.Combine(_tempDir, "catalog.db"), IndexWriteOptions.Default);
        db.RunMigrations();
        var worker = new LocalIndexerSnapshotWorker(db, config, checkouts, _log.Add);
        var identityHash = request.ToIdentity(IndexProfileDescriptor.FromConfiguration(config).ConfigurationHash).Hash;
        var scratch = Directory.CreateDirectory(Path.Combine(_tempDir, "scratch")).FullName;

        var result = await worker.ProduceAsync(request, identityHash, scratch, CancellationToken.None);

        var diagnostics = string.Join("\n", result.Projects.Select(p => $"{p.Severity} {p.Code} {p.ProjectPath}: {p.Message}"));
        Assert.AreEqual(SnapshotJobStatus.Complete, result.Status, $"{result.Error}\n{diagnostics}\n{string.Join("\n", _log)}");
        Assert.IsNotNull(result.Coverage);
        Assert.IsFalse(result.Coverage!.IsPartial, string.Join(" ", result.Coverage.Reasons));
        Assert.AreEqual(0, result.Coverage.SubmodulesUnpopulated, "the submodule was provisioned, so coverage is complete");

        var conn = db.GetConnection();
        Assert.AreEqual(1, ScalarInt(conn, "SELECT COUNT(*) FROM snapshots WHERE is_provider = 1;"),
            "the submodule's project was indexed into ONE deduplicated provider snapshot (Phase 12)");
        Assert.AreEqual(1, CountProviderSymbol(conn, "Combine"), "the submodule-provided symbol is indexed");
        Assert.AreEqual(1, ScalarInt(conn, "SELECT COUNT(*) FROM snapshot_dependencies;"),
            "the parent recorded its dependency edge on the submodule provider");

        var providerUrl = (string)new SqliteCommand(
            "SELECT remote_url FROM repositories WHERE is_provider = 1 LIMIT 1;", conn).ExecuteScalar()!;
        Assert.AreEqual(GitRemoteNormalizer.Normalize(new Uri(providerDir).AbsoluteUri), providerUrl,
            "the provider is keyed by the submodule's OWN remote (not the parent's)");
        var combineKey = (string?)new SqliteCommand("""
            SELECT s.symbol_key FROM symbols s
            JOIN projects p ON p.id = s.project_id
            JOIN snapshots sn ON sn.id = p.snapshot_id
            WHERE sn.is_provider = 1 AND s.display_name = 'Combine' LIMIT 1;
            """, conn).ExecuteScalar();
        Assert.IsNotNull(combineKey);
        var usages = new SnapshotDependencyStore(conn).FindCrossRepositoryUsages(
            providerUrl, combineKey, CrossRepoUsageScope.DefaultHeads, authorizedConsumerRepositoryIds: null);
        Assert.AreEqual(1, usages.Count, "the parent's call into the submodule is a cross-repository usage");
        Assert.AreEqual("Program.cs", Path.GetFileName(usages[0].FilePath));
    }

    // ==== fixture builders ======================================================================

    /// <summary>The submodule source: a MixAndMatch-style library with one project and a shared symbol.</summary>
    private (string dir, string commit) CreateProviderRepo()
    {
        var dir = InitRepo("provider");
        Write(dir, ".gitignore", "bin/\nobj/\n");
        Write(dir, "Mix/Mix.csproj", Csproj());
        Write(dir, "Mix/Combiner.cs", "namespace Mix;\npublic class Combiner { public int Combine(int a, int b) => a + b; }\n");
        Git(dir, "add", "-A");
        Git(dir, "commit", "--quiet", "-m", "provider");
        return (dir, GitOut(dir, "rev-parse", "HEAD").Trim());
    }

    /// <summary>
    /// A parent whose <c>App</c> project references <c>libs/mix/Mix/Mix.csproj</c> inside a submodule declared
    /// with a RELATIVE url (<c>../provider</c>, resolved against the parent's clean origin) and pinned by a
    /// mode-160000 gitlink — the shape <c>git submodule add</c> leaves in the tree.
    /// </summary>
    private (string url, string commit) CreateParentRepo(string providerDir, string providerCommit)
    {
        Assert.AreEqual(_tempDir, Path.GetDirectoryName(providerDir), "the relative url assumes sibling repositories");
        var dir = InitRepo("parent");
        Write(dir, ".gitignore", "bin/\nobj/\n");
        Write(dir, ".gitmodules", "[submodule \"mix\"]\n\tpath = libs/mix\n\turl = ../provider\n");
        Write(dir, "App/App.csproj", Csproj(projectReference: "..\\libs\\mix\\Mix\\Mix.csproj"));
        Write(dir, "App/Program.cs", "namespace App;\npublic class Program { public int Run() => new Mix.Combiner().Combine(1, 2); }\n");
        Write(dir, "App.slnx",
            "<Solution>\n  <Project Path=\"App/App.csproj\" />\n  <Project Path=\"libs/mix/Mix/Mix.csproj\" />\n</Solution>\n");
        Git(dir, "add", "-A");
        Git(dir, "update-index", "--add", "--cacheinfo", $"160000,{providerCommit},libs/mix");
        Git(dir, "commit", "--quiet", "-m", "parent");
        return (new Uri(dir).AbsoluteUri, GitOut(dir, "rev-parse", "HEAD").Trim());
    }

    private string InitRepo(string name)
    {
        var dir = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(dir);
        Git(dir, "init", "--quiet", "--initial-branch", "main");
        Git(dir, "config", "user.email", "test@example.com");
        Git(dir, "config", "user.name", "Sextant Test");
        Git(dir, "config", "commit.gpgsign", "false");
        Git(dir, "config", "uploadpack.allowReachableSHA1InWant", "true");
        return dir;
    }

    private static string Csproj(string? projectReference = null)
    {
        var reference = projectReference == null
            ? ""
            : $"\n  <ItemGroup>\n    <ProjectReference Include=\"{projectReference}\" />\n  </ItemGroup>";
        return $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
              </PropertyGroup>{reference}
            </Project>
            """;
    }

    private static int CountProviderSymbol(SqliteConnection conn, string displayName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM symbols s
            JOIN projects p ON p.id = s.project_id
            JOIN snapshots sn ON sn.id = p.snapshot_id
            WHERE sn.is_provider = 1 AND s.display_name = @n;
            """;
        cmd.Parameters.AddWithValue("@n", displayName);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static int ScalarInt(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static void Write(string root, string relative, string content)
    {
        var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static void Git(string dir, params string[] args) => GitOut(dir, args);

    private static string GitOut(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        Process process;
        try { process = Process.Start(psi) ?? throw new InvalidOperationException("git not found"); }
        catch (Exception ex) { Assert.Inconclusive($"git is not available: {ex.Message}"); return ""; }
        using (process)
        {
            var stderr = process.StandardError.ReadToEndAsync();
            var stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
                Assert.Inconclusive($"git {string.Join(' ', args)} failed (exit {process.ExitCode}): {stderr.Result}");
            return stdout;
        }
    }

    private static void RestoreSolution(string solutionPath)
    {
        var psi = new ProcessStartInfo("dotnet", $"restore \"{solutionPath}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = Process.Start(psi)!;
        var stderr = process.StandardError.ReadToEndAsync();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            Assert.Inconclusive($"restore of the provisioned checkout failed (exit {process.ExitCode}): {stderr.Result}");
    }
}
