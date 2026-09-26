using Sextant.Indexer;

namespace Sextant.Indexer.Tests;

/// <summary>
/// Proves the deterministic, explicit solution selection of <see cref="SolutionSelector"/> (issue #109):
/// an explicit per-repo <c>solutions</c> list is honored in order (missing/invalid entries recorded
/// skipped-with-reason, never dropped), and — with no config — a single stable default root solution is
/// chosen, preferring a root-level, Linux-loadable solution over a nested or platform-head one. These are
/// hermetic: they plant empty <c>.sln</c>/<c>.slnx</c> files on disk (no MSBuild), so only the SELECTION
/// logic is under test.
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

    [TestMethod]
    public void NoConfig_PrefersLinuxLoadableRoot_OverPlatformHeadAndNested()
    {
        // A root with several heads plus a nested solution. The deterministic default must prefer the
        // explicitly-Linux root over the neutral root, the platform head, and the nested solution.
        Plant("App-no-macos.slnx");
        Plant("App.slnx");
        Plant("App-ios.slnx");
        Plant("nested/Deep.slnx");

        var selection = SolutionSelector.Select(_root, configuredSolutions: null);

        Assert.AreEqual(SolutionSelectionSource.DefaultRoot, selection.Source);
        Assert.AreEqual(1, selection.SolutionPaths.Count);
        Assert.IsTrue(selection.SolutionPaths[0].EndsWith("App-no-macos.slnx", StringComparison.Ordinal),
            $"expected the Linux-loadable root, got '{selection.SolutionPaths[0]}'");
        Assert.AreEqual(0, selection.SkippedSolutions.Count);
    }

    [TestMethod]
    public void NoConfig_NeutralRootBeatsPlatformHead_WhenNoLinuxMarker()
    {
        Plant("App.slnx");        // neutral
        Plant("App-android.slnx"); // platform head
        Plant("App-windows.sln");  // platform head

        var selection = SolutionSelector.Select(_root, configuredSolutions: null);

        Assert.IsTrue(selection.SolutionPaths[0].EndsWith("App.slnx", StringComparison.Ordinal),
            $"a neutral root must beat a platform head, got '{selection.SolutionPaths[0]}'");
    }

    [TestMethod]
    public void NoConfig_PrefersSlnxOverSln_AtSameDepthAndPlatformRank()
    {
        Plant("App.sln");
        Plant("App.slnx");

        var selection = SolutionSelector.Select(_root, configuredSolutions: null);

        Assert.IsTrue(selection.SolutionPaths[0].EndsWith("App.slnx", StringComparison.Ordinal),
            $".slnx must be preferred over .sln at the same rank, got '{selection.SolutionPaths[0]}'");
    }

    [TestMethod]
    public void NoConfig_PrefersRootOverDeeperSolution()
    {
        Plant("deep/nested/Root.slnx");
        Plant("Shallow.slnx");

        var selection = SolutionSelector.Select(_root, configuredSolutions: null);

        Assert.IsTrue(selection.SolutionPaths[0].EndsWith("Shallow.slnx", StringComparison.Ordinal),
            "a shallower (root) solution must be preferred over a deeper one");
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
        Assert.IsTrue(first.SolutionPaths[0].EndsWith("Alpha.slnx", StringComparison.Ordinal),
            "the ordinal tiebreak picks the lexicographically-first path");
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
