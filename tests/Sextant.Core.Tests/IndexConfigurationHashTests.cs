using Sextant.Core;

namespace Sextant.Core.Tests;

/// <summary>
/// Phase 8, acceptance criterion 1: the configuration hash recorded in every index run is stable
/// (same input/profile ⇒ identical hash) and a profile change yields a different hash so the daemon
/// treats it as a generation-invalidating change.
/// </summary>
[TestClass]
public class IndexConfigurationHashTests
{
    [TestMethod]
    public void SameProfile_ProducesIdenticalHash_AcrossCalls()
    {
        var a = IndexConfigurationHash.Compute(
            IndexProfiles.Standard, IndexFeature.Standard, GeneratedSourcePolicies.Exclude);
        var b = IndexConfigurationHash.Compute(
            IndexProfiles.Standard, IndexFeature.Standard, GeneratedSourcePolicies.Exclude);

        Assert.AreEqual(a, b, "the hash must be deterministic for identical inputs");
        Assert.AreEqual(64, a.Length, "SHA-256 hex is 64 lowercase characters");
    }

    [TestMethod]
    public void DescriptorHash_MatchesRawCompute_ForEachProfile()
    {
        foreach (var name in IndexProfiles.All)
        {
            var descriptor = IndexProfileDescriptor.For(name);
            var expected = IndexConfigurationHash.Compute(
                descriptor.Profile, descriptor.Features, descriptor.GeneratedSourcePolicy);
            Assert.AreEqual(expected, descriptor.ConfigurationHash, $"descriptor hash for '{name}' must match Compute");
        }
    }

    [TestMethod]
    public void DifferentProfiles_ProduceDifferentHashes()
    {
        var core = IndexProfileDescriptor.For(IndexProfiles.Core).ConfigurationHash;
        var standard = IndexProfileDescriptor.For(IndexProfiles.Standard).ConfigurationHash;
        var deep = IndexProfileDescriptor.For(IndexProfiles.Deep).ConfigurationHash;

        Assert.AreNotEqual(core, standard, "core→standard is a generation-invalidating change (optional tables were never built)");
        Assert.AreNotEqual(standard, deep, "standard→deep adds dataflow, a distinct configuration");
        Assert.AreNotEqual(core, deep, "core and deep are distinct configurations");
    }

    [TestMethod]
    public void UnknownProfile_NormalizesToStandardHash()
    {
        var typo = IndexProfileDescriptor.For("stanadrd").ConfigurationHash;
        var standard = IndexProfileDescriptor.For(IndexProfiles.Standard).ConfigurationHash;

        Assert.AreEqual(standard, typo, "an unrecognized profile degrades to the default, keeping the hash stable");
    }

    [TestMethod]
    public void ProfileName_IsCaseAndWhitespaceInsensitive()
    {
        var canonical = IndexProfileDescriptor.For(IndexProfiles.Deep).ConfigurationHash;
        var messy = IndexProfileDescriptor.For("  DEEP ").ConfigurationHash;

        Assert.AreEqual(canonical, messy, "the canonical profile name drives the hash, not casing/whitespace");
    }

    [TestMethod]
    public void FullDescriptor_EqualsDeepHash()
    {
        Assert.AreEqual(
            IndexProfileDescriptor.For(IndexProfiles.Deep).ConfigurationHash,
            IndexProfileDescriptor.Full.ConfigurationHash,
            "Full is the everything-on (deep) descriptor");
    }

    [TestMethod]
    public void GeneratedSourcePolicy_IsPartOfHash()
    {
        var exclude = IndexConfigurationHash.Compute(
            IndexProfiles.Standard, IndexFeature.Standard, GeneratedSourcePolicies.Exclude);
        var include = IndexConfigurationHash.Compute(
            IndexProfiles.Standard, IndexFeature.Standard, "include");

        Assert.AreNotEqual(exclude, include, "a generated-source policy change must change the hash");
    }

    // ==== Issue #39: the document_extractor toggle is part of the configuration hash ==============

    [TestMethod]
    public void DocumentExtractorToggle_ChangesHash()
    {
        // The document-oriented extractor and the legacy declaration-driven path are not row-identical,
        // so flipping the toggle must change the configuration hash — the daemon then treats it as a
        // generation-invalidating change and forces a rebuild (issue #39).
        var document = IndexConfigurationHash.Compute(
            IndexProfiles.Standard, IndexFeature.Standard, GeneratedSourcePolicies.Exclude, documentExtractor: true);
        var legacy = IndexConfigurationHash.Compute(
            IndexProfiles.Standard, IndexFeature.Standard, GeneratedSourcePolicies.Exclude, documentExtractor: false);

        Assert.AreNotEqual(document, legacy, "flipping document_extractor must change the configuration hash (#39)");
    }

    [TestMethod]
    public void DefaultCompute_MatchesDocumentExtractorOn()
    {
        // The 3-arg overload defaults to the document extractor (the shipped default), so a pre-#39
        // caller keeps its exact hash — no spurious rebuild for the default configuration.
        var implicitDefault = IndexConfigurationHash.Compute(
            IndexProfiles.Standard, IndexFeature.Standard, GeneratedSourcePolicies.Exclude);
        var explicitOn = IndexConfigurationHash.Compute(
            IndexProfiles.Standard, IndexFeature.Standard, GeneratedSourcePolicies.Exclude, documentExtractor: true);

        Assert.AreEqual(implicitDefault, explicitOn, "the default overload must equal document_extractor=on");
    }

    [TestMethod]
    public void DescriptorExtractorToggle_FlowsIntoHash()
    {
        // The descriptor threads the toggle through to the hash, so a config that disables the document
        // extractor produces a different descriptor hash than the default-on descriptor (#39).
        var on = IndexProfileDescriptor.For(IndexProfiles.Standard, documentExtractor: true).ConfigurationHash;
        var off = IndexProfileDescriptor.For(IndexProfiles.Standard, documentExtractor: false).ConfigurationHash;

        Assert.AreNotEqual(on, off, "the descriptor must fold the extractor toggle into its configuration hash (#39)");
    }
}
