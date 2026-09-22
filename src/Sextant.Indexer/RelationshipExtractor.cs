using Sextant.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Sextant.Indexer;

public static class RelationshipExtractor
{
    /// <summary>
    /// A type relationship carrying both endpoints' bound symbols alongside their stable keys, so a
    /// caller can resolve each endpoint's exact owning project (compilation-scoped resolution, #32)
    /// rather than a key-only lowest-id pick that could conflate two projects sharing an FQN/key.
    /// </summary>
    public readonly record struct RelationshipEdge(
        string FromKey, string ToKey, RelationshipKind Kind, ISymbol FromSymbol, ISymbol ToSymbol);

    /// <summary>
    /// Back-compat projection used by the legacy declaration-driven path: the same edges as
    /// <see cref="ExtractRelationshipsWithTargets"/> without the bound endpoint symbols.
    /// </summary>
    public static List<(string fromKey, string toKey, RelationshipKind kind)> ExtractRelationships(INamedTypeSymbol type)
        => ExtractRelationshipsWithTargets(type).Select(e => (e.FromKey, e.ToKey, e.Kind)).ToList();

    /// <summary>
    /// Extracts a type's declared relationships with each endpoint's bound symbol. For every kind the
    /// endpoint that can live in another project (the base type, interface, overridden member, return
    /// type, or — for <see cref="RelationshipKind.ParameterOf"/> — the parameter type) carries its real
    /// symbol so the document extractor can bind it to the exact per-project row (#32).
    /// </summary>
    public static List<RelationshipEdge> ExtractRelationshipsWithTargets(INamedTypeSymbol type)
    {
        var relationships = new List<RelationshipEdge>();
        var typeKey = SemanticSymbolKeyFactory.DeclarationKey(type);

        // Inherits
        if (type.BaseType != null &&
            type.BaseType.SpecialType != SpecialType.System_Object &&
            type.BaseType.SpecialType != SpecialType.System_ValueType)
        {
            relationships.Add(new RelationshipEdge(typeKey,
                SemanticSymbolKeyFactory.DeclarationKey(type.BaseType), RelationshipKind.Inherits, type, type.BaseType));
        }

        // Implements (direct only)
        foreach (var iface in type.Interfaces)
        {
            relationships.Add(new RelationshipEdge(typeKey,
                SemanticSymbolKeyFactory.DeclarationKey(iface), RelationshipKind.Implements, type, iface));
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
                relationships.Add(new RelationshipEdge(SemanticSymbolKeyFactory.DeclarationKey(member),
                    SemanticSymbolKeyFactory.DeclarationKey(overridden), RelationshipKind.Overrides, member, overridden));
            }

            // Returns — for methods with named return types
            if (member is IMethodSymbol method &&
                method.MethodKind == MethodKind.Ordinary &&
                method.ReturnType is INamedTypeSymbol returnType &&
                returnType.SpecialType == SpecialType.None &&
                returnType.TypeKind != TypeKind.Error)
            {
                relationships.Add(new RelationshipEdge(SemanticSymbolKeyFactory.DeclarationKey(method),
                    SemanticSymbolKeyFactory.DeclarationKey(returnType), RelationshipKind.Returns, method, returnType));
            }

            // ParameterOf — for method parameters with named types. Note the parameter TYPE is the
            // "from" endpoint (the cross-project one) and the method is owner-local, so the type carries
            // the resolvable symbol.
            if (member is IMethodSymbol paramMethod)
            {
                foreach (var param in paramMethod.Parameters)
                {
                    if (param.Type is INamedTypeSymbol paramType &&
                        paramType.SpecialType == SpecialType.None &&
                        paramType.TypeKind != TypeKind.Error)
                    {
                        relationships.Add(new RelationshipEdge(SemanticSymbolKeyFactory.DeclarationKey(paramType),
                            SemanticSymbolKeyFactory.DeclarationKey(paramMethod), RelationshipKind.ParameterOf, paramType, paramMethod));
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
