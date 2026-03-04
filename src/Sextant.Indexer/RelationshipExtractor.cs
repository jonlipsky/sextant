using Sextant.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Sextant.Indexer;

public static class RelationshipExtractor
{
    public static List<(string fromFqn, string toFqn, RelationshipKind kind)> ExtractRelationships(INamedTypeSymbol type)
    {
        var relationships = new List<(string, string, RelationshipKind)>();
        var fqnFormat = SymbolDisplayFormat.FullyQualifiedFormat;
        var typeFqn = type.ToDisplayString(fqnFormat);

        // Inherits
        if (type.BaseType != null &&
            type.BaseType.SpecialType != SpecialType.System_Object &&
            type.BaseType.SpecialType != SpecialType.System_ValueType)
        {
            relationships.Add((typeFqn, type.BaseType.ToDisplayString(fqnFormat), RelationshipKind.Inherits));
        }

        // Implements (direct only)
        foreach (var iface in type.Interfaces)
        {
            relationships.Add((typeFqn, iface.ToDisplayString(fqnFormat), RelationshipKind.Implements));
        }

        // Overrides, Returns, ParameterOf
        foreach (var member in type.GetMembers())
        {
            string? overriddenFqn = member switch
            {
                IMethodSymbol m when m.OverriddenMethod != null => m.OverriddenMethod.ToDisplayString(fqnFormat),
                IPropertySymbol p when p.OverriddenProperty != null => p.OverriddenProperty.ToDisplayString(fqnFormat),
                IEventSymbol e when e.OverriddenEvent != null => e.OverriddenEvent.ToDisplayString(fqnFormat),
                _ => null
            };

            if (overriddenFqn != null)
            {
                relationships.Add((member.ToDisplayString(fqnFormat), overriddenFqn, RelationshipKind.Overrides));
            }

            // Returns — for methods with named return types
            if (member is IMethodSymbol method &&
                method.MethodKind == MethodKind.Ordinary &&
                method.ReturnType is INamedTypeSymbol returnType &&
                returnType.SpecialType == SpecialType.None &&
                returnType.TypeKind != TypeKind.Error)
            {
                relationships.Add((method.ToDisplayString(fqnFormat), returnType.ToDisplayString(fqnFormat), RelationshipKind.Returns));
            }

            // ParameterOf — for method parameters with named types
            if (member is IMethodSymbol paramMethod)
            {
                foreach (var param in paramMethod.Parameters)
                {
                    if (param.Type is INamedTypeSymbol paramType &&
                        paramType.SpecialType == SpecialType.None &&
                        paramType.TypeKind != TypeKind.Error)
                    {
                        relationships.Add((paramType.ToDisplayString(fqnFormat), paramMethod.ToDisplayString(fqnFormat), RelationshipKind.ParameterOf));
                    }
                }
            }
        }

        return relationships;
    }

    /// <summary>
    /// Extract Instantiates relationships by walking method bodies for object creation expressions.
    /// Requires a semantic model for the syntax trees containing the type's methods.
    /// </summary>
    public static List<(string fromFqn, string toFqn, RelationshipKind kind)> ExtractInstantiates(
        INamedTypeSymbol type, Compilation compilation)
    {
        var relationships = new List<(string, string, RelationshipKind)>();
        var fqnFormat = SymbolDisplayFormat.FullyQualifiedFormat;

        foreach (var member in type.GetMembers())
        {
            if (member is not IMethodSymbol method) continue;

            foreach (var syntaxRef in method.DeclaringSyntaxReferences)
            {
                var syntax = syntaxRef.GetSyntax();
                var tree = syntax.SyntaxTree;
                var semanticModel = compilation.GetSemanticModel(tree);

                foreach (var node in syntax.DescendantNodes())
                {
                    INamedTypeSymbol? createdType = null;

                    if (node is ObjectCreationExpressionSyntax objCreation)
                    {
                        var symbolInfo = semanticModel.GetSymbolInfo(objCreation);
                        createdType = (symbolInfo.Symbol as IMethodSymbol)?.ContainingType;
                    }
                    else if (node is ImplicitObjectCreationExpressionSyntax implicitCreation)
                    {
                        var symbolInfo = semanticModel.GetSymbolInfo(implicitCreation);
                        createdType = (symbolInfo.Symbol as IMethodSymbol)?.ContainingType;
                    }

                    if (createdType != null &&
                        createdType.SpecialType == SpecialType.None &&
                        createdType.TypeKind != TypeKind.Error)
                    {
                        relationships.Add((
                            method.ToDisplayString(fqnFormat),
                            createdType.ToDisplayString(fqnFormat),
                            RelationshipKind.Instantiates));
                    }
                }
            }
        }

        return relationships;
    }
}
