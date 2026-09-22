using Sextant.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Sextant.Indexer;

public static class RelationshipExtractor
{
    public static List<(string fromKey, string toKey, RelationshipKind kind)> ExtractRelationships(INamedTypeSymbol type)
    {
        var relationships = new List<(string, string, RelationshipKind)>();
        var typeKey = SemanticSymbolKeyFactory.DeclarationKey(type);

        // Inherits
        if (type.BaseType != null &&
            type.BaseType.SpecialType != SpecialType.System_Object &&
            type.BaseType.SpecialType != SpecialType.System_ValueType)
        {
            relationships.Add((typeKey, SemanticSymbolKeyFactory.DeclarationKey(type.BaseType), RelationshipKind.Inherits));
        }

        // Implements (direct only)
        foreach (var iface in type.Interfaces)
        {
            relationships.Add((typeKey, SemanticSymbolKeyFactory.DeclarationKey(iface), RelationshipKind.Implements));
        }

        // Overrides, Returns, ParameterOf
        foreach (var member in type.GetMembers())
        {
            ISymbol? overridden = member switch
            {
                IMethodSymbol m when m.OverriddenMethod != null => m.OverriddenMethod,
                IPropertySymbol p when p.OverriddenProperty != null => p.OverriddenProperty,
                IEventSymbol e when e.OverriddenEvent != null => e.OverriddenEvent,
                _ => null
            };

            if (overridden != null)
            {
                relationships.Add((SemanticSymbolKeyFactory.DeclarationKey(member),
                    SemanticSymbolKeyFactory.DeclarationKey(overridden), RelationshipKind.Overrides));
            }

            // Returns — for methods with named return types
            if (member is IMethodSymbol method &&
                method.MethodKind == MethodKind.Ordinary &&
                method.ReturnType is INamedTypeSymbol returnType &&
                returnType.SpecialType == SpecialType.None &&
                returnType.TypeKind != TypeKind.Error)
            {
                relationships.Add((SemanticSymbolKeyFactory.DeclarationKey(method),
                    SemanticSymbolKeyFactory.DeclarationKey(returnType), RelationshipKind.Returns));
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
                        relationships.Add((SemanticSymbolKeyFactory.DeclarationKey(paramType),
                            SemanticSymbolKeyFactory.DeclarationKey(paramMethod), RelationshipKind.ParameterOf));
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
    public static List<(string fromKey, string toKey, RelationshipKind kind)> ExtractInstantiates(
        INamedTypeSymbol type, Compilation compilation)
    {
        var relationships = new List<(string, string, RelationshipKind)>();

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
                        createdType.TypeKind != TypeKind.Error &&
                        !SemanticSymbolKeyFactory.IsExcludedArtifact(createdType))
                    {
                        relationships.Add((
                            SemanticSymbolKeyFactory.DeclarationKey(method),
                            SemanticSymbolKeyFactory.DeclarationKey(createdType),
                            RelationshipKind.Instantiates));
                    }
                }
            }
        }

        return relationships;
    }
}
