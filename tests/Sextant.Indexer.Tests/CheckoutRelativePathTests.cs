using Sextant.Core;
using Sextant.Indexer;

namespace Sextant.Indexer.Tests;

[TestClass]
public sealed class CheckoutRelativePathTests
{
    private static readonly string Checkout = Path.Combine(Path.GetTempPath(), "sextant-checkout-path");

    [TestMethod]
    public void ASubmoduleProject_IsNamedFromTheCheckoutRoot()
    {
        var project = Path.Combine(Checkout, "external", "Sub", "src", "Droid", "Droid.csproj");
        Assert.AreEqual("external/Sub/src/Droid/Droid.csproj",
            IndexOrchestrator.CheckoutRelativePath(Checkout, project, "src/Droid/Droid.csproj"));
    }

    [TestMethod]
    public void ARootProject_AndAPathOutsideTheCheckout_HaveNoSeparateName()
    {
        Assert.IsNull(IndexOrchestrator.CheckoutRelativePath(
            Checkout, Path.Combine(Checkout, "src", "A", "A.csproj"), "src/A/A.csproj"));
        Assert.IsNull(IndexOrchestrator.CheckoutRelativePath(
            Checkout, Path.Combine(Path.GetTempPath(), "other", "B.csproj"), "B.csproj"));
        Assert.IsNull(IndexOrchestrator.CheckoutRelativePath(null, Path.Combine(Checkout, "a.csproj"), "x/a.csproj"));
    }

    [TestMethod]
    public void WhenTheFirstProjectIsInASubmodule_TheGitRootAloneDoesNothing_ButTheCheckoutDirWorks()
    {
        var submoduleRoot = Path.Combine(Checkout, "external", "Sub");
        var project = Path.Combine(submoduleRoot, "src", "Droid", "Droid.csproj");

        Assert.IsNull(IndexOrchestrator.CheckoutRelativePath(submoduleRoot, project, "src/Droid/Droid.csproj"),
            "relative to the submodule root the path is the project's own");

        var context = new SnapshotContext
        {
            RepositoryRemoteUrl = "https://example.test/o/r", CommitSha = "c", BranchName = "main", CheckoutDir = Checkout
        };
        var root = IndexOrchestrator.BindingCheckoutRoot(context, submoduleRoot);
        Assert.AreEqual(Checkout, root);
        Assert.AreEqual("external/Sub/src/Droid/Droid.csproj",
            IndexOrchestrator.CheckoutRelativePath(root, project, "src/Droid/Droid.csproj"));
    }

    [TestMethod]
    public void WithoutACheckoutDir_TheGitRootIsKept()
    {
        Assert.AreEqual(Checkout, IndexOrchestrator.BindingCheckoutRoot(null, Checkout));
        var context = new SnapshotContext { RepositoryRemoteUrl = "u", CommitSha = "c", BranchName = "main" };
        Assert.AreEqual(Checkout, IndexOrchestrator.BindingCheckoutRoot(context, Checkout));
    }
}
