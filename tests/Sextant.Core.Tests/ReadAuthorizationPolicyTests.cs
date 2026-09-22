using Sextant.Core;

namespace Sextant.Core.Tests;

/// <summary>
/// Phase 17 — criterion 1. The <see cref="ReadAuthorizationPolicy"/> wire-format parser and lookup: a blank
/// spec is disabled (zero-friction local default), a valid spec enables enforcement, principals map to
/// repository allowlists (with <c>"*"</c> as an all-repositories grant), and token lookup is exact.
/// </summary>
[TestClass]
public class ReadAuthorizationPolicyTests
{
    [TestMethod]
    public void Parse_NullOrBlank_IsDisabled()
    {
        Assert.IsFalse(ReadAuthorizationPolicy.Parse(null).Enabled);
        Assert.IsFalse(ReadAuthorizationPolicy.Parse("").Enabled);
        Assert.IsFalse(ReadAuthorizationPolicy.Parse("   ").Enabled);
    }

    [TestMethod]
    public void Parse_SingleTenant_MapsTokenToRepositories()
    {
        var policy = ReadAuthorizationPolicy.Parse("tok-a=https://github.com/org/a|https://github.com/org/b");
        Assert.IsTrue(policy.Enabled);
        Assert.AreEqual(1, policy.Principals.Count);
        Assert.IsTrue(policy.Allows("tok-a", "https://github.com/org/a"));
        Assert.IsTrue(policy.Allows("tok-a", "https://github.com/org/b"));
        Assert.IsFalse(policy.Allows("tok-a", "https://github.com/org/c"));
    }

    [TestMethod]
    public void Parse_MultiTenant_KeepsTenantsIsolated()
    {
        var policy = ReadAuthorizationPolicy.Parse("tok-a=https://github.com/org/a;tok-b=https://github.com/org/b");
        Assert.AreEqual(2, policy.Principals.Count);
        Assert.IsTrue(policy.Allows("tok-a", "https://github.com/org/a"));
        Assert.IsFalse(policy.Allows("tok-a", "https://github.com/org/b"), "tenant A cannot read tenant B");
        Assert.IsTrue(policy.Allows("tok-b", "https://github.com/org/b"));
        Assert.IsFalse(policy.Allows("tok-b", "https://github.com/org/a"), "tenant B cannot read tenant A");
    }

    [TestMethod]
    public void Parse_Wildcard_GrantsEveryRepository()
    {
        var policy = ReadAuthorizationPolicy.Parse("admin=*");
        Assert.IsTrue(policy.Allows("admin", "https://github.com/anything/here"));
        Assert.IsTrue(policy.Find("admin")!.AllowsAll);
    }

    [TestMethod]
    public void Parse_MalformedEntries_AreSkippedWithinAnEnforcedPolicy()
    {
        // A malformed entry ALONGSIDE a valid one is skipped (the dropped principal just has no token, which
        // fails closed for that tenant); the policy still enforces on the valid principal.
        var mixed = ReadAuthorizationPolicy.Parse("bad;tok-a=https://github.com/org/a");
        Assert.IsTrue(mixed.Enabled);
        Assert.AreEqual(1, mixed.Principals.Count);
    }

    [TestMethod]
    public void Parse_NonBlankButNoValidPrincipals_FailsClosed()
    {
        // A NON-blank spec is an explicit request to enforce; if it parses to zero principals the operator
        // mis-specified it. It must throw (fail closed), never silently collapse to the allow-all default —
        // otherwise a policy typo would silently disable all read authorization (criterion 1).
        Assert.ThrowsExactly<FormatException>(() => ReadAuthorizationPolicy.Parse("garbage-no-equals"));
        Assert.ThrowsExactly<FormatException>(() => ReadAuthorizationPolicy.Parse("=onlyrepos"));
        Assert.ThrowsExactly<FormatException>(() => ReadAuthorizationPolicy.Parse("tok="));
    }

    [TestMethod]
    public void Find_UnknownOrEmptyToken_IsNull()
    {
        var policy = ReadAuthorizationPolicy.Parse("tok-a=https://github.com/org/a");
        Assert.IsNull(policy.Find(null));
        Assert.IsNull(policy.Find(""));
        Assert.IsNull(policy.Find("nope"));
        Assert.IsFalse(policy.IsKnownPrincipal("nope"));
        Assert.IsTrue(policy.IsKnownPrincipal("tok-a"));
    }

    [TestMethod]
    public void DisabledPolicy_AllowsNothingByName_ButIsTheOpenDefaultUpstream()
    {
        // The disabled policy itself grants no principals; the AUTHORIZER (PolicyReadAuthorizer) treats a
        // disabled policy as allow-all. This test pins the policy-object contract: disabled ⇒ no principals.
        Assert.IsFalse(ReadAuthorizationPolicy.Disabled.Enabled);
        Assert.AreEqual(0, ReadAuthorizationPolicy.Disabled.Principals.Count);
        Assert.IsFalse(ReadAuthorizationPolicy.Disabled.Allows("anything", "anywhere"));
    }
}
