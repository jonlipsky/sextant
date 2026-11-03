using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Data.Sqlite;
using Sextant.Store;

namespace Sextant.Indexer.Tests;

/// <summary>
/// Issue #388: the windowed path (a fresh solution per group of projects, the projects outside a window read as
/// metadata-only images from a <see cref="SkeletonCache"/>) must persist exactly what the one-solution path does,
/// across a window boundary, for the symbols whose image form differs from their source form: implicit constructors,
/// records, delegates, generics, nested types, internals made visible, partial types, and a project whose image
/// cannot be emitted (so its source is used instead).
/// </summary>
[TestClass]
public class CompilationWindowsEquivalenceTests
{
    private string _root = null!;

    [TestInitialize]
    public void Init()
    {
        _root = Path.Combine(Path.GetTempPath(), $"sextant_windows_eq_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [TestMethod]
    [DataRow(1, 1, true)]
    [DataRow(2, 1, true)]
    [DataRow(2, 3, true)]
    [DataRow(3, 4, true)]
    [DataRow(2, 1, false)]
    [DataRow(1, 4, false)]
    public async Task WindowedIndex_EqualsTheOneSolutionIndex(int window, int projectsInFlight, bool skeletons)
    {
        var whole = await IndexAsync(window: 0, projectsInFlight, skeletons: false, tag: "whole");
        var windowed = await IndexAsync(window, projectsInFlight, skeletons, tag: "windowed");

        Assert.IsTrue(whole.Contains("Widget"), "the fixture was indexed");
        Assert.AreEqual(whole, windowed, FirstDifference(whole, windowed));
    }

    [TestMethod]
    public async Task WindowedIndex_StoresNoImplicitMemberOfAnImage()
    {
        var dump = await IndexAsync(window: 1, projectsInFlight: 1, skeletons: true, tag: "implicit");
        foreach (var banned in new[] { "M:Base.Point.Clone", "M:Base.Point.PrintMembers", "P:Base.Point.EqualityContract", "M:Base.Handler.Invoke" })
            Assert.IsFalse(dump.Contains(banned), $"{banned} is implicit and must not be stored");
    }

    private async Task<string> IndexAsync(int window, int projectsInFlight, bool skeletons, string tag)
    {
        var dbPath = Path.Combine(_root, $"{tag}-{window}-{projectsInFlight}-{skeletons}.db");
        using var db = new IndexDatabase(dbPath);
        db.RunMigrations();
        var options = new ExtractionParallelismOptions
        {
            MaxParallelism = 4, QueueCapacity = 2, ProjectsInFlight = projectsInFlight,
            CompilationWindow = window, CompilationWindowMinProjects = 0, CompilationSkeletons = skeletons
        };
        await new IndexOrchestrator(db, useDocumentExtractor: true, parallelism: options)
            .IndexSolutionAsync(BuildSolution(tag));
        return Dump(db.GetConnection());
    }

    private Solution BuildSolution(string tag)
    {
        var workspace = new AdhocWorkspace();
        var solution = workspace.CurrentSolution;
        var ids = new Dictionary<string, ProjectId>();

        void Add(string name, string[] references, params (string File, string Source)[] files)
        {
            var id = ProjectId.CreateNewId();
            ids[name] = id;
            var dir = Path.Combine(_root, "fixture", name);
            Directory.CreateDirectory(dir);
            var projectPath = Path.Combine(dir, name + ".csproj");
            File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            solution = solution.AddProject(ProjectInfo.Create(id, VersionStamp.Default, name, name, LanguageNames.CSharp,
                filePath: projectPath, metadataReferences: Framework(),
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                parseOptions: new CSharpParseOptions(LanguageVersion.Latest)));
            foreach (var (file, source) in files)
            {
                var path = Path.Combine(dir, file);
                File.WriteAllText(path, source);
                solution = solution.AddDocument(DocumentId.CreateNewId(id), file, SourceText.From(source), filePath: path);
            }
            foreach (var reference in references)
                solution = solution.AddProjectReference(id, new ProjectReference(ids[reference]));
        }

        Add("Base", [],
            ("Types.cs", """
                using System.Runtime.CompilerServices;
                [assembly: InternalsVisibleTo("Mid")]
                namespace Base
                {
                    // TODO: base todo
                    public class Widget { public int Size; public Nested.Part Part = new Nested.Part(); }
                    public class Nested { public class Part { public int Id; } }
                    public record Point(int X, int Y);
                    public record struct Pair(int A, int B);
                    public delegate int Handler(int value);
                    public class Box<T> { public T Value; public Box(T value) { Value = value; } public T Get() => Value; }
                    internal class Secret { public int Reveal() => 42; }
                    public partial class Split { public int One() => 1; }
                    public class Primary(int seed) { public int Seed => seed; }
                }
                """),
            ("Split2.cs", """
                namespace Base
                {
                    // FIXME: second half
                    public partial class Split { public int Two() => 2; }
                }
                """));

        // Its image cannot be emitted (an unresolved type), so referencing projects read its source.
        Add("Broken", ["Base"],
            ("Broken.cs", """
                namespace Broken
                {
                    public class Faulty { public Missing.Thing Field; public Base.Widget W = new Base.Widget(); public int Ok() => 1; }
                    public class Healthy { public int Fine() => 2; }
                }
                """));

        Add("Mid", ["Base", "Broken"],
            ("Mid.cs", """
                using Base;
                namespace Mid
                {
                    public class Consumer
                    {
                        // TODO: use everything
                        public int Run()
                        {
                            var w = new Widget();
                            var p = new Point(1, 2);
                            var q = p with { X = 3 };
                            var (x, y) = q;
                            Handler h = v => v + x + y;
                            var box = new Box<Widget>(w);
                            var part = new Nested.Part();
                            var split = new Split();
                            var primary = new Primary(7);
                            var pair = new Pair(1, 2);
                            var faulty = new Broken.Faulty();
                            var healthy = new Broken.Healthy();
                            return h.Invoke(w.Size) + box.Get().Size + part.Id + new Secret().Reveal() + split.One() + split.Two()
                                + primary.Seed + pair.A + faulty.Ok() + healthy.Fine() + (p.Equals(q) ? 1 : 0);
                        }
                    }
                    public interface IUseHandler { Handler Make(); }
                }
                """));

        Add("LeafA", ["Mid", "Base"],
            ("LeafA.cs", """
                namespace LeafA
                {
                    public class A : Mid.Consumer, Mid.IUseHandler
                    {
                        public Base.Handler Make() => v => v;
                        public int Go() => Run() + new Base.Widget().Size + new Base.Point(1, 1).X;
                    }
                }
                """));

        Add("LeafB", ["LeafA", "Broken"],
            ("LeafB.cs", """
                namespace LeafB
                {
                    public class B { public int Go() => new LeafA.A().Go() + new Broken.Healthy().Fine() + new Broken.Faulty().Ok(); }
                }
                """));

        Add("LeafC", ["Base", "LeafB"],
            ("LeafC.cs", """
                namespace LeafC
                {
                    public class C { public Base.Box<Base.Point> Make() => new Base.Box<Base.Point>(new Base.Point(0, 0)); public int Go() => new LeafB.B().Go(); }
                }
                """));

        return solution;
    }

    private static MetadataReference[] Framework()
    {
        var runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        return
        [
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Runtime.dll")),
        ];
    }

    private static string Dump(SqliteConnection conn)
    {
        var sb = new StringBuilder();
        Section(sb, conn, "symbols", """
            SELECT p.canonical_id, s.symbol_key, s.fully_qualified_name, s.kind, s.accessibility, s.line_start, s.line_end
            FROM symbols s JOIN projects p ON s.project_id = p.id ORDER BY 1, 2, 3
            """);
        Section(sb, conn, "occurrences", """
            SELECT ip.canonical_id, tp.canonical_id, ts.symbol_key, COALESCE(sp.canonical_id,''), COALESCE(ss.symbol_key,''),
                   COALESCE(f.repo_relative_path,''), o.line, o.col, o.kind, o.flags
            FROM occurrences o
            JOIN symbols ts ON o.target_symbol_id = ts.id JOIN projects tp ON ts.project_id = tp.id
            JOIN projects ip ON o.in_project_id = ip.id
            LEFT JOIN symbols ss ON o.source_symbol_id = ss.id LEFT JOIN projects sp ON ss.project_id = sp.id
            LEFT JOIN file_versions fv ON fv.id = o.file_version_id LEFT JOIN files f ON f.id = fv.file_id
            ORDER BY 1, 2, 3, 4, 5, 6, 7, 8, 9, 10
            """);
        Section(sb, conn, "relationships", """
            SELECT fp.canonical_id, fs.symbol_key, tp.canonical_id, ts.symbol_key, rel.kind
            FROM relationships rel
            JOIN symbols fs ON rel.from_symbol_id = fs.id JOIN projects fp ON fs.project_id = fp.id
            JOIN symbols ts ON rel.to_symbol_id = ts.id JOIN projects tp ON ts.project_id = tp.id
            ORDER BY 1, 2, 3, 4, 5
            """);
        Section(sb, conn, "comments", """
            SELECT p.canonical_id, COALESCE(f.repo_relative_path,''), c.line, c.tag, c.text, COALESCE(s.symbol_key,'')
            FROM comments c JOIN projects p ON c.project_id = p.id
            LEFT JOIN file_versions fv ON fv.id = c.file_version_id LEFT JOIN files f ON f.id = fv.file_id
            LEFT JOIN symbols s ON s.id = c.enclosing_symbol_id
            ORDER BY 1, 2, 3, 4
            """);
        return sb.ToString();
    }

    private static void Section(StringBuilder sb, SqliteConnection conn, string name, string sql)
    {
        sb.Append("=== ").Append(name).Append('\n');
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            for (var i = 0; i < reader.FieldCount; i++)
            {
                if (i > 0) sb.Append('\u001f');
                sb.Append(reader.IsDBNull(i) ? "\u2205" : reader.GetValue(i).ToString());
            }
            sb.Append('\n');
        }
    }

    private static string FirstDifference(string a, string b)
    {
        var x = a.Split('\n');
        var y = b.Split('\n');
        for (var i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            var l = i < x.Length ? x[i] : "<eof>";
            var r = i < y.Length ? y[i] : "<eof>";
            if (l != r) return $"line {i + 1}:\n  whole:    {l}\n  windowed: {r}";
        }
        return "(equal)";
    }
}
