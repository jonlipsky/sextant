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

            var app = File.ReadAllText(Path.Combine(root, "App", "App.csproj"));
            StringAssert.Contains(app, "../Lib/Lib.csproj");

            // Partial type split across two files.
            Assert.IsTrue(File.Exists(Path.Combine(root, "Lib", "Widget.Part1.cs")));
            Assert.IsTrue(File.Exists(Path.Combine(root, "Lib", "Widget.Part2.cs")));

            // Duplicate member names across distinct types.
            var duplicates = File.ReadAllText(Path.Combine(root, "Lib", "Duplicates.cs"));
            StringAssert.Contains(duplicates, "class AlphaService");
            StringAssert.Contains(duplicates, "class BetaService");
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
