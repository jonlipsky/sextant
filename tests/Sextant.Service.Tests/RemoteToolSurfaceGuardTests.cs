using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ModelContextProtocol.Server;
using Sextant.Mcp;
using Sextant.Mcp.Tools;
using Sextant.Service.Host;
using Sextant.Store;

namespace Sextant.Service.Tests;

/// <summary>
/// S12: the service's remote MCP surface lists exactly the tools agents call (<see cref="ServiceApp.RemoteQueryTools"/>),
/// and nothing it sends names a tool an agent cannot call there. An agent told "call get_index_status" by a warning,
/// or "a project_id from get_index_status" by a parameter description, wastes a turn on a tool that does not exist on
/// its server.
/// <list type="bullet">
/// <item>Through the real host over <c>/mcp</c>: <c>tools/list</c> (both deployments) is exactly that list; no tool
/// description, parameter description or the <c>initialize</c> instructions names another tool; and the texts the
/// tools emit (warnings, errors, messages, size-cut hints) on the remote path name none either.</item>
/// <item>Statically, for the texts a test cannot reach over HTTP: every string constant, and every string literal in
/// the source of the remote path (Sextant.Mcp, Sextant.Service, Sextant.Service.Host, Sextant.Store), except the
/// local-only tool classes and the local stdio composition root.</item>
/// </list>
/// Everything here reads the set from <see cref="ServiceApp.RemoteQueryTools"/>: the forbidden names are every
/// <c>[McpServerTool]</c> name in the tool assemblies outside it, plus the retired remote-only names, so adding or
/// dropping a remote tool needs no edit here unless the tool has no <c>EmittedTexts</c> call yet.
/// </summary>
[TestClass]
public sealed partial class RemoteToolSurfaceGuardTests
{
    /// <summary>
    /// The remote surface, read from <see cref="ServiceApp.RemoteQueryTools"/>: that list is the one place the set is
    /// decided, so every test of the surface reads it from here and changing the set is a one-line change.
    /// </summary>
    internal static readonly string[] AgentTools = ToolNames(ServiceApp.RemoteQueryTools);

    /// <summary>The <c>[McpServerTool]</c> names <paramref name="toolTypes"/> declare, in ordinal order.</summary>
    internal static string[] ToolNames(IEnumerable<Type> toolTypes) => toolTypes
        .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
        .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name)
        .OfType<string>()
        .Order(StringComparer.Ordinal)
        .ToArray();

    // Tools that were once on the remote surface and no longer exist in any assembly; no text may bring them back.
    private static readonly string[] RetiredToolNames = ["search_symbols", "research_codebase"];

    private static readonly Assembly[] ToolAssemblies = [typeof(FindSymbolTool).Assembly, typeof(SnapshotService).Assembly];

    private static readonly Assembly[] RemotePathAssemblies =
    [
        typeof(FindSymbolTool).Assembly, typeof(SnapshotService).Assembly, typeof(ServiceApp).Assembly,
        typeof(IndexDatabase).Assembly
    ];

    private static readonly string[] RemotePathSourceDirectories =
        ["src/Sextant.Mcp", "src/Sextant.Service", "src/Sextant.Service.Host", "src/Sextant.Store"];

    // The local stdio composition root: never on the remote path, and it may describe the full local tool set.
    private static readonly string[] LocalOnlySourceFiles = ["src/Sextant.Mcp/McpServerSetup.cs"];

    private static readonly Lazy<IReadOnlyList<string>> Forbidden = new(() =>
    {
        var names = ToolAssemblies.SelectMany(a => a.GetTypes())
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .OfType<string>()
            .Concat(RetiredToolNames)
            .Except(AgentTools, StringComparer.Ordinal)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        return names;
    });

    // ==== the surface =================================================================================

    [TestMethod]
    public void ForbiddenNames_CoverEveryLocalOnlyTool()
    {
        var forbidden = Forbidden.Value;

        // Tools that never go remote (they read outside the grant gate, or report on the whole local index).
        foreach (var name in new[] { "get_index_status", "get_source_context", "get_base_snapshot_symbols", "find_cross_repository_usages", "search_symbols" })
            CollectionAssert.Contains(forbidden.ToList(), name);
        Assert.AreEqual(0, forbidden.Intersect(AgentTools).Count());
        Assert.IsTrue(AgentTools.Length > 0);
    }

    [TestMethod]
    [DataRow(false, DisplayName = "query token, selection required")]
    [DataRow(true, DisplayName = "delegate callers (implicit selection)")]
    public async Task ToolsList_IsExactlyTheAgentTools_AndNoDescriptionNamesAnotherTool(bool delegateCallers)
    {
        await using var host = await AgentOutputHarness.StartAsync(delegateCallers);

        using var doc = JsonDocument.Parse(await host.RpcAsync("tools/list"));
        var tools = doc.RootElement.GetProperty("tools").EnumerateArray().ToList();

        CollectionAssert.AreEqual(AgentTools,
            tools.Select(t => t.GetProperty("name").GetString()!).Order(StringComparer.Ordinal).ToList(),
            "tools/list is exactly the agent tools");
        foreach (var tool in tools)
        {
            var name = tool.GetProperty("name").GetString()!;
            AssertNamesNoOtherTool($"tools/list {name}", tool.GetRawText());
        }
    }

    [TestMethod]
    public async Task Initialize_InstructionsNameNoOtherTool()
    {
        await using var host = await AgentOutputHarness.StartAsync();

        using var doc = JsonDocument.Parse(await host.RpcAsync("initialize", Initialize()));
        var instructions = doc.RootElement.GetProperty("instructions").GetString()!;

        AssertNamesNoOtherTool("initialize instructions", instructions);
        StringAssert.Contains(instructions, "list_repositories");
    }

    // The agent-behaviour harness preflight opens a fresh session and reads meta.snapshot.commit off its first
    // find_symbol call; trimming the lean meta must keep it.
    [TestMethod]
    public async Task FindSymbol_FirstCallOfAFreshSession_CarriesTheSnapshotCommit()
    {
        await using var host = await AgentOutputHarness.StartAsync();

        await host.RpcAsync("initialize", Initialize());
        var body = await host.CallAsync("find_symbol", new JsonObject { ["name"] = "IStore" });

        Assert.AreEqual(AgentOutputFixture.CommitA[..12],
            body.GetProperty("meta").GetProperty("snapshot").GetProperty("commit").GetString(), body.ToString());
    }

    // ==== texts the tools emit on the remote path ====================================================

    [TestMethod]
    public async Task EmittedTexts_NameNoOtherTool()
    {
        await using var host = await AgentOutputHarness.StartAsync(maxResponseChars: 1_500);
        var repoA = AgentOutputFixture.RepoA;
        // Each call states the outcome it must reach (an error code, a size cut, the partial warning, or a plain
        // answer), so a call that fails earlier than its label (say, on selection) cannot silently stop scanning
        // the text it is here for.
        var calls = new (string Label, string Expected, string Tool, JsonObject Arguments)[]
        {
            ("partial warning", Warned, "find_symbol", new JsonObject { ["name"] = "OtherStore", ["repository"] = AgentOutputFixture.RepoB }),
            ("no repository", "repository_required", "find_symbol", new JsonObject { ["name"] = "IStore" }),
            ("unknown repository", "repository_not_found", "find_symbol", new JsonObject { ["name"] = "IStore", ["repository"] = "org/absent" }),
            ("symbol not found", "symbol_not_found", "find_references", new JsonObject { ["symbol_fqn"] = "App.NoSuchType", ["repository"] = repoA }),
            ("namespace", InvalidArgument, "find_references", new JsonObject { ["symbol_fqn"] = "N:App.Core", ["repository"] = repoA }),
            ("malformed symbol", InvalidArgument, "get_type_members", new JsonObject { ["symbol_fqn"] = "App.Core.IStore.Get(int", ["repository"] = repoA }),
            ("unknown project_id", InvalidArgument, "find_symbol", new JsonObject { ["name"] = "IStore", ["project_id"] = "0000000000000000", ["repository"] = repoA }),
            ("unknown include_projects", InvalidArgument, "find_references", new JsonObject
            {
                ["symbol_fqn"] = AgentOutputFixture.TargetInterface, ["include_projects"] = "0000000000000000", ["repository"] = repoA
            }),
            ("unknown project scope", InvalidArgument, "find_references", new JsonObject
            {
                ["symbol_fqn"] = AgentOutputFixture.TargetInterface, ["scope"] = "project:0000000000000000", ["repository"] = repoA
            }),
            ("unknown solution scope", InvalidArgument, "find_symbol", new JsonObject { ["name"] = "IStore", ["scope"] = "solution:Missing.slnx", ["repository"] = repoA }),
            ("bad scope", InvalidArgument, "find_symbol", new JsonObject { ["name"] = "IStore", ["scope"] = "folder:src", ["repository"] = repoA }),
            ("bad kind", InvalidArgument, "find_symbol", new JsonObject { ["name"] = "IStore", ["kind"] = "namespace", ["repository"] = repoA }),
            ("bad cursor", Paging.InvalidCursorCode, "find_references", new JsonObject
            {
                ["symbol_fqn"] = AgentOutputFixture.TargetInterface, ["cursor"] = "not-a-cursor", ["repository"] = repoA
            }),
            ("size-cut page", SizeCut, "find_references", new JsonObject
            {
                ["symbol_fqn"] = AgentOutputFixture.TargetInterface, ["limit"] = 200, ["repository"] = repoA
            }),
            ("size-cut unpaged", SizeCut, "find_symbol", new JsonObject { ["name"] = "Handler*", ["fuzzy"] = true, ["repository"] = repoA }),
            ("absolute path", InvalidArgument, "get_file_symbols", new JsonObject { ["file_path"] = "/etc/passwd", ["repository"] = repoA }),
            ("callers", SizeCut, "get_call_hierarchy", new JsonObject
            {
                ["symbol_fqn"] = AgentOutputFixture.TargetMethod, ["direction"] = "callers", ["repository"] = repoA
            }),
            ("bad direction", InvalidArgument, "get_call_hierarchy", new JsonObject
            {
                ["symbol_fqn"] = AgentOutputFixture.TargetMethod, ["direction"] = "sideways", ["repository"] = repoA
            }),
            ("hierarchy", Answered, "get_type_hierarchy", new JsonObject { ["symbol_fqn"] = AgentOutputFixture.TargetInterface, ["repository"] = repoA }),
            ("implementors", SizeCut, "get_implementors", new JsonObject { ["symbol_fqn"] = AgentOutputFixture.TargetInterface, ["repository"] = repoA }),
            ("ambiguous member", "ambiguous_symbol", "get_call_hierarchy", new JsonObject { ["symbol_fqn"] = "Get", ["direction"] = "callers", ["repository"] = repoA }),
            ("list_repositories without a caller", "caller_required", "list_repositories", new JsonObject()),
            // Candidates for the surface: their rows run only while the tool is on it, so adding one to
            // RemoteQueryTools needs no edit here.
            ("dependents", SizeCut, "get_type_dependents", new JsonObject { ["symbol_fqn"] = AgentOutputFixture.TargetInterface, ["repository"] = repoA }),
            ("bad dependency kind", InvalidArgument, "get_type_dependents", new JsonObject
            {
                ["symbol_fqn"] = AgentOutputFixture.TargetInterface, ["dependency_kind"] = "sideways", ["repository"] = repoA
            }),
            ("topic search", SizeCut, "semantic_search", new JsonObject { ["query"] = "Handler*", ["max_results"] = 200, ["repository"] = repoA }),
            ("attribute", Answered, "find_by_attribute", new JsonObject { ["attribute_fqn"] = "Obsolete", ["repository"] = repoA }),
            ("unreferenced", SizeCut, "find_unreferenced", new JsonObject { ["repository"] = repoA }),
            ("unknown project_id (unreferenced)", InvalidArgument, "find_unreferenced", new JsonObject
            {
                ["project_id"] = "0000000000000000", ["repository"] = repoA
            })
        };

        var unknown = calls.Select(c => c.Tool).Except(AgentTools).Except(Forbidden.Value).ToList();
        Assert.AreEqual(0, unknown.Count, "no such tool: " + string.Join(", ", unknown));
        var unexercised = AgentTools.Except(calls.Select(c => c.Tool)).ToList();
        Assert.AreEqual(0, unexercised.Count, "every tool on the remote surface needs a call here: " + string.Join(", ", unexercised));

        var missed = new List<string>();
        foreach (var (label, expected, tool, arguments) in calls.Where(c => AgentTools.Contains(c.Tool)))
        {
            var text = await host.CallTextAsync(tool, arguments);
            var outcome = Outcome(text);
            if (outcome != expected)
                missed.Add($"{tool} ({label}): expected {expected}, got {outcome}");
            AssertNamesNoOtherTool($"{tool} ({label})", text);
        }
        Assert.AreEqual(0, missed.Count, "a call did not reach the text it scans:\n" + string.Join("\n", missed));

        var warning = (await host.CallAsync("find_symbol", new JsonObject { ["name"] = "OtherStore" }, AgentOutputFixture.RepoB))
            .GetProperty("meta").GetProperty("snapshot").GetProperty("warning").GetString()!;
        Assert.IsTrue(warning.Length <= RemoteResponsePresenter.MaxPartialWarningChars, $"{warning.Length}: {warning}");
    }

    [TestMethod]
    public void Warnings_NameNoOtherTool_AndThePartialWarningStaysShort()
    {
        int[] counts = [0, 1, int.MaxValue];
        foreach (var projects in counts)
        foreach (var other in counts)
        {
            var warning = RemoteResponsePresenter.PartialWarningFor(new Sextant.Core.SnapshotCoverage
            {
                Verdict = Sextant.Core.SnapshotCoverageVerdict.Partial,
                Reasons = ["Call get_index_status for details (src/App/App.csproj)."],
                ProjectsDeclared = projects, ProjectsSkipped = projects,
                Binding = new Sextant.Core.BindingHealth { ProjectsDegraded = other },
                SubmodulesDeclared = other, SubmodulesUnpopulated = other, SolutionsSkipped = other, ScanErrors = other
            });
            AssertNamesNoOtherTool("partial warning", warning);
            Assert.IsTrue(warning.Length <= RemoteResponsePresenter.MaxPartialWarningChars, $"{warning.Length}: {warning}");
            Assert.IsFalse(warning.Contains("App.csproj", StringComparison.Ordinal), "a recorded reason is never quoted");
        }
        AssertNamesNoOtherTool("generic partial warning", RemoteResponsePresenter.PartialWarning);
        AssertNamesNoOtherTool("incompatible warning", RemoteResponsePresenter.IncompatibleWarning);
        AssertNamesNoOtherTool("dirty warning", RemoteResponsePresenter.DirtyWarning);
    }

    // ==== static texts on the remote path ============================================================

    [TestMethod]
    public void StringConstants_OnTheRemotePath_NameNoOtherTool()
    {
        var localOnlyTools = LocalOnlyToolTypes();
        var checkedFields = 0;
        foreach (var type in RemotePathAssemblies.SelectMany(a => a.GetTypes()))
        {
            if (type.ContainsGenericParameters || IsCompilerGenerated(type) || IsWithin(type, localOnlyTools))
                continue;
            foreach (var field in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (field.IsDefined(typeof(CompilerGeneratedAttribute)) || !field.IsLiteral && !field.IsInitOnly)
                    continue;
                foreach (var text in StringsOf(field))
                {
                    checkedFields++;
                    AssertNamesNoOtherTool($"{type.FullName}.{field.Name}", text);
                }
            }
        }
        Assert.IsTrue(checkedFields > 100, $"only {checkedFields} string constants were checked");
    }

    [TestMethod]
    public void StringLiterals_InTheRemotePathSource_NameNoOtherTool()
    {
        var root = RepositoryRoot();
        var remoteTypeNames = ServiceApp.RemoteQueryTools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var checkedFiles = 0;
        foreach (var directory in RemotePathSourceDirectories)
        {
            foreach (var path in Directory.EnumerateFiles(Path.Combine(root, directory), "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal)
                    || LocalOnlySourceFiles.Contains(relative, StringComparer.Ordinal))
                    continue;
                var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path));
                var syntaxRoot = tree.GetRoot();
                if (DeclaresLocalOnlyTool(syntaxRoot, remoteTypeNames))
                    continue;
                checkedFiles++;
                foreach (var token in syntaxRoot.DescendantTokens().Where(IsStringText))
                {
                    var line = token.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    AssertNamesNoOtherTool($"{relative}:{line}", token.ValueText);
                }
            }
        }
        Assert.IsTrue(checkedFiles > 50, $"only {checkedFiles} source files were checked");
    }

    // ==== helpers =====================================================================================

    private const string InvalidArgument = "invalid_argument";
    private const string SizeCut = "size cut";
    private const string Warned = "partial warning";
    private const string Answered = "answered";

    // What a tool call reached: its error code, else a size-cut page, else an answer carrying the snapshot warning,
    // else a plain answer.
    private static string Outcome(string text)
    {
        using var doc = JsonDocument.Parse(text);
        if (!doc.RootElement.TryGetProperty("meta", out var meta))
            return Answered;
        if (meta.TryGetProperty("error", out var error))
            return error.GetProperty("code").GetString()!;
        if (meta.TryGetProperty("page_truncated_by", out var cut) && cut.GetString() == "size")
            return SizeCut;
        if (meta.TryGetProperty("snapshot", out var snapshot) && snapshot.TryGetProperty("warning", out _))
            return Warned;
        return Answered;
    }
    private static void AssertNamesNoOtherTool(string where, string text)
    {
        foreach (var name in Forbidden.Value)
        {
            if (ToolNameIn(name).IsMatch(text))
                Assert.Fail($"{where} names '{name}', which is not on the remote surface: {Excerpt(text, name)}");
        }
    }

    private static Regex ToolNameIn(string name) =>
        new($@"(?<![A-Za-z0-9_]){Regex.Escape(name)}(?![A-Za-z0-9_])", RegexOptions.CultureInvariant);

    private static string Excerpt(string text, string name)
    {
        var at = text.IndexOf(name, StringComparison.Ordinal);
        var start = Math.Max(0, at - 80);
        return text.Substring(start, Math.Min(text.Length - start, name.Length + 160));
    }

    private static JsonObject Initialize() => new()
    {
        ["protocolVersion"] = "2025-06-18",
        ["capabilities"] = new JsonObject(),
        ["clientInfo"] = new JsonObject { ["name"] = "guard", ["version"] = "1" }
    };

    private static HashSet<Type> LocalOnlyToolTypes() =>
        ToolAssemblies.SelectMany(a => a.GetTypes())
            .Where(t => t.IsDefined(typeof(McpServerToolTypeAttribute), inherit: false))
            .Except(ServiceApp.RemoteQueryTools)
            .Append(typeof(McpServerSetup))
            .ToHashSet();

    private static bool IsWithin(Type type, HashSet<Type> outer)
    {
        for (var t = type; t is not null; t = t.DeclaringType)
            if (outer.Contains(t))
                return true;
        return false;
    }

    private static bool IsCompilerGenerated(Type type)
    {
        for (var t = type; t is not null; t = t.DeclaringType)
            if (t.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false) || t.Name.StartsWith('<'))
                return true;
        return false;
    }

    private static IEnumerable<string> StringsOf(FieldInfo field)
    {
        object? value;
        try
        {
            value = field.IsLiteral ? field.GetRawConstantValue() : field.GetValue(null);
        }
        catch (TargetInvocationException)
        {
            yield break;
        }
        catch (TypeInitializationException)
        {
            yield break;
        }
        switch (value)
        {
            case string s:
                yield return s;
                break;
            case IEnumerable<string> many:
                foreach (var s in many)
                    yield return s;
                break;
        }
    }

    private static bool DeclaresLocalOnlyTool(SyntaxNode root, HashSet<string> remoteTypeNames) =>
        root.DescendantNodes().OfType<ClassDeclarationSyntax>().Any(c =>
            !remoteTypeNames.Contains(c.Identifier.ValueText)
            && c.AttributeLists.SelectMany(l => l.Attributes)
                .Any(a => a.Name.ToString() is "McpServerToolType" or "McpServerToolTypeAttribute"));

    private static bool IsStringText(SyntaxToken token) => token.Kind() is
        SyntaxKind.StringLiteralToken or SyntaxKind.Utf8StringLiteralToken
        or SyntaxKind.SingleLineRawStringLiteralToken or SyntaxKind.MultiLineRawStringLiteralToken
        or SyntaxKind.Utf8SingleLineRawStringLiteralToken or SyntaxKind.Utf8MultiLineRawStringLiteralToken
        or SyntaxKind.InterpolatedStringTextToken;

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Sextant.slnx")))
                return dir.FullName;
        throw new InvalidOperationException("Sextant.slnx was not found above the test output directory.");
    }
}
