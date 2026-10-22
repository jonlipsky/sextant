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
            $"<configuration><packageSources><clear /><add key=\"feed\" value=\"{Feed}\" /></packageSources></configuration>");
        File.WriteAllText(Path.Combine(Checkout, "Directory.Build.props"),
            "<Project><PropertyGroup><BaseIntermediateOutputPath>$(MSBuildThisFileDirectory)artifacts/obj/$(MSBuildProjectName)/</BaseIntermediateOutputPath></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(Checkout, "Lib", "Lib.csproj"),
            $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>{Tfm}</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include=\"Dep\" Version=\"1.0.0\" /></ItemGroup></Project>");
        File.WriteAllText(Path.Combine(Checkout, "Lib", "Class1.cs"), "namespace Lib; public static class Class1 { public static string Hi() => Dep.Greeter.Hello(); }");
        File.WriteAllText(Path.Combine(Checkout, "App", "App.csproj"),
            $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>{Tfm}</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include=\"../Lib/Lib.csproj\" /></ItemGroup></Project>");
        File.WriteAllText(Path.Combine(Checkout, "App", "Use.cs"), "namespace App; public static class Use { public static string Go() => Lib.Class1.Hi(); }");
        File.WriteAllText(Path.Combine(Checkout, "All.slnx"), "<Solution><Project Path=\"Lib/Lib.csproj\" /><Project Path=\"App/App.csproj\" /></Solution>");
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
