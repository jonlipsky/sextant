using Sextant.Cli.Handlers;

namespace Sextant.Cli.Tests;

/// <summary>
/// The PURE pre-capture decision for <c>sextant contribute</c> (Phase 16 acceptance criteria 5 and 6). It
/// encodes the two supply-chain safety rules that must hold regardless of environment, so they are proven
/// without git or MSBuild: a DIRTY working tree is never uploaded (criterion 6), and a normal developer
/// build with no service is never made to fail (criterion 5, reinforced by the uploader's non-throwing
/// unavailable path).
/// </summary>
[TestClass]
public class ContributionCliPlanTests
{
    private static ContributeOptions Options(
        string? service = null, string? outFile = null, bool allowDirty = false, bool require = false) => new()
    {
        SolutionPath = "App.sln",
        ServiceUrl = service,
        OutFile = outFile,
        AllowDirty = allowDirty,
        Require = require
    };

    [TestMethod]
    public void No_git_root_is_an_error()
    {
        var decision = ContributionCliPlan.Decide(hasGitRoot: false, isDirty: false, Options(service: "https://svc"));
        Assert.AreEqual(ContributionDestination.Error, decision.Destination);
        StringAssert.Contains(decision.Error, "committed git checkout");
    }

    // ---- criterion 6: dirty source is NEVER uploaded without an explicit local-only opt-in ---------------

    [TestMethod]
    public void Dirty_tree_without_allow_dirty_is_an_error()
    {
        var decision = ContributionCliPlan.Decide(hasGitRoot: true, isDirty: true, Options(service: "https://svc"));
        Assert.AreEqual(ContributionDestination.Error, decision.Destination);
        StringAssert.Contains(decision.Error, "--allow-dirty");
    }

    [TestMethod]
    public void Dirty_tree_with_allow_dirty_and_service_is_refused_never_uploaded()
    {
        var decision = ContributionCliPlan.Decide(hasGitRoot: true, isDirty: true, Options(service: "https://svc", allowDirty: true));
        Assert.AreEqual(ContributionDestination.Error, decision.Destination);
        StringAssert.Contains(decision.Error, "never uploaded");
    }

    [TestMethod]
    public void Dirty_tree_with_allow_dirty_and_out_writes_local_only()
    {
        var decision = ContributionCliPlan.Decide(hasGitRoot: true, isDirty: true, Options(outFile: "c.bin", allowDirty: true));
        Assert.AreEqual(ContributionDestination.WriteLocal, decision.Destination);
    }

    [TestMethod]
    public void Dirty_tree_with_allow_dirty_but_no_out_is_an_error()
    {
        var decision = ContributionCliPlan.Decide(hasGitRoot: true, isDirty: true, Options(allowDirty: true));
        Assert.AreEqual(ContributionDestination.Error, decision.Destination);
        StringAssert.Contains(decision.Error, "--out");
    }

    // ---- clean tree destinations ------------------------------------------------------------------------

    [TestMethod]
    public void Clean_tree_with_service_uploads()
    {
        var decision = ContributionCliPlan.Decide(hasGitRoot: true, isDirty: false, Options(service: "https://svc"));
        Assert.AreEqual(ContributionDestination.Upload, decision.Destination);
    }

    [TestMethod]
    public void Clean_tree_with_only_out_writes_local()
    {
        var decision = ContributionCliPlan.Decide(hasGitRoot: true, isDirty: false, Options(outFile: "c.bin"));
        Assert.AreEqual(ContributionDestination.WriteLocal, decision.Destination);
    }

    [TestMethod]
    public void Clean_tree_with_no_destination_is_an_error()
    {
        var decision = ContributionCliPlan.Decide(hasGitRoot: true, isDirty: false, Options());
        Assert.AreEqual(ContributionDestination.Error, decision.Destination);
        StringAssert.Contains(decision.Error, "destination");
    }

    [TestMethod]
    public void Unknown_cleanliness_with_a_service_is_refused_never_uploaded()
    {
        // git status couldn't be determined (isDirty == null). We cannot prove the tree is clean, so uploading
        // would risk shipping uncommitted source — criterion 6 forbids that, so upload is fatal.
        var decision = ContributionCliPlan.Decide(hasGitRoot: true, isDirty: null, Options(service: "https://svc"));
        Assert.AreEqual(ContributionDestination.Error, decision.Destination);
    }

    [TestMethod]
    public void Unknown_cleanliness_is_treated_conservatively_as_dirty_for_local_write()
    {
        // With no service and an --out target, unknown cleanliness resolves conservatively to dirty, so it
        // still requires the explicit --allow-dirty opt-in and only ever writes locally.
        var decision = ContributionCliPlan.Decide(hasGitRoot: true, isDirty: null, Options(outFile: "c.bin", allowDirty: true));
        Assert.AreEqual(ContributionDestination.WriteLocal, decision.Destination);
    }
}
