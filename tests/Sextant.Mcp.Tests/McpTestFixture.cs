using Sextant.Core;
using Sextant.Mcp;
using Sextant.Store;

namespace Sextant.Mcp.Tests;

public class McpTestFixture : IDisposable
{
    public string DbPath { get; }
    public IndexDatabase Db { get; }
    public DatabaseProvider DbProvider { get; }
    public long ProjectId { get; }
    public long ProjectId2 { get; }

    public McpTestFixture()
    {
        DbPath = Path.Combine(Path.GetTempPath(), $"sextant_mcp_test_{Guid.NewGuid():N}.db");
        Db = new IndexDatabase(DbPath);
        Db.RunMigrations();

        DbProvider = new DatabaseProvider(DbPath);

        var conn = Db.GetConnection();
        var projectStore = new ProjectStore(conn);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        ProjectId = projectStore.Insert(new ProjectIdentity
        {
            CanonicalId = "proj_alpha_123456",
            GitRemoteUrl = "https://github.com/test/alpha",
            RepoRelativePath = "src/Alpha/Alpha.csproj"
        }, now);

        ProjectId2 = projectStore.Insert(new ProjectIdentity
        {
            CanonicalId = "proj_beta_7890ab",
            GitRemoteUrl = "https://github.com/test/beta",
            RepoRelativePath = "src/Beta/Beta.csproj"
        }, now);

        SeedSymbolsAndReferences(conn, now);
    }

    private void SeedSymbolsAndReferences(Microsoft.Data.Sqlite.SqliteConnection conn, long now)
    {
        var symbolStore = new SymbolStore(conn);
        var referenceStore = new ReferenceStore(conn);
        var relationshipStore = new RelationshipStore(conn);
        var callGraphStore = new CallGraphStore(conn);
        var commentStore = new CommentStore(conn);
        var argumentFlowStore = new ArgumentFlowStore(conn);
        var returnFlowStore = new ReturnFlowStore(conn);

        // --- Base types ---
        var baseClassId = symbolStore.Insert(new SymbolInfo
        {
            ProjectId = ProjectId,
            FullyQualifiedName = "global::Alpha.BaseService",
            DisplayName = "BaseService",
            Kind = SymbolKind.Class,
            Accessibility = Accessibility.Public,
            FilePath = "src/Alpha/BaseService.cs",
            LineStart = 1, LineEnd = 20,
            LastIndexedAt = now
        });

        var derivedClassId = symbolStore.Insert(new SymbolInfo
        {
            ProjectId = ProjectId,
            FullyQualifiedName = "global::Alpha.DerivedService",
            DisplayName = "DerivedService",
            Kind = SymbolKind.Class,
            Accessibility = Accessibility.Public,
            FilePath = "src/Alpha/DerivedService.cs",
            LineStart = 1, LineEnd = 30,
            LastIndexedAt = now
        });

        relationshipStore.Insert(new RelationshipInfo
        {
            FromSymbolId = derivedClassId,
            ToSymbolId = baseClassId,
            Kind = RelationshipKind.Inherits
        });

        // --- Interface ---
        var interfaceId = symbolStore.Insert(new SymbolInfo
        {
            ProjectId = ProjectId,
            FullyQualifiedName = "global::Alpha.IProcessor",
            DisplayName = "IProcessor",
            Kind = SymbolKind.Interface,
            Accessibility = Accessibility.Public,
            FilePath = "src/Alpha/IProcessor.cs",
            LineStart = 1, LineEnd = 5,
            LastIndexedAt = now
        });

        relationshipStore.Insert(new RelationshipInfo
        {
            FromSymbolId = derivedClassId,
            ToSymbolId = interfaceId,
            Kind = RelationshipKind.Implements
        });

        // --- Method with void return, no params ---
        var voidMethodId = symbolStore.Insert(new SymbolInfo
        {
            ProjectId = ProjectId,
            FullyQualifiedName = "global::Alpha.BaseService.Init()",
            DisplayName = "Init",
            Kind = SymbolKind.Method,
            Accessibility = Accessibility.Public,
            Signature = "public void Init()",
            FilePath = "src/Alpha/BaseService.cs",
            LineStart = 5, LineEnd = 10,
            LastIndexedAt = now
        });

        // --- Method with string param ---
        var stringParamMethodId = symbolStore.Insert(new SymbolInfo
        {
            ProjectId = ProjectId,
            FullyQualifiedName = "global::Alpha.BaseService.Process(string)",
            DisplayName = "Process",
            Kind = SymbolKind.Method,
            Accessibility = Accessibility.Public,
            Signature = "public string Process(string input)",
            FilePath = "src/Alpha/BaseService.cs",
            LineStart = 12, LineEnd = 18,
            LastIndexedAt = now
        });

        // --- Method with two params ---
        symbolStore.Insert(new SymbolInfo
        {
            ProjectId = ProjectId,
            FullyQualifiedName = "global::Alpha.BaseService.Transform(string, int)",
            DisplayName = "Transform",
            Kind = SymbolKind.Method,
            Accessibility = Accessibility.Public,
            Signature = "public void Transform(string data, int count)",
            FilePath = "src/Alpha/BaseService.cs",
            LineStart = 19, LineEnd = 20,
            LastIndexedAt = now
        });

        // --- Async method returning Task ---
        symbolStore.Insert(new SymbolInfo
        {
            ProjectId = ProjectId,
            FullyQualifiedName = "global::Alpha.BaseService.LoadAsync()",
            DisplayName = "LoadAsync",
            Kind = SymbolKind.Method,
            Accessibility = Accessibility.Public,
            Signature = "public Task LoadAsync()",
            FilePath = "src/Alpha/BaseService.cs",
            LineStart = 21, LineEnd = 25,
            LastIndexedAt = now
        });

        // --- String property ---
        symbolStore.Insert(new SymbolInfo
        {
            ProjectId = ProjectId,
            FullyQualifiedName = "global::Alpha.BaseService.Name",
            DisplayName = "Name",
            Kind = SymbolKind.Property,
            Accessibility = Accessibility.Public,
            Signature = "public string Name { get; set; }",
            FilePath = "src/Alpha/BaseService.cs",
            LineStart = 3, LineEnd = 3,
            LastIndexedAt = now
        });

        // --- Method with 3 params including generic ---
        symbolStore.Insert(new SymbolInfo
        {
            ProjectId = ProjectId,
            FullyQualifiedName = "global::Alpha.BaseService.Merge(string, Dictionary<string, int>, bool)",
            DisplayName = "Merge",
            Kind = SymbolKind.Method,
            Accessibility = Accessibility.Public,
            Signature = "public void Merge(string key, Dictionary<string, int> values, bool overwrite)",
            FilePath = "src/Alpha/BaseService.cs",
            LineStart = 26, LineEnd = 30,
            LastIndexedAt = now
        });

        // --- Symbols in project2 ---
        var betaClassId = symbolStore.Insert(new SymbolInfo
        {
            ProjectId = ProjectId2,
            FullyQualifiedName = "global::Beta.Consumer",
            DisplayName = "Consumer",
            Kind = SymbolKind.Class,
            Accessibility = Accessibility.Public,
            FilePath = "src/Beta/Consumer.cs",
            LineStart = 1, LineEnd = 40,
            LastIndexedAt = now
        });

        var betaMethodId = symbolStore.Insert(new SymbolInfo
        {
            ProjectId = ProjectId2,
            FullyQualifiedName = "global::Beta.Consumer.Run()",
            DisplayName = "Run",
            Kind = SymbolKind.Method,
            Accessibility = Accessibility.Public,
            Signature = "public void Run()",
            FilePath = "src/Beta/Consumer.cs",
            LineStart = 5, LineEnd = 15,
            LastIndexedAt = now
        });

        // --- Method that returns BaseService (for type dependents) ---
        symbolStore.Insert(new SymbolInfo
        {
            ProjectId = ProjectId2,
            FullyQualifiedName = "global::Beta.Consumer.GetService()",
            DisplayName = "GetService",
            Kind = SymbolKind.Method,
            Accessibility = Accessibility.Public,
            Signature = "public BaseService GetService()",
            FilePath = "src/Beta/Consumer.cs",
            LineStart = 16, LineEnd = 20,
            LastIndexedAt = now
        });

        // --- Method that takes BaseService as param ---
        symbolStore.Insert(new SymbolInfo
        {
            ProjectId = ProjectId2,
            FullyQualifiedName = "global::Beta.Consumer.UseService(BaseService)",
            DisplayName = "UseService",
            Kind = SymbolKind.Method,
            Accessibility = Accessibility.Public,
            Signature = "public void UseService(BaseService svc)",
            FilePath = "src/Beta/Consumer.cs",
            LineStart = 21, LineEnd = 25,
            LastIndexedAt = now
        });

        // --- References with AccessKind ---
        referenceStore.Insert(new ReferenceInfo
        {
            SymbolId = stringParamMethodId,
            InProjectId = ProjectId,
            FilePath = "src/Alpha/Caller.cs",
            Line = 10,
            ContextSnippet = "var x = svc.Process(name);",
            ReferenceKind = ReferenceKind.Invocation,
            AccessKind = AccessKind.Read
        });

        referenceStore.Insert(new ReferenceInfo
        {
            SymbolId = stringParamMethodId,
            InProjectId = ProjectId2,
            FilePath = "src/Beta/Consumer.cs",
            Line = 8,
            ContextSnippet = "svc.Process(input);",
            ReferenceKind = ReferenceKind.Invocation,
            AccessKind = AccessKind.Read
        });

        referenceStore.Insert(new ReferenceInfo
        {
            SymbolId = stringParamMethodId,
            InProjectId = ProjectId,
            FilePath = "src/Alpha/Writer.cs",
            Line = 22,
            ContextSnippet = "result = svc.Process(data);",
            ReferenceKind = ReferenceKind.Invocation,
            AccessKind = AccessKind.Write
        });

        // --- Reference with null access kind (for backward compat test) ---
        referenceStore.Insert(new ReferenceInfo
        {
            SymbolId = voidMethodId,
            InProjectId = ProjectId,
            FilePath = "src/Alpha/Caller.cs",
            Line = 5,
            ContextSnippet = "svc.Init();",
            ReferenceKind = ReferenceKind.Invocation,
            AccessKind = null
        });

        // --- Test method symbols ---
        symbolStore.Insert(new SymbolInfo
        {
            ProjectId = ProjectId,
            FullyQualifiedName = "global::Alpha.Tests.BaseServiceTests.Process_ReturnsExpected()",
            DisplayName = "Process_ReturnsExpected",
            Kind = SymbolKind.Method,
            Accessibility = Accessibility.Public,
            Signature = "public void Process_ReturnsExpected()",
            FilePath = "tests/Alpha.Tests/BaseServiceTests.cs",
            LineStart = 10, LineEnd = 20,
            Attributes = """["global::Xunit.FactAttribute"]""",
            LastIndexedAt = now
        });

        symbolStore.Insert(new SymbolInfo
        {
            ProjectId = ProjectId,
            FullyQualifiedName = "global::Alpha.Tests.BaseServiceTests.Init_Works()",
            DisplayName = "Init_Works",
            Kind = SymbolKind.Method,
            Accessibility = Accessibility.Public,
            Signature = "public void Init_Works()",
            FilePath = "tests/Alpha.Tests/BaseServiceTests.cs",
            LineStart = 22, LineEnd = 30,
            Attributes = """["global::Xunit.FactAttribute"]""",
            LastIndexedAt = now
        });

        // NUnit test method
        symbolStore.Insert(new SymbolInfo
        {
            ProjectId = ProjectId,
            FullyQualifiedName = "global::Alpha.Tests.NUnitTests.SomeTest()",
            DisplayName = "SomeTest",
            Kind = SymbolKind.Method,
            Accessibility = Accessibility.Public,
            Signature = "public void SomeTest()",
            FilePath = "tests/Alpha.Tests/NUnitTests.cs",
            LineStart = 5, LineEnd = 10,
            Attributes = """["global::NUnit.Framework.TestAttribute"]""",
            LastIndexedAt = now
        });

        // --- Comments ---
        commentStore.Insert(ProjectId, "src/Alpha/BaseService.cs", 7, "TODO",
            "Refactor this to use async pattern", voidMethodId, now);
        commentStore.Insert(ProjectId, "src/Alpha/BaseService.cs", 15, "HACK",
            "Temporary workaround for encoding issue", stringParamMethodId, now);
        commentStore.Insert(ProjectId2, "src/Beta/Consumer.cs", 12, "TODO",
            "Add retry logic for error handling", betaMethodId, now);
        commentStore.Insert(ProjectId, "src/Alpha/DerivedService.cs", 5, "FIXME",
            "Memory leak when processing large inputs", derivedClassId, now);

        // --- Call graph + argument flow + return flow for TraceValue ---
        var callEdgeId = callGraphStore.Insert(new CallGraphEdge
        {
            CallerSymbolId = betaMethodId,
            CalleeSymbolId = stringParamMethodId,
            CallSiteFile = "src/Beta/Consumer.cs",
            CallSiteLine = 8,
            LastIndexedAt = now
        });

        argumentFlowStore.Insert(callEdgeId, 0, "input",
            "this.Name", "member_access", "global::Beta.Consumer.Name", now);

        returnFlowStore.Insert(callEdgeId, "assignment", "result",
            "global::Beta.Consumer.Run()", now);

        var callerMethodId = symbolStore.Insert(new SymbolInfo
        {
            ProjectId = ProjectId,
            FullyQualifiedName = "global::Alpha.Orchestrator.Execute()",
            DisplayName = "Execute",
            Kind = SymbolKind.Method,
            Accessibility = Accessibility.Public,
            Signature = "public void Execute()",
            FilePath = "src/Alpha/Orchestrator.cs",
            LineStart = 5, LineEnd = 15,
            LastIndexedAt = now
        });

        var callEdge2Id = callGraphStore.Insert(new CallGraphEdge
        {
            CallerSymbolId = callerMethodId,
            CalleeSymbolId = stringParamMethodId,
            CallSiteFile = "src/Alpha/Orchestrator.cs",
            CallSiteLine = 10,
            LastIndexedAt = now
        });

        argumentFlowStore.Insert(callEdge2Id, 0, "input",
            "\"hardcoded\"", "literal", null, now);

        returnFlowStore.Insert(callEdge2Id, "discard", null, null, now);

        // --- References to BaseService type (for type dependents: methods returning/taking it) ---
        referenceStore.Insert(new ReferenceInfo
        {
            SymbolId = baseClassId,
            InProjectId = ProjectId2,
            FilePath = "src/Beta/Consumer.cs",
            Line = 16,
            ContextSnippet = "public BaseService GetService()",
            ReferenceKind = ReferenceKind.TypeRef,
            AccessKind = AccessKind.Read
        });

        referenceStore.Insert(new ReferenceInfo
        {
            SymbolId = baseClassId,
            InProjectId = ProjectId2,
            FilePath = "src/Beta/Consumer.cs",
            Line = 21,
            ContextSnippet = "public void UseService(BaseService svc)",
            ReferenceKind = ReferenceKind.TypeRef,
            AccessKind = AccessKind.Read
        });
    }

    public void Dispose()
    {
        DbProvider.Dispose();
        Db.Dispose();
        if (File.Exists(DbPath))
            File.Delete(DbPath);
    }
}
