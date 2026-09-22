using Sextant.Core.Platform;

namespace Sextant.Core.Tests;

/// <summary>
/// Issue #65 — the ONE canonical TFM parser shared by capability detection (the placement probe and the
/// Phase-16 contribution manifest builder). It must accurately split framework / OS platform / platform
/// version for real TFMs AND for Roslyn multi-TFM display names, WITHOUT mis-reading an ordinary hyphenated
/// project name as a bogus platform — so a contribution declares exactly what it built and assembles only
/// with compatible inputs.
/// </summary>
[TestClass]
public class TargetFrameworkFactsTests
{
    [TestMethod]
    [DataRow("net8.0-windows", "net8.0", "windows", null)]
    [DataRow("net9.0-ios", "net9.0", "ios", null)]
    [DataRow("net8.0-maccatalyst", "net8.0", "maccatalyst", null)]
    [DataRow("net8.0-windows10.0.19041.0", "net8.0", "windows", "10.0.19041.0")]
    [DataRow("net8.0-android34.0", "net8.0", "android", "34.0")]
    public void Parse_platform_specific_tfm(string tfm, string framework, string platform, string? version)
    {
        var facts = TargetFrameworkFacts.Parse(tfm);
        Assert.AreEqual(framework, facts.Framework);
        Assert.AreEqual(platform, facts.Platform);
        Assert.AreEqual(version, facts.PlatformVersion);
        Assert.IsTrue(facts.IsPlatformSpecific);
    }

    [TestMethod]
    [DataRow("net8.0", "net8.0")]
    [DataRow("net48", "net48")]
    [DataRow("netstandard2.0", "netstandard2.0")]
    [DataRow("netcoreapp3.1", "netcoreapp3.1")]
    public void Parse_portable_tfm_has_no_platform(string tfm, string framework)
    {
        var facts = TargetFrameworkFacts.Parse(tfm);
        Assert.AreEqual(framework, facts.Framework);
        Assert.IsNull(facts.Platform);
        Assert.IsFalse(facts.IsPlatformSpecific);
    }

    [TestMethod]
    [DataRow("Foo (net8.0-windows)", "windows")]
    [DataRow("Foo (net8.0-windows10.0.19041)", "windows")]
    [DataRow("My (Legacy) App (net8.0-ios)", "ios")]
    [DataRow("Foo (net8.0)", null)]
    public void Parse_unwraps_roslyn_display_suffix(string displayName, string? expectedPlatform)
    {
        Assert.AreEqual(expectedPlatform, TargetFrameworkFacts.Parse(displayName).Platform);
    }

    [TestMethod]
    [DataRow("Acme-Cli")]          // an ordinary hyphenated project name is NOT a platform-bearing TFM
    [DataRow("Net-Foo")]           // "net" without a version digit is not a .NET TFM token
    [DataRow("some-thing-else")]
    public void Parse_does_not_invent_a_platform_from_a_plain_hyphenated_name(string name)
    {
        Assert.IsNull(TargetFrameworkFacts.Parse(name).Platform,
            "a plain hyphenated name must never be mis-read as a platform-specific TFM (issue #65)");
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public void Parse_blank_is_unknown(string? tfm)
    {
        var facts = TargetFrameworkFacts.Parse(tfm);
        Assert.AreEqual(string.Empty, facts.Framework);
        Assert.IsNull(facts.Platform);
        Assert.IsFalse(facts.IsPlatformSpecific);
    }

    [TestMethod]
    public void Unknown_is_the_blank_result()
    {
        Assert.AreEqual(string.Empty, TargetFrameworkFacts.Unknown.Framework);
        Assert.IsFalse(TargetFrameworkFacts.Unknown.IsPlatformSpecific);
    }
}
