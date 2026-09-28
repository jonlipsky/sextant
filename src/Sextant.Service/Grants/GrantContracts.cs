using Sextant.Store;

namespace Sextant.Service.Grants;

/// <summary>Which principal a grant route writes for (SVC-4).</summary>
public enum GrantScope
{
    /// <summary>The verified user caller's own grants (<c>/control/grants/self</c>, <c>act=user</c>).</summary>
    Self,

    /// <summary>The caller's tenant-wide enrollments, principal <c>'*'</c> (<c>/control/grants/tenant</c>, <c>act=application</c>).</summary>
    Tenant
}

/// <summary>The refusal codes of the grant routes and the grant-gated control routes (SVC-4).</summary>
public static class GrantReason
{
    /// <summary>The request body or query names a principal; the principal comes only from the caller assertion.</summary>
    public const string PrincipalInBody = "principal_in_body";

    /// <summary>The caller's actor (<c>act</c>) is not the one the route requires.</summary>
    public const string WrongActor = "wrong_actor";

    /// <summary>The grant would exceed the per-principal or per-tenant limit.</summary>
    public const string GrantLimit = "grant_limit";

    /// <summary>A user caller ensured a repository it holds no grant for.</summary>
    public const string NotGranted = "not_granted";

    /// <summary>The request body is not a JSON object.</summary>
    public const string InvalidBody = "invalid_body";

    /// <summary>The branch is not a name a grant can hold.</summary>
    public const string BranchNotAllowed = "branch_not_allowed";

    /// <summary><c>GET /control/grants</c> was not asked for <c>scope=tenant</c> (the only listing scope).</summary>
    public const string InvalidScope = "invalid_scope";
}

/// <summary>The <c>snapshot_status</c> vocabulary of a grant's status and of a listed branch (SVC-4).</summary>
public static class GrantSnapshotStatus
{
    /// <summary>The branch points at a complete snapshot that covers the whole checkout.</summary>
    public const string Complete = "complete";

    /// <summary>The branch points at a complete snapshot whose coverage is partial (issue #119).</summary>
    public const string Partial = "partial";

    /// <summary>No complete snapshot yet, but an ensure for the branch is queued or running.</summary>
    public const string Pending = "pending";

    /// <summary>No complete snapshot and no ensure in flight.</summary>
    public const string Missing = "missing";
}

/// <summary>The outcome of a grant write.</summary>
public sealed record GrantWriteResult
{
    /// <summary>The stored grant (null when refused).</summary>
    public RepositoryGrantRow? Grant { get; init; }

    /// <summary>True when the write created the grant; false when it already existed.</summary>
    public bool Created { get; init; }

    /// <summary>A <see cref="GrantReason"/> code when the write was refused (the limit), else null.</summary>
    public string? Refusal { get; init; }
}

/// <summary>Where a grant's repository branch stands in the catalog (<c>GET /control/grants/self</c>).</summary>
public sealed record GrantStatus
{
    /// <summary>The branch the grant resolves to: its own name, or the default branch's name (null while unknown).</summary>
    public string? ResolvedBranch { get; init; }

    /// <summary>A <see cref="GrantSnapshotStatus"/> value.</summary>
    public required string SnapshotStatus { get; init; }

    public string? CommitSha { get; init; }
    public long? PublishedAt { get; init; }
    public string? IdentityHash { get; init; }
}

/// <summary>One of the caller's own grants with its catalog status.</summary>
public sealed record GrantView
{
    /// <summary>The repository URL as first submitted.</summary>
    public required string Repository { get; init; }

    /// <summary>The granted branch; empty for the default branch.</summary>
    public required string Branch { get; init; }

    public required string Source { get; init; }
    public required long CreatedAt { get; init; }
    public required GrantStatus Status { get; init; }
}

/// <summary>One branch of a repository listed by <c>list_repositories</c>.</summary>
public sealed record VisibleBranch
{
    /// <summary>The branch name; empty only for a default-branch grant whose default branch is not known yet.</summary>
    public required string Branch { get; init; }

    public required bool IsDefault { get; init; }

    /// <summary>A <see cref="GrantSnapshotStatus"/> value.</summary>
    public required string Status { get; init; }

    public string? CommitSha { get; init; }
    public long? PublishedAt { get; init; }
}

/// <summary>One repository visible to a caller, listed by <c>list_repositories</c>.</summary>
public sealed record VisibleRepository
{
    /// <summary>The repository URL: the oldest tenant-wide grant's spelling, else the oldest grant's.</summary>
    public required string Repository { get; init; }

    /// <summary>The distinct sources of the caller's visible grants on the repository, sorted.</summary>
    public required IReadOnlyList<string> Sources { get; init; }

    /// <summary>The repository's catalog branches plus any granted branch the catalog does not know yet.</summary>
    public required IReadOnlyList<VisibleBranch> Branches { get; init; }
}
