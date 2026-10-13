using Sextant.Service.Restore;

namespace Sextant.Service.Tests;

/// <summary>Issue #272: per-solution restores use static-graph evaluation, with a kill switch and a one-shot fallback.</summary>
[TestClass]
public class PackageRestoreStaticGraphTests
{
    private const string StaticProperty = "-p:RestoreUseStaticGraphEvaluation=true";
    private static readonly string Tfm = $"net{Environment.Version.Major}.0";

    [TestMethod]
    public void Arguments_AddTheStaticGraphProperty_OnlyWhenAsked()
    {
        var off = PackageRestoreRunner.Arguments("All.slnx", []);
        var on = PackageRestoreRunner.Arguments("All.slnx", [], staticGraph: true);

        CollectionAssert.AreEqual(
            new[] { "restore", "All.slnx", "-p:DesignTimeBuild=true", "--ignore-failed-sources", "--disable-build-servers", "-nodeReuse:false", "-nologo" },
            off.ToArray(), "off is exactly today's arguments");
        CollectionAssert.AreEqual(
            new[] { "restore", "All.slnx", "-p:DesignTimeBuild=true", StaticProperty, "--ignore-failed-sources", "--disable-build-servers", "-nodeReuse:false", "-nologo" },
            on.ToArray());
    }

    [TestMethod]
    public void TheSwitch_DefaultsOn_ForTheServiceAndADirectRunner()
    {
        const string name = "SEXTANT_SERVICE_RESTORE_STATIC_GRAPH";
        var config = new Sextant.Core.SextantConfiguration { DbPath = ServiceTestFixtures.NewDbPath() };
        Environment.SetEnvironmentVariable(name, null);
        try
        {
            Assert.IsTrue(ServiceOptions.FromEnvironment(config).RestoreStaticGraph);
            Environment.SetEnvironmentVariable(name, "false");
            Assert.IsFalse(ServiceOptions.FromEnvironment(config).RestoreStaticGraph);
            Assert.IsTrue(new PackageRestoreRunner().StaticGraphRestore);
            Assert.IsFalse(new PackageRestoreRunner(staticGraphRestore: false).StaticGraphRestore);
            Assert.IsTrue(new PackageRestoreRunner(staticGraphRestore: true).StaticGraphRestore);
        }
        finally { Environment.SetEnvironmentVariable(name, null); }
    }

    [TestMethod]
    public async Task ASwitchedOnRestore_PassesTheProperty_AndRecordsIt()
    {
        RequireUnix();
        var root = NewRoot("on");
        try
        {
            var fake = CreateFakeDotnet(Path.Combine(root, "fake"), "exit 0");
            var solution = WriteSolution(root, "All.slnx", "A");

            var outcome = await new PackageRestoreRunner(timeout: TimeSpan.FromSeconds(60), staticGraphRestore: true) { DotnetPath = fake.Path }
                .RunAsync(root, [solution], limit: null, CancellationToken.None, Path.Combine(root, "scratch"));

            Assert.AreEqual(1, fake.Calls.Count);
            StringAssert.Contains(fake.Calls[0], StaticProperty);
            Assert.AreEqual(1, outcome.StaticGraphPasses);
            Assert.AreEqual(0, outcome.StaticGraphFallbacks);

            var recorder = new JobTimingsRecorder();
            recorder.RecordRestore(outcome, persistentPackageFolder: false);
            var restore = recorder.Build().Restore!;
            Assert.IsTrue(restore.StaticGraph);
            Assert.AreEqual(0, restore.StaticGraphFallbacks);
        }
        finally { TryDelete(root); }
    }

    [TestMethod]
    public async Task ASwitchedOffRestore_PassesNoProperty_AndRecordsNone()
    {
        RequireUnix();
        var root = NewRoot("off");
        try
        {
            var fake = CreateFakeDotnet(Path.Combine(root, "fake"), "exit 0");
            var solution = WriteSolution(root, "All.slnx", "A");

            var outcome = await new PackageRestoreRunner(timeout: TimeSpan.FromSeconds(60), staticGraphRestore: false) { DotnetPath = fake.Path }
                .RunAsync(root, [solution], limit: null, CancellationToken.None, Path.Combine(root, "scratch"));

            Assert.IsFalse(fake.Calls[0].Contains("StaticGraph", StringComparison.Ordinal));
            Assert.AreEqual(0, outcome.StaticGraphPasses);
            var recorder = new JobTimingsRecorder();
            recorder.RecordRestore(outcome, persistentPackageFolder: false);
            Assert.IsFalse(recorder.Build().Restore!.StaticGraph);
        }
        finally { TryDelete(root); }
    }

    [TestMethod]
    public async Task AWholePassFailure_WithStaticGraph_IsRerunOnceWithoutIt()
    {
        RequireUnix();
        var root = NewRoot("fallback");
        try
        {
            // Only the static-graph invocation fails, with an error that names no project.
            var fake = CreateFakeDotnet(
                Path.Combine(root, "fake"),
                "case \"$*\" in *RestoreUseStaticGraphEvaluation*) echo 'error : static graph evaluation failed'; exit 1;; *) exit 0;; esac");
            var solution = WriteSolution(root, "All.slnx", "A");
            var logs = new List<string>();

            var outcome = await new PackageRestoreRunner(timeout: TimeSpan.FromSeconds(60), log: logs.Add, staticGraphRestore: true)
                { DotnetPath = fake.Path }
                .RunAsync(root, [solution], limit: null, CancellationToken.None, Path.Combine(root, "scratch"));

            Assert.AreEqual(2, fake.Calls.Count);
            StringAssert.Contains(fake.Calls[0], StaticProperty);
            Assert.IsFalse(fake.Calls[1].Contains("StaticGraph", StringComparison.Ordinal));
            Assert.AreEqual(1, outcome.StaticGraphFallbacks);
            Assert.AreEqual(1, outcome.SolutionsSucceeded, "the default re-run is the one reported");
            Assert.IsTrue(outcome.Clean);
            Assert.AreEqual(1, logs.Count(l => l.Contains("failed with static-graph evaluation; re-running", StringComparison.Ordinal)));

            var recorder = new JobTimingsRecorder();
            recorder.RecordRestore(outcome, persistentPackageFolder: false);
            var restore = recorder.Build().Restore!;
            Assert.IsTrue(restore.StaticGraph);
            Assert.AreEqual(1, restore.StaticGraphFallbacks);
        }
        finally { TryDelete(root); }
    }

    [TestMethod]
    public async Task APerProjectError_WithStaticGraph_IsNotRerun()
    {
        RequireUnix();
        var root = NewRoot("no-fallback");
        try
        {
            var a = Path.Combine(root, "A", "A.csproj");
            var fake = CreateFakeDotnet(
                Path.Combine(root, "fake"), $"echo '{a} : error NU1101: Unable to find package Acme.X. No packages exist'; exit 1");
            Directory.CreateDirectory(Path.Combine(root, "A", "obj"));
            var solution = WriteSolution(root, "All.slnx", "A");
            File.WriteAllText(Path.Combine(root, "A", "obj", "project.assets.json"), "{}");

            var outcome = await new PackageRestoreRunner(timeout: TimeSpan.FromSeconds(60), staticGraphRestore: true) { DotnetPath = fake.Path }
                .RunAsync(root, [solution], limit: null, CancellationToken.None, Path.Combine(root, "scratch"));

            Assert.AreEqual(1, fake.Calls.Count, "a failure that names a project is not re-run");
            StringAssert.Contains(fake.Calls[0], StaticProperty);
            Assert.AreEqual(0, outcome.StaticGraphFallbacks);
            Assert.AreEqual("A/A.csproj", outcome.Projects.Single().Project);
        }
        finally { TryDelete(root); }
    }

    [TestMethod]
    public async Task AProjectNamedStaticFailure_LeavingNoAssets_IsHandledByThePerProjectRetry_NotAWholeRerun()
    {
        RequireUnix();
        var root = NewRoot("named-retry");
        try
        {
            var a = Path.Combine(root, "A", "A.csproj");
            var fake = CreateFakeDotnet(
                Path.Combine(root, "fake"),
                $"case \"$1\" in msbuild) exit 0;; *) echo '{a} : error NU1101: Unable to find package Acme.X. No packages exist'; exit 1;; esac");
            var solution = WriteSolution(root, "All.slnx", "A");

            var outcome = await new PackageRestoreRunner(timeout: TimeSpan.FromSeconds(60), staticGraphRestore: true) { DotnetPath = fake.Path }
                .RunAsync(root, [solution], limit: null, CancellationToken.None, Path.Combine(root, "scratch"));

            var calls = fake.Calls;
            Assert.AreEqual(2, calls.Count);
            StringAssert.Contains(calls[0], StaticProperty);
            StringAssert.StartsWith(calls[1], "msbuild");
            Assert.AreEqual(0, outcome.StaticGraphFallbacks);
            Assert.AreEqual(1, outcome.SolutionsRetriedPerProject);
        }
        finally { TryDelete(root); }
    }

    [TestMethod]
    public async Task ATimedOutStaticPass_IsNotRerun()
    {
        RequireUnix();
        var root = NewRoot("timeout");
        try
        {
            var fake = CreateFakeDotnet(Path.Combine(root, "fake"), "exec sleep 30");
            var solution = WriteSolution(root, "All.slnx", "A");

            var outcome = await new PackageRestoreRunner(timeout: TimeSpan.FromSeconds(1), staticGraphRestore: true) { DotnetPath = fake.Path }
                .RunAsync(root, [solution], limit: null, CancellationToken.None, Path.Combine(root, "scratch"));

            Assert.AreEqual(1, fake.Calls.Count);
            Assert.IsTrue(outcome.TimedOut);
            Assert.AreEqual(0, outcome.StaticGraphFallbacks);
        }
        finally { TryDelete(root); }
    }

    [TestMethod]
    public async Task ACancelledStaticPass_IsNotRerun()
    {
        RequireUnix();
        var root = NewRoot("cancel");
        try
        {
            var fake = CreateFakeDotnet(Path.Combine(root, "fake"), "exec sleep 30");
            var solution = WriteSolution(root, "All.slnx", "A");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            var cancelled = false;

            try
            {
                await new PackageRestoreRunner(timeout: TimeSpan.FromSeconds(60), staticGraphRestore: true) { DotnetPath = fake.Path }
                    .RunAsync(root, [solution], limit: null, cts.Token, Path.Combine(root, "scratch"));
            }
            catch (OperationCanceledException) { cancelled = true; }

            Assert.IsTrue(cancelled);
            Assert.AreEqual(1, fake.Calls.Count);
        }
        finally { TryDelete(root); }
    }

    [TestMethod]
    public async Task AFailureAtTheDeadline_LeavesNoFallback_AndIsBookedLikeATimeout()
    {
        RequireUnix();
        var root = NewRoot("spent");
        try
        {
            // The static pass fails as the shared deadline is reached; a fallback that would succeed must not run or be counted.
            var fake = CreateFakeDotnet(
                Path.Combine(root, "fake"),
                "case \"$*\" in *RestoreUseStaticGraphEvaluation*) sleep 1; echo 'error : late'; exit 1;; *) exit 0;; esac");
            var solution = WriteSolution(root, "All.slnx", "A");

            var outcome = await new PackageRestoreRunner(timeout: TimeSpan.FromSeconds(1), staticGraphRestore: true) { DotnetPath = fake.Path }
                .RunAsync(root, [solution], limit: null, CancellationToken.None, Path.Combine(root, "scratch"));

            Assert.IsTrue(outcome.TimedOut);
            Assert.AreEqual(0, outcome.SolutionsSucceeded);
            Assert.AreEqual(1, outcome.SolutionsAttempted);
            Assert.AreEqual(1, fake.Calls.Count);
        }
        finally { TryDelete(root); }
    }

    [TestMethod]
    public async Task AFellBackSolution_IsRestoredWithoutStaticGraph_NextTime()
    {
        RequireUnix();
        var root = NewRoot("memo");
        try
        {
            var fake = CreateFakeDotnet(
                Path.Combine(root, "fake"),
                "case \"$*\" in *RestoreUseStaticGraphEvaluation*) echo 'error : static graph evaluation failed'; exit 1;; *) exit 0;; esac");
            var solution = WriteSolution(root, "All.slnx", "A");
            var logs = new List<string>();
            var runner = new PackageRestoreRunner(timeout: TimeSpan.FromSeconds(60), log: logs.Add, staticGraphRestore: true) { DotnetPath = fake.Path };

            var first = await runner.RunAsync(root, [solution], limit: null, CancellationToken.None, Path.Combine(root, "scratch"));
            var second = await runner.RunAsync(root, [solution], limit: null, CancellationToken.None, Path.Combine(root, "scratch"));

            Assert.AreEqual(3, fake.Calls.Count, "static + default, then default only");
            Assert.IsFalse(fake.Calls[2].Contains("StaticGraph", StringComparison.Ordinal));
            Assert.AreEqual(1, first.StaticGraphFallbacks);
            Assert.AreEqual(0, second.StaticGraphPasses);
            Assert.AreEqual(0, second.StaticGraphFallbacks);
            Assert.IsTrue(second.Clean);
            Assert.AreEqual(1, logs.Count(l => l.Contains("failed with static-graph evaluation", StringComparison.Ordinal)), "logged once");
        }
        finally { TryDelete(root); }
    }

    [TestMethod]
    public async Task AFallbackThatFailsAsAWhole_IsStillRetriedPerProject()
    {
        RequireUnix();
        var root = NewRoot("fallback-retry");
        try
        {
            // Both restores fail as a whole and leave no assets; the #296 per-project traversal then succeeds.
            var fake = CreateFakeDotnet(Path.Combine(root, "fake"), "echo 'error : graph failed'; exit 1");
            var solution = WriteSolution(root, "All.slnx", "A");

            var outcome = await new PackageRestoreRunner(timeout: TimeSpan.FromSeconds(60), staticGraphRestore: true) { DotnetPath = fake.Path }
                .RunAsync(root, [solution], limit: null, CancellationToken.None, Path.Combine(root, "scratch"));

            var calls = fake.Calls;
            Assert.AreEqual(3, calls.Count);
            StringAssert.Contains(calls[0], StaticProperty);
            Assert.IsFalse(calls[1].Contains("StaticGraph", StringComparison.Ordinal));
            StringAssert.StartsWith(calls[2], "msbuild");
            Assert.AreEqual(1, outcome.StaticGraphFallbacks);
            Assert.AreEqual(1, outcome.SolutionsRetriedPerProject);
        }
        finally { TryDelete(root); }
    }

    [TestMethod]
    public async Task TheUnionTraversal_IsUnchanged_ByTheSwitch()
    {
        RequireUnix();
        var root = NewRoot("union");
        try
        {
            var fake = CreateFakeDotnet(Path.Combine(root, "fake"), "exit 0");
            var first = WriteSolution(root, "One.slnx", "A");
            var second = WriteSolution(root, "Two.slnx", "A", "B");

            var outcome = await new PackageRestoreRunner(timeout: TimeSpan.FromSeconds(60), staticGraphRestore: true) { DotnetPath = fake.Path }
                .RunAsync(root, [first, second], limit: null, CancellationToken.None, Path.Combine(root, "scratch"));

            Assert.IsTrue(outcome.UsedProjectUnion);
            Assert.IsFalse(fake.Traversal.Contains("StaticGraph", StringComparison.Ordinal), fake.Traversal);
            Assert.AreEqual(0, outcome.StaticGraphPasses);
        }
        finally { TryDelete(root); }
    }

    private sealed class FakeDotnet
    {
        public required string Path { get; init; }
        public required string CallLog { get; init; }
        public required string TraversalCopy { get; init; }

        public IReadOnlyList<string> Calls => File.Exists(CallLog) ? File.ReadAllLines(CallLog) : [];
        public string Traversal => File.Exists(TraversalCopy) ? File.ReadAllText(TraversalCopy) : string.Empty;
    }

    private static FakeDotnet CreateFakeDotnet(string dir, string restoreBody)
    {
        Directory.CreateDirectory(dir);
        var script = Path.Combine(dir, "dotnet");
        var callLog = Path.Combine(dir, "calls.txt");
        var copy = Path.Combine(dir, "traversal.proj");
        File.WriteAllText(script, $"""
            #!/bin/sh
            echo "$@" >> "{callLog}"
            case "$1" in
              restore) {restoreBody} ;;
              msbuild) cp "$2" "{copy}"; exit 0 ;;
            esac
            """.Replace("\r\n", "\n"));
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return new FakeDotnet { Path = script, CallLog = callLog, TraversalCopy = copy };
    }

    private static string NewRoot(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), $"sextant-restore-sg-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static string WriteSolution(string root, string name, params string[] projectNames)
    {
        foreach (var project in projectNames)
        {
            Directory.CreateDirectory(Path.Combine(root, project));
            File.WriteAllText(Path.Combine(root, project, project + ".csproj"),
                $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>{Tfm}</TargetFramework></PropertyGroup></Project>");
        }
        var solution = Path.Combine(root, name);
        File.WriteAllText(solution,
            "<Solution>" + string.Join(string.Empty, projectNames.Select(p => $"<Project Path=\"{p}/{p}.csproj\" />")) + "</Solution>");
        return solution;
    }

    private static void RequireUnix()
    {
        if (OperatingSystem.IsWindows())
            Assert.Inconclusive("The scripted dotnet host is a POSIX shell script.");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
