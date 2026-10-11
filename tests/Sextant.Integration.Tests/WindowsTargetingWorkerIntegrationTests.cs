using Sextant.Core;
using Sextant.Indexer;
using Sextant.Service;
using Sextant.Store;
using Sextant.TestSupport;

namespace Sextant.Integration.Tests;

/// <summary>
/// Issue #293: a project targeting <c>net*-windows*</c> restores, evaluates and binds on a non-Windows worker, through
/// the REAL <see cref="LocalIndexerSnapshotWorker"/> (real restore by the service's runner, real MSBuild load, real
/// publish). Without <c>EnableWindowsTargeting=true</c> the SDK fails both with <c>NETSDK1100</c> off Windows, and the
/// projects are indexed from their surviving documents only (<c>project_evaluation_degraded</c>, partial coverage).
/// <para>
/// The fixture has a WPF project (<c>net{N}-windows</c>, <c>UseWPF</c>) whose class derives from
/// <c>System.Windows.Controls.Button</c> and uses its members, a Windows-SDK-projection project
/// (<c>net{N}-windows10.0.19041.0</c>, the WinUI-style TFM) that uses <c>Windows.Data.Json</c>, and an App that
/// references both and calls into them. The reference packs (<c>Microsoft.WindowsDesktop.App.Ref</c>,
/// <c>Microsoft.Windows.SDK.NET.Ref</c>) are ordinary packages, so the checkout's <c>nuget.config</c> names nuget.org
/// only: the test needs it (or the packs already in the global packages folder), and is inconclusive when a restore
/// reports nuget.org unreachable.
/// </para>
/// </summary>
[TestClass]
[TestCategory("Integration")]
public sealed class WindowsTargetingWorkerIntegrationTests : IDisposable
{
    private const string RemoteUrl = "https://example.invalid/org/windows-targeting-fixture.git";
    private const string PressKey = "M:Wpf.MainButton.Press";
    private const string MakeKey = "M:WinRt.Links.Make(System.String)";

    private static readonly string Net = $"net{Environment.Version.Major}.0";

    private readonly string _root;
    private readonly string _checkout;

    public WindowsTargetingWorkerIntegrationTests()
    {
        _ = IntegrationFixture.Instance; // MSBuildLocator registered before any Roslyn type loads
        _root = Path.Combine(Path.GetTempPath(), $"sextant_windows_targeting_{Guid.NewGuid():N}");
        _checkout = Path.Combine(_root, "checkouts", "windows-targeting-fixture");
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
    public Task Worker_RestoresAndLoadsWindowsTargetingProjects_WithoutDegradation_AndTheirReferencesBind()
        => RunAsync(withPlainSolution: false);

    /// <summary>
    /// Two selected solutions, one without a Windows target: the union restore and the union load make one decision for
    /// the whole selection, so the plain solution's project loads too and the Windows projects still bind.
    /// </summary>
    [TestMethod]
    public Task Worker_UnionWithAPlainSolution_RestoresAndLoadsEveryProject_AndTheWindowsReferencesBind()
        => RunAsync(withPlainSolution: true);

    private async Task RunAsync(bool withPlainSolution)
    {
        var commit = CreateRepo(withPlainSolution);
        var config = new SextantConfiguration();
        var resolution = new CheckoutResolution
        {
            CheckoutDir = _checkout,
            SelectedSolutions = withPlainSolution
                ? [Path.Combine(_checkout, "App.slnx"), Path.Combine(_checkout, "Lib.slnx")]
                : [Path.Combine(_checkout, "App.slnx")],
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

        var conn = db.GetConnection();
        Assert.IsNotNull(result.SnapshotId, "the snapshot is published: " + result.Error);
        var coverage = new SnapshotCoverageStore(conn).Get(result.SnapshotId.Value)!;
        if (coverage.Notes?.Any(n => n.Contains("could not be reached", StringComparison.Ordinal)) == true)
            Assert.Inconclusive("nuget.org is unreachable, so the Windows reference packs cannot be restored: " +
                string.Join(" ", coverage.Notes));

        var diagnostics = string.Join("; ", result.Projects.Select(p => $"{p.Code} {p.ProjectPath}: {p.Message}"));
        Assert.AreEqual(SnapshotJobStatus.Complete, result.Status, $"{result.Error} {diagnostics}");
        Assert.AreEqual(SnapshotCoverageVerdict.Complete, coverage.Verdict, string.Join(" ", coverage.Reasons));
        Assert.AreEqual(0, coverage.ProjectsDegraded, string.Join(" ", coverage.Reasons));
        Assert.IsFalse(result.Projects.Any(p =>
            p.Code is "project_evaluation_degraded" or LocalIndexerSnapshotWorker.PackageRestoreIncompleteCode), diagnostics);
        foreach (var project in withPlainSolution ? new[] { "Wpf", "WinRt", "App", "Lib" } : ["Wpf", "WinRt", "App"])
        {
            Assert.IsTrue(File.Exists(Path.Combine(_checkout, "src", project, "obj", "project.assets.json")),
                $"the worker restored {project}");
        }
        Assert.IsTrue(coverage.Binding is null || coverage.Binding.Projects.Count == 0,
            "every name binds, WPF and Windows SDK types included: " + string.Join("; ",
                coverage.Binding?.Projects.Select(p => $"{p.Project} {p.TargetFramework}: {p.UnboundNames} unbound names") ?? []));

        var symbols = new SymbolStore(conn);
        var references = new ReferenceStore(conn);
        foreach (var key in new[] { PressKey, MakeKey })
        {
            var symbol = symbols.GetBySymbolKeyInScope(key).Single();
            var refs = references.GetBySymbolId(symbol.Id);
            Assert.AreEqual(1, refs.Count, $"App's call to {key} is indexed");
            Assert.IsFalse(refs.Single().IsCandidate, $"and binds exactly to {key}");
        }
    }

    private string CreateRepo(bool withPlainSolution)
    {
        Write("nuget.config", """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
              </packageSources>
            </configuration>
            """);
        Write(".gitignore", "bin/\nobj/\n");
        Write("App.slnx", """
            <Solution>
              <Project Path="src/Wpf/Wpf.csproj" />
              <Project Path="src/WinRt/WinRt.csproj" />
              <Project Path="src/App/App.csproj" />
            </Solution>
            """);
        if (withPlainSolution)
        {
            Write("Lib.slnx", """
                <Solution>
                  <Project Path="src/Lib/Lib.csproj" />
                </Solution>
                """);
            WriteProject("Lib", Net, "", "", """
                namespace Lib
                {
                    public static class Text
                    {
                        public static string Upper(string value) => value.ToUpperInvariant();
                    }
                }
                """);
        }
        WriteProject("Wpf", $"{Net}-windows", "<UseWPF>true</UseWPF>", "", """
            namespace Wpf
            {
                public class MainButton : System.Windows.Controls.Button
                {
                    public void Press()
                    {
                        Content = "pressed";
                        IsEnabled = !IsDefault;
                        Visibility = System.Windows.Visibility.Visible;
                    }
                }
            }
            """);
        WriteProject("WinRt", $"{Net}-windows10.0.19041.0", "", "", """
            namespace WinRt
            {
                public static class Links
                {
                    public static Windows.Data.Json.JsonObject Make(string value)
                    {
                        var json = new Windows.Data.Json.JsonObject();
                        json.SetNamedValue("v", Windows.Data.Json.JsonValue.CreateStringValue(value));
                        return json;
                    }
                }
            }
            """);
        WriteProject("App", $"{Net}-windows10.0.19041.0", "<UseWPF>true</UseWPF>", """
            <ProjectReference Include="../Wpf/Wpf.csproj" />
                <ProjectReference Include="../WinRt/WinRt.csproj" />
            """, """
            namespace App
            {
                public class Shell
                {
                    public string Open(Wpf.MainButton button)
                    {
                        button.Press();
                        System.Windows.Controls.Button plain = button;
                        plain.IsCancel = true;
                        return WinRt.Links.Make("x").GetNamedString("v");
                    }
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

    private void WriteProject(string name, string targetFramework, string properties, string items, string source)
    {
        Write($"src/{name}/{name}.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>{targetFramework}</TargetFramework>
                {properties}
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
