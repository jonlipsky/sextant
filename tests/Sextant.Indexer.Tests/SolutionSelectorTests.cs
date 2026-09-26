using Sextant.Indexer;

namespace Sextant.Indexer.Tests;

/// <summary>
/// Proves the deterministic, explicit solution selection of <see cref="SolutionSelector"/> (issues #109,
/// #124): an explicit per-repo <c>solutions</c> list is honored in order (missing/invalid entries recorded
/// skipped-with-reason, never dropped), and — with no config — EVERY discovered solution is selected (the
/// default union), in a total order that is independent of enumeration order and host path separator:
/// shallow before deep, explicitly-Linux before neutral before platform heads, <c>.slnx</c> before
/// <c>.sln</c>, then a '/'-normalized ordinal path. These are hermetic: they plant empty
/// <c>.sln</c>/<c>.slnx</c> files on disk (no MSBuild), so only the SELECTION logic is under test.
/// </summary>
[TestClass]
public sealed class SolutionSelectorTests
{
    private string _root = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "sextant_solsel_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    private string Plant(string relativePath)
    {
        var full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, string.Empty);
        return full;
    }

    private string[] Relative(IEnumerable<string> paths) =>
        paths.Select(p => Path.GetRelativePath(_root, p).Replace('\\', '/')).ToArray();

    // Replaces the pre-#124 single-pick test "NoConfig_PrefersLinuxLoadableRoot_OverPlatformHeadAndNested":
    // the same tree now selects EVERY solution; the old winner is still FIRST (the ranking became the order).
    [TestMethod]
    public void NoConfig_SelectsUnionOfAllDiscovered_LinuxRootFirst_PlatformHeadAndNestedIncluded()
    {
        Plant("App-no-macos.slnx");
        Plant("App.slnx");
        Plant("App-ios.slnx");
        Plant("nested/Deep.slnx");

        var selection = SolutionSelector.Select(_root, configuredSolutions: null);

        Assert.AreEqual(SolutionSelectionSource.DefaultUnion, selection.Source);
        CollectionAssert.AreEqual(
            new[] { "App-no-macos.slnx", "App.slnx", "App-ios.slnx", "nested/Deep.slnx" },
            Relative(selection.SolutionPaths),
            "every discovered solution is selected: root before nested, Linux-marker before neutral before " +
            "the platform head (which is INCLUDED, not dropped)");
        Assert.AreEqual(0, selection.SkippedSolutions.Count);
        Assert.AreEqual(4, selection.DiscoveredSolutions.Count);
        CollectionAssert.AreEquivalent(selection.DiscoveredSolutions.ToList(), selection.SolutionPaths.ToList(),
            "under the default union nothing discovered is left unselected");
    }

    // Replaces "NoConfig_NeutralRootBeatsPlatformHead_WhenNoLinuxMarker": platform heads are now selected
    // too, ordered after the neutral root.
    [TestMethod]
    public void NoConfig_OrdersNeutralBeforePlatformHeads_AndKeepsTheHeads()
    {
        Plant("App.slnx");         // neutral
        Plant("App-android.slnx"); // platform head
        Plant("App-windows.sln");  // platform head

        var selection = SolutionSelector.Select(_root, configuredSolutions: null);

        CollectionAssert.AreEqual(
            new[] { "App.slnx", "App-android.slnx", "App-windows.sln" },
            Relative(selection.SolutionPaths),
            "a neutral root orders before platform heads; both heads are still selected");
    }

    // Replaces "NoConfig_PrefersSlnxOverSln_AtSameDepthAndPlatformRank": both are selected, .slnx first.
    [TestMethod]
    public void NoConfig_OrdersSlnxBeforeSln_AtSameDepthAndPlatformRank()
    {
        Plant("App.sln");
        Plant("App.slnx");

        var selection = SolutionSelector.Select(_root, configuredSolutions: null);

        CollectionAssert.AreEqual(new[] { "App.slnx", "App.sln" }, Relative(selection.SolutionPaths),
            ".slnx orders before .sln at the same rank; both are selected");
    }

    // Replaces "NoConfig_PrefersRootOverDeeperSolution": both are selected, the shallower one first.
    [TestMethod]
    public void NoConfig_OrdersRootBeforeDeeperSolution()
    {
        Plant("deep/nested/Root.slnx");
        Plant("Shallow.slnx");

        var selection = SolutionSelector.Select(_root, configuredSolutions: null);

        CollectionAssert.AreEqual(new[] { "Shallow.slnx", "deep/nested/Root.slnx" }, Relative(selection.SolutionPaths),
            "a shallower (root) solution orders before a deeper one; both are selected");
    }

    [TestMethod]
    public void NoConfig_IsStableAcrossRuns()
    {
        Plant("Beta.slnx");
        Plant("Alpha.slnx");
        Plant("Gamma.slnx");

        var first = SolutionSelector.Select(_root, null);
        var second = SolutionSelector.Select(_root, null);

        // All three are root-level, neutral, .slnx → the ordinal path tiebreak decides deterministically.
        CollectionAssert.AreEqual(first.SolutionPaths.ToList(), second.SolutionPaths.ToList(),
            "selection must be stable across runs for an identical tree");
        CollectionAssert.AreEqual(new[] { "Alpha.slnx", "Beta.slnx", "Gamma.slnx" }, Relative(first.SolutionPaths),
            "the ordinal tiebreak orders equally-ranked solutions lexicographically");
    }

    [TestMethod]
    public void NoConfig_SingleSolution_SelectsJustThatSolution()
    {
        // A one-solution repo is unaffected by #124: the union of one is that solution (and the loader keeps
        // its whole-solution fast path).
        Plant("src/Only.slnx");

        var selection = SolutionSelector.Select(_root, configuredSolutions: null);

        Assert.AreEqual(SolutionSelectionSource.DefaultUnion, selection.Source);
        CollectionAssert.AreEqual(new[] { "src/Only.slnx" }, Relative(selection.SolutionPaths));
    }

    [TestMethod]
    public void OrderForUnion_IsIndependentOfInputOrder()
    {
        // Every permutation of the same SET yields the identical order (enumeration-order independence):
        // depth, then platform rank, then extension, then ordinal path.
        var set = new[]
        {
            "z/App.slnx", "App-ios.slnx", "App.sln", "App.slnx", "App-linux.slnx", "b/Mac.sln", "a/Core.slnx",
            "a/Core.sln", "a/b/Deep.slnx"
        }.Select(r => Path.GetFullPath(Path.Combine(_root, r.Replace('/', Path.DirectorySeparatorChar)))).ToArray();
        var expected = new[]
        {
            "App-linux.slnx", "App.slnx", "App.sln", "App-ios.slnx", "a/Core.slnx", "z/App.slnx", "a/Core.sln",
            "b/Mac.sln", "a/b/Deep.slnx"
        };

        var rng = new Random(124);
        for (var i = 0; i < 50; i++)
        {
            var shuffled = set.OrderBy(_ => rng.Next()).ToArray();
            CollectionAssert.AreEqual(expected, Relative(SolutionSelector.OrderForUnion(_root, shuffled)),
                $"permutation {i} must produce the identical union order");
        }
        CollectionAssert.AreEqual(expected, Relative(SolutionSelector.OrderForUnion(_root, set.Reverse())));
    }

    [TestMethod]
    public void OrderForUnion_DeduplicatesTheSameSolution()
    {
        var a = Path.Combine(_root, "A.slnx");
        var messy = Path.Combine(_root, ".", "A.slnx");

        var ordered = SolutionSelector.OrderForUnion(_root, [a, messy]);

        Assert.AreEqual(1, ordered.Count, "a differently-spelled path to the same solution is selected once");
    }

    [TestMethod]
    public void NoConfig_OrderingUsesSlashNormalizedPaths_IdenticalOnEveryOs()
    {
        // With the raw Windows separator, "x/a0/…" would sort BEFORE "x/a\…" ('0' 0x30 < '\' 0x5C) while on
        // Linux "x/a/…" sorts first ('/' 0x2F < '0'). Normalizing to '/' makes the order the same everywhere.
        Plant("x/a0/c.slnx");
        Plant("x/a/b.slnx");

        var selection = SolutionSelector.Select(_root, configuredSolutions: null);
        var discovered = SolutionSelector.DiscoverSolutions(_root);

        CollectionAssert.AreEqual(new[] { "x/a/b.slnx", "x/a0/c.slnx" }, Relative(selection.SolutionPaths),
            "the union order must not depend on the host directory separator");
        CollectionAssert.AreEqual(new[] { "x/a/b.slnx", "x/a0/c.slnx" }, Relative(discovered),
            "the discovery order must not depend on the host directory separator");
    }

    [TestMethod]
    public void ToSlashSeparated_NormalizesAWindowsHostSeparator_OnEveryOs()
    {
        // Exercises the Windows branch on every OS: the CI gate runs on Linux, where the host separator is
        // already '/', so the discovery-level test above cannot catch a regression there.
        var ab = SolutionSelector.ToSlashSeparated(@"x\a\b.slnx", '\\');
        var a0c = SolutionSelector.ToSlashSeparated(@"x\a0\c.slnx", '\\');
        Assert.AreEqual("x/a/b.slnx", ab);
        Assert.AreEqual("x/a0/c.slnx", a0c);
        Assert.IsTrue(string.CompareOrdinal(ab, a0c) < 0, "normalized keys order a/ before a0/ as on Linux");
        Assert.IsTrue(string.CompareOrdinal(@"x\a\b.slnx", @"x\a0\c.slnx") > 0,
            "sanity: the raw Windows keys order the other way, which is what normalization prevents");
        // On a '/' host a '\' is a legal file-name character and must not be reinterpreted as a separator.
        Assert.AreEqual(@"x/we\ird.slnx", SolutionSelector.ToSlashSeparated(@"x/we\ird.slnx", '/'));
    }

    [TestMethod]
    public void NoConfig_ExcludesObjAndBinDirectories()
    {
        Plant("obj/Generated.slnx");
        Plant("bin/Output.sln");
        Plant("Real.slnx");

        var discovered = SolutionSelector.DiscoverSolutions(_root);

        Assert.AreEqual(1, discovered.Count, "solutions under obj/ and bin/ must be excluded from discovery");
        Assert.IsTrue(discovered[0].EndsWith("Real.slnx", StringComparison.Ordinal));
    }

    [TestMethod]
    public void NoConfig_EmptyCheckout_YieldsNoneWithNoSolutions()
    {
        var selection = SolutionSelector.Select(_root, configuredSolutions: null);

        Assert.AreEqual(SolutionSelectionSource.None, selection.Source);
        Assert.IsFalse(selection.HasSolutions);
        Assert.AreEqual(0, selection.SolutionPaths.Count);
    }

    [TestMethod]
    public void Config_HonorsListedSolutionsInOrder_UnionAcrossHeads()
    {
        Plant("Core.slnx");
        Plant("heads/Mobile.slnx");
        Plant("Unused.slnx"); // present but not listed → must not be selected

        var selection = SolutionSelector.Select(_root, ["Core.slnx", "heads/Mobile.slnx"]);

        Assert.AreEqual(SolutionSelectionSource.Configured, selection.Source);
        Assert.AreEqual(2, selection.SolutionPaths.Count);
        Assert.IsTrue(selection.SolutionPaths[0].EndsWith("Core.slnx", StringComparison.Ordinal));
        Assert.IsTrue(selection.SolutionPaths[1].EndsWith("Mobile.slnx", StringComparison.Ordinal));
        Assert.AreEqual(0, selection.SkippedSolutions.Count);
    }

    [TestMethod]
    public void Config_MissingEntry_IsSkippedWithReason_NotDropped()
    {
        Plant("Real.slnx");

        var selection = SolutionSelector.Select(_root, ["Real.slnx", "Ghost.slnx"]);

        Assert.AreEqual(1, selection.SolutionPaths.Count, "the existing solution is still selected");
        Assert.IsTrue(selection.SolutionPaths[0].EndsWith("Real.slnx", StringComparison.Ordinal));
        Assert.AreEqual(1, selection.SkippedSolutions.Count, "the missing entry is recorded, never silently dropped");
        Assert.AreEqual("Ghost.slnx", selection.SkippedSolutions[0].RequestedPath);
        StringAssert.Contains(selection.SkippedSolutions[0].Reason, "not found");
    }

    [TestMethod]
    public void Config_EntryOutsideCheckout_IsSkippedWithContainmentReason()
    {
        Plant("Real.slnx");

        var selection = SolutionSelector.Select(_root, ["../escape.slnx", "Real.slnx"]);

        Assert.AreEqual(1, selection.SolutionPaths.Count);
        Assert.IsTrue(selection.SkippedSolutions.Any(s =>
            s.RequestedPath == "../escape.slnx" && s.Reason.Contains("outside", StringComparison.Ordinal)),
            "a configured path escaping the checkout must be skipped-with-reason");
    }

    [TestMethod]
    public void Config_NonSolutionEntry_IsSkippedWithReason()
    {
        Plant("Real.slnx");
        Plant("notes.txt");

        var selection = SolutionSelector.Select(_root, ["notes.txt", "Real.slnx"]);

        Assert.AreEqual(1, selection.SolutionPaths.Count);
        Assert.IsTrue(selection.SkippedSolutions.Any(s =>
            s.RequestedPath == "notes.txt" && s.Reason.Contains("not a .sln", StringComparison.Ordinal)),
            "a non-solution configured entry must be skipped-with-reason");
    }

    [TestMethod]
    public void Config_DuplicateEntries_AreDeduplicated()
    {
        Plant("Core.slnx");

        var selection = SolutionSelector.Select(_root, ["Core.slnx", "Core.slnx"]);

        Assert.AreEqual(1, selection.SolutionPaths.Count, "a repeated configured solution is selected once");
    }
}
