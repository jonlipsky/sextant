using System.Text.RegularExpressions;
using Sextant.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace Sextant.Indexer;

/// <summary>
/// A usage-site occurrence contribution: a reference from a using document to a target declaration.
/// The target is identified by its stable declaration key (resolved to a stored symbol id later by
/// the orchestrator via <see cref="SymbolCatalog"/>); <see cref="FilePath"/> and <see cref="Line"/>
/// locate the usage in the owning (consumer) document. <see cref="TargetProjectId"/> carries the
/// target's exact owning project (from the bound symbol's containing assembly) when it maps to an
/// indexed project, so the orchestrator resolves the exact per-TFM row rather than a deterministic
/// pick among same-key rows; it is null for targets outside the indexed set (key-only fallback).
/// </summary>
public sealed record ReferenceContribution(
    string TargetKey,
    string FilePath,
    int Line,
    ReferenceKind Kind,
    AccessKind? Access,
    string? Snippet,
    long? TargetProjectId = null);

/// <summary>
/// A call-graph edge contributed at a usage site: an invocation inside <see cref="CallerKey"/>'s body
/// that targets <see cref="CalleeKey"/>. Carries the already-extracted argument/return
/// <see cref="Dataflow"/> (computed during the document walk) rather than the live invocation syntax
/// or semantic model, so a project's per-document models are not pinned in memory until persistence.
/// <see cref="CalleeProjectId"/> carries the callee's exact owning project (from its containing
/// assembly) for compilation-scoped resolution, or null for a callee outside the indexed set.
/// </summary>
public sealed record CallContribution(
    string CallerKey,
    string CalleeKey,
    string CallSiteFile,
    int CallSiteLine,
    int CallSiteColumn,
    DataflowResult Dataflow,
    long? CalleeProjectId = null);

/// <summary>
/// A type relationship contributed from a document (inherits/implements/overrides/returns/parameterOf
/// from the type's declaration, or instantiates from an object-creation usage site). <see cref="FromProjectId"/>
/// and <see cref="ToProjectId"/> carry each endpoint's exact owning project (from the bound symbol's
/// containing assembly) when it maps to an indexed project, so the orchestrator binds the exact per-project
/// row rather than a key-only lowest-id pick that could conflate two projects that share an FQN/key (#32);
/// each is null for an endpoint outside the indexed set or one that is always owner-local (key-only fallback).
/// </summary>
public sealed record RelationshipContribution(
    string FromKey,
    string ToKey,
    RelationshipKind Kind,
    long? FromProjectId = null,
    long? ToProjectId = null);

/// <summary>
/// Accumulates the occurrence contributions of one logical (per-TFM) project across all of its
/// documents, deduplicating occurrence keys before persistence (acceptance criterion 5). Dedup is
/// per-project so partial-type declarations spread across files coalesce to one relationship set and
/// repeated same-line occurrences collapse to a single row (the first snippet wins).
/// </summary>
public sealed class DocumentContributionSet
{
    private readonly List<ReferenceContribution> _references = [];
    private readonly List<CallContribution> _calls = [];
    private readonly List<RelationshipContribution> _relationships = [];

    // Occurrence keys exclude the derived snippet text: two mentions with the same target/location/
    // kind/access/target-project are the same occurrence and coalesce (the snippet is display data,
    // not identity). The key includes the resolved target project because compilation-scoped exact
    // resolution can bind two same-line mentions of the *same declaration key* to different projects
    // (a multi-TFM dependency, or an `extern alias`-duplicated assembly), which persist different
    // `symbol_id` rows — so they are distinct occurrences and must not collapse. Two same-line
    // occurrences that bind the *same* target row persist as byte-identical rows (no column is
    // stored), so coalescing them discards no information — an intentional, information-preserving
    // difference from the legacy per-occurrence path. Calls also carry per-occurrence dataflow, so
    // their key includes the call-site column: two distinct same-line calls to the same callee (e.g.
    // `F(a); F(b);`) are separate occurrences whose arguments differ and must not collapse.
    private readonly HashSet<(string, string, int, ReferenceKind, AccessKind?, long?)> _refKeys = [];
    private readonly HashSet<(string, string, string, int, int)> _callKeys = [];
    private readonly HashSet<(string, string, RelationshipKind, long?, long?)> _relKeys = [];

    public IReadOnlyList<ReferenceContribution> References => _references;
    public IReadOnlyList<CallContribution> Calls => _calls;
    public IReadOnlyList<RelationshipContribution> Relationships => _relationships;

    /// <summary>
    /// Count of malformed regions where the semantic model could not bind a name/invocation to a
    /// symbol (e.g. a compile-error region). Surfaced as an extraction completeness diagnostic.
    /// </summary>
    public int CompletenessDiagnostics { get; internal set; }

    public bool AddReference(ReferenceContribution r)
    {
        if (!_refKeys.Add((r.TargetKey, r.FilePath, r.Line, r.Kind, r.Access, r.TargetProjectId)))
            return false;
        _references.Add(r);
        return true;
    }

    public bool AddCall(CallContribution c)
    {
        if (!_callKeys.Add((c.CallerKey, c.CalleeKey, c.CallSiteFile, c.CallSiteLine, c.CallSiteColumn)))
            return false;
        _calls.Add(c);
        return true;
    }

    public bool AddRelationship(RelationshipContribution r)
    {
        if (!_relKeys.Add((r.FromKey, r.ToKey, r.Kind, r.FromProjectId, r.ToProjectId)))
            return false;
        _relationships.Add(r);
        return true;
    }

    /// <summary>
    /// Folds another document's contributions into this (project-level) set, replaying the source's
    /// relationships, references, and calls in their emission order through this set's first-wins
    /// dedup. Merging the per-document sets in ascending document ordinal reproduces the sequential
    /// single-sink append byte-for-byte: each list's internal order is preserved and cross-document
    /// dedup (only relationships can collapse across documents — reference/call keys include the file)
    /// resolves identically to the sequential path. This is what lets per-document extraction run in
    /// parallel while the persisted output stays independent of task completion order.
    /// </summary>
    public void MergeFrom(DocumentContributionSet other)
    {
        foreach (var relationship in other._relationships)
            AddRelationship(relationship);
        foreach (var reference in other._references)
            AddReference(reference);
        foreach (var call in other._calls)
            AddCall(call);
        CompletenessDiagnostics += other.CompletenessDiagnostics;
    }
}

/// <summary>
/// Deterministic per-document semantic extractor: a single pass over one document's syntax + cached
/// semantic model that emits usage-site references, calls, and relationships attributed to the nearest
/// enclosing source symbol in that document. Replaces the legacy declaration-driven whole-solution
/// <c>SymbolFinder.FindReferencesAsync</c> pass (which re-scanned the whole solution once per declared
/// symbol) with a linear walk whose cost scales with document size, not solution size.
/// </summary>
/// <remarks>
/// Ownership flip vs the legacy extractor: an occurrence is now produced by the <em>using</em>
/// document, not by the target declaration. References still persist <c>in_project_id</c> = the using
/// project and <c>symbol_id</c> = the target declaration, so the cross-project connectivity the Phase-4
/// incremental closure relies on (<c>ReferenceStore.GetCrossProjectPairs</c>) stays intact — every
/// cross-project call/inheritance emits a reference, so both endpoints remain in the undirected
/// project closure.
///
/// IOperation drives the evidence where it exists — invocation targets (<see cref="IInvocationOperation"/>),
/// object creation (<see cref="IObjectCreationOperation"/>), and field/property access classification
/// (<see cref="IMemberReferenceOperation"/>). Type/member <em>mentions</em> (declarations, base lists,
/// attributes, signatures, generic arguments, typeof/cast) have no operation, so they are resolved
/// from the bound symbol of each name-syntax node — the "narrow syntax handling where IOperation lacks
/// evidence" the design calls for.
/// </remarks>
public static class DocumentSemanticExtractor
{
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Walks one document once and appends its contributions to <paramref name="sink"/>. The caller
    /// supplies the cached <paramref name="root"/> + <paramref name="model"/> (one per document per run,
    /// acceptance criterion 2) and the document's <paramref name="text"/> for snippet extraction.
    /// <paramref name="resolveTargetProject"/> maps a bound target's containing assembly to its exact
    /// indexed project id (compilation-scoped resolution); pass null to skip it (the orchestrator then
    /// falls back to key-only resolution, and unit tests that don't exercise cross-project identity omit
    /// it). The delegate keeps the extractor database-free: it only maps a Roslyn assembly symbol the
    /// orchestrator already knows how to resolve against the solution. <paramref name="includeDataflow"/>
    /// gates the per-call argument/return-flow analysis: when false (a non-deep profile), the walk skips
    /// <see cref="DataflowExtractor.ExtractFromInvocation"/> entirely and carries an empty result, so the
    /// deep-only work is not merely dropped at persistence but never performed (Phase 8, criterion 2).
    /// </summary>
    public static void ExtractDocument(
        SyntaxNode root,
        SemanticModel model,
        string filePath,
        SourceText text,
        DocumentContributionSet sink,
        Func<IAssemblySymbol, long?>? resolveTargetProject = null,
        bool includeDataflow = true)
    {
        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case BaseTypeDeclarationSyntax:
                case DelegateDeclarationSyntax:
                    EmitTypeRelationships(node, model, sink, resolveTargetProject);
                    break;

                case SimpleNameSyntax name:
                    EmitReference(name, model, filePath, text, sink, resolveTargetProject);
                    break;

                case InvocationExpressionSyntax invocation:
                    EmitCall(invocation, model, filePath, sink, resolveTargetProject, includeDataflow);
                    break;

                case ObjectCreationExpressionSyntax objectCreation:
                    // The explicit type name is a SimpleName handled by EmitReference (classified
                    // ObjectCreation); here we only add the Instantiates relationship.
                    EmitInstantiation(objectCreation, model, sink, resolveTargetProject);
                    break;

                case ImplicitObjectCreationExpressionSyntax implicitCreation:
                    // Target-typed `new()` has no type-name syntax, so emit both the ObjectCreation
                    // reference and the Instantiates relationship from the creation node itself.
                    EmitInstantiation(implicitCreation, model, sink, resolveTargetProject);
                    EmitImplicitCreationReference(implicitCreation, model, filePath, text, sink, resolveTargetProject);
                    break;
            }
        }
    }

    /// <summary>Maps a resolved target symbol to its exact indexed project id, or null when no resolver
    /// is supplied or the symbol has no assembly that maps to an indexed project (key-only fallback).</summary>
    private static long? ExactTargetProject(ISymbol target, Func<IAssemblySymbol, long?>? resolveTargetProject)
        => resolveTargetProject != null && target.ContainingAssembly is { } assembly
            ? resolveTargetProject(assembly)
            : null;

    private static void EmitTypeRelationships(
        SyntaxNode declaration, SemanticModel model, DocumentContributionSet sink,
        Func<IAssemblySymbol, long?>? resolveTargetProject)
    {
        if (model.GetDeclaredSymbol(declaration) is not INamedTypeSymbol type
            || type.IsImplicitlyDeclared
            || SemanticSymbolKeyFactory.IsExcludedArtifact(type))
            return;

        foreach (var edge in RelationshipExtractor.ExtractRelationshipsWithTargets(type))
            sink.AddRelationship(new RelationshipContribution(edge.FromKey, edge.ToKey, edge.Kind,
                ExactTargetProject(edge.FromSymbol, resolveTargetProject),
                ExactTargetProject(edge.ToSymbol, resolveTargetProject)));
    }

    private static void EmitReference(
        SimpleNameSyntax name,
        SemanticModel model,
        string filePath,
        SourceText text,
        DocumentContributionSet sink,
        Func<IAssemblySymbol, long?>? resolveTargetProject)
    {
        // Only a symbol Roslyn resolved unambiguously (info.Symbol) becomes a stored occurrence.
        // Candidate symbols arise from ambiguous/overload-error binds in temporarily-uncompilable
        // code (common during daemon editing); persisting an arbitrary candidate would emit a false,
        // possibly nondeterministic edge, so instead count it as a completeness diagnostic and skip.
        var info = model.GetSymbolInfo(name);
        var symbol = info.Symbol;
        if (symbol == null)
        {
            if (info.CandidateReason != CandidateReason.None)
                sink.CompletenessDiagnostics++;
            return;
        }

        // The contextual `var` keyword is an IdentifierNameSyntax that binds to the *inferred* type,
        // which would record a phantom type occurrence at a position where the type name never
        // textually appears (e.g. `var x = new Foo();`, `foreach (var m in list)`). The legacy
        // FindReferencesAsync path does not report implicit-var occurrences, so skip it to preserve
        // parity and avoid inflating type occurrence counts. The check is semantic, not lexical: a
        // type (or member) literally named `var` binds to a symbol whose Name is "var" and is kept.
        if (name is IdentifierNameSyntax { IsVar: true } && symbol is ITypeSymbol { Name: not "var" })
            return;

        // An attribute name (and any bare constructor reference) binds to the constructor, not the
        // type. Retarget to the constructed type so type-usage/impact queries see the type as
        // referenced. The specific constructor overload is intentionally not a stored reference
        // target here — a documented difference from the legacy declaration-driven extractor, which
        // recorded both the type-level and constructor-level occurrence.
        if (symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } constructor)
            symbol = constructor.ContainingType;

        var target = Canonicalize(symbol);
        if (!IsStoredTarget(target))
            return;

        var kind = ClassifyReferenceKind(name, target);
        AccessKind? access = target is IFieldSymbol or IPropertySymbol
            ? ClassifyAccess(name, model)
            : null;

        var line = name.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        sink.AddReference(new ReferenceContribution(
            SemanticSymbolKeyFactory.DeclarationKey(target),
            filePath,
            line,
            kind,
            access,
            Snippet(text, name.Span),
            ExactTargetProject(target, resolveTargetProject)));
    }

    private static void EmitCall(
        InvocationExpressionSyntax invocation,
        SemanticModel model,
        string filePath,
        DocumentContributionSet sink,
        Func<IAssemblySymbol, long?>? resolveTargetProject,
        bool includeDataflow)
    {
        var callee = (model.GetOperation(invocation) as IInvocationOperation)?.TargetMethod
                     ?? model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
        if (callee == null)
            return;

        var canonicalCallee = Canonicalize(callee);
        // Only source methods have a stored declaration to anchor the edge; metadata callees (e.g.
        // Console.WriteLine) would fail catalog resolution anyway, so drop them here.
        if (!IsStoredTarget(canonicalCallee))
            return;

        var callerKey = EnclosingMemberKey(invocation, model);
        if (callerKey == null)
            return;

        var startPos = invocation.GetLocation().GetLineSpan().StartLinePosition;
        var line = startPos.Line + 1;
        // Extract dataflow now, while the semantic model for this document is live, and carry only the
        // plain result forward. Deferring it to persistence would pin every document's SemanticModel
        // for the whole project (they accumulate in the contribution set), defeating Roslyn's weak
        // model caching and working against the extractor's near-linear-scaling goal. When the active
        // profile omits dataflow (non-deep), skip the analysis entirely — not just its persistence —
        // so core/standard indexing does not pay the deep profile's cost (Phase 8, criterion 2).
        var dataflow = includeDataflow
            ? DataflowExtractor.ExtractFromInvocation(invocation, model)
            : new DataflowResult();
        sink.AddCall(new CallContribution(
            callerKey,
            SemanticSymbolKeyFactory.DeclarationKey(canonicalCallee),
            filePath,
            line,
            startPos.Character,
            dataflow,
            ExactTargetProject(canonicalCallee, resolveTargetProject)));
    }

    private static void EmitInstantiation(
        SyntaxNode creationNode, SemanticModel model, DocumentContributionSet sink,
        Func<IAssemblySymbol, long?>? resolveTargetProject)
    {
        var createdType = (model.GetOperation(creationNode) as IObjectCreationOperation)?.Constructor?.ContainingType
                          ?? (model.GetSymbolInfo(creationNode).Symbol as IMethodSymbol)?.ContainingType;
        if (createdType == null)
            return;

        createdType = createdType.OriginalDefinition;
        if (!IsStoredTarget(createdType))
            return;

        var enclosingKey = EnclosingMemberKey(creationNode, model);
        if (enclosingKey == null)
            return;

        // The enclosing member is always owner-local (key-only), so only the created type carries an
        // exact target project for compilation-scoped resolution (#32).
        sink.AddRelationship(new RelationshipContribution(
            enclosingKey,
            SemanticSymbolKeyFactory.DeclarationKey(createdType),
            RelationshipKind.Instantiates,
            null,
            ExactTargetProject(createdType, resolveTargetProject)));
    }

    private static void EmitImplicitCreationReference(
        ImplicitObjectCreationExpressionSyntax creation,
        SemanticModel model,
        string filePath,
        SourceText text,
        DocumentContributionSet sink,
        Func<IAssemblySymbol, long?>? resolveTargetProject)
    {
        var createdType = (model.GetOperation(creation) as IObjectCreationOperation)?.Constructor?.ContainingType
                          ?? (model.GetSymbolInfo(creation).Symbol as IMethodSymbol)?.ContainingType;
        if (createdType == null)
            return;

        createdType = createdType.OriginalDefinition;
        if (!IsStoredTarget(createdType))
            return;

        var line = creation.NewKeyword.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        sink.AddReference(new ReferenceContribution(
            SemanticSymbolKeyFactory.DeclarationKey(createdType),
            filePath,
            line,
            ReferenceKind.ObjectCreation,
            null,
            Snippet(text, creation.Span),
            ExactTargetProject(createdType, resolveTargetProject)));
    }

    /// <summary>
    /// Classifies a name reference by its syntactic role, matching the legacy reference-kind vocabulary:
    /// attribute usage, base-list inheritance, object-creation type, method invocation, else a general
    /// type/member reference (<see cref="ReferenceKind.TypeRef"/>).
    /// </summary>
    private static ReferenceKind ClassifyReferenceKind(SimpleNameSyntax name, ISymbol target)
    {
        if (name.FirstAncestorOrSelf<AttributeSyntax>() is { } attribute
            && attribute.Name.Span.Contains(name.Span))
            return ReferenceKind.Attribute;

        if (name.FirstAncestorOrSelf<BaseTypeSyntax>() is { } baseType
            && baseType.Type.Span.Contains(name.Span)
            && target is INamedTypeSymbol)
            return ReferenceKind.Inheritance;

        if (name.FirstAncestorOrSelf<ObjectCreationExpressionSyntax>() is { } creation
            && creation.Type.Span.Contains(name.Span))
            return ReferenceKind.ObjectCreation;

        if (target is IMethodSymbol && IsInvokedName(name))
            return ReferenceKind.Invocation;

        return ReferenceKind.TypeRef;
    }

    /// <summary>
    /// True when <paramref name="name"/> is the member/name being invoked by an enclosing invocation
    /// (so `M()` and the `M` in `a.M()`/`a?.M()` classify as invocations, but arguments do not).
    /// </summary>
    private static bool IsInvokedName(SimpleNameSyntax name)
    {
        var invocation = name.FirstAncestorOrSelf<InvocationExpressionSyntax>();
        if (invocation == null)
            return false;

        var invoked = invocation.Expression switch
        {
            SimpleNameSyntax s => s,
            MemberAccessExpressionSyntax ma => ma.Name,
            MemberBindingExpressionSyntax mb => mb.Name,
            _ => null
        };
        return ReferenceEquals(invoked, name);
    }

    /// <summary>
    /// Classifies a field/property access as read/write/read-write, preferring IOperation evidence
    /// (assignment target, compound assignment, increment/decrement, ref/out argument) and falling
    /// back to the syntactic classifier where no operation is available.
    /// </summary>
    private static AccessKind? ClassifyAccess(SimpleNameSyntax name, SemanticModel model)
    {
        SyntaxNode expression = name.Parent is MemberAccessExpressionSyntax ma && ReferenceEquals(ma.Name, name)
            ? ma
            : name;

        if (model.GetOperation(expression) is IMemberReferenceOperation memberReference)
            return AccessFromOperation(memberReference);

        return ReferenceExtractor.ClassifyAccessKind(name);
    }

    private static AccessKind AccessFromOperation(IMemberReferenceOperation reference)
    {
        switch (reference.Parent)
        {
            case ISimpleAssignmentOperation assignment when ReferenceEquals(assignment.Target, reference):
                return AccessKind.Write;
            case ICompoundAssignmentOperation compound when ReferenceEquals(compound.Target, reference):
                return AccessKind.ReadWrite;
            case ICoalesceAssignmentOperation coalesce when ReferenceEquals(coalesce.Target, reference):
                return AccessKind.ReadWrite;
            case IIncrementOrDecrementOperation:
                return AccessKind.ReadWrite;
            case IArgumentOperation { Parameter.RefKind: RefKind.Out }:
                return AccessKind.Write;
            case IArgumentOperation { Parameter.RefKind: RefKind.Ref }:
                return AccessKind.ReadWrite;
            default:
                return AccessKind.Read;
        }
    }

    /// <summary>
    /// Walks up to the nearest enclosing method/constructor/accessor declaration, treating lambdas and
    /// local functions as transparent (a call inside them attributes to the outer member — matching the
    /// legacy call-graph attribution). Returns the declaration's key, or null when there is no enclosing
    /// method-like member (e.g. a field/property initializer). Accessor keys are returned as-is and are
    /// dropped by catalog resolution (accessors are not stored symbols), reproducing the legacy behavior
    /// of not recording calls made from property accessor bodies.
    /// </summary>
    private static string? EnclosingMemberKey(SyntaxNode node, SemanticModel model)
    {
        for (var current = node.Parent; current != null; current = current.Parent)
        {
            if (current is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax)
            {
                var symbol = model.GetDeclaredSymbol(current);
                return symbol == null ? null : SemanticSymbolKeyFactory.DeclarationKey(symbol);
            }
        }
        return null;
    }

    /// <summary>
    /// Reduces a bound occurrence symbol to the stored declaration it should anchor on: the original
    /// definition (so `List&lt;int&gt;` / `M&lt;int&gt;()` map to their `List&lt;T&gt;` / `M&lt;T&gt;()`
    /// declarations) and, for reduced extension-method invocations, the original static method.
    /// </summary>
    private static ISymbol Canonicalize(ISymbol symbol)
    {
        if (symbol is IMethodSymbol { ReducedFrom: { } reduced })
            symbol = reduced;
        return symbol.OriginalDefinition;
    }

    /// <summary>
    /// True when a symbol is one we store and can therefore anchor an occurrence on: a source-declared,
    /// mappable (type or type member) symbol that is not an excluded artifact. Locals, parameters,
    /// namespaces, and metadata-only symbols are excluded (they resolve to no stored row).
    /// </summary>
    private static bool IsStoredTarget(ISymbol symbol)
        => !symbol.IsImplicitlyDeclared
           && symbol.Locations.Any(l => l.IsInSource)
           && SymbolExtractor.MapSymbolKind(symbol) != null
           && !SemanticSymbolKeyFactory.IsExcludedArtifact(symbol);

    private static string Snippet(SourceText text, TextSpan span)
    {
        var start = Math.Max(0, span.Start - 60);
        var end = Math.Min(text.Length, span.End + 60);
        var snippet = text.GetSubText(TextSpan.FromBounds(start, end)).ToString();
        snippet = Whitespace.Replace(snippet, " ").Trim();
        return snippet.Length > 120 ? snippet[..120] : snippet;
    }
}
