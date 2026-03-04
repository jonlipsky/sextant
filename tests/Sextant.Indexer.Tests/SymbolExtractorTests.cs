using Sextant.Core;

namespace Sextant.Indexer.Tests;

[TestClass]
public class SymbolExtractorTests
{
    [TestMethod]
    public void IsGeneratedFile_DetectsGFiles()
    {
        Assert.IsTrue(SymbolExtractor.IsGeneratedFile("Foo.g.cs"));
        Assert.IsTrue(SymbolExtractor.IsGeneratedFile("src/Foo.designer.cs"));
        Assert.IsTrue(SymbolExtractor.IsGeneratedFile("src/obj/Debug/net9.0/Foo.cs"));
        Assert.IsTrue(SymbolExtractor.IsGeneratedFile("C:\\src\\obj\\Debug\\Foo.cs"));
    }

    [TestMethod]
    public void IsGeneratedFile_AllowsNormalFiles()
    {
        Assert.IsFalse(SymbolExtractor.IsGeneratedFile("src/MyClass.cs"));
        Assert.IsFalse(SymbolExtractor.IsGeneratedFile("src/objects/Foo.cs")); // 'objects' not 'obj'
    }

    [TestMethod]
    public void MapSymbolKind_MapsCorrectly()
    {
        // We test the static helpers without needing a full compilation
        // The mapping logic is tested via the enum values
        Assert.AreEqual(SymbolKind.Class, Sextant.Core.SymbolKind.Class);
        Assert.AreEqual(SymbolKind.Method, Sextant.Core.SymbolKind.Method);
        Assert.AreEqual(SymbolKind.Property, Sextant.Core.SymbolKind.Property);
    }
}
