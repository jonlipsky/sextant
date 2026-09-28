using System.Reflection;

namespace Sextant.Service.Tests;

/// <summary>
/// Acceptance criterion 6 + the initiative's hard invariant: the core indexing libraries stay entirely
/// ProcessStack- and service-AGNOSTIC. All service/hosting code lives in NEW projects that depend on the
/// core libs — never the reverse — so a local-only Sextant installation keeps working with ZERO service
/// dependency. This asserts the dependency direction at the assembly level: no core assembly references
/// <c>Sextant.Service</c> or <c>Sextant.Service.Host</c>.
/// </summary>
[TestClass]
public class ArchitectureBoundaryTests
{
    private static readonly string[] ServiceAssemblies = ["Sextant.Service", "Sextant.Service.Host"];

    [DataTestMethod]
    [DataRow(typeof(Sextant.Core.SnapshotIdentity))]
    [DataRow(typeof(Sextant.Core.Platform.WorkerCapability))]
    [DataRow(typeof(Sextant.Core.Platform.CapabilityRouter))]
    [DataRow(typeof(Sextant.Core.Platform.ContributionManifest))]
    [DataRow(typeof(Sextant.Core.Platform.TargetFrameworkFacts))]
    [DataRow(typeof(Sextant.Store.IndexDatabase))]
    [DataRow(typeof(Sextant.Indexer.IndexOrchestrator))]
    [DataRow(typeof(Sextant.Daemon.DaemonHost))]
    [DataRow(typeof(Sextant.Mcp.DatabaseProvider))]
    public void CoreAssembly_DoesNotReferenceTheService(Type coreType)
    {
        var assembly = coreType.Assembly;
        var referenced = assembly.GetReferencedAssemblies().Select(a => a.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var service in ServiceAssemblies)
            Assert.IsFalse(referenced.Contains(service),
                $"core assembly '{assembly.GetName().Name}' must not reference '{service}' " +
                "— service/hosting code depends on core, never the reverse (criterion 6).");
    }

    // The app under apps/processstack/ builds against a private SDK feed, so nothing the public build
    // restores may reference that SDK or the app's projects, and Sextant.slnx must not include the app.
    private const string AppTree = "apps/processstack/";
    private const string SdkPackagePrefix = "Include=\"ProcessStack.";
    private static readonly string[] SkippedDirectories = ["bin", "obj", ".git", "TestResults", "node_modules"];

    [TestMethod]
    public void PlatformSdk_AndAppProjects_AreReferencedOnlyFromTheAppTree()
    {
        var root = RepositoryRoot();
        var offenders = new List<string>();
        foreach (var file in MsBuildFiles(root))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.StartsWith(AppTree, StringComparison.OrdinalIgnoreCase))
                continue;
            var text = File.ReadAllText(file).Replace('\\', '/');
            if (text.Contains(SdkPackagePrefix, StringComparison.OrdinalIgnoreCase)
                || text.Contains(AppTree, StringComparison.OrdinalIgnoreCase))
                offenders.Add(relative);
        }

        Assert.IsEmpty(offenders,
            $"only {AppTree} may reference the platform SDK packages or the app projects: {string.Join(", ", offenders)}");
    }

    [TestMethod]
    public void PublicSolution_DoesNotIncludeTheAppTree()
    {
        var solution = File.ReadAllText(Path.Combine(RepositoryRoot(), "Sextant.slnx")).Replace('\\', '/');

        Assert.IsFalse(solution.Contains("apps/", StringComparison.OrdinalIgnoreCase),
            "Sextant.slnx must build without the private SDK feed, so it must not include the app tree.");
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Sextant.slnx")))
                return dir.FullName;
        }
        throw new InvalidOperationException("Could not find Sextant.slnx above the test output directory.");
    }

    private static IEnumerable<string> MsBuildFiles(string root)
    {
        var pending = new Stack<string>([root]);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            foreach (var child in Directory.EnumerateDirectories(dir))
            {
                if (!SkippedDirectories.Contains(Path.GetFileName(child), StringComparer.OrdinalIgnoreCase))
                    pending.Push(child);
            }
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                var name = Path.GetFileName(file);
                if (name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".targets", StringComparison.OrdinalIgnoreCase))
                    yield return file;
            }
        }
    }

    [TestMethod]
    public void LocalQueryPath_WorksWithoutAnyServiceType()
    {
        // A local stdio consumer resolves symbols straight from the store with no SnapshotService involved,
        // proving the local path has zero service dependency (criterion 6).
        var dbPath = ServiceTestFixtures.NewDbPath();
        using var db = new Sextant.Store.IndexDatabase(dbPath);
        try
        {
            db.RunMigrations();
            var request = ServiceTestFixtures.Request();
            ServiceTestFixtures.PublishComplete(db, request, symbolCount: 2);

            var snapshots = new Sextant.Store.SnapshotStore(db.GetConnection());
            var snapshot = snapshots.GetByIdentityHash(request.ToIdentity().Hash);
            Assert.IsNotNull(snapshot);
            Assert.AreEqual(Sextant.Store.SnapshotStatus.Complete, snapshot!.Status);
            Assert.IsTrue(snapshots.GetSnapshotProjectIds(snapshot.Id).Count > 0,
                "the local store answers queries with no service dependency");
        }
        finally
        {
            SqliteTestDatabase.Delete(dbPath, db);
        }
    }
}
