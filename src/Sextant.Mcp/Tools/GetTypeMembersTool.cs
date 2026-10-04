using System.ComponentModel;
using Sextant.Core;
using Sextant.Store;
using ModelContextProtocol.Server;

namespace Sextant.Mcp.Tools;

[McpServerToolType]
public static class GetTypeMembersTool
{
    private const int MaxInheritanceDepth = 10;

    [McpServerTool(Name = "get_type_members"), Description("List all members of a type (methods, properties, fields, events) with signatures. Faster than reading the source file — includes inherited members.")]
    public static string GetTypeMembers(
        DatabaseProvider dbProvider,
        [Description("The fully qualified name of the type")] string symbol_fqn,
        [Description("Include inherited members")] bool include_inherited = false)
    {
        if (!dbProvider.TryBeginRead(out var db, out var readContext, out var authError))
            return authError;

        using var conn = db.OpenReadConnection();
        var symbolStore = new SymbolStore(conn) { Scope = readContext.Scope };
        var relationshipStore = new RelationshipStore(conn);
        var projectStore = new ProjectStore(conn) { Scope = readContext.Scope };

        var lookup = SymbolResolver.Lookup(symbolStore, projectStore, symbol_fqn, SymbolLookupOptions.Types);
        if (lookup.Status != SymbolLookupStatus.Resolved)
            return SymbolResolver.ErrorResponse(symbolStore, projectStore, lookup, readContext.Provenance);
        var typeSymbol = lookup.Symbol!;

        // A type's members are the rows whose documentation-ID key extends the type's (`M:Ns.Type.`…), which covers
        // every partial declaration in any file and never a nested type's members.
        var members = DeclaredMembers(symbolStore, typeSymbol);
        if (include_inherited)
            members.AddRange(InheritedMembers(symbolStore, relationshipStore, typeSymbol, members));

        var canonicalIdCache = FindSymbolTool.BuildCanonicalIdCache(projectStore);
        var namer = new SymbolNamer(symbolStore);
        var mapped = members
            .Select(s => FindSymbolTool.MapSymbol(
                s, FindSymbolTool.ResolveCanonicalId(s.ProjectId, canonicalIdCache), qualifiedName: namer.QualifiedName(s)))
            .ToList<object>();
        var freshness = members.Count > 0 ? members.Min(s => s.LastIndexedAt) : typeSymbol.LastIndexedAt;

        string? message = null;
        if (members.Count == 0)
        {
            message = $"{namer.QualifiedName(typeSymbol)} ({SymbolResolver.KindName(typeSymbol.Kind)}) declares no " +
                      (include_inherited ? "or inherits no indexed members." : "indexed members.");
        }
        var note = SymbolResolver.ResolutionNote(symbolStore, lookup);
        if (note is not null)
            message = message is null ? note : message + " " + note;
        return ResponseBuilder.Build(mapped, freshness, lookup.Ambiguity, readContext.Provenance, message: message);
    }

    // The members a type declares itself, in source order. A type whose key is not a documentation ID (a legacy or
    // fallback key) falls back to the rows of its declaring file inside its line span that are not types.
    private static List<SymbolInfo> DeclaredMembers(SymbolStore store, SymbolInfo type)
    {
        IEnumerable<SymbolInfo> members = type.SymbolKey.StartsWith("T:", StringComparison.Ordinal)
            ? SymbolResolver.Members(store, type)
            : store.GetByFile(type.FilePath, type.ProjectId).Where(s =>
                s.Id != type.Id && !SymbolQuery.TypeKinds.Contains(s.Kind) && s.Kind != SymbolKind.TypeParameter
                && s.LineStart >= type.LineStart && s.LineEnd <= type.LineEnd);
        return members
            .OrderBy(s => s.FilePath, StringComparer.Ordinal).ThenBy(s => s.LineStart).ThenBy(s => s.SymbolKey, StringComparer.Ordinal)
            .ToList();
    }

    // The members of the type's base classes (a class, struct or record) or base interfaces (an interface), nearest
    // first, without constructors and without a member the derived type already declares under the same name and
    // parameters (an override or a hiding member).
    private static List<SymbolInfo> InheritedMembers(
        SymbolStore store, RelationshipStore relationships, SymbolInfo type, List<SymbolInfo> declared)
    {
        var kind = type.Kind == SymbolKind.Interface ? RelationshipKind.Implements : RelationshipKind.Inherits;
        var seenSignatures = new HashSet<string>(declared.Select(MemberSignature), StringComparer.Ordinal);
        var visited = new HashSet<long> { type.Id };
        var inherited = new List<SymbolInfo>();
        var frontier = new List<SymbolInfo> { type };
        for (var depth = 0; depth < MaxInheritanceDepth && frontier.Count > 0; depth++)
        {
            var next = new List<SymbolInfo>();
            foreach (var current in frontier)
            {
                foreach (var rel in relationships.GetByFromSymbol(current.Id, kind))
                {
                    if (!visited.Add(rel.ToSymbolId) || store.GetById(rel.ToSymbolId) is not { } baseType)
                        continue;
                    next.Add(baseType);
                    foreach (var member in DeclaredMembers(store, baseType))
                    {
                        if (member.Kind == SymbolKind.Constructor || !seenSignatures.Add(MemberSignature(member)))
                            continue;
                        inherited.Add(member);
                    }
                }
            }
            frontier = next;
        }
        return inherited;
    }

    // A member's key after its type path (`M:` + `Run(System.Int32)`), so a base member with the same name and
    // parameters as a derived one compares equal.
    private static string MemberSignature(SymbolInfo member)
    {
        var key = member.SymbolKey;
        if (key.Length < 3 || key[1] != ':')
            return key;
        var open = key.IndexOf('(');
        var head = open >= 0 ? key[..open] : key;
        var lastDot = -1;
        var depth = 0;
        for (var i = 2; i < head.Length; i++)
        {
            if (head[i] == '{') depth++;
            else if (head[i] == '}') depth--;
            else if (head[i] == '.' && depth == 0) lastDot = i;
        }
        return lastDot < 0 ? key : key[..2] + key[(lastDot + 1)..];
    }
}
