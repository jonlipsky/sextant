using System.Text;
using Sextant.Core.Platform;
using Sextant.Service.Contributions;

namespace Sextant.Service.Tests;

/// <summary>
/// The content-addressed contribution wire container (Phase 16): deterministic framing, a stable content
/// address that changes iff the manifest or payload changes (the idempotency key for criterion 2), and the
/// size-limit / malformed-framing guards enforced BEFORE the payload is buffered whole.
/// </summary>
[TestClass]
public class ContributionArtifactTests
{
    private static ContributionManifest Manifest(string commit = "abc123", string capability = "linux-x64|net8.0") => new()
    {
        Tenant = "octo",
        RepositoryRemoteUrl = "https://github.com/octo/app",
        CommitSha = commit,
        SchemaVersion = 18,
        AnalyzerVersion = "1",
        CliVersion = "1.2.3",
        ToolchainFingerprint = "toolchain-abc",
        CapabilityFingerprint = capability,
        PayloadSnapshotIdentityHash = "payload-hash",
        Producer = "runner-1"
    };

    private static byte[] Payload(string content) => Encoding.UTF8.GetBytes(content);

    [TestMethod]
    public void Round_trips_through_bytes()
    {
        var original = ContributionArtifact.Create(Manifest(), Payload("payload-bytes"));

        var parsed = ContributionArtifact.ReadFrom(original.ToArray(), long.MaxValue);

        Assert.AreEqual(original.ContentAddress, parsed.ContentAddress);
        Assert.AreEqual(original.Manifest.CommitSha, parsed.Manifest.CommitSha);
        Assert.AreEqual(original.Manifest.ManifestHash, parsed.Manifest.ManifestHash);
        CollectionAssert.AreEqual(original.Payload.ToArray(), parsed.Payload.ToArray());
    }

    [TestMethod]
    public void Content_address_is_stable_for_identical_inputs()
    {
        var a = ContributionArtifact.Create(Manifest(), Payload("same"));
        var b = ContributionArtifact.Create(Manifest(), Payload("same"));
        Assert.AreEqual(a.ContentAddress, b.ContentAddress);
    }

    [TestMethod]
    public void Content_address_changes_when_manifest_changes()
    {
        var a = ContributionArtifact.Create(Manifest(commit: "aaa"), Payload("same"));
        var b = ContributionArtifact.Create(Manifest(commit: "bbb"), Payload("same"));
        Assert.AreNotEqual(a.ContentAddress, b.ContentAddress);
    }

    [TestMethod]
    public void Content_address_changes_when_payload_changes()
    {
        var a = ContributionArtifact.Create(Manifest(), Payload("one"));
        var b = ContributionArtifact.Create(Manifest(), Payload("two"));
        Assert.AreNotEqual(a.ContentAddress, b.ContentAddress);
    }

    [TestMethod]
    public void Oversized_artifact_is_rejected_before_parsing()
    {
        var bytes = ContributionArtifact.Create(Manifest(), Payload("some-larger-payload")).ToArray();
        Assert.ThrowsExactly<ContributionTooLargeException>(() => ContributionArtifact.ReadFrom(bytes, bytes.Length - 1));
    }

    [TestMethod]
    public void Bad_magic_is_a_format_error()
    {
        var bytes = Encoding.ASCII.GetBytes("NOTMAGIC and some trailing content here");
        Assert.ThrowsExactly<ContributionFormatException>(() => ContributionArtifact.ReadFrom(bytes, long.MaxValue));
    }

    [TestMethod]
    public void Truncated_manifest_length_is_a_format_error()
    {
        var bytes = ContributionArtifact.Create(Manifest(), Payload("payload")).ToArray();
        // Corrupt the big-endian manifest length (offset 8..11) to claim more than the buffer holds.
        bytes[8] = 0x7F;
        Assert.ThrowsExactly<ContributionFormatException>(() => ContributionArtifact.ReadFrom(bytes, long.MaxValue));
    }
}
