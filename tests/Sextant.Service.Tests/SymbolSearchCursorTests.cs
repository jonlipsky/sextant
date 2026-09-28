using System.Buffers.Text;
using System.Text;
using Sextant.Service.CallerIdentity;
using Sextant.Service.Search;

namespace Sextant.Service.Tests;

/// <summary>
/// SVC-F: the <c>search_symbols</c> cursor codec. A cursor round-trips only under the binding it was issued for; a
/// tampered, reshaped, oversized or foreign one is refused (the tool maps every refusal to <c>invalid_cursor</c>).
/// </summary>
[TestClass]
public class SymbolSearchCursorTests
{
    private static readonly string HashA = new('a', 64);
    private static readonly string HashB = new('b', 64);
    private static readonly string HashC = new('c', 64);

    private static CallerPrincipal User(string tenant = "tenant-a", string sub = "user-1") => new()
    {
        TenantId = tenant,
        TenantSlug = tenant,
        Actor = CallerActor.User,
        UserId = sub,
        App = "app-1",
        Connection = "conn-1",
        Via = "mcp-surface",
        KeyId = "kid-a",
        Jti = "jti-1"
    };

    private static CallerPrincipal Application(string tenant = "tenant-a", string app = "app-1") => new()
    {
        TenantId = tenant,
        TenantSlug = tenant,
        Actor = CallerActor.Application,
        App = app,
        Connection = "conn-1",
        Via = "mcp-surface",
        KeyId = "kid-a",
        Jti = "jti-1"
    };

    private static string Binding(CallerPrincipal? caller = null, string prefix = "Type", string? kind = null,
        string? repository = null, string? branch = null) =>
        SymbolSearchCursor.Binding(caller ?? User(), prefix, kind, repository, branch);

    private static SymbolSearchCursorState State() =>
        new([new SymbolSearchPosition(HashA, 12), new SymbolSearchPosition(HashB, 0)], HashC, HashA);

    [TestMethod]
    public void RoundTrip_UnderTheSameBinding()
    {
        var cursor = SymbolSearchCursor.Encode(State(), Binding());

        Assert.IsTrue(SymbolSearchCursor.TryDecode(cursor, Binding(), 100, out var decoded));
        Assert.AreEqual(HashC, decoded!.Watermark);
        Assert.AreEqual(HashA, decoded.Rotation);
        CollectionAssert.AreEqual(State().Active.ToList(), decoded.Active.ToList());
        Assert.IsTrue(cursor.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'), "the cursor is unpadded base64url");
    }

    [TestMethod]
    public void RoundTrip_OfAnEmptyState()
    {
        var cursor = SymbolSearchCursor.Encode(new SymbolSearchCursorState([], null), Binding());

        Assert.IsTrue(SymbolSearchCursor.TryDecode(cursor, Binding(), 100, out var decoded));
        Assert.IsNull(decoded!.Watermark);
        Assert.IsNull(decoded.Rotation);
        Assert.AreEqual(0, decoded.Active.Count);
    }

    [TestMethod]
    public void ForeignBinding_IsRefused()
    {
        var cursor = SymbolSearchCursor.Encode(State(), Binding());

        foreach (var other in new[]
                 {
                     Binding(User(sub: "user-2")),
                     Binding(User(tenant: "tenant-b")),
                     Binding(Application()),
                     Binding(prefix: "Typ"),
                     Binding(kind: "class"),
                     Binding(repository: "https://github.com/acme/widgets"),
                     Binding(branch: "main")
                 })
        {
            Assert.IsFalse(SymbolSearchCursor.TryDecode(cursor, other, 100, out var state), other);
            Assert.IsNull(state);
        }
    }

    [TestMethod]
    public void ApplicationBinding_DependsOnTheApplication()
    {
        var cursor = SymbolSearchCursor.Encode(State(), Binding(Application(app: "app-1")));

        Assert.IsTrue(SymbolSearchCursor.TryDecode(cursor, Binding(Application(app: "app-1")), 100, out _));
        Assert.IsFalse(SymbolSearchCursor.TryDecode(cursor, Binding(Application(app: "app-2")), 100, out _));
    }

    [TestMethod]
    public void TamperedContent_IsRefused()
    {
        var json = Decode(SymbolSearchCursor.Encode(State(), Binding()));

        foreach (var tampered in new[]
                 {
                     json.Replace("12]", "13]", StringComparison.Ordinal),
                     json.Replace(HashB, new string('b', 63) + "0", StringComparison.Ordinal),
                     json.Replace($"\"w\":\"{HashC}\"", $"\"w\":\"{new string('f', 64)}\"", StringComparison.Ordinal),
                     json.Replace($"\"r\":\"{HashA}\"", $"\"r\":\"{HashB}\"", StringComparison.Ordinal)
                 })
        {
            Assert.AreNotEqual(json, tampered);
            Assert.IsFalse(SymbolSearchCursor.TryDecode(Encode(tampered), Binding(), 100, out _), tampered);
        }
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("not base64url!")]
    [DataRow("e30")] // {}
    public void Malformed_IsRefused(string cursor) =>
        Assert.IsFalse(SymbolSearchCursor.TryDecode(cursor, Binding(), 100, out _));

    [TestMethod]
    public void Oversized_IsRefused()
    {
        Assert.IsFalse(SymbolSearchCursor.TryDecode(new string('A', SymbolSearchCursor.MaxLength + 1), Binding(), 100, out _));
    }

    [TestMethod]
    public void TooManyPositions_IsRefused()
    {
        var cursor = SymbolSearchCursor.Encode(State(), Binding());

        Assert.IsTrue(SymbolSearchCursor.TryDecode(cursor, Binding(), 2, out _));
        Assert.IsFalse(SymbolSearchCursor.TryDecode(cursor, Binding(), 1, out _));
    }

    [TestMethod]
    public void MaximalCursor_FitsTheLengthBound()
    {
        // The widest cursor the service can issue: every position it tracks, at the largest ids.
        var positions = Enumerable.Range(0, ServiceOptions.SearchMaxWidthCeiling)
            .Select(i => new SymbolSearchPosition(i.ToString("x64"), long.MaxValue))
            .ToList();
        var cursor = SymbolSearchCursor.Encode(new SymbolSearchCursorState(positions, new string('f', 64), new string('e', 64)),
            Binding(prefix: new string('x', SearchSymbolsTool.MaxNamePrefixLength)));

        Assert.IsTrue(cursor.Length <= SymbolSearchCursor.MaxLength, $"{cursor.Length} characters");
    }

    [TestMethod]
    public void ReshapedStates_AreRefused()
    {
        // Each reshaped state carries a digest that is valid for it, so only the shape checks refuse it.
        foreach (var json in new[]
                 {
                     Signed($$"""{"v":2,"a":[],"w":null,"r":null}""", new SymbolSearchCursorState([], null)),
                     Signed($$"""{"v":1,"a":[["{{HashA}}",1]],"w":null,"r":null}""", new SymbolSearchCursorState([new(HashA, 1)], null)),
                     Signed($$"""{"v":1,"a":[["{{HashB}}",1],["{{HashA}}",1]],"w":"{{HashC}}","r":null}""",
                         new SymbolSearchCursorState([new(HashB, 1), new(HashA, 1)], HashC)),
                     Signed($$"""{"v":1,"a":[["{{HashA}}",1],["{{HashA}}",2]],"w":"{{HashC}}","r":null}""",
                         new SymbolSearchCursorState([new(HashA, 1), new(HashA, 2)], HashC)),
                     Signed($$"""{"v":1,"a":[["{{HashC}}",1]],"w":"{{HashA}}","r":null}""",
                         new SymbolSearchCursorState([new(HashC, 1)], HashA)),
                     Signed($$"""{"v":1,"a":[["{{HashA}}",-1]],"w":"{{HashC}}","r":null}""",
                         new SymbolSearchCursorState([new(HashA, -1)], HashC)),
                     Signed($$"""{"v":1,"a":[["{{HashA.ToUpperInvariant()}}",1]],"w":"{{HashC}}","r":null}""",
                         new SymbolSearchCursorState([new(HashA.ToUpperInvariant(), 1)], HashC)),
                     Signed($$"""{"v":1,"a":[],"w":null,"r":null,"x":1}""", new SymbolSearchCursorState([], null)),
                     Signed($$"""{"v":1,"a":[],"w":null,"w":null,"r":null}""", new SymbolSearchCursorState([], null)),
                     Signed($$"""{"v":1,"a":[["{{HashA}}"]],"w":"{{HashC}}","r":null}""", new SymbolSearchCursorState([], HashC)),
                     Signed($$"""{"v":1,"a":[["{{HashA}}","1"]],"w":"{{HashC}}","r":null}""", new SymbolSearchCursorState([new(HashA, 1)], HashC)),
                     Signed($$"""{"v":1,"a":{},"w":null,"r":null}""", new SymbolSearchCursorState([], null)),
                     Signed($$"""{"v":1,"a":[],"w":"short","r":null}""", new SymbolSearchCursorState([], "short")),
                     // The rotation: required, a hash, and never after the watermark.
                     Signed($$"""{"v":1,"a":[],"w":"{{HashC}}"}""", new SymbolSearchCursorState([], HashC)),
                     Signed($$"""{"v":1,"a":[],"w":"{{HashC}}","r":"short"}""", new SymbolSearchCursorState([], HashC, "short")),
                     Signed($$"""{"v":1,"a":[],"w":"{{HashB}}","r":"{{HashC}}"}""", new SymbolSearchCursorState([], HashB, HashC)),
                     Signed($$"""{"v":1,"a":[],"w":null,"r":"{{HashA}}"}""", new SymbolSearchCursorState([], null, HashA))
                 })
        {
            Assert.IsFalse(SymbolSearchCursor.TryDecode(Encode(json), Binding(), 100, out _), json);
        }

        // The well-formed control case is accepted, so the refusals above are about shape, not the digest.
        var valid = Signed($$"""{"v":1,"a":[["{{HashA}}",1]],"w":"{{HashC}}","r":"{{HashC}}"}""",
            new SymbolSearchCursorState([new(HashA, 1)], HashC, HashC));
        Assert.IsTrue(SymbolSearchCursor.TryDecode(Encode(valid), Binding(), 100, out _), valid);
    }

    // Appends the digest the codec would compute for `state` to a hand-written object (which must end in '}').
    private static string Signed(string json, SymbolSearchCursorState state)
    {
        var issued = Decode(SymbolSearchCursor.Encode(state, Binding()));
        var digest = issued[(issued.IndexOf("\"b\":", StringComparison.Ordinal) + 4)..^1];
        return json[..^1] + ",\"b\":" + digest + "}";
    }

    private static string Decode(string cursor) => Encoding.UTF8.GetString(Base64Url.DecodeFromChars(cursor));

    private static string Encode(string json) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(json));
}
