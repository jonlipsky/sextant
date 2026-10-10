using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Sextant.Core;

namespace Sextant.Indexer.Tests;

/// <summary>
/// A call site whose binding fails must not vanish from the index. The shape reproduces the live incident: an
/// interface method has a trailing optional parameter whose type lives in an assembly the CALLING project does
/// not reference (on the indexer, a transitive ProjectReference that never flowed because NuGet restore did not
/// run). Every call to the method then fails overload resolution, its <c>var</c> result becomes an error type, and
/// the next call that takes that value fails as well. Before the fix both call sites (and the caller's call-graph
/// edges) were silently dropped.
/// </summary>
[TestClass]
public class CandidateBindingExtractorTests
{
    private const string AbsSource = """
        namespace Abs
        {
            public enum OwnerKind { User, Team }
        }
        """;

    private const string CoreSource = """
        using System.Threading;
        using System.Threading.Tasks;
        namespace Core
        {
            public interface IStore
            {
                Task<string> EnsureAsync(string name, CancellationToken ct, Abs.OwnerKind owner = Abs.OwnerKind.User);
                Task PublishAsync(string id, CancellationToken ct);
            }
        }
        """;

    private const string AppSource = """
        using System.Threading;
        using System.Threading.Tasks;
        namespace App
        {
            public class Controller
            {
                private readonly Core.IStore _store;
                public Controller(Core.IStore store) { _store = store; }

                public async Task PublishAsync(string name, CancellationToken ct)
                {
                    var id = await _store.EnsureAsync(name, ct);
                    await _store.PublishAsync(id, ct);
                }

                public Task PublishPlainAsync(string id, CancellationToken ct) => _store.PublishAsync(id, ct);
            }
        }
        """;

    private const string EnsureKey = "M:Core.IStore.EnsureAsync(System.String,System.Threading.CancellationToken,Abs.OwnerKind)";
    private const string PublishKey = "M:Core.IStore.PublishAsync(System.String,System.Threading.CancellationToken)";
    private const string CallerKey = "M:App.Controller.PublishAsync(System.String,System.Threading.CancellationToken)";

    private static MetadataReference[] Framework()
    {
        var runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        return
        [
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Runtime.dll")),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Threading.Tasks.dll")),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Threading.dll")),
        ];
    }

    private static CSharpCompilation Compile(string name, string source, params MetadataReference[] references) =>
        CSharpCompilation.Create(name,
            [CSharpSyntaxTree.ParseText(source, path: name + ".cs")],
            [.. Framework(), .. references],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    /// <summary>Extracts App's document with Core referenced and Abs referenced only when <paramref name="withAbs"/>.</summary>
    private static (DocumentContributionSet Sink, CSharpCompilation App) ExtractApp(bool withAbs)
    {
        var abs = Compile("Abs", AbsSource);
        var core = Compile("Core", CoreSource, abs.ToMetadataReference());
        Assert.AreEqual(0, core.GetDiagnostics().Count(d => d.Severity == DiagnosticSeverity.Error));

        var app = withAbs
            ? Compile("App", AppSource, core.ToMetadataReference(), abs.ToMetadataReference())
            : Compile("App", AppSource, core.ToMetadataReference());
        var tree = app.SyntaxTrees.Single();
        var sink = new DocumentContributionSet();
        DocumentSemanticExtractor.ExtractDocument(
            tree.GetRoot(), app.GetSemanticModel(tree), "App.cs", sink);
        return (sink, app);
    }

    private static int LineOf(string marker) =>
        AppSource.Split('\n').Select((l, i) => (l, i)).First(x => x.l.Contains(marker, StringComparison.Ordinal)).i + 1;

    [TestMethod]
    public void UnresolvedOptionalParameterType_KeepsBothCallSitesAsCandidates()
    {
        var (sink, app) = ExtractApp(withAbs: false);
        Assert.IsTrue(app.GetDiagnostics().Any(d => d.Id == "CS0012"),
            "the fixture must reproduce the missing-assembly binding failure");

        var ensureLine = LineOf("_store.EnsureAsync(name, ct)");
        var publishLine = LineOf("_store.PublishAsync(id, ct);");

        var ensureRef = sink.References.Single(r => r.TargetKey == EnsureKey && r.Line == ensureLine);
        Assert.IsTrue(ensureRef.IsCandidate, "the call whose optional parameter type is unresolved is a candidate");
        Assert.AreEqual(ReferenceKind.Invocation, ensureRef.Kind);

        var publishRef = sink.References.Single(r => r.TargetKey == PublishKey && r.Line == publishLine);
        Assert.IsTrue(publishRef.IsCandidate, "the call that takes the error-typed `var` result is a candidate too");

        var calls = sink.Calls.Where(c => c.CallerKey == CallerKey).ToList();
        CollectionAssert.AreEquivalent(new[] { EnsureKey, PublishKey }, calls.Select(c => c.CalleeKey).ToArray(),
            "the caller keeps both call-graph edges");
        Assert.IsTrue(calls.All(c => c.IsCandidate));
        Assert.IsTrue(calls.All(c => c.Dataflow.Arguments.Count == 0), "no bound argument mapping, so no dataflow");

        Assert.IsTrue(sink.UnboundInvocations >= 2, $"unbound invocations: {sink.UnboundInvocations}");
        Assert.IsTrue(sink.UnboundNames >= 2, $"unbound names: {sink.UnboundNames}");
        Assert.IsTrue(sink.NamesExamined > sink.UnboundNames);
    }

    [TestMethod]
    public void ExactlyBoundCallSite_IsNotACandidate_EvenBesideAFailedOne()
    {
        var (sink, _) = ExtractApp(withAbs: false);

        var plainLine = LineOf("PublishPlainAsync");
        var plain = sink.References.Single(r => r.TargetKey == PublishKey && r.Line == plainLine);
        Assert.IsFalse(plain.IsCandidate, "a call whose arguments all bind is exact");
        Assert.IsFalse(sink.Calls.Single(c => c.CalleeKey == PublishKey && c.CallSiteLine == plainLine).IsCandidate);
    }

    [TestMethod]
    public void WhenTheMissingAssemblyIsReferenced_EverySiteBindsExactly()
    {
        var (sink, app) = ExtractApp(withAbs: true);
        Assert.AreEqual(0, app.GetDiagnostics().Count(d => d.Severity == DiagnosticSeverity.Error));

        Assert.IsTrue(sink.References.Any(r => r.TargetKey == EnsureKey));
        Assert.AreEqual(3, sink.References.Count(r => r.TargetKey is EnsureKey or PublishKey));
        Assert.IsFalse(sink.References.Any(r => r.IsCandidate));
        Assert.IsFalse(sink.Calls.Any(c => c.IsCandidate));
        Assert.AreEqual(0, sink.UnboundNames);
        Assert.AreEqual(0, sink.UnboundInvocations);
    }

    [TestMethod]
    public void ExactOccurrence_WinsOverACandidateAtTheSameSite()
    {
        var set = new DocumentContributionSet();
        var candidate = new ReferenceContribution("T:N.A", "A.cs", 3, ReferenceKind.TypeRef, null, IsCandidate: true);
        var exact = candidate with { IsCandidate = false };

        set.AddReference(candidate);
        set.AddReference(exact);
        set.AddReference(candidate);

        Assert.IsFalse(set.References.Single().IsCandidate, "one row per site; the exact one wins whatever the order");
    }
}
