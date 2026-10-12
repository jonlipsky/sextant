using Microsoft.CodeAnalysis;
using Sextant.Core;
using Sextant.Indexer;
using Sextant.Service;
using Sextant.Store;
using Sextant.TestSupport;

namespace Sextant.Integration.Tests;

/// <summary>
/// Issue #295: an iOS project fails evaluation on a Linux worker (the workload cannot be installed), and is then
/// compiled against the platform reference pack, downloaded from nuget.org as an ordinary package. The coverage
/// stays partial with a distinct reason. Inconclusive when nuget.org is unreachable.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public sealed class PlatformReferencePackIntegrationTests : IDisposable
{
    private const string RemoteUrl = "https://example.invalid/org/ios-pack-fixture.git";
    private static readonly string Net = $"net{Environment.Version.Major}.0";

    private readonly string _root;
    private readonly string _checkout;

    public PlatformReferencePackIntegrationTests()
    {
        _ = IntegrationFixture.Instance;
        _root = Path.Combine(Path.GetTempPath(), $"sextant_ios_pack_{Guid.NewGuid():N}");
        _checkout = Path.Combine(_root, "checkouts", "ios-pack-fixture");
        Directory.CreateDirectory(_checkout);
    }

    public void Dispose() => SqliteTestDatabase.DeleteDirectory(_root);

    [TestMethod]
    public async Task Loader_BindsAnIosProjectAgainstThePlatformReferencePack()
    {
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsWindows())
            Assert.Inconclusive("the iOS workload may be installable on this host; the missing-workload path is Linux-specific.");
        WriteFixture();
        var scratch = Path.Combine(_root, "scratch");
        var packages = Path.Combine(_root, "packages");
        Directory.CreateDirectory(scratch);
        Directory.CreateDirectory(packages);
        var messages = new List<string>();

        var load = await MultiSolutionLoader.LoadAsync([Path.Combine(_checkout, "App.slnx")], messages.Add, scratchDirectory: scratch);
        var loadedProject = load.Solution.Projects.Single(p => p.Documents.Any());
        var degraded = new DegradedProject(Path.Combine(_checkout, "src", "Ios", "Ios.csproj"), $"{Net}-ios",
            "NETSDK1147: workload ios is not installed") { ProjectId = loadedProject.Id };

        var (solution, bindings) = await PlatformReferencePacks.ApplyAsync(
            load.Solution, [degraded], new PlatformPackOptions { PackageFolders = [packages], ScratchDirectory = scratch },
            messages.Add, CancellationToken.None);

        if (bindings.Count == 0 && messages.Any(m => m.Contains("could not be downloaded", StringComparison.Ordinal)))
            Assert.Inconclusive("nuget.org is unreachable: " + string.Join(" ", messages));
        Assert.AreEqual(1, bindings.Count, string.Join("; ", messages));

        var project = solution.GetProject(loadedProject.Id)!;
        var compilation = (await project.GetCompilationAsync())!;
        var unbound = compilation.GetDiagnostics().Where(d => d.Id is "CS0246" or "CS0234").ToList();
        Assert.AreEqual(0, unbound.Count, string.Join("; ", unbound.Select(d => d.GetMessage())));
        Assert.IsNotNull(compilation.GetTypeByMetadataName("UIKit.UIView"));
    }

    [TestMethod]
    public async Task Worker_RecordsADistinctCoverageReasonForAnIosProjectBoundToTheReferencePack()
    {
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsWindows())
            Assert.Inconclusive("the iOS workload may be installable on this host; the missing-workload path is Linux-specific.");
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

        using var db = new IndexDatabase(Path.Combine(_root, "catalog.db"));
        db.RunMigrations();
        var result = await new LocalIndexerSnapshotWorker(db, config, new FixedCheckoutProvider(resolution))
            .ProduceAsync(request, identity, scratch, CancellationToken.None);

        Assert.IsNotNull(result.SnapshotId, "the snapshot is published: " + result.Error);
        var diagnostics = string.Join("; ", result.Projects.Select(p => $"{p.Code} {p.ProjectPath}: {p.Message}"));
        if (!result.Projects.Any(p => p.Code == "platform_reference_pack") && diagnostics.Contains("could not be downloaded", StringComparison.Ordinal))
            Assert.Inconclusive("nuget.org is unreachable: " + diagnostics);
        Assert.IsTrue(result.Projects.Any(p => p.Code == "platform_reference_pack"), diagnostics);
        var coverage = new SnapshotCoverageStore(db.GetConnection()).Get(result.SnapshotId.Value)!;
        Assert.AreEqual(SnapshotCoverageVerdict.Partial, coverage.Verdict);
        Assert.IsTrue(coverage.Reasons.Any(r => r.Contains("reference pack without the workload", StringComparison.Ordinal)),
            string.Join(" ", coverage.Reasons));
        Assert.IsTrue(coverage.Binding is null || coverage.Binding.Projects.All(p => p.UnboundNames == 0),
            "the platform types bind: " + string.Join("; ", coverage.Binding?.Projects.Select(p => $"{p.Project}: {p.UnboundNames}") ?? []));
    }

    private void WriteFixture()
    {
        Write("App.slnx", "<Solution>\n  <Project Path=\"src/Ios/Ios.csproj\" />\n</Solution>\n");
        Write("src/Ios/Ios.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>{Net}-ios</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        Write("src/Ios/Cell.cs", """
            namespace Ios
            {
                public class Cell : UIKit.UIView
                {
                    public UIKit.UILabel Title { get; } = new UIKit.UILabel();

                    public void Press()
                    {
                        Hidden = false;
                        AddSubview(Title);
                    }
                }
            }
            """);
    }

    private string CreateRepo()
    {
        WriteFixture();
        Write(".gitignore", "bin/\nobj/\n");
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
