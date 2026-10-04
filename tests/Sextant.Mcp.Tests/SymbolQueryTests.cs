namespace Sextant.Mcp.Tests;

[TestClass]
public class SymbolQueryTests
{
    [TestMethod]
    public void GenericArgumentsNestedToTheLimit_Parse()
    {
        var query = SymbolQuery.Parse($"Type.Method({Nested("A<", ">", SymbolTypeTerm.MaxNestingDepth)})");

        Assert.IsNull(query.Error);
        Assert.AreEqual(1, query.Parameters!.Count);
    }

    [TestMethod]
    [DataRow("A<", ">")]
    [DataRow("(int, ", ")")]
    public void TypesNestedBeyondTheLimit_AreInvalid(string open, string close)
    {
        var query = SymbolQuery.Parse($"Type.Method({Nested(open, close, SymbolTypeTerm.MaxNestingDepth + 1)})");

        Assert.IsNotNull(query.Error, "a nesting deeper than the limit is refused, never recursed into");
        StringAssert.Contains(query.Error, "could not be read");
    }

    [TestMethod]
    [DataRow("A<", ">")]
    [DataRow("(int, ", ")")]
    [DataRow("A{", "}")]
    public void HugelyNestedInput_IsInvalid_WithoutExhaustingTheStack(string open, string close)
    {
        // Far deeper than any stack the recursion could survive; refused as text before it is parsed.
        var query = SymbolQuery.Parse($"M:Type.Method({Nested(open, close, 20_000)})");

        Assert.IsNotNull(query.Error);
        StringAssert.Contains(query.Error, $"longer than {SymbolQuery.MaxLength} characters");
    }

    [TestMethod]
    public void InputLongerThanTheLimit_IsInvalid()
    {
        Assert.IsNull(SymbolQuery.Parse(new string('a', SymbolQuery.MaxLength)).Error);
        StringAssert.Contains(SymbolQuery.Parse(new string('a', SymbolQuery.MaxLength + 1)).Error,
            $"longer than {SymbolQuery.MaxLength} characters");
    }

    [TestMethod]
    public void ResolvingHugelyNestedInput_IsAnInvalidLookup()
    {
        var conn = McpTestFixtureInstance.Instance.Db.GetConnection();
        var lookup = SymbolResolver.Lookup(
            new Sextant.Store.SymbolStore(conn), new Sextant.Store.ProjectStore(conn),
            $"Type.Method({Nested("A<", ">", 20_000)})");

        Assert.AreEqual(SymbolLookupStatus.Invalid, lookup.Status);
    }

    private static string Nested(string open, string close, int depth) =>
        string.Concat(Enumerable.Repeat(open, depth)) + "B" + string.Concat(Enumerable.Repeat(close, depth));
}
