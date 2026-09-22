using System.Text;

namespace Sextant.Benchmarks;

/// <summary>
/// Deterministically writes benchmark fixture solutions to disk. Output is byte-for-byte
/// stable across runs (no timestamps or ordering nondeterminism), so a generated corpus can be
/// regenerated and compared. Nothing is written inside the Sextant source tree.
/// </summary>
public static class CorpusGenerator
{
    /// <summary>
    /// Writes the correctness corpus: a small multi-project solution that exercises duplicate
    /// member names across types, overloaded methods, partial types split across files, a
    /// project reference, and a multi-target project. Returns the solution (.slnx) path.
    /// </summary>
    public static string GenerateCorrectnessCorpus(string rootDir)
    {
        ResetDirectory(rootDir);

        WriteProject(rootDir, "Lib", ["net10.0"], projectRefs: [], new()
        {
            ["Duplicates.cs"] = """
                namespace Lib;

                // Duplicate member names across distinct types plus per-type overloads.
                public class AlphaService
                {
                    public int Handle(int id) => id + 1;
                    public string Handle(string name) => name + "!";
                }

                public class BetaService
                {
                    public int Handle(int id) => id - 1;
                    public string Handle(string name) => name + "?";
                }
                """,
            ["Overloads.cs"] = """
                namespace Lib;

                public class Calculator
                {
                    public int Add(int a, int b) => a + b;
                    public double Add(double a, double b) => a + b;
                    public int Add(int a, int b, int c) => a + b + c;
                }
                """,
            ["Widget.Part1.cs"] = """
                namespace Lib;

                public partial class Widget
                {
                    public int Width { get; set; }
                    public int Area() => Width * Height;
                }
                """,
            ["Widget.Part2.cs"] = """
                namespace Lib;

                public partial class Widget
                {
                    public int Height { get; set; }
                    public int Perimeter() => 2 * (Width + Height);
                }
                """,
        });

        WriteProject(rootDir, "MultiTarget", ["net10.0", "netstandard2.0"], projectRefs: [], new()
        {
            ["Formatter.cs"] = """
                using System.Text;

                namespace MultiTarget;

                public class Formatter
                {
                    public string Join(string a, string b)
                    {
                        var sb = new StringBuilder();
                        sb.Append(a);
                        sb.Append('/');
                        sb.Append(b);
                        return sb.ToString();
                    }
                }
                """,
        });

        WriteProject(rootDir, "App", ["net10.0"], projectRefs: ["Lib"], new()
        {
            ["Runner.cs"] = """
                using Lib;

                namespace App;

                public class Runner
                {
                    public int Run()
                    {
                        var alpha = new AlphaService();
                        var beta = new BetaService();
                        var calc = new Calculator();
                        var widget = new Widget { Width = 3, Height = 4 };

                        var total = alpha.Handle(1) + beta.Handle(2);
                        total += calc.Add(1, 2) + calc.Add(1, 2, 3);
                        total += alpha.Handle("x").Length + beta.Handle("y").Length;
                        total += widget.Area() + widget.Perimeter();
                        return total;
                    }
                }
                """,
        });

        return WriteSolution(rootDir, "Correctness", ["Lib", "MultiTarget", "App"]);
    }

    /// <summary>
    /// Writes a large generated solution of <paramref name="projectCount"/> chained projects, each
    /// with <paramref name="typesPerProject"/> types of <paramref name="methodsPerType"/> methods.
    /// Types call sibling methods and a type from the referenced project to produce references and
    /// call-graph edges at scale. Returns the solution (.slnx) path.
    /// </summary>
    public static string GenerateLargeSolution(
        string rootDir, int projectCount, int typesPerProject, int methodsPerType)
    {
        ResetDirectory(rootDir);
        var projectNames = new List<string>();

        for (var p = 0; p < projectCount; p++)
        {
            var name = $"Proj{p:D3}";
            projectNames.Add(name);
            var refs = p > 0 ? new List<string> { $"Proj{p - 1:D3}" } : [];

            var files = new Dictionary<string, string>();
            for (var t = 0; t < typesPerProject; t++)
                files[$"Type{t:D3}.cs"] = GenerateType(p, t, typesPerProject, methodsPerType);

            WriteProject(rootDir, name, ["net10.0"], refs, files);
        }

        return WriteSolution(rootDir, "Large", projectNames);
    }

    private static string GenerateType(int project, int type, int typesPerProject, int methodsPerType)
    {
        var sb = new StringBuilder();
        sb.Append("namespace Proj").Append(project.ToString("D3")).AppendLine(";").AppendLine();
        sb.Append("public class Type").Append(project.ToString("D3")).Append('_').Append(type.ToString("D3")).AppendLine();
        sb.AppendLine("{");

        for (var m = 0; m < methodsPerType; m++)
        {
            sb.Append("    public int M").Append(m.ToString("D2")).AppendLine("(int x)");
            sb.AppendLine("    {");
            sb.AppendLine("        var acc = x;");
            // Call a sibling method on this type to create intra-type call edges.
            var sibling = (m + 1) % methodsPerType;
            if (methodsPerType > 1 && sibling != m)
                sb.Append("        if (x > 0) acc += M").Append(sibling.ToString("D2")).AppendLine("(x - 1);");
            // Reference a type in the previous project to create cross-project references.
            if (project > 0)
            {
                var depType = $"Proj{(project - 1):D3}.Type{(project - 1):D3}_{(type % typesPerProject):D3}";
                sb.Append("        acc += new ").Append(depType).Append("().M00(1);").AppendLine();
            }
            sb.AppendLine("        return acc;");
            sb.AppendLine("    }");
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    private static void WriteProject(
        string rootDir, string name, IReadOnlyList<string> tfms,
        IReadOnlyList<string> projectRefs, Dictionary<string, string> sourceFiles)
    {
        var projectDir = Path.Combine(rootDir, name);
        Directory.CreateDirectory(projectDir);

        var tfmElement = tfms.Count == 1
            ? $"<TargetFramework>{tfms[0]}</TargetFramework>"
            : $"<TargetFrameworks>{string.Join(';', tfms)}</TargetFrameworks>";

        var csproj = new StringBuilder();
        csproj.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">");
        csproj.AppendLine("  <PropertyGroup>");
        csproj.AppendLine($"    {tfmElement}");
        csproj.AppendLine("    <LangVersion>latest</LangVersion>");
        csproj.AppendLine("    <Nullable>enable</Nullable>");
        csproj.AppendLine("    <ImplicitUsings>enable</ImplicitUsings>");
        csproj.AppendLine("    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>");
        csproj.AppendLine("  </PropertyGroup>");
        if (projectRefs.Count > 0)
        {
            csproj.AppendLine("  <ItemGroup>");
            foreach (var r in projectRefs)
                csproj.AppendLine($"    <ProjectReference Include=\"../{r}/{r}.csproj\" />");
            csproj.AppendLine("  </ItemGroup>");
        }
        csproj.AppendLine("</Project>");

        WriteFile(Path.Combine(projectDir, $"{name}.csproj"), csproj.ToString());

        foreach (var (fileName, content) in sourceFiles.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            WriteFile(Path.Combine(projectDir, fileName), content.ReplaceLineEndings("\n").TrimEnd() + "\n");
    }

    private static string WriteSolution(string rootDir, string name, IReadOnlyList<string> projectNames)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<Solution>");
        foreach (var p in projectNames)
            sb.AppendLine($"  <Project Path=\"{p}/{p}.csproj\" />");
        sb.AppendLine("</Solution>");

        var slnxPath = Path.Combine(rootDir, $"{name}.slnx");
        WriteFile(slnxPath, sb.ToString());
        return slnxPath;
    }

    private static void ResetDirectory(string rootDir)
    {
        // Regenerate from a clean slate so files left by a prior run with larger dimensions (which
        // SDK-default source globbing would otherwise re-index) never contaminate the corpus.
        if (Directory.Exists(rootDir))
            Directory.Delete(rootDir, recursive: true);
        Directory.CreateDirectory(rootDir);
    }

    private static void WriteFile(string path, string content)
    {
        // Normalize newlines and use a BOM-free UTF-8 encoding for byte-stable output.
        File.WriteAllText(path, content.ReplaceLineEndings("\n"), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
