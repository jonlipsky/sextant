using System.Text.Json.Serialization;
using Sextant.Core;

namespace Sextant.Service.Search;

/// <summary>One <c>search_symbols</c> query (SVC-F), already validated by the tool.</summary>
internal sealed record SymbolSearchQuery
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    /// <summary>The case-insensitive (ASCII) prefix every result's <c>display_name</c> starts with.</summary>
    public required string NamePrefix { get; init; }

    /// <summary>The <see cref="Grants.RepositoryGrantKey"/> the search is narrowed to, or null for every visible repository.</summary>
    public string? RepositoryKey { get; init; }

    /// <summary>The branch the search is narrowed to, or null for every granted branch.</summary>
    public string? Branch { get; init; }

    /// <summary>The symbol kind the search is narrowed to, or null for every kind.</summary>
    public SymbolKind? Kind { get; init; }

    /// <summary>
    /// The most symbols read from each snapshot per call, 1..<see cref="MaxLimit"/>. The call's total is also capped by
    /// <see cref="ServiceOptions.SearchMaxHits"/>, which the snapshots a page reads share.
    /// </summary>
    public int Limit { get; init; } = DefaultLimit;
}

/// <summary>A (repository, branch) the caller may search: a <c>pending</c>, <c>unavailable</c> or <c>truncated</c> entry.</summary>
public sealed record SymbolSearchTarget
{
    /// <summary>The repository as the caller's grant spells it.</summary>
    public required string Repository { get; init; }

    /// <summary>The branch name, or <c>""</c> for a default-branch grant whose default branch is not known yet.</summary>
    public required string Branch { get; init; }
}

/// <summary>One <c>search_symbols</c> result row.</summary>
public sealed record SymbolSearchHit
{
    public required string Repository { get; init; }
    public required string Branch { get; init; }
    public required string IdentityHash { get; init; }
    public required string SymbolKey { get; init; }

    /// <summary>The symbol's <c>display_name</c>.</summary>
    public required string Name { get; init; }

    public required string FullyQualifiedName { get; init; }

    /// <summary>The lowercase <see cref="SymbolKind"/> name, as the local tools render it.</summary>
    public required string Kind { get; init; }

    /// <summary>The snake_case accessibility name, as the local tools render it.</summary>
    public required string Accessibility { get; init; }

    /// <summary>The project's canonical id, or null for a row without one.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Project { get; init; }
}

/// <summary>The outcome of one <c>search_symbols</c> call, before the tool renders it.</summary>
internal sealed record SymbolSearchOutcome
{
    /// <summary>True when the caller has no visible target at all (the uniform <c>no_visible_repositories</c> error).</summary>
    public bool NoTargets { get; init; }

    public IReadOnlyList<SymbolSearchHit> Symbols { get; init; } = [];

    /// <summary>The state the next page resumes from, or null when the search is exhausted.</summary>
    public SymbolSearchCursorState? Next { get; init; }

    /// <summary>Targets with no complete snapshot yet.</summary>
    public IReadOnlyList<SymbolSearchTarget> Pending { get; init; } = [];

    /// <summary>Targets whose snapshot could not be read this call; they keep their position in the cursor.</summary>
    public IReadOnlyList<SymbolSearchTarget> Unavailable { get; init; } = [];

    /// <summary>Targets beyond the width cap, deferred to later pages.</summary>
    public IReadOnlyList<SymbolSearchTarget> Truncated { get; init; } = [];

    /// <summary>The newest <c>published_at</c> among the snapshots read this call, or 0.</summary>
    public long IndexFreshness { get; init; }

    /// <summary>True when the page holds fewer symbols than were read, to fit the response size budget.</summary>
    public bool CutBySize { get; init; }
}
