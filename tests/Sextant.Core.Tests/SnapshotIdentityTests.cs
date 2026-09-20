using Sextant.Core;

namespace Sextant.Core.Tests;

/// <summary>
/// Issue #47 regression: an OVERLAY snapshot and a FULL-LOCAL fallback snapshot that indexed the same
/// dirty working tree (same commit, tree, versions, and working-tree delta) must NOT collide on
/// <see cref="SnapshotIdentity.Hash"/>. Before the fix the overlay/fallback discriminator was absent from
/// the identity, so the two hashed identically and a fallback request could be deduped onto an overlay
/// whose shared base rows may have been garbage-collected (a reader selecting the wrong, dangling
/// generation). The discriminator is folded in ONLY for a dirty tree, so a clean committed base's identity
/// is byte-identical to before the field existed (no mass rebuild).
/// </summary>
[TestClass]
public class SnapshotIdentityTests
{
    private static SnapshotIdentity Identity(string? delta, bool isOverlay) => new()
    {
        RepositoryRemoteUrl = "https://github.com/org/repo",
        CommitSha = "commit_abc",
        TreeSha = "tree_abc",
        SchemaVersion = 14,
        AnalyzerVersion = "1",
        ConfigHash = "cfg",
        ToolchainFingerprint = "tc",
        WorkingTreeDelta = delta,
        IsOverlay = isOverlay
    };

    [TestMethod]
    public void DirtyOverlay_AndDirtyFallback_SameTree_HaveDistinctIdentities()
    {
        var overlay = Identity("delta_edit1", isOverlay: true);
        var fallback = Identity("delta_edit1", isOverlay: false);

        Assert.AreNotEqual(overlay.Hash, fallback.Hash,
            "an overlay and a full-local fallback over the same dirty tree must not share an identity (#47)");
    }

    [TestMethod]
    public void CleanBase_IdentityIsIndependentOfOverlayFlag()
    {
        // A clean committed base is never an overlay; the discriminator must NOT be folded in when the
        // tree is clean, so its hash is stable regardless of the (irrelevant) IsOverlay flag — this is
        // what keeps every previously-published base snapshot's identity byte-identical.
        var cleanFalse = Identity(delta: null, isOverlay: false);
        var cleanTrue = Identity(delta: null, isOverlay: true);

        Assert.AreEqual(cleanFalse.Hash, cleanTrue.Hash,
            "a clean base's identity must not depend on the overlay discriminator (byte-identical, no rebuild)");
    }

    [TestMethod]
    public void DirtyOverlay_IsDeterministic()
    {
        Assert.AreEqual(
            Identity("delta_x", isOverlay: true).Hash,
            Identity("delta_x", isOverlay: true).Hash,
            "the same overlay inputs must recompute the same identity (idempotent restart)");
    }
}
