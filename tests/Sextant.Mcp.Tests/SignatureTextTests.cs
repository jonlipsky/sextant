namespace Sextant.Mcp.Tests;

/// <summary>
/// <see cref="SignatureText"/> reads <c>find_by_signature</c>'s return and parameter types out of both signature
/// spellings the index holds: a declaration (migration 026) and the legacy display, which has neither a return type
/// nor parameter names.
/// </summary>
[TestClass]
public class SignatureTextTests
{
    [TestMethod]
    [DataRow("Task<ChatRecord> CreateAsync(string tenantId, string applicationInstanceId, DateTime installedAt, CancellationToken cancellationToken)",
        "Task<ChatRecord>", "string|string|DateTime|CancellationToken")]
    [DataRow("Task<ChatRecord?> FindAsync(string tenantId, CancellationToken cancellationToken = default)",
        "Task<ChatRecord?>", "string|CancellationToken")]
    [DataRow("static IEnumerable<T> Where2<T>(this IEnumerable<T> source, Func<T, bool> pred, params string[] tags)",
        "IEnumerable<T>", "IEnumerable<T>|Func<T, bool>|string[]")]
    [DataRow("static void M(ref int a, out string b, in long c, int d = 5, string e = \"x, y\", char g = ',', object? h = null)",
        "void", "int|string|long|int|string|char|object?")]
    [DataRow("public void Merge(string key, Dictionary<string, int> values, bool overwrite)",
        "void", "string|Dictionary<string, int>|bool")]
    [DataRow("abstract void Run()", "void", "")]
    [DataRow("void IDisposable.Dispose()", "void", "")]
    [DataRow("Base(int x, string? y = null)", null, "int|string?")]
    [DataRow("int Count { get; }", "int", "")]
    [DataRow("virtual int Size { get; protected set; }", "int", "")]
    [DataRow("string this[int index, string key] { get; set; }", "string", "int|string")]
    [DataRow("EventHandler<EventArgs>? Changed", "EventHandler<EventArgs>?", "")]
    [DataRow("event EventHandler<EventArgs>? Changed", "EventHandler<EventArgs>?", "")]
    [DataRow("const int Max = 10", "int", "")]
    [DataRow("static readonly string Field", "string", "")]
    [DataRow("Red = 1", null, "")]
    [DataRow("Task<int> Handler<T>(T item, CancellationToken ct = default)", "Task<int>", "T|CancellationToken")]
    [DataRow("static Money operator +(Money a, Money b)", "Money", "Money|Money")]
    [DataRow("static bool operator ==(Money a, Money b)", "bool", "Money|Money")]
    [DataRow("static bool operator <(Money a, Money b)", "bool", "Money|Money")]
    [DataRow("static implicit operator decimal(Money m)", "decimal", "Money")]
    // The legacy display: no return type, parameter types only.
    [DataRow("App.Channels.IChatStore.CreateAsync(string, string, System.DateTime, System.Threading.CancellationToken)",
        null, "string|string|System.DateTime|System.Threading.CancellationToken")]
    [DataRow("App.Channels.IChatStore.Load()", null, "")]
    [DataRow("App.Channels.IChatStore.Count", null, "")]
    public void Parse_ReadsReturnAndParameterTypes(string signature, string? returnType, string parameterTypes)
    {
        var parts = SignatureText.Parse(signature);

        Assert.AreEqual(returnType, parts.ReturnType, signature);
        Assert.AreEqual(parameterTypes, string.Join('|', parts.ParameterTypes), signature);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public void Parse_Empty_HasNoReturnTypeOrParameters(string? signature)
    {
        var parts = SignatureText.Parse(signature);

        Assert.IsNull(parts.ReturnType);
        Assert.AreEqual(0, parts.ParameterTypes.Count);
    }
}
