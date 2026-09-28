using System.Reflection;
using System.Text.RegularExpressions;

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

    // The ProcessStack app has moved to a separate private repository. It builds against a private SDK feed,
    // so no project here may reference that SDK and the app must not come back: the public build never needs
    // the private feed.
    private const string AppTree = "apps/processstack/";
    private const string AppManifest = "psapp.yaml";
    private static readonly Regex SdkReference = new(
        """\b(?:Include|Update)\s*=\s*["']\s*ProcessStack\.""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    // Build output and other git-ignored directories.
    private static readonly string[] SkippedDirectories =
        ["bin", "obj", "artifacts", ".git", ".vs", ".idea", ".sextant", "TestResults", "node_modules"];

    [TestMethod]
    public void NoProject_ReferencesTheProcessStackSdk()
    {
        var offenders = SdkReferences(RepositoryRoot());

        Assert.IsEmpty(offenders,
            $"no project may reference a ProcessStack.* SDK package: {string.Join(", ", offenders)}");
    }

    [TestMethod]
    public void ProcessStackApp_DoesNotReappear()
    {
        var root = RepositoryRoot();
        var offenders = new List<string>();
        foreach (var file in RepositoryFiles(root))
        {
            var relative = Relative(root, file);
            if (relative.StartsWith(AppTree, StringComparison.OrdinalIgnoreCase))
                offenders.Add(relative);
            else if (Path.GetFileName(file).Equals(AppManifest, StringComparison.OrdinalIgnoreCase))
                offenders.Add(relative);
            else if (IsMsBuildFile(file)
                     && File.ReadAllText(file).Replace('\\', '/').Contains(AppTree, StringComparison.OrdinalIgnoreCase))
                offenders.Add(relative);
        }
        offenders.AddRange(SdkReferences(root));

        Assert.IsEmpty(offenders,
            $"the ProcessStack app lives in a separate private repository; no {AppTree} tree, {AppManifest} or " +
            $"ProcessStack.* package reference may come back (delete any leftover local build output under " +
            $"{AppTree}): {string.Join(", ", offenders.Distinct())}");
    }

    private static List<string> SdkReferences(string root) =>
        RepositoryFiles(root)
            .Where(file => IsMsBuildFile(file) && SdkReference.IsMatch(File.ReadAllText(file)))
            .Select(file => Relative(root, file))
            .ToList();

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Sextant.slnx")))
                return dir.FullName;
        }
        throw new InvalidOperationException("Could not find Sextant.slnx above the test output directory.");
    }

    private static string Relative(string root, string file) => Path.GetRelativePath(root, file).Replace('\\', '/');

    private static bool IsMsBuildFile(string file)
    {
        var name = Path.GetFileName(file);
        return name.EndsWith("proj", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".targets", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> RepositoryFiles(string root)
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
                yield return file;
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
