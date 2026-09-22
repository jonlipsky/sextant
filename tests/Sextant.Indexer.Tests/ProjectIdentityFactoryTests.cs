namespace Sextant.Indexer.Tests;

/// <summary>
/// Unit tests for the pure target-framework helpers in <see cref="ProjectIdentityFactory"/>. The
/// end-to-end multi-target behavior — where a suffix is confirmed against the csproj's declared
/// frameworks — is covered by the benchmark integration test; these cover the pure suffix-extraction
/// and TFM-shape heuristic directly without needing an MSBuild-loaded project.
/// </summary>
[TestClass]
public class ProjectIdentityFactoryTests
{
    // ---- ExtractParentheticalSuffix: raw, unfiltered inner text of a trailing (...) ----

    [DataTestMethod]
    [DataRow("MultiTarget(net10.0)", "net10.0")]
    [DataRow("MultiTarget(netstandard2.0)", "netstandard2.0")]
    [DataRow("Windows(net8.0-windows)", "net8.0-windows")]
    [DataRow("Nested.Name(net10.0)", "net10.0")]
    [DataRow("Mobile(uap10.0)", "uap10.0")]
    [DataRow("Foo(Debug)", "Debug")]          // raw extraction does NOT filter — confirmation happens later
    [DataRow("Foo( net10.0 )", "net10.0")]    // trimmed
    public void ExtractParentheticalSuffix_ReturnsInnerText(string name, string expected)
    {
        Assert.AreEqual(expected, ProjectIdentityFactory.ExtractParentheticalSuffix(name));
    }

    [DataTestMethod]
    [DataRow("SingleTarget")]                 // no suffix at all
    [DataRow("Unbalanced(net10.0")]           // missing closing paren
    [DataRow("Foo()")]                        // empty parentheses
    [DataRow("")]                              // empty
    public void ExtractParentheticalSuffix_ReturnsNull_WhenNoSuffix(string name)
    {
        Assert.IsNull(ProjectIdentityFactory.ExtractParentheticalSuffix(name));
    }

    [TestMethod]
    public void ExtractParentheticalSuffix_NullName_ReturnsNull()
    {
        Assert.IsNull(ProjectIdentityFactory.ExtractParentheticalSuffix(null));
    }

    // ---- LooksLikeTfm: heuristic used only when a csproj cannot confirm the suffix ----

    [DataTestMethod]
    [DataRow("net10.0")]
    [DataRow("net48")]
    [DataRow("netstandard2.0")]
    [DataRow("netcoreapp3.1")]
    [DataRow("net8.0-windows")]
    [DataRow("uap10.0")]
    [DataRow("tizen8.0")]
    [DataRow("monoandroid12.0")]
    [DataRow("xamarinios10")]
    public void LooksLikeTfm_AcceptsFrameworkMonikers(string suffix)
    {
        Assert.IsTrue(ProjectIdentityFactory.LooksLikeTfm(suffix));
    }

    [DataTestMethod]
    [DataRow("networking")]                   // starts with "net" but no digit — not a TFM
    [DataRow("Debug")]                         // build configuration
    [DataRow("Release")]                       // build configuration
    [DataRow("x86")]                           // platform: has a digit but no framework family
    [DataRow("AnyCPU")]                         // platform
    public void LooksLikeTfm_RejectsNonFrameworkText(string suffix)
    {
        Assert.IsFalse(ProjectIdentityFactory.LooksLikeTfm(suffix),
            "Only a known framework family with a version digit should pass the heuristic.");
    }

    // ---- ResolveTfm: the identity-critical branch ordering (pure, no MSBuild needed) ----

    [TestMethod]
    public void ResolveTfm_ConfirmedSuffix_Wins()
    {
        Assert.AreEqual("net8.0",
            ProjectIdentityFactory.ResolveTfm("Foo(net8.0)", new[] { "net8.0", "net9.0" }));
    }

    [TestMethod]
    public void ResolveTfm_UnevaluatedMultiTarget_KeepsDistinctPerInstanceTfms()
    {
        // <TargetFrameworks>$(Prop)</TargetFrameworks> is dropped by the literal filter, so the
        // declared set is empty; each instance must still resolve to its own suffix so the two
        // evaluated instances do NOT collapse onto one identity (the data-loss bug this phase fixes).
        var declared = System.Array.Empty<string>();
        Assert.AreEqual("net8.0", ProjectIdentityFactory.ResolveTfm("Foo(net8.0)", declared));
        Assert.AreEqual("net9.0", ProjectIdentityFactory.ResolveTfm("Foo(net9.0)", declared));
    }

    [TestMethod]
    public void ResolveTfm_MixedLiteralAndProperty_DoesNotCollapseOntoTheLiteral()
    {
        // <TargetFrameworks>net8.0;$(Extra)</TargetFrameworks> → declared filtered to ["net8.0"].
        // The net9.0 instance must NOT fall through to the single-declared "net8.0" and collide.
        var declared = new[] { "net8.0" };
        Assert.AreEqual("net8.0", ProjectIdentityFactory.ResolveTfm("Foo(net8.0)", declared));
        Assert.AreEqual("net9.0", ProjectIdentityFactory.ResolveTfm("Foo(net9.0)", declared));
    }

    [TestMethod]
    public void ResolveTfm_SingleTargetNoSuffix_ReturnsDeclared()
    {
        Assert.AreEqual("net8.0", ProjectIdentityFactory.ResolveTfm("Foo", new[] { "net8.0" }));
    }

    [TestMethod]
    public void ResolveTfm_NonTfmSuffixOnSingleTarget_FallsBackToDeclared()
    {
        // A non-TFM parenthetical (won't confirm, doesn't look like a TFM) must not be used; the sole
        // declared framework wins.
        Assert.AreEqual("net8.0", ProjectIdentityFactory.ResolveTfm("Foo(Debug)", new[] { "net8.0" }));
    }

    [TestMethod]
    public void ResolveTfm_NothingResolvable_ReturnsEmptyNotNull()
    {
        Assert.AreEqual(string.Empty, ProjectIdentityFactory.ResolveTfm("Foo", System.Array.Empty<string>()));
        Assert.AreEqual(string.Empty, ProjectIdentityFactory.ResolveTfm(null, System.Array.Empty<string>()));
    }

    [TestMethod]
    public void ResolveTfm_IsDeterministic()
    {
        var declared = new[] { "net8.0", "net9.0" };
        var first = ProjectIdentityFactory.ResolveTfm("Foo(net9.0)", declared);
        var second = ProjectIdentityFactory.ResolveTfm("Foo(net9.0)", declared);
        Assert.AreEqual(first, second);
        Assert.AreEqual("net9.0", first);
    }

    // ---- ReadDeclaredTargetFrameworks: literal filtering of unevaluated MSBuild expressions ----

    [TestMethod]
    public void ReadDeclaredTargetFrameworks_DropsUnevaluatedExpressions()
    {
        var csproj = Path.Combine(Path.GetTempPath(), $"sx_tfm_{System.Guid.NewGuid():N}.csproj");
        File.WriteAllText(csproj,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
            "<TargetFrameworks>net8.0;$(ExtraTfms)</TargetFrameworks>" +
            "</PropertyGroup></Project>");
        try
        {
            var declared = ProjectIdentityFactory.ReadDeclaredTargetFrameworks(csproj);
            CollectionAssert.AreEquivalent(new[] { "net8.0" }, declared.ToArray(),
                "The unevaluated $(...) token must be dropped, leaving only the literal framework.");
        }
        finally { File.Delete(csproj); }
    }

    [TestMethod]
    public void ReadDeclaredTargetFrameworks_MissingFile_ReturnsEmpty()
    {
        var declared = ProjectIdentityFactory.ReadDeclaredTargetFrameworks(
            Path.Combine(Path.GetTempPath(), $"sx_missing_{System.Guid.NewGuid():N}.csproj"));
        Assert.AreEqual(0, declared.Count);
    }
}
