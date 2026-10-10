using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Sextant.Indexer;

public static partial class SymbolExtractor
{
    [GeneratedRegex(@"[/\\]obj[/\\]", RegexOptions.IgnoreCase)]
    private static partial Regex ObjDirPattern();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex XmlTagPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();

    private static readonly SymbolDisplayFormat FqnFormat = SymbolDisplayFormat.FullyQualifiedFormat;

    /// <summary>
    /// A member as C# declares it (<c>Task&lt;Record&gt; CreateAsync(string id, CancellationToken ct = default)</c>):
    /// return type, parameter names, ref/out/in/params/this modifiers, default and constant values, and the member
    /// modifiers (static, abstract, virtual, override, sealed, const, readonly, required). Types are named as written
    /// (no namespace) so the text stays short; the symbol's <c>fully_qualified_name</c> carries the qualified name.
    /// </summary>
    internal static readonly SymbolDisplayFormat DeclarationFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters | SymbolDisplayGenericsOptions.IncludeVariance,
        memberOptions: SymbolDisplayMemberOptions.IncludeType | SymbolDisplayMemberOptions.IncludeParameters
            | SymbolDisplayMemberOptions.IncludeRef | SymbolDisplayMemberOptions.IncludeExplicitInterface
            | SymbolDisplayMemberOptions.IncludeConstantValue | SymbolDisplayMemberOptions.IncludeModifiers,
        delegateStyle: SymbolDisplayDelegateStyle.NameAndSignature,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName
            | SymbolDisplayParameterOptions.IncludeParamsRefOut | SymbolDisplayParameterOptions.IncludeDefaultValue
            | SymbolDisplayParameterOptions.IncludeExtensionThis | SymbolDisplayParameterOptions.IncludeModifiers,
        propertyStyle: SymbolDisplayPropertyStyle.ShowReadWriteDescriptor,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
            | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers
            | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
            | SymbolDisplayMiscellaneousOptions.UseAsterisksInMultiDimensionalArrays
            | SymbolDisplayMiscellaneousOptions.AllowDefaultLiteral);

    public static async Task<List<Sextant.Core.SymbolInfo>> ExtractFromProjectAsync(
        Project project, long projectId, bool includeDocComments = true)
        => (await ExtractFromProjectWithStatusAsync(project, projectId, includeDocComments)).Symbols;

    /// <summary>
    /// Extracts a project's top-level symbols and reports whether a Roslyn compilation was actually
    /// produced. A null compilation (missing SDK/reference, broken evaluation) yields an empty symbol
    /// list AND <c>CompilationAvailable == false</c>, letting the orchestrator distinguish a genuinely
    /// empty project from a failed one and mark an incomplete generation partial (criterion 3) rather
    /// than publishing it as a complete branch head.
    /// </summary>
    /// <remarks>
    /// Issue #269: the project's syntax trees are extracted on up to <paramref name="maxParallelism"/> threads (a
    /// compilation and its semantic models are safe to query concurrently), and the per-tree results are concatenated
    /// in syntax-tree order, so the output is identical at every parallelism. Each tree is walked by
    /// <see cref="DeclarationCandidates"/>, which never binds an executable body. Issue #282: when several projects are
    /// extracted at once they pass one shared <paramref name="workers"/> semaphore, which each tree's walk holds, so the
    /// walks of every project in flight together stay within the one parallelism.
    /// </remarks>
    public static async Task<(List<Sextant.Core.SymbolInfo> Symbols, bool CompilationAvailable)> ExtractFromProjectWithStatusAsync(
        Project project, long projectId, bool includeDocComments = true, int maxParallelism = 1,
        SemaphoreSlim? workers = null, CancellationToken cancellationToken = default)
    {
        var compilation = await project.GetCompilationAsync(cancellationToken);
        if (compilation == null)
            return ([], false);

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var trees = compilation.SyntaxTrees.Where(t => !IsGeneratedFile(t.FilePath)).ToList();
        var perTree = new List<Sextant.Core.SymbolInfo>[trees.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, trees.Count),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, maxParallelism), CancellationToken = cancellationToken },
            async (index, token) =>
            {
                if (workers != null)
                    await workers.WaitAsync(token);
                try
                {
                    var syntaxTree = trees[index];
                    var root = await syntaxTree.GetRootAsync(token);
                    perTree[index] = ExtractFromTree(compilation.GetSemanticModel(syntaxTree), root, projectId, includeDocComments, now);
                }
                finally
                {
                    workers?.Release();
                }
            });

        var symbols = new List<Sextant.Core.SymbolInfo>(perTree.Sum(t => t.Count));
        foreach (var treeSymbols in perTree)
            symbols.AddRange(treeSymbols);
        return (symbols, true);
    }

    private static List<Sextant.Core.SymbolInfo> ExtractFromTree(
        SemanticModel semanticModel, SyntaxNode root, long projectId, bool includeDocComments, long now)
    {
        var symbols = new List<Sextant.Core.SymbolInfo>();
        var filePath = root.SyntaxTree.FilePath;
        foreach (var node in DeclarationCandidates(root))
        {
            var declaredSymbol = semanticModel.GetDeclaredSymbol(node);
            if (declaredSymbol == null)
                continue;

            if (declaredSymbol.IsImplicitlyDeclared)
                continue;

            if (SemanticSymbolKeyFactory.IsExcludedArtifact(declaredSymbol))
                continue;

            var kind = MapSymbolKind(declaredSymbol);
            if (kind == null)
                continue;

            var location = declaredSymbol.Locations.FirstOrDefault();
            if (location == null || !location.IsInSource)
                continue;

            var lineSpan = location.GetLineSpan();
            var signature = GetSignature(declaredSymbol);

            symbols.Add(new Sextant.Core.SymbolInfo
            {
                ProjectId = projectId,
                SymbolKey = SemanticSymbolKeyFactory.DeclarationKey(declaredSymbol),
                FullyQualifiedName = declaredSymbol.ToDisplayString(FqnFormat),
                DisplayName = declaredSymbol.Name,
                Kind = kind.Value,
                Accessibility = MapAccessibility(declaredSymbol.DeclaredAccessibility),
                IsStatic = declaredSymbol.IsStatic,
                IsAbstract = declaredSymbol.IsAbstract,
                IsVirtual = declaredSymbol.IsVirtual,
                IsOverride = declaredSymbol.IsOverride,
                Signature = signature,
                SignatureHash = signature != null ? HashSignature(signature) : null,
                Declaration = GetDeclaration(declaredSymbol),
                DocComment = includeDocComments ? GetDocComment(declaredSymbol) : null,
                FilePath = filePath,
                LineStart = lineSpan.StartLinePosition.Line + 1,
                LineEnd = lineSpan.EndLinePosition.Line + 1,
                Attributes = GetAttributes(declaredSymbol),
                LastIndexedAt = now
            });
        }
        return symbols;
    }

    /// <summary>
    /// The nodes of <paramref name="root"/> whose declared symbol can be indexed, in the pre-order
    /// <see cref="SyntaxNode.DescendantNodes(Func{SyntaxNode, bool}?, bool)"/> visits them (issue #269). Inside an
    /// executable body (a block, an expression body, an initializer, a constructor initializer, a top-level statement)
    /// every declaration is a local, a parameter, a lambda, a range variable, a local function or an anonymous-type
    /// member, all of which <see cref="MapSymbolKind"/> or <see cref="SemanticSymbolKeyFactory.IsExcludedArtifact"/>
    /// drop, except a local function's type parameters. So a body yields only those, and asking the semantic model
    /// for any other declaration in it, which binds the whole body, is skipped.
    /// </summary>
    internal static IEnumerable<SyntaxNode> DeclarationCandidates(SyntaxNode root)
    {
        // Each entry is a node still to visit and whether it lies inside an executable body.
        var pending = new Stack<(SyntaxNode Node, bool InBody)>();
        foreach (var child in root.ChildNodes().Reverse())
            pending.Push((child, false));
        while (pending.Count > 0)
        {
            var (node, inBody) = pending.Pop();
            var bodyHere = inBody || IsExecutableBodyRoot(node);
            if (!bodyHere || IsLocalFunctionTypeParameter(node))
                yield return node;
            foreach (var child in node.ChildNodes().Reverse())
                pending.Push((child, bodyHere));
        }
    }

    private static bool IsExecutableBodyRoot(SyntaxNode node) => node is
        Microsoft.CodeAnalysis.CSharp.Syntax.BlockSyntax
        or Microsoft.CodeAnalysis.CSharp.Syntax.ArrowExpressionClauseSyntax
        or Microsoft.CodeAnalysis.CSharp.Syntax.EqualsValueClauseSyntax
        or Microsoft.CodeAnalysis.CSharp.Syntax.ConstructorInitializerSyntax
        or Microsoft.CodeAnalysis.CSharp.Syntax.GlobalStatementSyntax;

    private static bool IsLocalFunctionTypeParameter(SyntaxNode node) =>
        node is Microsoft.CodeAnalysis.CSharp.Syntax.TypeParameterSyntax
        { Parent.Parent: Microsoft.CodeAnalysis.CSharp.Syntax.LocalFunctionStatementSyntax };

    public static Sextant.Core.SymbolInfo? ExtractSymbolInfo(ISymbol declaredSymbol, long projectId)
    {
        if (SemanticSymbolKeyFactory.IsExcludedArtifact(declaredSymbol))
            return null;

        var kind = MapSymbolKind(declaredSymbol);
        if (kind == null) return null;

        var location = declaredSymbol.Locations.FirstOrDefault();
        if (location == null || !location.IsInSource) return null;

        var lineSpan = location.GetLineSpan();
        var signature = GetSignature(declaredSymbol);

        return new Sextant.Core.SymbolInfo
        {
            ProjectId = projectId,
            SymbolKey = SemanticSymbolKeyFactory.DeclarationKey(declaredSymbol),
            FullyQualifiedName = declaredSymbol.ToDisplayString(FqnFormat),
            DisplayName = declaredSymbol.Name,
            Kind = kind.Value,
            Accessibility = MapAccessibility(declaredSymbol.DeclaredAccessibility),
            IsStatic = declaredSymbol.IsStatic,
            IsAbstract = declaredSymbol.IsAbstract,
            IsVirtual = declaredSymbol.IsVirtual,
            IsOverride = declaredSymbol.IsOverride,
            Signature = signature,
            SignatureHash = signature != null ? HashSignature(signature) : null,
            Declaration = GetDeclaration(declaredSymbol),
            DocComment = GetDocComment(declaredSymbol),
            FilePath = location.SourceTree?.FilePath ?? "",
            LineStart = lineSpan.StartLinePosition.Line + 1,
            LineEnd = lineSpan.EndLinePosition.Line + 1,
            Attributes = GetAttributes(declaredSymbol),
            LastIndexedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
    }

    public static bool IsGeneratedFile(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return false;

        if (filePath.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
            filePath.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase))
            return true;

        if (ObjDirPattern().IsMatch(filePath))
            return true;

        return false;
    }

    public static Sextant.Core.SymbolKind? MapSymbolKind(ISymbol symbol)
    {
        return symbol switch
        {
            INamedTypeSymbol nts => nts.TypeKind switch
            {
                TypeKind.Class when nts.IsRecord => Sextant.Core.SymbolKind.Record,
                TypeKind.Class => Sextant.Core.SymbolKind.Class,
                TypeKind.Interface => Sextant.Core.SymbolKind.Interface,
                TypeKind.Struct when nts.IsRecord => Sextant.Core.SymbolKind.Record,
                TypeKind.Struct => Sextant.Core.SymbolKind.Struct,
                TypeKind.Enum => Sextant.Core.SymbolKind.Enum,
                TypeKind.Delegate => Sextant.Core.SymbolKind.Delegate,
                _ => null
            },
            IMethodSymbol ms => ms.MethodKind switch
            {
                MethodKind.Constructor or MethodKind.StaticConstructor => Sextant.Core.SymbolKind.Constructor,
                MethodKind.Ordinary or MethodKind.ExplicitInterfaceImplementation => Sextant.Core.SymbolKind.Method,
                _ => null
            },
            IPropertySymbol ps => ps.IsIndexer ? Sextant.Core.SymbolKind.Indexer : Sextant.Core.SymbolKind.Property,
            IFieldSymbol => Sextant.Core.SymbolKind.Field,
            IEventSymbol => Sextant.Core.SymbolKind.Event,
            ITypeParameterSymbol => Sextant.Core.SymbolKind.TypeParameter,
            _ => null
        };
    }

    public static Sextant.Core.Accessibility MapAccessibility(Microsoft.CodeAnalysis.Accessibility accessibility)
    {
        return accessibility switch
        {
            Microsoft.CodeAnalysis.Accessibility.Public => Sextant.Core.Accessibility.Public,
            Microsoft.CodeAnalysis.Accessibility.Internal => Sextant.Core.Accessibility.Internal,
            Microsoft.CodeAnalysis.Accessibility.Protected => Sextant.Core.Accessibility.Protected,
            Microsoft.CodeAnalysis.Accessibility.Private => Sextant.Core.Accessibility.Private,
            Microsoft.CodeAnalysis.Accessibility.ProtectedOrInternal => Sextant.Core.Accessibility.ProtectedInternal,
            Microsoft.CodeAnalysis.Accessibility.ProtectedAndInternal => Sextant.Core.Accessibility.PrivateProtected,
            _ => Sextant.Core.Accessibility.Private
        };
    }

    private static string? GetSignature(ISymbol symbol)
    {
        if (symbol is IMethodSymbol method)
            return method.ToDisplayString();
        if (symbol is IPropertySymbol property)
            return property.ToDisplayString();
        return null;
    }

    /// <summary>The <see cref="DeclarationFormat"/> text of a member or delegate; null for every other kind.</summary>
    internal static string? GetDeclaration(ISymbol symbol) => symbol switch
    {
        IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol => symbol.ToDisplayString(DeclarationFormat),
        INamedTypeSymbol { TypeKind: TypeKind.Delegate } => symbol.ToDisplayString(DeclarationFormat),
        _ => null
    };

    private static string HashSignature(string signature)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(signature));
        return Convert.ToHexStringLower(bytes);
    }

    private static string? GetDocComment(ISymbol symbol)
    {
        var xml = symbol.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(xml))
            return null;

        // Strip XML tags, keeping content
        var stripped = XmlTagPattern().Replace(xml, " ").Trim();
        stripped = WhitespacePattern().Replace(stripped, " ");
        return string.IsNullOrWhiteSpace(stripped) ? null : stripped;
    }

    private static string? GetAttributes(ISymbol symbol)
    {
        var attrs = symbol.GetAttributes();
        if (attrs.Length == 0)
            return null;

        var fqns = attrs
            .Where(a => a.AttributeClass != null)
            .Select(a => a.AttributeClass!.ToDisplayString(FqnFormat))
            .ToList();

        return fqns.Count > 0 ? JsonSerializer.Serialize(fqns) : null;
    }
}
