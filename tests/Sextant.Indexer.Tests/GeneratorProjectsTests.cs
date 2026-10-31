using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Sextant.Indexer.Tests;

/// <summary>
/// A source generator referenced as a project (<c>OutputItemType="Analyzer"</c>) is built from the workspace, so the
/// consumer's compilation sees the code it generates (issue #352).
/// </summary>
[TestClass]
public class GeneratorProjectsTests
{
    private const string GeneratorSource = """
        using Microsoft.CodeAnalysis;
        [Generator]
        public sealed class HelloGenerator : ISourceGenerator
        {
            public void Initialize(GeneratorInitializationContext context) { }
            public void Execute(GeneratorExecutionContext context) =>
                context.AddSource("Hello.g.cs", "public static class Hello { public static int Value => 1; }");
        }
        """;

    private string _root = null!;

    [TestInitialize]
    public void Init()
    {
        _root = Path.Combine(Path.GetTempPath(), $"sextant_generator_projects_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [TestMethod]
    public async Task AGeneratorProject_IsBuilt_AndItsOutputBindsInTheConsumer()
    {
        var (solution, consumerId) = Build(GeneratorSource, out var output);
        var messages = new List<string>();

        var resolved = await GeneratorProjects.ResolveAsync(solution, Path.Combine(_root, "scratch"), messages.Add);

        var consumer = resolved.GetProject(consumerId)!;
        Assert.AreEqual(0, consumer.AnalyzerReferences.OfType<UnresolvedAnalyzerReference>().Count(), string.Join("; ", messages));
        var compilation = (await consumer.GetCompilationAsync())!;
        var driver = CSharpGeneratorDriver.Create(
            consumer.AnalyzerReferences.SelectMany(r => r.GetGenerators(LanguageNames.CSharp)).ToArray());
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out _);
        Assert.IsTrue(updated.SyntaxTrees.Any(t => t.FilePath.EndsWith("Hello.g.cs", StringComparison.Ordinal)),
            $"the generator ran; {output}");
    }

    [TestMethod]
    public async Task AGeneratorThatDoesNotCompile_KeepsTheUnresolvedReference_AndReportsWhy()
    {
        var (solution, consumerId) = Build("public class Broken : NotAType { }", out _);
        var messages = new List<string>();

        var resolved = await GeneratorProjects.ResolveAsync(solution, Path.Combine(_root, "scratch"), messages.Add);

        Assert.AreEqual(1, resolved.GetProject(consumerId)!.AnalyzerReferences.OfType<UnresolvedAnalyzerReference>().Count());
        Assert.IsTrue(messages.Count > 0, "a diagnostic explains the unresolved generator");
    }

    [TestMethod]
    public async Task WithoutScratch_NothingIsBuilt()
    {
        var (solution, _) = Build(GeneratorSource, out _);

        Assert.AreSame(solution, await GeneratorProjects.ResolveAsync(solution, null));
    }

    private (Solution Solution, ProjectId Consumer) Build(
        string generatorSource, out string output, string subDirectory = "", IEnumerable<MetadataReference>? generatorReferences = null,
        Func<ProjectInfo, ProjectInfo>? configureGenerator = null, Func<Workspace, Solution>? addProjects = null)
    {
        var root = Path.Combine(_root, subDirectory);
        output = Path.Combine(root, "gen", "bin", "Gen.dll");
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(p => MetadataReference.CreateFromFile(p))
            .Append(MetadataReference.CreateFromFile(typeof(ISourceGenerator).Assembly.Location))
            .ToList();

        var workspace = new AdhocWorkspace();
        var generatorId = ProjectId.CreateNewId();
        var generator = ProjectInfo.Create(generatorId, VersionStamp.Create(), "Gen", "Gen", LanguageNames.CSharp,
                filePath: Path.Combine(root, "gen", "Gen.csproj"), outputFilePath: output,
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                metadataReferences: references.Concat(generatorReferences ?? []))
            .WithDocuments([DocumentInfo.Create(DocumentId.CreateNewId(generatorId), "Gen.cs",
                loader: TextLoader.From(TextAndVersion.Create(Microsoft.CodeAnalysis.Text.SourceText.From(generatorSource), VersionStamp.Create())),
                filePath: Path.Combine(root, "gen", "Gen.cs"))]);
        if (configureGenerator is not null)
            generator = configureGenerator(generator);

        var consumerId = ProjectId.CreateNewId();
        var consumer = ProjectInfo.Create(consumerId, VersionStamp.Create(), "App", "App", LanguageNames.CSharp,
                filePath: Path.Combine(root, "app", "App.csproj"),
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                metadataReferences: references)
            .WithAnalyzerReferences([new UnresolvedAnalyzerReference(output)]);

        var solution = workspace.CurrentSolution.AddProject(generator).AddProject(consumer);
        return (addProjects?.Invoke(workspace) is { } extended ? extended : solution, consumerId);
    }

    private static string UniqueGenerator(string marker, string body = "\"1\"") =>
        $$"""
        using Microsoft.CodeAnalysis;
        [Generator]
        public sealed class G{{marker}} : ISourceGenerator
        {
            public void Initialize(GeneratorInitializationContext context) { }
            public void Execute(GeneratorExecutionContext context) =>
                context.AddSource("Out{{marker}}.g.cs", "public static class Out{{marker}} { public static string Value => \"" + {{body}} + "\"; }");
        }
        """;

    private static async Task<bool> GeneratesAsync(Solution solution, ProjectId consumerId, string marker)
    {
        var consumer = solution.GetProject(consumerId)!;
        var compilation = (await consumer.GetCompilationAsync())!;
        var driver = CSharpGeneratorDriver.Create(
            consumer.AnalyzerReferences.SelectMany(r => r.GetGenerators(LanguageNames.CSharp)).ToArray());
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out _);
        return updated.SyntaxTrees.Any(t => t.FilePath.EndsWith($"Out{marker}.g.cs", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task TheSameGeneratorLoadedTwice_CreatesOneContext_AndSharesTheReference()
    {
        var marker = Guid.NewGuid().ToString("N");
        var source = UniqueGenerator(marker);
        var cache = Path.Combine(_root, "cache");
        var contexts = GeneratorProjects.LoadedContexts;
        var entries = GeneratorProjects.CachedEntries;

        // A second job: another workspace and checkout location, the same generator source.
        var (first, firstConsumer) = Build(source, out _, "job1");
        var (second, secondConsumer) = Build(source, out _, "job2");
        var a = await GeneratorProjects.ResolveAsync(first, cache);
        var b = await GeneratorProjects.ResolveAsync(second, cache);

        Assert.AreEqual(contexts + 1, GeneratorProjects.LoadedContexts, "one load context for one generator");
        Assert.AreEqual(entries + 1, GeneratorProjects.CachedEntries);
        Assert.AreSame(
            a.GetProject(firstConsumer)!.AnalyzerReferences.Single(), b.GetProject(secondConsumer)!.AnalyzerReferences.Single());
        Assert.IsTrue(await GeneratesAsync(b, secondConsumer, marker));
        Assert.AreEqual(1, Directory.GetDirectories(cache).Length, "one stable directory per content hash");
    }

    [TestMethod]
    public async Task ChangedGeneratorSource_BuildsAnotherEntry()
    {
        var cache = Path.Combine(_root, "cache");
        var entries = GeneratorProjects.CachedEntries;
        var contexts = GeneratorProjects.LoadedContexts;
        var (one, _) = Build(UniqueGenerator(Guid.NewGuid().ToString("N")), out _, "a");
        var (two, _) = Build(UniqueGenerator(Guid.NewGuid().ToString("N")), out _, "b");

        await GeneratorProjects.ResolveAsync(one, cache);
        await GeneratorProjects.ResolveAsync(two, cache);

        Assert.AreEqual(entries + 2, GeneratorProjects.CachedEntries);
        Assert.AreEqual(contexts + 2, GeneratorProjects.LoadedContexts);
    }

    [TestMethod]
    public async Task ConcurrentJobsOfOneGenerator_LoadItOnce()
    {
        var marker = Guid.NewGuid().ToString("N");
        var cache = Path.Combine(_root, "cache");
        var contexts = GeneratorProjects.LoadedContexts;
        var jobs = Enumerable.Range(0, 8).Select(i => Build(UniqueGenerator(marker), out _, $"job{i}")).ToList();

        var results = await Task.WhenAll(jobs.Select(j => Task.Run(() => GeneratorProjects.ResolveAsync(j.Solution, cache))));

        Assert.AreEqual(contexts + 1, GeneratorProjects.LoadedContexts);
        for (var i = 0; i < jobs.Count; i++)
            Assert.IsTrue(await GeneratesAsync(results[i], jobs[i].Consumer, marker), $"job {i}");
    }

    [TestMethod]
    public async Task PastTheCap_NothingMoreIsLoaded_AndTheGeneratorIsReported()
    {
        var cache = Path.Combine(_root, "cache");
        var previous = GeneratorProjects.CapForTests;
        try
        {
            var (kept, _) = Build(UniqueGenerator(Guid.NewGuid().ToString("N")), out _, "kept");
            var (over, overConsumer) = Build(UniqueGenerator(Guid.NewGuid().ToString("N")), out _, "over");
            GeneratorProjects.CapForTests = GeneratorProjects.CachedEntries + 1;
            await GeneratorProjects.ResolveAsync(kept, cache);
            var contexts = GeneratorProjects.LoadedContexts;
            var messages = new List<string>();
            var warnings = new List<string>();

            var resolved = await GeneratorProjects.ResolveAsync(over, cache, messages.Add, onWarning: warnings.Add);

            Assert.AreEqual(contexts, GeneratorProjects.LoadedContexts, "nothing is loaded past the cap");
            Assert.AreEqual(1, resolved.GetProject(overConsumer)!.AnalyzerReferences.OfType<UnresolvedAnalyzerReference>().Count());
            Assert.IsTrue(messages.Any(m => m.Contains("could not be built from source", StringComparison.Ordinal)
                && m.Contains("distinct generator builds", StringComparison.Ordinal)), string.Join("; ", messages));
            Assert.IsTrue(warnings.Any(m => m.Contains("distinct generator builds", StringComparison.Ordinal)), "a warning-level line");
        }
        finally { GeneratorProjects.CapForTests = previous; }
    }

    [TestMethod]
    public async Task ANuGetDependencyOfTheGenerator_IsLoadedFromItsReferencePath()
    {
        var marker = Guid.NewGuid().ToString("N");
        var depName = "SextantDep" + marker;
        var depDirectory = Path.Combine(_root, "packages", "dep", "1.0.0", "lib", "netstandard2.0");
        Directory.CreateDirectory(depDirectory);
        var depPath = Path.Combine(depDirectory, depName + ".dll");
        var depCompilation = CSharpCompilation.Create(
            depName,
            [CSharpSyntaxTree.ParseText($"namespace {depName} {{ public static class Dep {{ public static string Text => \"from-dep\"; }} }}")],
            ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(p => MetadataReference.CreateFromFile(p)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.IsTrue(depCompilation.Emit(depPath).Success);

        var (solution, consumerId) = Build(
            UniqueGenerator(marker, $"{depName}.Dep.Text"), out _, generatorReferences: [MetadataReference.CreateFromFile(depPath)]);
        var messages = new List<string>();

        var resolved = await GeneratorProjects.ResolveAsync(solution, Path.Combine(_root, "cache"), messages.Add);

        Assert.AreEqual(0, resolved.GetProject(consumerId)!.AnalyzerReferences.OfType<UnresolvedAnalyzerReference>().Count(), string.Join("; ", messages));
        var consumer = resolved.GetProject(consumerId)!;
        var driver = CSharpGeneratorDriver.Create(consumer.AnalyzerReferences.SelectMany(r => r.GetGenerators(LanguageNames.CSharp)).ToArray());
        driver = (CSharpGeneratorDriver)driver.RunGeneratorsAndUpdateCompilation((await consumer.GetCompilationAsync())!, out _, out var diagnostics);
        var text = driver.GetRunResult().GeneratedTrees.Single().GetText().ToString();
        Assert.IsTrue(text.Contains("from-dep", StringComparison.Ordinal), text + string.Join("; ", diagnostics));
    }

    [TestMethod]
    public async Task TwoProjectsOfTheClosureWithOneAssemblyName_FailTheBuild_NeitherOverwritesTheOther()
    {
        var cache = Path.Combine(_root, "cache");
        var libId = ProjectId.CreateNewId();
        var (solution, consumerId) = Build(
            UniqueGenerator(Guid.NewGuid().ToString("N")), out _,
            configureGenerator: g => g.WithProjectReferences([new ProjectReference(libId)]),
            addProjects: null);
        var lib = ProjectInfo.Create(libId, VersionStamp.Create(), "OtherLib", "Gen", LanguageNames.CSharp,
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                metadataReferences: solution.Projects.First().MetadataReferences)
            .WithDocuments([DocumentInfo.Create(DocumentId.CreateNewId(libId), "Lib.cs",
                loader: TextLoader.From(TextAndVersion.Create(Microsoft.CodeAnalysis.Text.SourceText.From("public class L { }"), VersionStamp.Create())))]);
        solution = solution.AddProject(lib);
        var messages = new List<string>();

        var resolved = await GeneratorProjects.ResolveAsync(solution, cache, messages.Add);

        Assert.AreEqual(1, resolved.GetProject(consumerId)!.AnalyzerReferences.OfType<UnresolvedAnalyzerReference>().Count());
        Assert.IsTrue(messages.Any(m => m.Contains("both produce the assembly 'Gen'", StringComparison.Ordinal)), string.Join("; ", messages));
    }

    [TestMethod]
    public async Task FailedBuilds_DoNotCountAgainstTheCap_AndAreRetried()
    {
        var cache = Path.Combine(_root, "cache");
        var entries = GeneratorProjects.CachedEntries;
        var broken = $"public class B{Guid.NewGuid():N} : NotAType {{ }}";
        for (var i = 0; i < 3; i++)
        {
            var (solution, _) = Build(broken, out _, $"f{i}");
            await GeneratorProjects.ResolveAsync(solution, cache);
        }
        Assert.AreEqual(entries, GeneratorProjects.CachedEntries);

        // A transient failure (the cache location is a file) is not remembered: the next job succeeds.
        var blocked = Path.Combine(_root, "blocked");
        var marker = Guid.NewGuid().ToString("N");
        var (retry, consumer) = Build(UniqueGenerator(marker), out _, "retry");
        File.WriteAllText(blocked, "x");
        var messages = new List<string>();
        await GeneratorProjects.ResolveAsync(retry, blocked, messages.Add);
        Assert.IsTrue(messages.Any(m => m.Contains("could not be built", StringComparison.Ordinal)), string.Join("; ", messages));
        Assert.AreEqual(entries, GeneratorProjects.CachedEntries, "the failed entry is dropped");
        File.Delete(blocked);

        var resolved = await GeneratorProjects.ResolveAsync(retry, blocked);
        Assert.IsTrue(await GeneratesAsync(resolved, consumer, marker));
    }

    [TestMethod]
    public async Task ADependency_IsCopiedIntoTheEntry_SoRemovingTheOriginalDoesNotBreakIt()
    {
        var marker = Guid.NewGuid().ToString("N");
        var depName = "SextantCopy" + marker;
        var depDirectory = Path.Combine(_root, "packages", "dep", "lib", "netstandard2.0");
        Directory.CreateDirectory(depDirectory);
        var depPath = Path.Combine(depDirectory, depName + ".dll");
        var depCompilation = CSharpCompilation.Create(
            depName,
            [CSharpSyntaxTree.ParseText($"namespace {depName} {{ public static class Dep {{ public static string Text => \"copied\"; }} }}")],
            ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(p => MetadataReference.CreateFromFile(p)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.IsTrue(depCompilation.Emit(depPath).Success);
        var cache = Path.Combine(_root, "cache");
        var (solution, consumerId) = Build(
            UniqueGenerator(marker, $"{depName}.Dep.Text"), out _, generatorReferences: [MetadataReference.CreateFromFile(depPath)]);

        var resolved = await GeneratorProjects.ResolveAsync(solution, cache);
        File.Delete(depPath);

        var entryDirectory = Directory.GetDirectories(cache).Single();
        Assert.IsTrue(File.Exists(Path.Combine(entryDirectory, "deps", depName + ".dll")), "the dependency lives in the entry");
        var consumer = resolved.GetProject(consumerId)!;
        var driver = CSharpGeneratorDriver.Create(consumer.AnalyzerReferences.SelectMany(r => r.GetGenerators(LanguageNames.CSharp)).ToArray());
        driver = (CSharpGeneratorDriver)driver.RunGeneratorsAndUpdateCompilation((await consumer.GetCompilationAsync())!, out _, out _);
        Assert.IsTrue(driver.GetRunResult().GeneratedTrees.Single().GetText().ToString().Contains("copied", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ADependencyWithChangedBytes_BuildsAnotherEntry()
    {
        var marker = Guid.NewGuid().ToString("N");
        var depName = "SextantChg" + marker;
        var cache = Path.Combine(_root, "cache");
        var contexts = GeneratorProjects.LoadedContexts;
        foreach (var text in new[] { "one", "two" })
        {
            var depDirectory = Path.Combine(_root, "packages-" + text, "lib", "netstandard2.0");
            Directory.CreateDirectory(depDirectory);
            var depPath = Path.Combine(depDirectory, depName + ".dll");
            var depCompilation = CSharpCompilation.Create(
                depName,
                [CSharpSyntaxTree.ParseText($"namespace {depName} {{ public static class Dep {{ public static string Text => \"{text}\"; }} }}")],
                ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(p => MetadataReference.CreateFromFile(p)),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            Assert.IsTrue(depCompilation.Emit(depPath).Success);
            var (solution, _) = Build(
                UniqueGenerator(marker, $"{depName}.Dep.Text"), out _, text, generatorReferences: [MetadataReference.CreateFromFile(depPath)]);
            await GeneratorProjects.ResolveAsync(solution, cache);
        }

        Assert.AreEqual(contexts + 2, GeneratorProjects.LoadedContexts, "the dependency's content is part of the hash");
    }

    [TestMethod]
    public void OnlyANuGetReferenceAssemblyLayout_IsFilteredAsAReferencePath()
    {
        var sep = Path.DirectorySeparatorChar;
        string P(params string[] parts) => sep + string.Join(sep, parts);

        Assert.IsTrue(GeneratorProjects.IsReferenceAssemblyPath(P("packs", "Microsoft.NETCore.App.Ref", "8.0.0", "ref", "net8.0", "System.dll")));
        Assert.IsTrue(GeneratorProjects.IsReferenceAssemblyPath(P("pkg", "ref", "netstandard2.0", "A.dll")));
        Assert.IsFalse(GeneratorProjects.IsReferenceAssemblyPath(P("work", "ref", "src", "bin", "A.dll")));
        Assert.IsFalse(GeneratorProjects.IsReferenceAssemblyPath(P("work", "ref", "A.dll")));
        Assert.IsFalse(GeneratorProjects.IsReferenceAssemblyPath(P("pkg", "lib", "netstandard2.0", "A.dll")));
    }

    [TestMethod]
    public async Task TheFirstUseAfterARestart_SweepsUnusedHashDirectories_AndTmpFiles()
    {
        var cache = Path.Combine(_root, "cache");
        var stale = Path.Combine(cache, new string('a', 32));
        Directory.CreateDirectory(stale);
        File.WriteAllText(Path.Combine(stale, "x.dll"), "x");
        var other = Path.Combine(cache, "keepme");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(cache, "left.over.tmp"), "x");
        var (solution, _) = Build(UniqueGenerator(Guid.NewGuid().ToString("N")), out _);

        await GeneratorProjects.ResolveAsync(solution, cache);

        Assert.IsFalse(Directory.Exists(stale), "an unused hash directory is removed");
        Assert.IsFalse(File.Exists(Path.Combine(cache, "left.over.tmp")));
        Assert.IsTrue(Directory.Exists(other), "only hash-named directories are touched");
    }
}
