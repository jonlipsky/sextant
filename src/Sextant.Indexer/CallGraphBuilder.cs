using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Sextant.Indexer;

public static class CallGraphBuilder
{
    /// <summary>
    /// Represents a call graph edge with the callee identified by FQN (for later resolution to ID).
    /// </summary>
    public sealed class CallEdge
    {
        public required string CalleeFqn { get; init; }
        public required string CallSiteFile { get; init; }
        public int CallSiteLine { get; init; }
        public InvocationExpressionSyntax? InvocationSyntax { get; init; }
        public SemanticModel? SemanticModel { get; init; }
    }

    public static async Task<List<CallEdge>> BuildCallGraphAsync(
        IMethodSymbol method, Project project)
    {
        var edges = new List<CallEdge>();
        var compilation = await project.GetCompilationAsync();
        if (compilation == null)
            return edges;

        var fqnFormat = SymbolDisplayFormat.FullyQualifiedFormat;

        foreach (var syntaxRef in method.DeclaringSyntaxReferences)
        {
            var syntaxNode = await syntaxRef.GetSyntaxAsync();
            var semanticModel = compilation.GetSemanticModel(syntaxRef.SyntaxTree);

            foreach (var invocation in syntaxNode.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var symbolInfo = semanticModel.GetSymbolInfo(invocation);
                if (symbolInfo.Symbol is not IMethodSymbol callee)
                    continue;

                var lineSpan = invocation.GetLocation().GetLineSpan();

                edges.Add(new CallEdge
                {
                    CalleeFqn = callee.ToDisplayString(fqnFormat),
                    CallSiteFile = syntaxRef.SyntaxTree.FilePath,
                    CallSiteLine = lineSpan.StartLinePosition.Line + 1,
                    InvocationSyntax = invocation,
                    SemanticModel = semanticModel
                });
            }
        }

        return edges;
    }
}
