using System.Text.Json;
using Sextant.Mcp.Tools;
using Sextant.Store;

namespace Sextant.Mcp;

/// <summary>
/// How a host infers the repository of a symbol or path call that names none: one probe per repository the caller can
/// read (<see cref="DatabaseProvider.ProbeRepositories"/>), each asking whether that repository holds the call's own
/// argument, read exactly as the tool reads it. The host answers from the one repository that holds it, lists the
/// holders when several do, and otherwise leaves the call as it is (<c>repository_required</c>). Inference only chooses
/// among repositories the caller can already read: every probe and the final read go through the request's authorizer.
/// </summary>
public static class RepositoryInference
{
    /// <summary>A repository holds the argument exactly as written (or HTML-decoded).</summary>
    public const int ExactRank = 0;

    /// <summary>A repository holds the argument only by its trailing <c>Type.Member</c> or <c>Type</c>.</summary>
    public const int TrailingNameRank = 1;

    /// <summary>The tools whose call may be inferred, each with the argument a probe reads.</summary>
    public static readonly IReadOnlyDictionary<string, string> Tools = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["find_symbol"] = "name",
        ["find_references"] = "symbol_fqn",
        ["get_call_hierarchy"] = "symbol_fqn",
        ["get_implementors"] = "symbol_fqn",
        ["get_type_hierarchy"] = "symbol_fqn",
        ["get_type_members"] = "symbol_fqn",
        ["get_file_symbols"] = "file_path"
    };

    /// <summary>
    /// The probe for one call of <paramref name="tool"/> with <paramref name="arguments"/>, or null when the call cannot
    /// be inferred: another tool, a missing or blank argument, an absolute path, an unknown <c>kind</c>, or a
    /// <c>find_symbol</c> narrowed by <c>project_id</c> or a <c>scope</c> other than <c>all</c>.
    /// The probe returns <see cref="ExactRank"/> or <see cref="TrailingNameRank"/> for a repository that holds the
    /// argument (resolves it, possibly ambiguously), or null for one that does not.
    /// </summary>
    public static Func<IndexDatabase, FederatedReadContext, int?>? ProbeFor(
        string tool, IDictionary<string, JsonElement>? arguments)
    {
        if (!Tools.TryGetValue(tool, out var argument) || Text(arguments, argument) is not { } value)
            return null;

        switch (tool)
        {
            case "get_file_symbols":
                return PathPresenter.IsAbsolute(value) ? null : (db, context) => FileRank(db, context, value);
            case "find_symbol":
                if (Text(arguments, "project_id") is not null
                    || Text(arguments, "scope") is { } scope && scope != "all"
                    || !SymbolKindNames.TryParse(Text(arguments, "kind"), out var kinds, out var description, out _))
                    return null;
                if (Flag(arguments, "fuzzy"))
                {
                    var maxResults = Sextant.Core.SextantConfiguration.FromEnvironment().FtsMaxResults;
                    return (db, context) =>
                    {
                        using var conn = db.OpenReadConnection();
                        var symbols = new SymbolStore(conn) { Scope = context.Scope };
                        return FindSymbolTool.SearchFuzzy(symbols, value, maxResults, kinds).Count > 0 ? ExactRank : null;
                    };
                }
                var options = new SymbolLookupOptions { Kinds = kinds, KindsExplicit = kinds != null, KindDescription = description };
                return (db, context) => SymbolRank(db, context, value, options);
            case "get_call_hierarchy":
                return (db, context) => SymbolRank(db, context, value, GetCallHierarchyTool.CallableOptions);
            case "get_type_hierarchy" or "get_type_members":
                return (db, context) => SymbolRank(db, context, value, SymbolLookupOptions.Types);
            default:
                return (db, context) => SymbolRank(db, context, value, SymbolLookupOptions.Any);
        }
    }

    /// <summary>
    /// The repositories that hold the argument at the best rank any of them reached, in input order: a repository that
    /// holds the name exactly outranks one that holds it only by its trailing name.
    /// </summary>
    public static IReadOnlyList<string> Holders(IReadOnlyList<(string Repository, int Rank)> hits)
    {
        ArgumentNullException.ThrowIfNull(hits);
        if (hits.Count == 0)
            return [];
        var best = hits.Min(h => h.Rank);
        return hits.Where(h => h.Rank == best).Select(h => h.Repository).ToList();
    }

    private static int? SymbolRank(IndexDatabase db, FederatedReadContext context, string name, SymbolLookupOptions options)
    {
        using var conn = db.OpenReadConnection();
        var lookup = SymbolResolver.Lookup(
            new SymbolStore(conn) { Scope = context.Scope }, new ProjectStore(conn) { Scope = context.Scope }, name,
            options with { SkipSuggestions = true });
        return lookup.Status switch
        {
            SymbolLookupStatus.Resolved or SymbolLookupStatus.Ambiguous => lookup.ByTrailingName ? TrailingNameRank : ExactRank,
            _ => null
        };
    }

    private static int? FileRank(IndexDatabase db, FederatedReadContext context, string path)
    {
        using var conn = db.OpenReadConnection();
        var symbols = new SymbolStore(conn) { Scope = context.Scope };
        return symbols.GetByStoredRelativePaths(PathPresenter.StoredCandidates(path))
            .Any(s => context.Paths.Matches(s.FilePath, path))
            ? ExactRank
            : null;
    }

    private static string? Text(IDictionary<string, JsonElement>? arguments, string name) =>
        arguments is not null && arguments.TryGetValue(name, out var element)
        && element.ValueKind == JsonValueKind.String && element.GetString() is { } text
        && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;

    private static bool Flag(IDictionary<string, JsonElement>? arguments, string name) =>
        arguments is not null && arguments.TryGetValue(name, out var element) && element.ValueKind == JsonValueKind.True;
}
