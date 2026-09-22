using Sextant.Core.Platform;
using Sextant.Service.Contributions;

namespace Sextant.Service.Tests;

/// <summary>
/// Phase 17 slice 2, fold issue #70: the assembly FINALIZE completeness/topology gate. A materially
/// incomplete assembly — a contributor that declared a project version partial/unsupported, or an assembled
/// graph with a dangling reference to a SAME-REPOSITORY project version that was never contributed — must
/// publish PARTIAL, never silently Complete. A well-formed assembly (all projects complete, every intra-repo
/// reference present, cross-repo provider references excluded) publishes Complete.
/// </summary>
[TestClass]
public class AssemblyFinalizeGateTests
{
    [TestMethod]
    public void CompleteAssembly_WithAllReferencesPresent_PublishesComplete()
    {
        var manifest = Manifest(
            Project("a", "complete", referenced: ["b"]),
            Project("b", "complete"));

        var result = AssemblyFinalizeGate.Evaluate(
            manifest,
            anyContributionIncomplete: false,
            assembledCanonicalIds: Set("a", "b"),
            knownRepositoryCanonicalIds: Set("a", "b"));

        Assert.IsTrue(result.IsComplete);
        Assert.AreEqual(0, result.Reasons.Count);
    }

    [TestMethod]
    public void ProjectDeclaredPartial_PublishesPartial()
    {
        var manifest = Manifest(Project("a", "partial"));

        var result = AssemblyFinalizeGate.Evaluate(
            manifest, false, Set("a"), Set("a"));

        Assert.IsFalse(result.IsComplete, "a project declaring itself partial makes the assembly materially incomplete");
    }

    [TestMethod]
    public void PriorContributionIncomplete_PublishesPartial()
    {
        var manifest = Manifest(Project("a", "complete"));

        var result = AssemblyFinalizeGate.Evaluate(
            manifest, anyContributionIncomplete: true, Set("a"), Set("a"));

        Assert.IsFalse(result.IsComplete, "a durable non-complete contribution flag forces Partial across the multi-call assembly");
    }

    [TestMethod]
    public void DanglingIntraRepositoryReference_PublishesPartial()
    {
        // 'a' references 'b'; 'b' is a KNOWN project of this repository but was NOT assembled ⇒ incomplete.
        var manifest = Manifest(Project("a", "complete", referenced: ["b"]));

        var result = AssemblyFinalizeGate.Evaluate(
            manifest, false, assembledCanonicalIds: Set("a"), knownRepositoryCanonicalIds: Set("a", "b"));

        Assert.IsFalse(result.IsComplete, "a dangling reference to an intra-repo project version that is missing from the assembly is incomplete");
    }

    [TestMethod]
    public void CrossRepositoryProviderReference_IsNotCountedAsIncompleteness()
    {
        // 'a' references 'ext'; 'ext' is NOT a known project of this repository (a provider reference,
        // resolved via snapshot_dependencies — #72 deferred), so it must NOT force Partial.
        var manifest = Manifest(Project("a", "complete", referenced: ["ext"]));

        var result = AssemblyFinalizeGate.Evaluate(
            manifest, false, assembledCanonicalIds: Set("a"), knownRepositoryCanonicalIds: Set("a"));

        Assert.IsTrue(result.IsComplete, "a cross-repo provider reference is not intra-repo incompleteness");
    }

    private static IReadOnlySet<string> Set(params string[] ids) => new HashSet<string>(ids);

    private static ContributionProjectEntry Project(string id, string completeness, string[]? referenced = null) => new()
    {
        CanonicalId = id,
        RepoRelativePath = $"src/{id}/{id}.csproj",
        CapabilityFingerprint = "cap",
        Completeness = completeness,
        ReferencedProjectVersionKeys = referenced ?? []
    };

    private static ContributionManifest Manifest(params ContributionProjectEntry[] projects) => new()
    {
        Tenant = "t",
        RepositoryRemoteUrl = "https://github.com/org/app",
        CommitSha = "deadbeef",
        SchemaVersion = 1,
        AnalyzerVersion = "a",
        CliVersion = "c",
        ToolchainFingerprint = "tc",
        CapabilityFingerprint = "cap",
        PayloadSnapshotIdentityHash = "h",
        Producer = "p",
        Projects = projects
    };
}
