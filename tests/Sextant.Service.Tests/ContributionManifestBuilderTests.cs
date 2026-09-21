using Sextant.Service.Contributions;

namespace Sextant.Service.Tests;

/// <summary>
/// <see cref="ContributionManifestBuilder"/> derives the manifest from the produced payload catalog, so
/// "declared == built" holds by construction (Phase 16 CRITICAL 1): the capability and per-project facts a
/// contribution DECLARES are read straight from what the payload snapshot RECORDED building, and the
/// server's capability check then catches any manifest that claims otherwise. Exercised via the public
/// artifact the fixture builds through the real builder.
/// </summary>
[TestClass]
public class ContributionManifestBuilderTests
{
    private const string Repo = "https://github.com/octo/app";
    private const string Commit = "cafebabecafebabecafebabecafebabecafebabe";
    private const string Cap = "win-x64|net8.0-windows|sdk-8.0.400";

    [TestMethod]
    public void Manifest_declares_the_capability_the_payload_recorded()
    {
        var artifact = ContributionTestFixtures.BuildArtifact(Repo, Commit, Cap, [new PayloadProjectSpec("src/App/App.csproj", "net8.0-windows")]);

        Assert.AreEqual(Cap, artifact.Manifest.CapabilityFingerprint);
        Assert.AreEqual(1, artifact.Manifest.Projects.Count);
        Assert.AreEqual(Cap, artifact.Manifest.Projects[0].CapabilityFingerprint,
            "each declared project version carries the capability that produced it (declared == built)");
    }

    [TestMethod]
    public void Manifest_carries_repository_commit_and_project_paths()
    {
        var artifact = ContributionTestFixtures.BuildArtifact(
            Repo, Commit, Cap, [new PayloadProjectSpec("src/A/A.csproj", "net8.0"), new PayloadProjectSpec("src/B/B.csproj", "net8.0")]);

        Assert.AreEqual(Repo, artifact.Manifest.RepositoryRemoteUrl);
        Assert.AreEqual(Commit, artifact.Manifest.CommitSha);
        CollectionAssert.AreEquivalent(
            new[] { "src/A/A.csproj", "src/B/B.csproj" },
            artifact.Manifest.Projects.Select(p => p.RepoRelativePath).ToArray());
    }

    [TestMethod]
    public void Manifest_source_fingerprints_are_path_at_hash()
    {
        var artifact = ContributionTestFixtures.BuildArtifact(Repo, Commit, Cap, [new PayloadProjectSpec("src/App/App.csproj", "net8.0")]);

        var fingerprints = artifact.Manifest.Projects[0].SourceFingerprints;
        Assert.IsTrue(fingerprints.Count > 0, "the server hash-verifies declared source fingerprints against provider git content");
        foreach (var fingerprint in fingerprints)
            StringAssert.Contains(fingerprint, "@", "a source fingerprint is 'repo-relative-path@content-hash'");
    }

    [TestMethod]
    public void Assembly_identity_is_capability_less_so_native_variants_share_one_snapshot()
    {
        var linux = ContributionTestFixtures.BuildArtifact(Repo, Commit, "linux-x64|net8.0", [new PayloadProjectSpec("src/App/App.csproj", "net8.0")]);
        var windows = ContributionTestFixtures.BuildArtifact(Repo, Commit, Cap, [new PayloadProjectSpec("src/App/App.csproj", "net8.0-windows")]);

        Assert.AreEqual(
            linux.Manifest.ToSnapshotIdentity().Hash,
            windows.Manifest.ToSnapshotIdentity().Hash,
            "different-capability contributions for the same commit target the SAME assembly snapshot (criterion 4)");
        Assert.AreNotEqual(linux.ContentAddress, windows.ContentAddress,
            "yet each contribution has its own content address (distinct idempotency keys)");
    }

    [TestMethod]
    public void Assembly_identity_ignores_toolchain_so_cross_os_producers_converge()
    {
        var artifact = ContributionTestFixtures.BuildArtifact(Repo, Commit, Cap, [new PayloadProjectSpec("src/App/App.csproj", "net8.0")]);
        var baseline = artifact.Manifest.ToSnapshotIdentity().Hash;

        // Same commit / tree / config / analyzer, but produced by a DIFFERENT OS+arch toolchain (the root of
        // #65's capability fidelity: ToolchainFingerprint.Current folds in os/arch/framework) and a different
        // native capability. The assembly identity must ignore BOTH so the producers converge on one snapshot.
        var crossOs = artifact.Manifest with
        {
            ToolchainFingerprint = "os=Windows 10;arch=X64;framework=.NET 8.0.11",
            CapabilityFingerprint = "win-x64|net8.0-windows|sdk-8.0.400"
        };

        Assert.AreEqual(baseline, crossOs.ToSnapshotIdentity().Hash,
            "the assembly identity excludes toolchain AND capability, so a Windows producer and a Linux producer for the same commit target ONE snapshot (criterion 4 cross-OS)");
    }
}
