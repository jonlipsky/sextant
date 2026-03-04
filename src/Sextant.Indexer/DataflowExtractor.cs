using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Sextant.Indexer;

public static class DataflowExtractor
{
    public static DataflowResult ExtractFromInvocation(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel)
    {
        var result = new DataflowResult();
        var symbolInfo = semanticModel.GetSymbolInfo(invocation);
        if (symbolInfo.Symbol is not IMethodSymbol method) return result;

        // --- Argument flow ---
        var args = invocation.ArgumentList.Arguments;
        for (int i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            var paramIndex = ResolveParameterIndex(arg, method, i);
            if (paramIndex < 0 || paramIndex >= method.Parameters.Length) continue;

            var param = method.Parameters[paramIndex];
            var argExpr = arg.Expression.ToString();
            var argKind = ClassifyArgumentKind(arg.Expression);
            var sourceSymbol = TryResolveSymbol(arg.Expression, semanticModel);

            result.Arguments.Add(new ArgumentFlowEntry
            {
                ParameterOrdinal = paramIndex,
                ParameterName = param.Name,
                ArgumentExpression = argExpr.Length > 200 ? argExpr[..200] : argExpr,
                ArgumentKind = argKind,
                SourceSymbolFqn = sourceSymbol
            });
        }

        // --- Return flow ---
        result.ReturnDestination = ClassifyReturnDestination(invocation, invocation.Parent, semanticModel);

        return result;
    }

    private static int ResolveParameterIndex(ArgumentSyntax arg, IMethodSymbol method, int positionalIndex)
    {
        if (arg.NameColon != null)
        {
            var name = arg.NameColon.Name.Identifier.Text;
            for (int i = 0; i < method.Parameters.Length; i++)
            {
                if (method.Parameters[i].Name == name)
                    return i;
            }
            return -1;
        }
        return positionalIndex;
    }

    private static string ClassifyArgumentKind(ExpressionSyntax expr)
    {
        return expr switch
        {
            LiteralExpressionSyntax => "literal",
            IdentifierNameSyntax => "variable",
            MemberAccessExpressionSyntax => "property_access",
            InvocationExpressionSyntax => "method_call",
            ObjectCreationExpressionSyntax => "new_object",
            ImplicitObjectCreationExpressionSyntax => "new_object",
            _ => "other"
        };
    }

    private static ReturnFlowEntry ClassifyReturnDestination(
        InvocationExpressionSyntax invocation, SyntaxNode? parent, SemanticModel model)
    {
        return parent switch
        {
            EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax vd } =>
                new ReturnFlowEntry
                {
                    DestinationKind = "assignment",
                    DestinationVariable = vd.Identifier.Text
                },

            AssignmentExpressionSyntax assignment =>
                new ReturnFlowEntry
                {
                    DestinationKind = "assignment",
                    DestinationVariable = assignment.Left.ToString(),
                    DestinationSymbolFqn = TryResolveSymbol(assignment.Left, model)
                },

            ArgumentSyntax =>
                new ReturnFlowEntry
                {
                    DestinationKind = "argument",
                    DestinationSymbolFqn = TryResolveEnclosingCall(parent, model)
                },

            ReturnStatementSyntax =>
                new ReturnFlowEntry { DestinationKind = "return" },

            AwaitExpressionSyntax =>
                new ReturnFlowEntry { DestinationKind = "await" },

            ExpressionStatementSyntax =>
                new ReturnFlowEntry { DestinationKind = "discard" },

            _ => new ReturnFlowEntry { DestinationKind = "other" }
        };
    }

    internal static string? TryResolveSymbol(ExpressionSyntax expr, SemanticModel model)
    {
        var symbol = model.GetSymbolInfo(expr).Symbol;
        return symbol?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    }

    private static string? TryResolveEnclosingCall(SyntaxNode node, SemanticModel model)
    {
        var current = node.Parent;
        while (current != null)
        {
            if (current is InvocationExpressionSyntax inv)
            {
                var symbol = model.GetSymbolInfo(inv).Symbol;
                return symbol?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            }
            current = current.Parent;
        }
        return null;
    }
}

public sealed class DataflowResult
{
    public List<ArgumentFlowEntry> Arguments { get; } = new();
    public ReturnFlowEntry? ReturnDestination { get; set; }
}

public sealed class ArgumentFlowEntry
{
    public int ParameterOrdinal { get; init; }
    public required string ParameterName { get; init; }
    public required string ArgumentExpression { get; init; }
    public required string ArgumentKind { get; init; }
    public string? SourceSymbolFqn { get; init; }
}

public sealed class ReturnFlowEntry
{
    public required string DestinationKind { get; init; }
    public string? DestinationVariable { get; init; }
    public string? DestinationSymbolFqn { get; init; }
}
