using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Sextant.Indexer.Tests;

/// <summary>
/// Each member's declaration as C# spells it (migration 026): what the tools print as its <c>signature</c>, so an
/// agent sees the return type, parameter names, modifiers and defaults without reading the file. The legacy
/// <c>Signature</c> (the basis of printed member names and of <c>SignatureHash</c>) must not change.
/// </summary>
[TestClass]
public class SymbolDeclarationTests
{
    private const string Source = """
        #nullable enable
        using System;
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;

        namespace App.Channels
        {
            public sealed record ChatRecord(string Id);

            public interface IChatStore
            {
                Task<ChatRecord> CreateAsync(string tenantId, string channelChatKind, string applicationInstanceId, DateTime installedAt, CancellationToken cancellationToken);
                Task<ChatRecord?> FindAsync(string tenantId, CancellationToken cancellationToken = default);
                int Count { get; }
                string this[int index, string key] { get; set; }
                event EventHandler<EventArgs>? Changed;
            }

            public delegate Task<int> Handler<T>(T item, CancellationToken ct = default);

            public static class Extensions
            {
                public const int Max = 10;
                public static readonly string Field = "f";
                public static IEnumerable<T> Where2<T>(this IEnumerable<T> source, Func<T, bool> pred, params string[] tags) => source;
                public static void M(ref int a, out string b, in long c, int d = 5, string e = "x, y", char g = ',', object? h = null) { b = ""; }
            }

            public abstract class Base : IDisposable
            {
                protected Base(int x, string? y = null) { }
                public abstract void Run();
                public virtual int Size { get; protected set; }
                void IDisposable.Dispose() { }
            }

            public enum Color { Red = 1 }
        }
        """;

    private static async Task<Dictionary<string, Sextant.Core.SymbolInfo>> ExtractAsync()
    {
        var references = new MetadataReference[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(Path.Combine(
                System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "System.Runtime.dll")),
        };
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var solution = workspace.CurrentSolution
            .AddProject(projectId, "TestProject", "TestAssembly", LanguageNames.CSharp)
            .WithProjectCompilationOptions(projectId, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .WithProjectMetadataReferences(projectId, references)
            .AddDocument(DocumentId.CreateNewId(projectId), "Test.cs", Source);
        var symbols = await SymbolExtractor.ExtractFromProjectAsync(solution.GetProject(projectId)!, 1);
        return symbols.GroupBy(s => s.SymbolKey).ToDictionary(g => g.Key, g => g.First());
    }

    [TestMethod]
    public async Task Declaration_HasReturnTypeParameterNamesModifiersAndDefaults()
    {
        var symbols = await ExtractAsync();
        string Decl(string key) => symbols[key].Declaration!;

        Assert.AreEqual(
            "Task<ChatRecord> CreateAsync(string tenantId, string channelChatKind, string applicationInstanceId, DateTime installedAt, CancellationToken cancellationToken)",
            Decl("M:App.Channels.IChatStore.CreateAsync(System.String,System.String,System.String,System.DateTime,System.Threading.CancellationToken)"));
        Assert.AreEqual("Task<ChatRecord?> FindAsync(string tenantId, CancellationToken cancellationToken = default)",
            Decl("M:App.Channels.IChatStore.FindAsync(System.String,System.Threading.CancellationToken)"));
        Assert.AreEqual("int Count { get; }", Decl("P:App.Channels.IChatStore.Count"));
        Assert.AreEqual("string this[int index, string key] { get; set; }", Decl("P:App.Channels.IChatStore.Item(System.Int32,System.String)"));
        Assert.AreEqual("EventHandler<EventArgs>? Changed", Decl("E:App.Channels.IChatStore.Changed"));
        Assert.AreEqual("Task<int> Handler<T>(T item, CancellationToken ct = default)", Decl("T:App.Channels.Handler`1"));
        Assert.AreEqual("const int Max = 10", Decl("F:App.Channels.Extensions.Max"));
        Assert.AreEqual("static readonly string Field", Decl("F:App.Channels.Extensions.Field"));
        Assert.AreEqual("static IEnumerable<T> Where2<T>(this IEnumerable<T> source, Func<T, bool> pred, params string[] tags)",
            Decl("M:App.Channels.Extensions.Where2``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Boolean},System.String[])"));
        Assert.AreEqual("static void M(ref int a, out string b, in long c, int d = 5, string e = \"x, y\", char g = ',', object? h = null)",
            Decl("M:App.Channels.Extensions.M(System.Int32@,System.String@,System.Int64@,System.Int32,System.String,System.Char,System.Object)"));
        Assert.AreEqual("Base(int x, string? y = null)", Decl("M:App.Channels.Base.#ctor(System.Int32,System.String)"));
        Assert.AreEqual("abstract void Run()", Decl("M:App.Channels.Base.Run"));
        Assert.AreEqual("virtual int Size { get; protected set; }", Decl("P:App.Channels.Base.Size"));
        Assert.AreEqual("void IDisposable.Dispose()", Decl("M:App.Channels.Base.System#IDisposable#Dispose"));
        Assert.AreEqual("Red = 1", Decl("F:App.Channels.Color.Red"));
    }

    [TestMethod]
    public async Task Declaration_IsNullForTypes_AndTheLegacySignatureIsUnchanged()
    {
        var symbols = await ExtractAsync();

        Assert.IsNull(symbols["T:App.Channels.IChatStore"].Declaration);
        Assert.IsNull(symbols["T:App.Channels.Color"].Declaration);

        // The legacy signature still names the member the way SymbolNamer prints it (#219) and keeps its hash.
        var create = symbols["M:App.Channels.IChatStore.CreateAsync(System.String,System.String,System.String,System.DateTime,System.Threading.CancellationToken)"];
        Assert.AreEqual("App.Channels.IChatStore.CreateAsync(string, string, string, System.DateTime, System.Threading.CancellationToken)", create.Signature);
        Assert.AreEqual("CreateAsync", create.FullyQualifiedName);
        Assert.IsNotNull(create.SignatureHash);
        Assert.IsNull(symbols["F:App.Channels.Extensions.Max"].Signature, "fields keep no legacy signature");
    }
}
