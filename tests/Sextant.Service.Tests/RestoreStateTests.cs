using Sextant.Service.Restore;

namespace Sextant.Service.Tests;

/// <summary>Issue #272: a repeat job of an unchanged repository replays the saved restore result instead of restoring.</summary>
[TestClass]
public class RestoreStateTests
{
    private static readonly string Tfm = $"net{Environment.Version.Major}.0";

    // Writes the files a real restore leaves per project (assets naming one package and one download-only dependency,
    // the cache listing a package file); $OUT is where the project's restore output goes.
    private const string RestoreBody = RestoreBodyHead + """
          OUT="$d/obj"
        """ + RestoreBodyTail;

    private const string RestoreBodyHead = """
        for p in $(find . -name '*.csproj' -not -path '*/obj/*' -not -path '*/artifacts/*'); do
          d=$(cd "$(dirname "$p")" && pwd)
        """ + "\n";

    private const string RestoreBodyTail = """

          mkdir -p "$OUT"
          printf '{"version":3,"libraries":{"X/1.0.0":{"type":"package","path":"x/1.0.0"},"P/1.0.0":{"type":"project","path":"../P/P.csproj"}},"packageFolders":{"%s/":{}},"project":{"restore":{"projectPath":"%s/%s","outputPath":"%s/"},"frameworks":{"net10.0":{"downloadDependencies":[{"name":"Y","version":"[2.0.0, 2.0.0]"}]}}}}' "$PKG_DIR" "$d" "$(basename "$p")" "$OUT" > "$OUT/project.assets.json"
          echo "{\"expectedPackageFiles\":[\"$PKG_FILE\"]}" > "$OUT/project.nuget.cache"
          echo '<Project />' > "$OUT/$(basename "$p").nuget.g.props"
        done; exit 0
        """;

    private static readonly string CustomOutputBody = RestoreBodyHead + "  OUT=\"$(pwd)/artifacts/obj/$(basename \"$d\")\"\n" + RestoreBodyTail;

    [TestMethod]
    public async Task Unchanged_WithAssetsAndState_SkipsRestore_AndReplaysIdenticalAssetsIntoAFreshCheckout()
    {
        RequireUnix();
        using var env = new Env();
        var first = await env.Run();
        Assert.AreEqual(1, env.Calls, "the first job restores");
        Assert.IsTrue(first.Clean);
        Assert.IsTrue(first.Reuse!.Saved, first.Reuse.SaveSkipped);
        var before = File.ReadAllBytes(env.Assets("A"));

        env.ResetCheckout(); // a new commit is provisioned into a fresh tree: obj/ is gone
        Assert.IsFalse(File.Exists(env.Assets("A")));
        var second = await env.Run();

        Assert.AreEqual(1, env.Calls, "no second restore");
        Assert.AreEqual("reused", second.Reuse!.Decision);
        Assert.IsTrue(second.Clean);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(env.Assets("A")));
        Assert.IsTrue(File.Exists(env.Assets("B")));
        StringAssert.Contains(second.Summary(), "reused");
    }

    [TestMethod]
    public async Task ChangedInput_Restores_AndTheNewStateIsReusedNext()
    {
        RequireUnix();
        using var env = new Env();
        await env.Run();
        File.WriteAllText(Path.Combine(env.Checkout, "Directory.Build.props"), "<Project><PropertyGroup><X>1</X></PropertyGroup></Project>");
        env.ResetCheckout();
        var changed = await env.Run();
        Assert.AreEqual(2, env.Calls);
        Assert.AreEqual("miss", changed.Reuse!.Decision);
        Assert.AreEqual("inputs_changed", changed.Reuse.Reason);

        env.ResetCheckout();
        var again = await env.Run();
        Assert.AreEqual(2, env.Calls);
        Assert.AreEqual("reused", again.Reuse!.Decision);
    }

    [TestMethod]
    public async Task MissingPackage_Restores()
    {
        RequireUnix();
        using var env = new Env();
        await env.Run();
        File.Delete(env.PackageFile);
        env.ResetCheckout();
        var outcome = await env.Run(createPackage: false);
        Assert.AreEqual(2, env.Calls);
        Assert.AreEqual("packages_missing", outcome.Reuse!.Reason);
    }

    [TestMethod]
    public async Task PackagesConfigProject_IsNeverSavedOrReplayed()
    {
        RequireUnix();
        using var env = new Env();
        File.WriteAllText(Path.Combine(env.Checkout, "A", "packages.config"), "<packages />");
        var first = await env.Run();
        Assert.AreEqual("legacy_restore", first.Reuse!.Reason);
        Assert.AreEqual("legacy_restore", first.Reuse.SaveSkipped);
        Assert.IsFalse(first.Reuse.Saved);
        env.ResetCheckout();
        var second = await env.Run();
        Assert.AreEqual(2, env.Calls);
        Assert.AreEqual("legacy_restore", second.Reuse!.Reason);
    }

    [TestMethod]
    public async Task CustomIntermediatePath_IsSavedAndReplayed()
    {
        RequireUnix();
        using var env = new Env(restoreBody: CustomOutputBody);
        var first = await env.Run();
        Assert.IsTrue(first.Reuse!.Saved, first.Reuse.SaveSkipped);
        env.ResetCheckout();
        var second = await env.Run();
        Assert.AreEqual(1, env.Calls);
        Assert.AreEqual("reused", second.Reuse!.Decision);
        Assert.IsTrue(File.Exists(Path.Combine(env.Checkout, "artifacts", "obj", "A", "project.assets.json")));
        Assert.IsTrue(File.Exists(Path.Combine(env.Checkout, "artifacts", "obj", "B", "project.nuget.cache")));
    }

    [TestMethod]
    public async Task ProjectWithoutRestoreOutput_MakesTheSaveIncomplete()
    {
        RequireUnix();
        // B's output is never written: the state would replay a partial restore.
        using var env = new Env(restoreBody: RestoreBody.Replace("for p in $(find .", "for p in $(find ./A"));
        var outcome = await env.Run();
        Assert.IsFalse(outcome.Reuse!.Saved);
        Assert.AreEqual("incomplete", outcome.Reuse.SaveSkipped);
    }

    [TestMethod]
    public async Task ProjectWithoutNugetCache_MakesTheSaveIncomplete()
    {
        RequireUnix();
        using var env = new Env(restoreBody: RestoreBody.Replace("echo \"{\\\"expectedPackageFiles", "true \"{\\\"expectedPackageFiles").Replace("> \"$OUT/project.nuget.cache\"", "> /dev/null"));
        var outcome = await env.Run();
        Assert.IsFalse(outcome.Reuse!.Saved);
        Assert.AreEqual("incomplete", outcome.Reuse.SaveSkipped);
    }

    [TestMethod]
    public async Task CheckoutWithRestoreOutputBeforeTheRestore_IsNotSaved()
    {
        RequireUnix();
        using var env = new Env();
        Directory.CreateDirectory(env.Obj("A"));
        File.WriteAllText(Path.Combine(env.Obj("A"), "project.assets.json"), "{}");
        var outcome = await env.Run();
        Assert.AreEqual("stale_outputs", outcome.Reuse!.SaveSkipped);
    }

    [TestMethod]
    public async Task SavedState_IsNotReplayedOverStaleOutputs()
    {
        RequireUnix();
        using var env = new Env();
        await env.Run();
        // obj/ still holds the first job's output: a replay must not mix with it.
        var second = await env.Run();
        Assert.AreEqual(2, env.Calls);
        Assert.AreEqual("checkout_not_clean", second.Reuse!.Reason);
    }

    [TestMethod]
    public async Task RestoreInputSymlinkedOutsideTheCheckout_IsRefused()
    {
        RequireUnix();
        using var env = new Env();
        var outside = Path.Combine(Path.GetDirectoryName(env.Checkout)!, "outside.props");
        File.WriteAllText(outside, "<Project />");
        File.CreateSymbolicLink(Path.Combine(env.Checkout, "Directory.Packages.props"), outside);
        var outcome = await env.Run();
        Assert.AreEqual("symlink_outside", outcome.Reuse!.Reason);
        Assert.IsFalse(outcome.Reuse.Saved);
    }

    [TestMethod]
    public async Task ReplayFailure_FallsBackToAFullRestore()
    {
        RequireUnix();
        using var env = new Env();
        await env.Run();
        env.ResetCheckout();
        // A file where B's obj directory must go makes the replay fail after A's files are staged.
        File.WriteAllText(env.Obj("B"), "in the way");
        var second = await env.Run();
        Assert.AreEqual("replay_failed", second.Reuse!.Reason);
        Assert.AreEqual(2, env.Calls, "the full restore ran");
    }

    [TestMethod]
    public async Task ReplayFailure_RemovesWhatItWrote()
    {
        RequireUnix();
        using var env = new Env();
        var first = await env.Run();
        env.ResetCheckout();
        File.WriteAllText(env.Obj("B"), "in the way");
        var decision = RestoreStateStore.TryReplay(new RestoreReuse(env.State, env.Packages), env.Checkout, env.SavedInputs(), null);
        Assert.AreEqual("replay_failed", decision.MissReason);
        Assert.IsFalse(Directory.Exists(env.Obj("A")), "A's staged output is rolled back");
        Assert.IsTrue(first.Clean);
    }

    [TestMethod]
    public async Task NoState_Restores()
    {
        RequireUnix();
        using var env = new Env();
        var outcome = await env.Run();
        Assert.AreEqual("no_state", outcome.Reuse!.Reason);
    }

    [TestMethod]
    public async Task FloatingVersion_IsNeverSaved()
    {
        RequireUnix();
        using var env = new Env();
        File.WriteAllText(Path.Combine(env.Checkout, "A", "A.csproj"),
            $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>{Tfm}</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include=\"X\" Version=\"1.*\" /></ItemGroup></Project>");
        var first = await env.Run();
        Assert.IsFalse(first.Reuse!.Saved);
        await env.Run();
        Assert.AreEqual(2, env.Calls);
    }

    [TestMethod]
    public async Task AnotherRepository_HasItsOwnState()
    {
        RequireUnix();
        using var env = new Env();
        await env.Run();
        var other = await env.Run(stateName: "other");
        Assert.AreEqual(2, env.Calls, "state of one repository is never read for another");
        Assert.AreEqual("no_state", other.Reuse!.Reason);
    }

    [TestMethod]
    public async Task UncleanRestore_IsNotSaved()
    {
        RequireUnix();
        using var env = new Env(restoreBody: "exit 1");
        var outcome = await env.Run();
        Assert.IsFalse(outcome.Clean);
        Assert.IsFalse(outcome.Reuse!.Saved);
        Assert.IsFalse(Directory.Exists(Path.Combine(env.State, "current")));
    }

    [TestMethod]
    public async Task Timeout_IsReported_AndNothingIsSaved()
    {
        RequireUnix();
        using var env = new Env(restoreBody: "sleep 30; exit 0");
        var outcome = await env.Run(timeout: TimeSpan.FromSeconds(1));
        Assert.IsTrue(outcome.TimedOut);
        Assert.IsFalse(outcome.Reuse!.Saved);
        Assert.IsTrue(outcome.Passes.Any(p => p.Result == "timed_out"), string.Join(";", outcome.Passes.Select(p => p.Result)));
        StringAssert.Contains(outcome.Summary(), "timed_out");
        Assert.IsFalse(Directory.Exists(Path.Combine(env.State, "current")));
    }

    [TestMethod]
    public async Task Telemetry_RecordsPassesAndReachesTheJobTimings()
    {
        RequireUnix();
        using var env = new Env();
        var outcome = await env.Run();
        Assert.AreEqual(1, outcome.Passes.Count);
        Assert.AreEqual(2, outcome.Passes[0].Projects);
        var recorder = new JobTimingsRecorder();
        recorder.RecordRestore(outcome, persistentPackageFolder: true);
        var restore = recorder.Build().Restore!;
        Assert.AreEqual(1, restore.Passes!.Count);
        Assert.IsNotNull(restore.Reuse);
    }

    [TestMethod]
    [DataRow("1.*", true)]
    [DataRow("(,1.0]", true)]
    [DataRow("[1.0,)", false)]
    [DataRow("1.0.0", false)]
    public void HasFloatingVersion_Detects(string version, bool floating)
    {
        var text = $"<Project><ItemGroup><PackageReference Include=\"X\" Version=\"{version}\" /></ItemGroup></Project>";
        Assert.AreEqual(floating, RestoreStateStore.HasFloatingVersion(text));
    }

    [TestMethod]
    public void HasFloatingVersion_IgnoresAssemblyVersion()
    {
        Assert.IsFalse(RestoreStateStore.HasFloatingVersion("<Project><PropertyGroup><AssemblyVersion>1.0.*</AssemblyVersion></PropertyGroup></Project>"));
    }

    [TestMethod]
    public void HasFloatingVersion_RegexTimeout_CountsAsFloating()
    {
        var slow = new System.Text.RegularExpressions.Regex(@"^(a+)+$", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(1));
        var fast = new System.Text.RegularExpressions.Regex("nomatch");
        var text = new string('a', 60) + "!";
        Assert.IsTrue(RestoreStateStore.HasFloatingVersion(text, slow, fast));
        Assert.IsTrue(RestoreStateStore.HasFloatingVersion(text, fast, slow));
    }

    // ==== Issue #349: an unclean restore still saves what restored cleanly ==============================================

    private static string FailingBBody(string error)
        => RestoreBody.Replace("done; exit 0", "done; echo \"$(pwd)/B/B.csproj : error " + error + " [$(pwd)/All.slnx]\" >&2; exit 1");

    private const string RecordTraversal = "cp \"$2\" \"$(dirname \"$0\")/traversal-$$.proj\"; echo \"$(pwd)/B/B.csproj : error NU1202: still failing [$(pwd)/All.slnx]\" >&2; exit 1";

    [TestMethod]
    [DataRow("NU1202: Package X 1.0.0 is not compatible with net10.0.")]
    [DataRow("NU1301: Unable to load the service index for source ******feed.example.test/index.json.")]
    [DataRow("NU4242: Something unrecognised happened.")]
    public async Task UncleanRestore_SavesTheCleanProjects_ReplaysThem_AndRestoresOnlyTheFailingOne(string error)
    {
        RequireUnix();
        using var env = new Env(restoreBody: FailingBBody(error), msbuildBody: RecordTraversal);
        var first = await env.Run();
        Assert.IsFalse(first.Clean);
        Assert.IsTrue(first.Reuse!.Saved, first.Reuse.SaveSkipped);
        Assert.AreEqual(1, first.Reuse.Projects);
        Assert.AreEqual(1, first.Reuse.ProjectsRemaining);
        StringAssert.Contains(first.Summary(), "saved=partial");
        var manifest = File.ReadAllText(Path.Combine(env.State, "current", RestoreStateStore.ManifestName));
        StringAssert.Contains(manifest, "A/obj/project.assets.json");
        // Never a failing project: its failed-restore assets are not saved, whatever the error was.
        Assert.IsFalse(manifest.Contains("B/obj", StringComparison.Ordinal), manifest);
        var assetsOfA = File.ReadAllText(env.Assets("A"));
        var callsAfterFirst = env.Calls;

        env.ResetCheckout();
        var second = await env.Run();
        Assert.AreEqual("partial", second.Reuse!.Decision);
        Assert.AreEqual(1, second.Reuse.Projects);
        Assert.AreEqual(1, second.Reuse.ProjectsRemaining);
        Assert.AreEqual(callsAfterFirst, env.Calls, "no whole-solution restore runs when the state covers the clean projects");
        Assert.AreEqual(1, second.SolutionsCoveredByReplay);
        Assert.AreEqual(assetsOfA, File.ReadAllText(env.Assets("A")), "the replayed assets are byte-identical");
        Assert.IsTrue(env.Traversals.Contains("B.csproj", StringComparison.Ordinal), "the failing project is restored again");
        Assert.IsFalse(env.Traversals.Contains("A.csproj", StringComparison.Ordinal), "the clean project is not restored again");
        StringAssert.Contains(second.Summary(), "cache=partial");
    }

    [TestMethod]
    public async Task FailureWithNoProjectAttributed_IsNotSavedAtAll()
    {
        RequireUnix();
        using var env = new Env(restoreBody: RestoreBody.Replace("done; exit 0", "done; echo 'error MSB1009: bad' >&2; exit 1"));
        var outcome = await env.Run();
        Assert.IsFalse(outcome.Clean);
        Assert.IsFalse(outcome.Reuse!.Saved);
        Assert.AreEqual("restore_not_clean", outcome.Reuse.SaveSkipped);
        Assert.IsFalse(Directory.Exists(Path.Combine(env.State, "current")));
    }

    [TestMethod]
    public async Task ErrorCodes_AreRecordedInTheJobTimings_AndLogged()
    {
        RequireUnix();
        using var env = new Env(restoreBody: FailingBBody("NETSDK1147: workloads must be installed: ios"), msbuildBody: RecordTraversal);
        var outcome = await env.Run();
        Assert.AreEqual("NETSDK1147", outcome.ErrorCodes()[0].Code);
        Assert.IsTrue(outcome.ErrorCodes()[0].Deterministic);
        Assert.IsFalse(outcome.HasNonDeterministicFailure);
        var recorder = new JobTimingsRecorder();
        recorder.RecordRestore(outcome, persistentPackageFolder: true);
        var restore = recorder.Build().Restore!;
        Assert.AreEqual(1, restore.ProjectsFailed);
        Assert.AreEqual("NETSDK1147", restore.ErrorCodes[0].Code);
        Assert.IsFalse(restore.FailuresNotProvablyDeterministic);
    }

    private const string StaticGraphFails = "case \"$*\" in *RestoreUseStaticGraphEvaluation*) echo 'error NETSDK1147: static graph failed' >&2; exit 1;; esac\n";
    private const string StaticGraphFailsWithoutCode = "case \"$*\" in *RestoreUseStaticGraphEvaluation*) echo 'error : killed' >&2; exit 1;; esac\n";

    [TestMethod]
    public async Task StaticGraphFailure_IsRememberedPerSolutionAndFingerprint_AndInvalidatedByTheFingerprint()
    {
        RequireUnix();
        using var env = new Env(restoreBody: StaticGraphFails + RestoreBody);
        PackageRestoreRunner.ResetStaticGraphMemo();
        var first = await env.Run(staticGraph: true);
        Assert.AreEqual(1, first.StaticGraphFallbacks, "the first job pays for the failed static-graph attempt");
        Assert.AreEqual(0, first.StaticGraphSkippedByMemo);
        Assert.AreEqual(2, env.Calls);
        Assert.IsTrue(File.Exists(Path.Combine(env.State, RestoreStateStore.MemoName)));

        // Same inputs, no usable state (so a restore runs) and no in-process memory: the persisted memo skips the attempt.
        env.RemoveSavedState();
        env.ResetCheckout();
        PackageRestoreRunner.ResetStaticGraphMemo();
        var second = await env.Run(staticGraph: true);
        Assert.AreEqual(1, second.StaticGraphSkippedByMemo);
        Assert.AreEqual(0, second.StaticGraphPasses);
        Assert.AreEqual(0, second.StaticGraphFallbacks);
        Assert.AreEqual(3, env.Calls, "one restore, without static-graph evaluation");

        // A changed input changes the fingerprint: the memo is not applied.
        File.AppendAllText(Path.Combine(env.Checkout, "B", "B.csproj"), "<!-- changed -->");
        env.RemoveSavedState();
        env.ResetCheckout();
        PackageRestoreRunner.ResetStaticGraphMemo();
        var third = await env.Run(staticGraph: true);
        Assert.AreEqual(0, third.StaticGraphSkippedByMemo);
        Assert.AreEqual(1, third.StaticGraphFallbacks);
        Assert.AreEqual(5, env.Calls);
    }

    [TestMethod]
    public async Task CodelessStaticGraphFailure_IsNeverRemembered()
    {
        RequireUnix();
        using var env = new Env(restoreBody: StaticGraphFailsWithoutCode + RestoreBody);
        PackageRestoreRunner.ResetStaticGraphMemo();
        var first = await env.Run(staticGraph: true);
        Assert.AreEqual(1, first.StaticGraphFallbacks);
        Assert.IsFalse(File.Exists(Path.Combine(env.State, RestoreStateStore.MemoName)), "no recognised code: nothing is proven");

        env.RemoveSavedState();
        env.ResetCheckout();
        PackageRestoreRunner.ResetStaticGraphMemo();
        var second = await env.Run(staticGraph: true);
        Assert.AreEqual(0, second.StaticGraphSkippedByMemo);
        Assert.AreEqual(1, second.StaticGraphFallbacks, "static graph is tried again");
    }

    [TestMethod]
    public async Task TransientFlip_RestoresThePreviouslyFailingProject_AndSavesTheFullState()
    {
        RequireUnix();
        // Job 1 fails B; once the flag exists the same script restores everything cleanly.
        var body = "if [ -f \"$PKG_DIR/ok.flag\" ]; then\n" + RestoreBody + "\nelse\n"
            + FailingBBody("NU1202: Package X 1.0.0 is not compatible with net10.0.") + "\nfi";
        using var env = new Env(restoreBody: body, msbuildBody: RecordTraversal);
        var first = await env.Run();
        Assert.IsFalse(first.Clean);
        Assert.AreEqual(1, first.Reuse!.ProjectsRemaining);

        File.WriteAllText(Path.Combine(env.Packages, "ok.flag"), "1");
        env.ResetCheckout();
        var second = await env.Run();
        Assert.AreEqual("partial", second.Reuse!.Decision);
        Assert.IsTrue(env.Traversals.Contains("B.csproj", StringComparison.Ordinal), "the previously failing project is restored");
        Assert.IsTrue(File.Exists(env.Assets("A")));
    }

    [TestMethod]
    public async Task CorruptManifest_IsRefused_AndFallsBackToAFullRestore()
    {
        RequireUnix();
        using var env = new Env();
        await env.Run();
        var manifestPath = Path.Combine(env.State, "current", RestoreStateStore.ManifestName);
        var good = File.ReadAllText(manifestPath);
        var node = System.Text.Json.Nodes.JsonNode.Parse(good)!.AsObject();
        var projectPaths = node["project_paths"]!.AsArray();

        foreach (var mutate in new Action<System.Text.Json.Nodes.JsonObject>[]
        {
            o => o["outputs"]!.AsObject().Remove("A/A.csproj"),
            o => o["remaining"]!.AsArray().Add("A/A.csproj"),
            o => { o["project_paths"]!.AsArray().Add("Ghost/Ghost.csproj"); o["projects"] = 3; o["outputs"]!.AsObject()["Ghost/Ghost.csproj"] = new System.Text.Json.Nodes.JsonObject { ["assets"] = "Ghost/obj/project.assets.json", ["cache"] = "Ghost/obj/project.nuget.cache" }; },
            o => o["outputs"]!.AsObject()["A/A.csproj"]!["cache"] = "A/obj/missing.cache"
        })
        {
            Assert.IsNotNull(projectPaths);
            var copy = System.Text.Json.Nodes.JsonNode.Parse(good)!.AsObject();
            mutate(copy);
            File.WriteAllText(manifestPath, copy.ToJsonString());
            env.ResetCheckout();
            var decision = RestoreStateStore.TryReplay(new RestoreReuse(env.State, env.Packages), env.Checkout, env.SavedInputs(),
                null, [Path.Combine(env.Checkout, "All.slnx")]);
            Assert.IsNotNull(decision.MissReason, copy.ToJsonString());
            Assert.IsFalse(File.Exists(env.Assets("A")), "nothing is left behind");
        }
    }

    [TestMethod]
    public void StaleMemoTemps_AreSweptWhenTheMemoIsWritten()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"sextant-memo-sweep-{Guid.NewGuid():N}");
        try
        {
            var reuse = new RestoreReuse(Path.Combine(dir, "state"), Path.Combine(dir, "packages"));
            Directory.CreateDirectory(reuse.StateDirectory);
            var stale = Path.Combine(reuse.StateDirectory, ".memo-stale.tmp");
            var fresh = Path.Combine(reuse.StateDirectory, ".memo-fresh.tmp");
            File.WriteAllText(stale, "x");
            File.WriteAllText(fresh, "x");
            File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-3));
            RestoreStateStore.SaveStaticGraphFailures(reuse, "fp", new HashSet<string> { "a.sln" }, null);
            Assert.IsFalse(File.Exists(stale));
            Assert.IsTrue(File.Exists(fresh));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [TestMethod]
    public void StaticGraphMemo_IsBoundToItsFingerprint()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"sextant-memo-{Guid.NewGuid():N}");
        try
        {
            var reuse = new RestoreReuse(Path.Combine(dir, "state"), Path.Combine(dir, "packages"));
            Assert.AreEqual(0, RestoreStateStore.LoadStaticGraphFailures(reuse, "fp1").Count, "miss: nothing remembered");
            RestoreStateStore.SaveStaticGraphFailures(reuse, "fp1", new HashSet<string> { "a.sln" }, null);
            RestoreStateStore.SaveStaticGraphFailures(reuse, "fp1", new HashSet<string> { "b.sln" }, null);
            CollectionAssert.AreEquivalent(new[] { "a.sln", "b.sln" }, RestoreStateStore.LoadStaticGraphFailures(reuse, "fp1").ToArray());
            Assert.AreEqual(0, RestoreStateStore.LoadStaticGraphFailures(reuse, "fp2").Count, "another fingerprint: invalidated");
            RestoreStateStore.SaveStaticGraphFailures(reuse, "fp2", new HashSet<string> { "c.sln" }, null);
            CollectionAssert.AreEqual(new[] { "c.sln" }, RestoreStateStore.LoadStaticGraphFailures(reuse, "fp2").ToArray());
            Assert.AreEqual(0, RestoreStateStore.LoadStaticGraphFailures(reuse, "fp1").Count, "the memo holds one fingerprint");
            File.WriteAllText(Path.Combine(reuse.StateDirectory, RestoreStateStore.MemoName), "{ not json");
            Assert.AreEqual(0, RestoreStateStore.LoadStaticGraphFailures(reuse, "fp2").Count, "an unreadable memo is a miss");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void RequireUnix()
    {
        if (OperatingSystem.IsWindows())
            Assert.Inconclusive("The scripted dotnet host is a POSIX shell script.");
    }

    private sealed class Env : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"sextant-restore-state-{Guid.NewGuid():N}");
        private readonly string _fake;
        private readonly string _callLog;

        public Env(string? restoreBody = null, string? msbuildBody = null)
        {
            Directory.CreateDirectory(_root);
            Checkout = Path.Combine(_root, "checkout");
            State = Path.Combine(_root, "state", "repo");
            Packages = Path.Combine(_root, "packages");
            PackageFile = Path.Combine(Packages, "x", "1.0.0", ".nupkg.metadata");
            Directory.CreateDirectory(Packages);
            _callLog = Path.Combine(_root, "calls.txt");
            _fake = Path.Combine(_root, "dotnet");
            File.WriteAllText(_fake, ("#!/bin/sh\necho \"$@\" >> \"" + _callLog + "\"\ncase \"$1\" in\n  restore) PKG_FILE=\"" + PackageFile + "\"; PKG_DIR=\"" + Packages + "\"\n"
                + (restoreBody ?? RestoreBody) + "\n ;;\n  msbuild) " + (msbuildBody ?? "exit 1") + " ;;\nesac\n").Replace("\r\n", "\n"));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(_fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Populate();
        }

        public string Checkout { get; }
        public string State { get; }
        public string Packages { get; }
        public string PackageFile { get; }
        public int Calls => File.Exists(_callLog) ? File.ReadAllLines(_callLog).Count(l => l.StartsWith("restore", StringComparison.Ordinal)) : 0;
        public string Assets(string project) => Path.Combine(Checkout, project, "obj", "project.assets.json");

        private void Populate()
        {
            foreach (var p in new[] { "A", "B" })
            {
                Directory.CreateDirectory(Path.Combine(Checkout, p));
                File.WriteAllText(Path.Combine(Checkout, p, p + ".csproj"),
                    $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>{Tfm}</TargetFramework></PropertyGroup></Project>");
            }
            File.WriteAllText(Path.Combine(Checkout, "All.slnx"), "<Solution><Project Path=\"A/A.csproj\" /><Project Path=\"B/B.csproj\" /></Solution>");
        }

        public void ResetCheckout()
        {
            foreach (var p in new[] { "A", "B" })
            {
                var obj = Path.Combine(Checkout, p, "obj");
                if (Directory.Exists(obj))
                    Directory.Delete(obj, recursive: true);
                else if (File.Exists(obj))
                    File.Delete(obj);
            }
            var artifacts = Path.Combine(Checkout, "artifacts");
            if (Directory.Exists(artifacts))
                Directory.Delete(artifacts, recursive: true);
        }

        public RestoreStateStore.Inputs SavedInputs()
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(State, "current", RestoreStateStore.ManifestName)));
            var name = doc.RootElement.EnumerateObject().First(p => p.Name.Equals("fingerprint", StringComparison.OrdinalIgnoreCase)).Value.GetString()!;
            return new RestoreStateStore.Inputs(name, false, 0);
        }

        public string Obj(string project) => Path.Combine(Checkout, project, "obj");

        // The generated traversal projects the fake msbuild copied (its $2 is the traversal), one file per call.
        public string Traversals => string.Concat(Directory.EnumerateFiles(_root, "traversal-*.proj").Order().Select(File.ReadAllText));

        public void RemoveSavedState() => Directory.Delete(Path.Combine(State, "current"), recursive: true);

        public Task<PackageRestoreOutcome> Run(TimeSpan? timeout = null, string? stateName = null, bool createPackage = true, bool staticGraph = false)
        {
            if (createPackage && !File.Exists(PackageFile))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PackageFile)!);
                File.WriteAllText(PackageFile, "x");
                Directory.CreateDirectory(Path.Combine(Packages, "y", "2.0.0"));
                File.WriteAllText(Path.Combine(Packages, "y", "2.0.0", ".nupkg.metadata"), "y");
            }
            var state = stateName is null ? State : Path.Combine(_root, "state", stateName);
            return new PackageRestoreRunner(timeout: timeout ?? TimeSpan.FromSeconds(60), staticGraphRestore: staticGraph) { DotnetPath = _fake }
                .RunAsync(Checkout, [Path.Combine(Checkout, "All.slnx")], limit: null, CancellationToken.None,
                    Path.Combine(_root, "scratch"), new RestoreReuse(state, Packages));
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
