using Sextant.Service.Restore;

namespace Sextant.Service.Tests;

/// <summary>Issue #408: a failed whole-solution restore narrows to its bad projects by halving, and the next job remembers them.</summary>
[TestClass]
public class PackageRestoreBisectTests
{
    private static readonly string Tfm = $"net{Environment.Version.Major}.0";
    private const string BadProject = "P07";

    [TestMethod]
    public async Task AWholeFailureThatNamesNoProject_IsNarrowedByHalving_NotProjectByProject()
    {
        RequireUnix();
        var env = new Env(64);
        try
        {
            env.MarkBad();
            var outcome = await env.RunAsync();

            // restore of the solution (1) + whole traversal (1) + two halves per level down to the single bad project.
            var calls = env.Calls();
            Assert.IsTrue(calls.Count <= 2 + 2 * 6, $"{calls.Count} restore processes for 64 projects");
            Assert.IsTrue(calls.Count < 64 / 2, "far fewer than one process per project");
            Assert.AreEqual(1, calls.Count(c => c.StartsWith("restore ", StringComparison.Ordinal)));
            Assert.AreEqual(1, outcome.SolutionsRetriedPerProject);
            Assert.AreEqual(64, outcome.ProjectsRetried);
            CollectionAssert.AreEqual(new[] { $"{BadProject}/{BadProject}.csproj" }, env.Remembered().ToArray());
        }
        finally { env.Dispose(); }
    }

    [TestMethod]
    public async Task TheNextJob_RestoresTheRestInOneCall_AndTriesTheKnownBadProjectInAnother()
    {
        RequireUnix();
        var env = new Env(64);
        try
        {
            env.MarkBad();
            await env.RunAsync();
            var firstJobCalls = env.Calls().Count;

            // A new commit: a project file changed, so the restore-state fingerprint differs. The memory is not keyed by it.
            File.AppendAllText(Path.Combine(env.Root, "P01", "P01.csproj"), "<!-- next commit -->");
            File.Delete(env.CallLog);
            var outcome = await env.RunAsync();

            var calls = env.Calls();
            Assert.AreEqual(2, calls.Count, string.Join("\n", calls));
            Assert.IsTrue(calls.All(c => c.StartsWith("msbuild ", StringComparison.Ordinal)), "the whole-solution restore is not run again");
            Assert.IsTrue(firstJobCalls > calls.Count);
            var rest = env.Traversal(1);
            var bad = env.Traversal(2);
            Assert.IsFalse(rest.Contains($"{BadProject}/{BadProject}.csproj", StringComparison.Ordinal));
            Assert.AreEqual(63, Count(rest, "<MSBuild Projects="));
            Assert.AreEqual(1, Count(bad, "<MSBuild Projects="));
            StringAssert.Contains(bad, $"{BadProject}/{BadProject}.csproj");
            Assert.AreEqual(1, outcome.KnownBadSolutions);
            Assert.AreEqual(1, outcome.KnownBadProjects);
            Assert.AreEqual(0, outcome.KnownBadHealed);
            Assert.AreEqual(0, outcome.SolutionsRetriedPerProject);
            CollectionAssert.AreEqual(new[] { $"{BadProject}/{BadProject}.csproj" }, env.Remembered().ToArray());
        }
        finally { env.Dispose(); }
    }

    [TestMethod]
    public async Task AKnownBadProjectThatNowRestores_IsForgotten()
    {
        RequireUnix();
        var env = new Env(16);
        try
        {
            env.MarkBad();
            await env.RunAsync();
            Assert.AreEqual(1, env.Remembered().Count);

            File.Delete(env.Marker);
            File.Delete(env.CallLog);
            var outcome = await env.RunAsync();

            Assert.AreEqual(2, env.Calls().Count);
            Assert.AreEqual(1, outcome.KnownBadHealed);
            Assert.AreEqual(0, env.Remembered().Count);
            Assert.AreEqual(1, outcome.SolutionsSucceeded);
        }
        finally { env.Dispose(); }
    }

    [TestMethod]
    public async Task SeveralBadProjects_AreAllFound_AndRemembered()
    {
        RequireUnix();
        var env = new Env(64);
        try
        {
            env.MarkBad("P03 P29 P61");
            var outcome = await env.RunAsync();

            CollectionAssert.AreEqual(
                new[] { "P03/P03.csproj", "P29/P29.csproj", "P61/P61.csproj" }, env.Remembered().ToArray());
            Assert.AreEqual(1, outcome.SolutionsRetriedPerProject);
            Assert.IsTrue(env.Calls().Count(c => c.StartsWith("msbuild ", StringComparison.Ordinal)) <= PackageRestoreRunner.MaxRetryInvocations);
        }
        finally { env.Dispose(); }
    }

    [TestMethod]
    public async Task TheInvocationCap_HoldsOnEveryCall_AndNoSiblingGroupIsStarved()
    {
        RequireUnix();
        var env = new Env(64);
        try
        {
            env.MarkBad(string.Join(' ', Enumerable.Range(0, 64).Select(i => $"P{i:00}")));
            await env.RunAsync();

            var traversals = env.Calls().Count(c => c.StartsWith("msbuild ", StringComparison.Ordinal));
            Assert.IsTrue(traversals <= PackageRestoreRunner.MaxRetryInvocations, $"{traversals} traversals");
            Assert.IsGreaterThan(PackageRestoreRunner.MaxRetryInvocations / 2, traversals, "the budget is used");
            Assert.AreEqual(0, env.Remembered().Count, "an incomplete search is not remembered");
            // Breadth first: both outermost halves, and the last project's group, were tried.
            var tried = Directory.GetFiles(Path.Combine(env.Root, "fake"), "traversal-*.proj").Select(File.ReadAllText).ToList();
            Assert.IsTrue(tried.Any(t => t.Contains("P63/P63.csproj", StringComparison.Ordinal) && !t.Contains("P00/P00.csproj", StringComparison.Ordinal)),
                "the right-hand side was not starved by the left");
        }
        finally { env.Dispose(); }
    }

    [TestMethod]
    public async Task AFailureOfTheRest_DropsTheMemory_AndFallsBackToTheWholeRestore()
    {
        RequireUnix();
        var env = new Env(16);
        try
        {
            env.MarkBad();
            await env.RunAsync();

            // Now a different project fails too, with no name: the remembered project explains nothing.
            File.WriteAllText(env.Marker, "P03 P07");
            File.Delete(env.CallLog);
            await env.RunAsync();

            var calls = env.Calls();
            Assert.IsTrue(calls.Any(c => c.StartsWith("restore ", StringComparison.Ordinal)), "the solution restore runs again");
            CollectionAssert.AreEquivalent(
                new[] { "P03/P03.csproj", "P07/P07.csproj" }, env.Remembered().ToArray());
        }
        finally { env.Dispose(); }
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static void RequireUnix()
    {
        if (OperatingSystem.IsWindows())
            Assert.Inconclusive("The scripted dotnet host is a POSIX shell script.");
    }

    // A checkout of N projects and a fake dotnet that fails, naming no project, any restore that includes a project listed in
    // the marker file (default: P07) while the marker exists.
    private sealed class Env : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"sextant-restore-bisect-{Guid.NewGuid():N}");
        public string Marker => Path.Combine(Root, "bad.flag");
        public string CallLog => Path.Combine(Root, "fake", "calls.txt");
        private string Fake => Path.Combine(Root, "fake", "dotnet");
        private string State => Path.Combine(Root, "state");
        private string Solution => Path.Combine(Root, "All.slnx");

        public Env(int projects)
        {
            var names = Enumerable.Range(0, projects).Select(i => $"P{i:00}").ToArray();
            foreach (var name in names)
            {
                Directory.CreateDirectory(Path.Combine(Root, name));
                File.WriteAllText(Path.Combine(Root, name, name + ".csproj"),
                    $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>{Tfm}</TargetFramework></PropertyGroup></Project>");
            }
            File.WriteAllText(Solution,
                "<Solution>" + string.Join(string.Empty, names.Select(p => $"<Project Path=\"{p}/{p}.csproj\" />")) + "</Solution>");
            Directory.CreateDirectory(Path.Combine(Root, "fake"));
            Directory.CreateDirectory(Path.Combine(Root, "packages"));
            File.WriteAllText(Fake, $$"""
                #!/bin/sh
                echo "$@" >> "{{CallLog}}"
                bad() {
                  [ -f "{{Marker}}" ] || return 1
                  for p in $(cat "{{Marker}}"); do grep -q "$p/$p.csproj" "$1" && return 0; done
                  return 1
                }
                case "$1" in
                  restore) if bad "$2"; then exit 1; fi; exit 0 ;;
                  msbuild)
                    n=$(wc -l < "{{CallLog}}")
                    cp "$2" "{{Root}}/fake/traversal-$n.proj"
                    if bad "$2"; then exit 1; fi
                    grep -o 'sextant-restore-ok: [^"]*' "$2"
                    exit 0 ;;
                esac
                """.Replace("\r\n", "\n"));
            File.SetUnixFileMode(Fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        public void MarkBad(string projects = BadProject) => File.WriteAllText(Marker, projects);

        public Task<PackageRestoreOutcome> RunAsync()
            => new PackageRestoreRunner(timeout: TimeSpan.FromSeconds(120), staticGraphRestore: false) { DotnetPath = Fake }
                .RunAsync(Root, [Solution], limit: null, CancellationToken.None, Path.Combine(Root, "scratch"),
                    new RestoreReuse(State, Path.Combine(Root, "packages")));

        public List<string> Calls() => File.Exists(CallLog) ? [.. File.ReadAllLines(CallLog)] : [];

        public string Traversal(int call) => File.ReadAllText(Path.Combine(Root, "fake", $"traversal-{call}.proj"));

        public List<string> Remembered()
            => [.. KnownBadProjects.Load(State).For("All.slnx").Order(StringComparer.Ordinal)];

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
