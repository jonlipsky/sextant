using Sextant.Core;
using Sextant.Store;

namespace Sextant.Service.Tests;

/// <summary>
/// Issue #84 wiring on the SERVICE side: the optional monotonic <c>BranchHeadSequence</c> on the ensure
/// request must (a) reach the orchestrator via the worker's <see cref="SnapshotContext"/> so the branch
/// advance can be gated, (b) round-trip over the snake_case wire as <c>branch_head_sequence</c>, and (c)
/// NEVER change the snapshot identity — the same committed state re-ensured under a different sequence is
/// the SAME immutable snapshot. A null sequence flows through unchanged so a non-sequence ensure keeps the
/// unconditional-advance behavior byte-for-byte (criterion 2).
/// </summary>
[TestClass]
public class EnsureBranchHeadSequenceTests
{
    [TestMethod]
    public void Worker_MapsBranchHeadSequence_FromRequestToSnapshotContext()
    {
        var request = ServiceTestFixtures.Request(branch: "main") with { BranchHeadSequence = 42 };

        var context = LocalIndexerSnapshotWorker.CreateSnapshotContext(request, capability: null);

        Assert.AreEqual(42L, context.BranchHeadSequence,
            "the worker carries the request's head sequence into the orchestrator so the advance is gated (#84)");
        Assert.AreEqual("main", context.BranchName);
    }

    [TestMethod]
    public void Worker_NullBranchHeadSequence_FlowsThroughAsNull()
    {
        var request = ServiceTestFixtures.Request(branch: "main");

        var context = LocalIndexerSnapshotWorker.CreateSnapshotContext(request, capability: null);

        Assert.IsNull(context.BranchHeadSequence,
            "a non-sequence ensure produces a null-sequence context → unconditional advance, byte-identical (criterion 2)");
    }

    [TestMethod]
    public void BranchHeadSequence_DoesNotChangeSnapshotIdentity()
    {
        var baseRequest = ServiceTestFixtures.Request();
        var withSeq = baseRequest with { BranchHeadSequence = 7 };
        var withOtherSeq = baseRequest with { BranchHeadSequence = 99 };

        Assert.AreEqual(baseRequest.ToIdentity().Hash, withSeq.ToIdentity().Hash,
            "the head sequence is an ordering hint, not part of the content-addressed snapshot identity");
        Assert.AreEqual(withSeq.ToIdentity().Hash, withOtherSeq.ToIdentity().Hash,
            "two ensures for the same commit under different sequences resolve to the ONE immutable snapshot");
    }

    [TestMethod]
    public void BranchHeadSequence_RoundTripsOverSnakeCaseWire()
    {
        var request = ServiceTestFixtures.Request(branch: "main") with { BranchHeadSequence = 123 };

        var json = System.Text.Json.JsonSerializer.Serialize(request, ServiceJson.Options);
        StringAssert.Contains(json, "\"branch_head_sequence\":123",
            "the wire field is snake_case branch_head_sequence (the contract ProcessStack sends)");

        var round = System.Text.Json.JsonSerializer.Deserialize<EnsureSnapshotRequest>(json, ServiceJson.Options)!;
        Assert.AreEqual(123L, round.BranchHeadSequence);
    }

    [TestMethod]
    public void BranchHeadSequence_OmittedFromWire_BindsAsNull()
    {
        const string json = """
            {"repository_remote_url":"https://github.com/org/app","commit_sha":"commit-aaaa"}
            """;

        var request = System.Text.Json.JsonSerializer.Deserialize<EnsureSnapshotRequest>(json, ServiceJson.Options)!;

        Assert.IsNull(request.BranchHeadSequence,
            "a legacy client that omits the field binds to null → today's unconditional-advance behavior (criterion 2)");
    }
}
