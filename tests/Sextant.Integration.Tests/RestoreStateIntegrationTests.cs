using Microsoft.CodeAnalysis;
using Sextant.Indexer;
using Sextant.Service.Restore;

namespace Sextant.Integration.Tests;

/// <summary>
/// Issue #272: with the REAL <c>dotnet restore</c> and the REAL solution loader, a job that replays the saved restore
/// state indexes exactly what a full restore does. The fixture has PackageReferences (from an offline local feed)
/// and a Directory.Build.props that moves the intermediate output, so the assets files are NOT under
/// <c>&lt;project&gt;/obj</c>. A wrapper <c>dotnet</c> records every restore process started.
/// </summary>
[TestClass]
[TestCategory("Integration")]
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class RestoreStateIntegrationTests : IDisposable
{
    private static readonly string Tfm = $"net{Environment.Version.Major}.0";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sextant_restore_state_{Guid.NewGuid():N}");
    private string Checkout => Path.Combine(_root, "checkout");
    private string Packages => Path.Combine(_root, "packages");
    private string Feed => Path.Combine(_root, "feed");
    private string State => Path.Combine(_root, "state");
    private string Calls => Path.Combine(_root, "restore-calls.txt");
    private string Wrapper => Path.Combine(_root, "dotnet-wrapper.sh");
    private bool _withBad;
    private bool _unreachableFeed;
    private bool _withDependent;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [TestMethod]
    public async Task ReplayedState_LoadsExactlyWhatAFullRestoreLoads()
    {
        RequireUnix();
        Arrange();
        var full = await Restore();
        Assert.IsTrue(full.Clean, full.Summary());
        Assert.IsTrue(full.Reuse!.Saved, full.Reuse.SaveSkipped);
        Assert.IsTrue(File.Exists(Path.Combine(Checkout, "artifacts", "obj", "App", "project.assets.json")), "restore output follows BaseIntermediateOutputPath");
        var expected = await Load();

        RecreateCheckout();
        var callsBefore = RestoreCalls();
        var reused = await Restore();
        Assert.AreEqual("reused", reused.Reuse!.Decision, reused.Summary());
        Assert.AreEqual(callsBefore, RestoreCalls(), "no dotnet restore process was started");
        var actual = await Load();

        Assert.AreEqual(0, expected.Errors, "the fixture compiles");
        CollectionAssert.AreEqual(expected.References, actual.References);
        CollectionAssert.AreEqual(expected.Documents, actual.Documents);
        Assert.AreEqual(expected.LoadDiagnostics, actual.LoadDiagnostics);
        Assert.AreEqual(expected.Errors, actual.Errors);
        Assert.IsTrue(actual.References.Any(r => r.Contains("dep", StringComparison.OrdinalIgnoreCase)), "the package reference binds");
    }

    [TestMethod]
    public async Task ChangedDirectoryPackagesProps_IsAMiss_AndRestores()
    {
        RequireUnix();
        Arrange();
        await Restore();
        RecreateCheckout();
        File.WriteAllText(Path.Combine(Checkout, "Directory.Packages.props"),
            "<Project><PropertyGroup><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally></PropertyGroup></Project>");
        var before = RestoreCalls();
        var outcome = await Restore();
        Assert.AreEqual("miss", outcome.Reuse!.Decision);
        Assert.AreEqual("inputs_changed", outcome.Reuse.Reason);
        Assert.IsTrue(RestoreCalls() > before, "a full restore ran");
    }

    [TestMethod]
    public async Task EvictedPackageFolder_IsPackagesMissing_AndRestores()
    {
        RequireUnix();
        Arrange();
        await Restore();
        RecreateCheckout();
        Directory.Delete(Path.Combine(Packages, "dep"), recursive: true);
        var before = RestoreCalls();
        var outcome = await Restore();
        Assert.AreEqual("packages_missing", outcome.Reuse!.Reason);
        Assert.IsTrue(RestoreCalls() > before);
        Assert.IsTrue(outcome.Clean, outcome.Summary());
    }

    [TestMethod]
    public async Task PackagesConfigProject_IsNeverReplayed()
    {
        RequireUnix();
        Arrange();
        File.WriteAllText(Path.Combine(Checkout, "Lib", "packages.config"), "<packages />");
        var first = await Restore();
        Assert.AreEqual("legacy_restore", first.Reuse!.Reason);
        Assert.IsFalse(first.Reuse.Saved);
        RecreateCheckout();
        File.WriteAllText(Path.Combine(Checkout, "Lib", "packages.config"), "<packages />");
        var before = RestoreCalls();
        var second = await Restore();
        Assert.AreEqual("legacy_restore", second.Reuse!.Reason);
        Assert.IsTrue(RestoreCalls() > before);
    }

    [TestMethod]
    public async Task ReplayFailure_FallsBackToAFullRestore_WithTheSameResult()
    {
        RequireUnix();
        Arrange();
        await Restore();
        var expected = await Load();

        RecreateCheckout();
        // The replay cannot write into a read-only output directory; the wrapper restores write access for the full restore.
        var blocked = Path.Combine(Checkout, "artifacts", "obj", "App");
        Directory.CreateDirectory(blocked);
        File.SetUnixFileMode(blocked, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var probe = Path.Combine(blocked, "probe");
            try
            {
                File.WriteAllText(probe, "x");
                File.Delete(probe);
                Assert.Inconclusive("Running as a user that ignores directory permissions.");
            }
            catch (UnauthorizedAccessException) { }

            var before = RestoreCalls();
            var outcome = await Restore();
            Assert.AreEqual("replay_failed", outcome.Reuse!.Reason, outcome.Summary());
            Assert.IsTrue(RestoreCalls() > before, "the full restore ran");
            Assert.IsTrue(outcome.Clean, outcome.Summary());
        }
        finally
        {
            File.SetUnixFileMode(blocked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var actual = await Load();
        CollectionAssert.AreEqual(expected.References, actual.References);
        CollectionAssert.AreEqual(expected.Documents, actual.Documents);
    }

    // Issue #349: a project that depends on a package no source has fails its restore (NU1101) while Lib and App restore
    // cleanly, so the restore is not clean but two of the three projects are.
    [TestMethod]
    public async Task DeterministicallyFailingProject_OnlyTheCleanProjectsAreReplayed_AndTheFailingOneIsRestoredAgain()
    {
        RequireUnix();
        _withBad = true;
        Arrange();
        var first = await Restore();
        Assert.IsFalse(first.Clean, first.Summary());
        Assert.IsTrue(first.Reuse!.Saved, first.Reuse.SaveSkipped);
        Assert.AreEqual(2, first.Reuse.Projects, first.Summary());
        Assert.AreEqual(1, first.Reuse.ProjectsRemaining);
        Assert.AreEqual("NU1101", first.ErrorCodes()[0].Code, first.Summary());
        Assert.IsTrue(first.ErrorCodes()[0].Deterministic);
        Assert.IsFalse(SavedText(Path.Combine(State, "current", RestoreStateStore.ManifestName)).Contains("Bad", StringComparison.Ordinal), "a failing project is never saved");
        var expected = await Load();

        RecreateCheckout();
        var callsBefore = RestoreCalls();
        var second = await Restore();
        Assert.AreEqual("partial", second.Reuse!.Decision, second.Summary());
        Assert.AreEqual(callsBefore, RestoreCalls(), "no whole-solution restore process: only the failing project is restored again");
        Assert.AreEqual("NU1101", second.ErrorCodes()[0].Code, "the failure is reproduced");
        var actual = await Load();

        static bool Clean(string item) => !item.StartsWith("Bad|", StringComparison.Ordinal);
        CollectionAssert.AreEqual(expected.References.Where(Clean).ToList(), actual.References.Where(Clean).ToList());
        CollectionAssert.AreEqual(expected.Documents.Where(Clean).ToList(), actual.Documents.Where(Clean).ToList());
        Assert.IsTrue(actual.References.Any(r => r.StartsWith("Lib|", StringComparison.Ordinal) && r.Contains("dep", StringComparison.OrdinalIgnoreCase)));
    }

    // A project with a ProjectReference to the deterministically failing one: whether it is replayed or restored again,
    // the index it produces after a partial replay equals the one a full restore produced.
    [TestMethod]
    public async Task ProjectReferencingTheFailingProject_IndexesTheSameAfterAPartialReplay()
    {
        RequireUnix();
        _withBad = true;
        _withDependent = true;
        Arrange();
        var first = await Restore();
        Assert.IsFalse(first.Clean, first.Summary());
        Assert.IsTrue(first.Reuse!.Saved, first.Reuse.SaveSkipped);
        var expected = await Load();

        RecreateCheckout();
        var second = await Restore();
        Assert.AreEqual("partial", second.Reuse!.Decision, second.Summary());
        var actual = await Load();

        static bool NotBad(string item) => !item.StartsWith("Bad|", StringComparison.Ordinal);
        CollectionAssert.AreEqual(expected.References.Where(NotBad).ToList(), actual.References.Where(NotBad).ToList());
        CollectionAssert.AreEqual(expected.Documents.Where(NotBad).ToList(), actual.Documents.Where(NotBad).ToList());
        Assert.IsTrue(actual.Documents.Any(d => d.StartsWith("Dependent|", StringComparison.Ordinal)));
        Assert.AreEqual(expected.Errors, actual.Errors);
    }

    [TestMethod]
    public async Task TransientFailure_NeverCachesTheFailingProject()
    {
        RequireUnix();
        _withBad = true;
        _unreachableFeed = true;
        Arrange();
        var first = await Restore();
        Assert.IsFalse(first.Clean, first.Summary());
        Assert.IsTrue(first.HasNonDeterministicFailure, first.Summary());
        var manifestPath = Path.Combine(State, "current", RestoreStateStore.ManifestName);
        if (File.Exists(manifestPath))
            Assert.IsFalse(SavedText(manifestPath).Contains("Bad", StringComparison.Ordinal));

        RecreateCheckout();
        var callsBefore = RestoreCalls();
        var second = await Restore();
        Assert.IsFalse(second.Clean, second.Summary());
        Assert.AreNotEqual("reused", second.Reuse!.Decision);
        Assert.IsTrue(second.Projects.Any(p => p.Project.StartsWith("Bad/", StringComparison.Ordinal)), "the failing project is restored again");
        Assert.IsTrue(RestoreCalls() >= callsBefore);
    }

    // The manifest without its list of projects left to restore (which names the failing ones by design).
    private static string SavedText(string manifestPath)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        foreach (var key in node.Select(kv => kv.Key).Where(k => k.Equals("remaining", StringComparison.OrdinalIgnoreCase)).ToList())
            node.Remove(key);
        return node.ToJsonString();
    }

    private void Arrange()
    {
        Directory.CreateDirectory(Packages);
        Directory.CreateDirectory(Feed);
        PackDep();
        File.WriteAllText(Wrapper,
            "#!/bin/sh\n" +
            $"export NUGET_PACKAGES=\"{Packages}\"\n" +
            $"if [ \"$1\" = restore ]; then echo \"$@\" >> \"{Calls}\"; chmod -R u+w \"{Checkout}\" 2>/dev/null; fi\n" +
            "exec dotnet \"$@\"\n");
        File.SetUnixFileMode(Wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        WriteCheckout();
    }

    // The same checkout at the same path, as a fresh provisioning of an unchanged commit would leave it: no obj state.
    private void RecreateCheckout()
    {
        Directory.Delete(Checkout, recursive: true);
        WriteCheckout();
    }

    private void WriteCheckout()
    {
        Directory.CreateDirectory(Path.Combine(Checkout, "Lib"));
        Directory.CreateDirectory(Path.Combine(Checkout, "App"));
        File.WriteAllText(Path.Combine(Checkout, "nuget.config"),
            $"<configuration><packageSources><clear /><add key=\"feed\" value=\"{Feed}\" />"
            + (_unreachableFeed ? "<add key=\"down\" value=\"http://127.0.0.1:1/index.json\" allowInsecureConnections=\"true\" />" : string.Empty)
            + "</packageSources></configuration>");
        File.WriteAllText(Path.Combine(Checkout, "Directory.Build.props"),
            "<Project><PropertyGroup><BaseIntermediateOutputPath>$(MSBuildThisFileDirectory)artifacts/obj/$(MSBuildProjectName)/</BaseIntermediateOutputPath></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(Checkout, "Lib", "Lib.csproj"),
            $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>{Tfm}</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include=\"Dep\" Version=\"1.0.0\" /></ItemGroup></Project>");
        File.WriteAllText(Path.Combine(Checkout, "Lib", "Class1.cs"), "namespace Lib; public static class Class1 { public static string Hi() => Dep.Greeter.Hello(); }");
        File.WriteAllText(Path.Combine(Checkout, "App", "App.csproj"),
            $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>{Tfm}</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include=\"../Lib/Lib.csproj\" /></ItemGroup></Project>");
        File.WriteAllText(Path.Combine(Checkout, "App", "Use.cs"), "namespace App; public static class Use { public static string Go() => Lib.Class1.Hi(); }");
        var bad = string.Empty;
        if (_withBad)
        {
            Directory.CreateDirectory(Path.Combine(Checkout, "Bad"));
            File.WriteAllText(Path.Combine(Checkout, "Bad", "Bad.csproj"),
                $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>{Tfm}</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include=\"No.Such.Package.Sextant349\" Version=\"9.9.9\" /></ItemGroup></Project>");
            File.WriteAllText(Path.Combine(Checkout, "Bad", "Bad.cs"), "namespace Bad; public static class Bad { }");
            bad = "<Project Path=\"Bad/Bad.csproj\" />";
            if (_withDependent)
            {
                Directory.CreateDirectory(Path.Combine(Checkout, "Dependent"));
                File.WriteAllText(Path.Combine(Checkout, "Dependent", "Dependent.csproj"),
                    $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>{Tfm}</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include=\"../Bad/Bad.csproj\" /><ProjectReference Include=\"../Lib/Lib.csproj\" /></ItemGroup></Project>");
                File.WriteAllText(Path.Combine(Checkout, "Dependent", "Dep.cs"), "namespace Dependent; public static class Dep { public static string Go() => Lib.Class1.Hi() + nameof(Bad.Bad); }");
                bad += "<Project Path=\"Dependent/Dependent.csproj\" />";
            }
        }

        File.WriteAllText(Path.Combine(Checkout, "All.slnx"), "<Solution><Project Path=\"Lib/Lib.csproj\" /><Project Path=\"App/App.csproj\" />" + bad + "</Solution>");
    }

    private void PackDep()
    {
        var source = Path.Combine(_root, "dep-src");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "Dep.csproj"),
            $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>{Tfm}</TargetFramework><Version>1.0.0</Version></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(source, "Greeter.cs"), "namespace Dep; public static class Greeter { public static string Hello() => \"hi\"; }");
        var start = new System.Diagnostics.ProcessStartInfo("dotnet") { WorkingDirectory = source };
        foreach (var arg in new[] { "pack", Path.Combine(source, "Dep.csproj"), "-o", Feed, "--disable-build-servers", "-nodeReuse:false" })
            start.ArgumentList.Add(arg);
        start.Environment["NUGET_PACKAGES"] = Path.Combine(_root, "pack-packages");
        BoundedProcess.ApplyDotnetEnvironment(start);
        var result = BoundedProcess.Run(start, TimeSpan.FromMinutes(5));
        Assert.IsTrue(result.Succeeded, result.Describe());
    }

    private Task<PackageRestoreOutcome> Restore()
        => new PackageRestoreRunner(timeout: TimeSpan.FromMinutes(5)) { DotnetPath = Wrapper }
            .RunAsync(Checkout, [Path.Combine(Checkout, "All.slnx")], null, CancellationToken.None,
                Path.Combine(_root, "scratch"), new RestoreReuse(State, Packages));

    private int RestoreCalls() => File.Exists(Calls) ? File.ReadAllLines(Calls).Length : 0;

    private async Task<Loaded> Load()
    {
        _ = IntegrationFixture.Instance;
        var diagnostics = new List<string>();
        var solution = await SolutionLoader.LoadSolutionAsync(Path.Combine(Checkout, "All.slnx"), diagnostics.Add);
        var references = new List<string>();
        var documents = new List<string>();
        var errors = 0;
        foreach (var project in solution.Projects.OrderBy(p => p.FilePath, StringComparer.Ordinal))
        {
            references.AddRange(project.MetadataReferences.OfType<PortableExecutableReference>()
                .Select(r => project.Name + "|" + r.FilePath).OrderBy(r => r, StringComparer.Ordinal));
            documents.AddRange(project.Documents.Select(d => project.Name + "|" + d.FilePath).OrderBy(d => d, StringComparer.Ordinal));
            var compilation = await project.GetCompilationAsync();
            errors += compilation!.GetDiagnostics().Count(d => d.Severity == DiagnosticSeverity.Error);
        }
        return new Loaded(references, documents, diagnostics.Count, errors);
    }

    private static void RequireUnix()
    {
        if (OperatingSystem.IsWindows())
            Assert.Inconclusive("The wrapper dotnet is a POSIX shell script.");
    }

    private sealed record Loaded(List<string> References, List<string> Documents, int LoadDiagnostics, int Errors);
}
