using Sextant.Store;

namespace Sextant.Store.Tests;

/// <summary>
/// Issue #29 regression: <see cref="BreakingChangeDetector.DetectChanges"/> keyed its dictionaries by
/// fully-qualified name, so the moment a project exposed two public overloads sharing one FQN (e.g.
/// <c>Foo(int)</c> and <c>Foo(string)</c>) the <c>ToDictionary(s =&gt; s.fqn)</c> threw a duplicate-key
/// <see cref="ArgumentException"/> — a live crash for any overloaded public API. The fix re-keys by the
/// stable Phase-2 <c>symbol_key</c>, which distinguishes overloads. These tests exercise the exact crash
/// shape and prove overloads are now compared independently.
/// </summary>
[TestClass]
public class BreakingChangeDetectorOverloadTests
{
    private const string Fqn = "global::Api.Widget.Foo";

    [TestMethod]
    public void DetectChanges_TwoPublicOverloadsSharingFqn_DoesNotThrow()
    {
        // Two overloads: same FQN, distinct symbol_keys (documentation ids encode the parameter list).
        var old = new List<(string symbolKey, string fqn, string signatureHash, string accessibility)>
        {
            ("M:Api.Widget.Foo(System.Int32)", Fqn, "hash_int", "public"),
            ("M:Api.Widget.Foo(System.String)", Fqn, "hash_str", "public")
        };
        var @new = new List<(string symbolKey, string fqn, string signatureHash, string accessibility)>
        {
            ("M:Api.Widget.Foo(System.Int32)", Fqn, "hash_int", "public"),
            ("M:Api.Widget.Foo(System.String)", Fqn, "hash_str", "public")
        };

        // Before #29 this threw ArgumentException (duplicate key 'global::Api.Widget.Foo').
        var changes = BreakingChangeDetector.DetectChanges(old, @new);

        Assert.AreEqual(0, changes.Count, "identical overload sets are non-breaking, not a crash");
    }

    [TestMethod]
    public void DetectChanges_OneOverloadRemoved_IsBreaking_OtherUnaffected()
    {
        var old = new List<(string symbolKey, string fqn, string signatureHash, string accessibility)>
        {
            ("M:Api.Widget.Foo(System.Int32)", Fqn, "hash_int", "public"),
            ("M:Api.Widget.Foo(System.String)", Fqn, "hash_str", "public")
        };
        // The string overload was removed; the int overload is unchanged.
        var @new = new List<(string symbolKey, string fqn, string signatureHash, string accessibility)>
        {
            ("M:Api.Widget.Foo(System.Int32)", Fqn, "hash_int", "public")
        };

        var changes = BreakingChangeDetector.DetectChanges(old, @new);

        Assert.AreEqual(1, changes.Count, "exactly one overload changed");
        Assert.AreEqual(ChangeClassification.Breaking, changes[0].Classification);
        Assert.AreEqual("Symbol removed", changes[0].Reason);
        Assert.AreEqual(Fqn, changes[0].SymbolFqn);
    }

    [TestMethod]
    public void DetectChanges_OneOverloadAdded_IsAdditive()
    {
        var old = new List<(string symbolKey, string fqn, string signatureHash, string accessibility)>
        {
            ("M:Api.Widget.Foo(System.Int32)", Fqn, "hash_int", "public")
        };
        var @new = new List<(string symbolKey, string fqn, string signatureHash, string accessibility)>
        {
            ("M:Api.Widget.Foo(System.Int32)", Fqn, "hash_int", "public"),
            ("M:Api.Widget.Foo(System.String)", Fqn, "hash_str", "public")
        };

        var changes = BreakingChangeDetector.DetectChanges(old, @new);

        Assert.AreEqual(1, changes.Count);
        Assert.AreEqual(ChangeClassification.Additive, changes[0].Classification);
        Assert.AreEqual("Symbol added", changes[0].Reason);
    }

    [TestMethod]
    public void DetectChanges_OneOverloadSignatureChanged_OnlyThatOverloadIsBreaking()
    {
        var old = new List<(string symbolKey, string fqn, string signatureHash, string accessibility)>
        {
            ("M:Api.Widget.Foo(System.Int32)", Fqn, "hash_int_v1", "public"),
            ("M:Api.Widget.Foo(System.String)", Fqn, "hash_str", "public")
        };
        var @new = new List<(string symbolKey, string fqn, string signatureHash, string accessibility)>
        {
            ("M:Api.Widget.Foo(System.Int32)", Fqn, "hash_int_v2", "public"),
            ("M:Api.Widget.Foo(System.String)", Fqn, "hash_str", "public")
        };

        var changes = BreakingChangeDetector.DetectChanges(old, @new);

        Assert.AreEqual(1, changes.Count, "only the int overload's signature changed");
        Assert.AreEqual(ChangeClassification.Breaking, changes[0].Classification);
        Assert.AreEqual("Signature changed", changes[0].Reason);
    }
}
