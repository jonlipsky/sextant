using Sextant.Cli.Handlers;

namespace Sextant.Cli.Tests;

[TestClass]
public class QueryHandlerTests
{
    [TestMethod]
    public void Limit_IsAbsentOrAPositiveInteger()
    {
        Assert.IsNull(QueryHandler.GetLimit(["App.Foo"]));
        Assert.AreEqual(25, QueryHandler.GetLimit(["App.Foo", "--limit", "25"]));
    }

    // A malformed --limit is a usage error (Run prints ArgumentException messages), never an unhandled crash.
    [TestMethod]
    [DataRow("abc")]
    [DataRow("0")]
    [DataRow("-5")]
    [DataRow("99999999999")]
    public void Limit_Malformed_IsAUsageError(string value)
    {
        var ex = Assert.ThrowsExactly<ArgumentException>(() => QueryHandler.GetLimit(["App.Foo", "--limit", value]));
        StringAssert.Contains(ex.Message, "--limit");
    }
}
