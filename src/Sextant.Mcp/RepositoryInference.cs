using System.Text.Json;
using Microsoft.Data.Sqlite;
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
                    // No safe query: the call falls through, and the tool reports invalid_argument.
                    if (FtsQueryText.ToSafeQuery(value) is null)
                        return null;
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

    /// <summary>The time one call may spend probing repositories before inference is abandoned.</summary>
    public static readonly TimeSpan ProbeBudget = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Runs <paramref name="work"/> under a hard bound of <paramref name="budget"/> and returns its result, or null when
    /// the budget ran out: read connections opened inside <paramref name="work"/> are interrupted
    /// (<see cref="ReadInterrupt"/>) so a query still running at that moment fails at once instead of finishing. A
    /// partial answer is never returned. <paramref name="clock"/> (default the system clock) times the budget.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static T? RunWithin<T>(
        TimeSpan budget, CancellationToken cancellationToken, Func<CancellationToken, T> work, TimeProvider? clock = null)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(work);
        using var timeout = new CancellationTokenSource(budget, clock ?? TimeProvider.System);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        using var interrupt = ReadInterrupt.Begin(linked.Token);
        try
        {
            return work(linked.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            return null;
        }
        catch (SqliteException e) when (e.SqliteErrorCode == ReadInterrupt.InterruptedErrorCode && linked.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
    }

    /// <summary>
    /// Probes <paramref name="repositories"/> like <see cref="DatabaseProvider.ProbeRepositories"/>, but gives up once
    /// <paramref name="budget"/> has passed, interrupting a probe query that is still running (see
    /// <see cref="RunWithin{T}"/>), and returns null: a partial answer would name the wrong holders, so the call then
    /// goes on without inference, as when nothing is inferred.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static IReadOnlyList<(string Repository, int Rank)>? ProbeWithin(
        DatabaseProvider provider, IReadOnlyList<string> repositories,
        Func<IndexDatabase, FederatedReadContext, int?> probe, TimeSpan budget, CancellationToken cancellationToken,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return RunWithin(budget, cancellationToken, token => provider.ProbeRepositories(repositories, probe, token), clock);
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
