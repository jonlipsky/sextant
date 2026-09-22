namespace Sextant.Benchmarks.Tests;

[TestClass]
public sealed class CorpusGeneratorTests
{
    [TestMethod]
    public void CorrectnessCorpusEmitsRequiredShapes()
    {
        var root = NewTempDir();
        try
        {
            var slnx = CorpusGenerator.GenerateCorrectnessCorpus(root);

            Assert.IsTrue(File.Exists(slnx), "solution file should exist");

            var multiTarget = File.ReadAllText(Path.Combine(root, "MultiTarget", "MultiTarget.csproj"));
            StringAssert.Contains(multiTarget, "net10.0;netstandard2.0");

            // Multi-target source carries a shared member plus a #if NET10_0-gated conditional member.
            var formatter = File.ReadAllText(Path.Combine(root, "MultiTarget", "Formatter.cs"));
            StringAssert.Contains(formatter, "public string Join(");
            StringAssert.Contains(formatter, "#if NET10_0");
            StringAssert.Contains(formatter, "public string JoinModern(");

            var app = File.ReadAllText(Path.Combine(root, "App", "App.csproj"));
            StringAssert.Contains(app, "../Lib/Lib.csproj");

            // Partial type split across two files.
            Assert.IsTrue(File.Exists(Path.Combine(root, "Lib", "Widget.Part1.cs")));
            Assert.IsTrue(File.Exists(Path.Combine(root, "Lib", "Widget.Part2.cs")));

            // Duplicate member names across distinct types.
            var duplicates = File.ReadAllText(Path.Combine(root, "Lib", "Duplicates.cs"));
            StringAssert.Contains(duplicates, "class AlphaService");
            StringAssert.Contains(duplicates, "class BetaService");

            // Generic type + generic method (fallback-key territory for type parameters).
            var generics = File.ReadAllText(Path.Combine(root, "Lib", "Generics.cs"));
            StringAssert.Contains(generics, "class Box<T>");
            StringAssert.Contains(generics, "Map<TOut>");

            // Explicit interface implementations sharing a display name.
            var interfaces = File.ReadAllText(Path.Combine(root, "Lib", "Interfaces.cs"));
            StringAssert.Contains(interfaces, "void ILeft.Run()");
            StringAssert.Contains(interfaces, "void IRight.Run()");

            // Record with a primary constructor.
            var records = File.ReadAllText(Path.Combine(root, "Lib", "Records.cs"));
            StringAssert.Contains(records, "record Point(int X, int Y)");

            // Anonymous-object property names that must not become top-level symbols.
            var anonymous = File.ReadAllText(Path.Combine(root, "Lib", "Anonymous.cs"));
            StringAssert.Contains(anonymous, "type = \"widget\"");
            StringAssert.Contains(anonymous, "description = \"a thing\"");

            // Same fully-qualified name (global::Lib.Shared) in two separate assemblies.
            var libShared = File.ReadAllText(Path.Combine(root, "Lib", "Shared.cs"));
            var lib2Shared = File.ReadAllText(Path.Combine(root, "Lib2", "Shared.cs"));
            StringAssert.Contains(libShared, "class Shared");
            StringAssert.Contains(lib2Shared, "class Shared");
            StringAssert.Contains(lib2Shared, "namespace Lib;");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public void GeneratedLargeSolutionIsByteForByteDeterministic()
    {
        var a = NewTempDir();
        var b = NewTempDir();
        try
        {
            CorpusGenerator.GenerateLargeSolution(a, projectCount: 3, typesPerProject: 2, methodsPerType: 2);
            CorpusGenerator.GenerateLargeSolution(b, projectCount: 3, typesPerProject: 2, methodsPerType: 2);

            var filesA = RelativeFiles(a);
            var filesB = RelativeFiles(b);
            CollectionAssert.AreEquivalent(filesA, filesB, "file sets must match");

            foreach (var rel in filesA)
            {
                var bytesA = File.ReadAllBytes(Path.Combine(a, rel));
                var bytesB = File.ReadAllBytes(Path.Combine(b, rel));
                CollectionAssert.AreEqual(bytesA, bytesB, $"content of {rel} must be identical across runs");
            }
        }
        finally
        {
            TryDelete(a);
            TryDelete(b);
        }
    }

    private static List<string> RelativeFiles(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sextant-corpus-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }
}
