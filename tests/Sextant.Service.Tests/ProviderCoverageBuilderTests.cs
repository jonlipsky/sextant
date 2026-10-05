using Microsoft.CodeAnalysis;
using Sextant.Core;
using Sextant.Indexer;

namespace Sextant.Service.Tests;

/// <summary>
/// Issue #162 (b): the per-provider coverage rule (<see cref="SnapshotCoverageBuilder.BuildProviders"/>). A
/// Phase-12 provider snapshot holds only the projects of its submodule subtree that the PARENT's selection
/// declared or loaded, so its verdict is computed over that subtree: partial when a provider project was
/// skipped, left unreached, or when the provider's own solutions were not the selection basis. Reasons name
/// provider-relative paths only, because the record is served to readers of the provider repository.
/// </summary>
[TestClass]
public class ProviderCoverageBuilderTests
{
    private static readonly string CheckoutDir =
        Path.GetFullPath(Path.Combine(Path.GetTempPath(), "sextant_provcov_checkout"));

    private static string At(string relative) => Path.GetFullPath(Path.Combine(CheckoutDir, relative));

    private static readonly DeclaredSubmodule Mix = new("libs/mix", Populated: true);

    [TestMethod]
    public void ProviderSolutionSelected_AndEveryProjectLoaded_IsComplete()
    {
        var resolution = Resolution([At("App.slnx"), At("libs/mix/Mix.slnx")]);
        var load = Load([At("src/App/App.csproj"), At("libs/mix/src/Mix/Mix.csproj")]);
        var inventory = Inventory([At("src/App/App.csproj"), At("libs/mix/src/Mix/Mix.csproj")], [Mix],
            solutions: [At("libs/mix/Mix.slnx")]);

        var coverage = SnapshotCoverageBuilder.BuildProviders(CheckoutDir, resolution, load, inventory)["libs/mix"];

        Assert.AreEqual(SnapshotCoverageVerdict.Complete, coverage.Verdict, string.Join(" | ", coverage.Reasons));
        Assert.AreEqual("default_union", coverage.SelectionSource, "the provider's own solution was the selection basis");
        Assert.AreEqual(1, coverage.SolutionsSelected);
        Assert.AreEqual(1, coverage.ProjectsDeclared);
        Assert.AreEqual(1, coverage.ProjectsLoaded);
        Assert.AreEqual(1, coverage.ProjectFilesOnDisk, "the parent's projects are outside the provider subtree");
    }

    [TestMethod]
    public void ProviderBuiltOnlyFromParentReachableProjects_IsPartial()
    {
        // Configured parent scope: only App.slnx; Mix is loaded only through App's ProjectReference.
        var resolution = Resolution([At("App.slnx")], SolutionSelectionSource.Configured);
        var load = Load([At("src/App/App.csproj")], loaded: [At("src/App/App.csproj"), At("libs/mix/src/Mix/Mix.csproj")]);
        var inventory = Inventory([At("src/App/App.csproj"), At("libs/mix/src/Mix/Mix.csproj")], [Mix],
            solutions: [At("libs/mix/Mix.slnx")]);

        var coverage = SnapshotCoverageBuilder.BuildProviders(CheckoutDir, resolution, load, inventory)["libs/mix"];

        Assert.AreEqual(SnapshotCoverageVerdict.Partial, coverage.Verdict);
        Assert.AreEqual(SnapshotCoverageBuilder.ParentSelectionSource, coverage.SelectionSource);
        Assert.AreEqual(1, coverage.SolutionsDiscovered);
        Assert.AreEqual(0, coverage.SolutionsSelected);
        Assert.AreEqual(1, coverage.SolutionsNotSelected);
        StringAssert.Contains(coverage.Reasons.Single(), "built only from the projects the indexing checkout's solution selection reaches");
    }

    [TestMethod]
    public void UnreachedProviderProject_IsPartial_EvenUnderAConfiguredParentScope()
    {
        var resolution = Resolution([At("App.slnx")], SolutionSelectionSource.Configured);
        var load = Load([At("src/App/App.csproj")], loaded: [At("src/App/App.csproj"), At("libs/mix/src/Mix/Mix.csproj")]);
        var inventory = Inventory(
            [At("src/App/App.csproj"), At("libs/mix/src/Mix/Mix.csproj"), At("libs/mix/tools/Tools/Tools.csproj")],
            [Mix]);

        var coverage = SnapshotCoverageBuilder.BuildProviders(CheckoutDir, resolution, load, inventory)["libs/mix"];

        Assert.AreEqual(SnapshotCoverageVerdict.Partial, coverage.Verdict,
            "the parent's configured scope does not scope the PROVIDER repository");
        Assert.AreEqual(2, coverage.ProjectFilesOnDisk);
        Assert.AreEqual(1, coverage.ProjectFilesUnreferenced);
        var reason = coverage.Reasons.Single();
        StringAssert.Contains(reason, "tools/Tools/Tools.csproj", "named relative to the provider root");
        Assert.IsFalse(reason.Contains("libs/mix", StringComparison.Ordinal), "the parent's layout never leaks");
        Assert.AreEqual(SnapshotCoverageBuilder.ParentSelectionSource, coverage.SelectionSource,
            "a provider with no solution of its own is judged on its projects alone");
    }

    [TestMethod]
    public void SkippedProviderProject_IsPartial()
    {
        var resolution = Resolution([At("App.slnx"), At("libs/mix/Mix.slnx")]);
        var mix = At("libs/mix/src/Mix/Mix.csproj");
        var load = Load([At("src/App/App.csproj"), mix], loaded: [At("src/App/App.csproj")],
            skipped: [new SkippedProject(mix, "evaluation failed")]);
        var inventory = Inventory([At("src/App/App.csproj"), mix], [Mix], solutions: [At("libs/mix/Mix.slnx")]);

        var coverage = SnapshotCoverageBuilder.BuildProviders(CheckoutDir, resolution, load, inventory)["libs/mix"];

        Assert.AreEqual(SnapshotCoverageVerdict.Partial, coverage.Verdict);
        Assert.AreEqual(1, coverage.ProjectsSkipped);
        StringAssert.Contains(coverage.Reasons.Single(), "src/Mix/Mix.csproj");
    }

    [TestMethod]
    public void ProviderProjectTheLoadDeferred_IsPartial_NamedRelativeToTheProvider()
    {
        // Issue #245: the parent's load deadline passed before the provider project was opened.
        var resolution = Resolution([At("App.slnx"), At("libs/mix/Mix.slnx")]);
        var mix = At("libs/mix/src/Mix/Mix.csproj");
        var load = Load([At("src/App/App.csproj"), mix], loaded: [At("src/App/App.csproj")]) with
        {
            DeferredProjects = [mix]
        };
        var inventory = Inventory([At("src/App/App.csproj"), mix], [Mix], solutions: [At("libs/mix/Mix.slnx")]);

        var coverage = SnapshotCoverageBuilder.BuildProviders(CheckoutDir, resolution, load, inventory)["libs/mix"];

        Assert.AreEqual(SnapshotCoverageVerdict.Partial, coverage.Verdict);
        Assert.AreEqual(0, coverage.ProjectsSkipped, "a deferred project is not a load failure");
        Assert.AreEqual(0, coverage.ProjectFilesUnreferenced, "it was declared, so it is accounted for");
        var reason = coverage.Reasons.Single();
        StringAssert.Contains(reason, "time budget ran out (src/Mix/Mix.csproj)");
        Assert.IsFalse(reason.Contains("libs/mix", StringComparison.Ordinal), "the parent's layout never leaks");
    }

    [TestMethod]
    public void NestedUnpopulatedSubmodule_MakesTheOuterProviderPartial_AndUnpopulatedHasNoEntry()
    {
        var resolution = Resolution([At("App.slnx"), At("libs/mix/Mix.slnx")]);
        var load = Load([At("src/App/App.csproj"), At("libs/mix/src/Mix/Mix.csproj")]);
        var inventory = Inventory([At("src/App/App.csproj"), At("libs/mix/src/Mix/Mix.csproj")],
            [Mix, new DeclaredSubmodule("libs/mix/deps/inner", Populated: false), new DeclaredSubmodule("libs/other", Populated: false)],
            solutions: [At("libs/mix/Mix.slnx")]);

        var all = SnapshotCoverageBuilder.BuildProviders(CheckoutDir, resolution, load, inventory);

        Assert.AreEqual(1, all.Count, "only populated submodules can publish a provider");
        var coverage = all["libs/mix"];
        Assert.AreEqual(SnapshotCoverageVerdict.Partial, coverage.Verdict);
        Assert.AreEqual(1, coverage.SubmodulesDeclared);
        Assert.AreEqual(1, coverage.SubmodulesUnpopulated);
        StringAssert.Contains(coverage.Reasons.Single(), "deps/inner");
    }

    [TestMethod]
    public void ScanErrors_MakeEveryProviderPartial()
    {
        var resolution = Resolution([At("App.slnx"), At("libs/mix/Mix.slnx")]);
        var load = Load([At("src/App/App.csproj"), At("libs/mix/src/Mix/Mix.csproj")]);
        var inventory = Inventory([At("src/App/App.csproj"), At("libs/mix/src/Mix/Mix.csproj")], [Mix],
            solutions: [At("libs/mix/Mix.slnx")], errors: ["somewhere: cannot list files (IOException)"]);

        var coverage = SnapshotCoverageBuilder.BuildProviders(CheckoutDir, resolution, load, inventory)["libs/mix"];

        Assert.AreEqual(SnapshotCoverageVerdict.Partial, coverage.Verdict);
        Assert.AreEqual(1, coverage.ScanErrors);
    }

    [TestMethod]
    public void SdkPinOverrides_ApplyOnlyWhenTheyGovernTheProvider_AndAreProviderRelative()
    {
        var resolution = Resolution([At("App.slnx"), At("libs/mix/Mix.slnx")]);
        var load = Load([At("src/App/App.csproj"), At("libs/mix/src/Mix/Mix.csproj")]);
        var inventory = Inventory([At("src/App/App.csproj"), At("libs/mix/src/Mix/Mix.csproj")], [Mix],
            solutions: [At("libs/mix/Mix.slnx")]);
        SdkPinOverride[] pins =
        [
            new() { GlobalJsonPath = "global.json", RequestedVersion = "10.0.100" },
            new() { GlobalJsonPath = "libs/mix/global.json", RequestedVersion = "10.0.200" },
            new() { GlobalJsonPath = "src/App/global.json", RequestedVersion = "10.0.300" }
        ];

        var coverage = SnapshotCoverageBuilder.BuildProviders(CheckoutDir, resolution, load, inventory, pins)["libs/mix"];

        Assert.AreEqual(SnapshotCoverageVerdict.Complete, coverage.Verdict, "a pin override is provenance, not a gap");
        CollectionAssert.AreEquivalent(
            new[] { "<indexing checkout>/global.json", "global.json" },
            coverage.SdkPinOverrides!.Select(p => p.GlobalJsonPath).ToArray());
    }

    [TestMethod]
    public void Scan_FindsSolutionsOnlyUnderPopulatedSubmodules()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sextant_provscan_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "libs", "mix", "src"));
            Directory.CreateDirectory(Path.Combine(root, "libs", "mix", "bin"));
            File.WriteAllText(Path.Combine(root, ".gitmodules"), "[submodule \"mix\"]\n\tpath = libs/mix\n");
            File.WriteAllText(Path.Combine(root, "libs", "mix", ".git"), "gitdir: ../../.git/modules/mix\n");
            File.WriteAllText(Path.Combine(root, "App.slnx"), "<Solution />");
            File.WriteAllText(Path.Combine(root, "libs", "mix", "Mix.slnx"), "<Solution />");
            File.WriteAllText(Path.Combine(root, "libs", "mix", "bin", "Copied.sln"), "");

            var inventory = SnapshotCoverageBuilder.Inventory.Scan(root);

            Assert.IsTrue(inventory.Submodules.Single().Populated, "fixture: the submodule is populated");
            CollectionAssert.AreEqual(
                new[] { Path.GetFullPath(Path.Combine(root, "libs", "mix", "Mix.slnx")) },
                inventory.SubmoduleSolutionFiles!.ToArray(),
                "the parent's own solutions and build output are not provider solutions");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    // ---- helpers --------------------------------------------------------------------------------

    private static CheckoutResolution Resolution(
        IReadOnlyList<string> selected, SolutionSelectionSource source = SolutionSelectionSource.DefaultUnion) => new()
        {
            CheckoutDir = CheckoutDir,
            SelectedSolutions = selected,
            Source = source
        };

    private static MultiSolutionLoadResult Load(
        IReadOnlyList<string> declared, IReadOnlyList<string>? loaded = null, IReadOnlyList<SkippedProject>? skipped = null)
    {
        var workspace = new AdhocWorkspace();
        foreach (var path in loaded ?? declared)
            workspace.AddProject(ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Default,
                Path.GetFileNameWithoutExtension(path), Path.GetFileNameWithoutExtension(path), LanguageNames.CSharp,
                filePath: path));
        return new MultiSolutionLoadResult(workspace.CurrentSolution, skipped ?? [], [])
        {
            DeclaredProjects = declared
        };
    }

    private static SnapshotCoverageBuilder.Inventory Inventory(
        IReadOnlyList<string> projects, IReadOnlyList<DeclaredSubmodule> submodules,
        IReadOnlyList<string>? solutions = null, IReadOnlyList<string>? errors = null) =>
        new(projects, submodules, errors) { SubmoduleSolutionFiles = solutions };
}
