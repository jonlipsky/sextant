using System.Diagnostics;

namespace Sextant.Service;

/// <summary>
/// An <see cref="ICheckoutProvider"/> DECORATOR that makes the service self-sufficient: it clones a
/// repository at the requested commit into the persistent checkout volume ON A LOCATE MISS, then delegates
/// to the wrapped locate-only provider to find the solution to index. This closes the gap where the data
/// plane could only index a checkout that some external orchestrator had already provisioned — a bare
/// <c>ensure(repo_url, commit)</c> from ProcessStack (which does not clone) previously terminated
/// <c>unsupported</c>.
///
/// <para>
/// Cache + idempotency: the canonical checkout directory IS the cache. The FIRST action is always the inner
/// locate; a repo already on disk WITH a solution AT the requested commit short-circuits with NO git — so
/// repeated ensures for the same repo+commit are cheap and never re-clone. A cached checkout at a DIFFERENT
/// commit is re-provisioned (atomic swap) so a snapshot is never produced from the wrong revision; a
/// non-git / externally-provisioned tree is trusted as-is (never clobbered).
/// </para>
/// <para>
/// Atomicity: the clone lands in a temp directory UNDER the checkout root and is only published to the
/// canonical directory via an atomic <see cref="Directory.Move"/> (same volume ⇒ a rename). A failed clone
/// deletes its temp dir, so the locate provider never observes a partially-cloned tree as a valid checkout.
/// </para>
/// <para>
/// Concurrency: a per-repo lock serializes ensures for the SAME repository so two concurrent deliveries
/// don't race the same directory; the loser re-locates the winner's freshly-published checkout.
/// </para>
/// <para>
/// Containment: the temp + canonical directories are derived from the canonical
/// <see cref="ServicePaths.RepoDirectoryName"/> mapping and asserted to stay inside the checkout root, so a
/// crafted repository URL can never write outside the volume (defense in depth with URL sanitization).
/// </para>
/// </summary>
public sealed class CloningCheckoutProvider : ICheckoutProvider
{
    private readonly ICheckoutProvider _inner;
    private readonly ServicePaths _paths;
    private readonly string? _token;
    private readonly string _gitExecutable;
    private readonly Action<string>? _log;

    /// <summary>Upper bound on any single git invocation (a shallow fetch of one commit).</summary>
    private static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Prefix for the transient clone-staging directories under the checkout root.</summary>
    private const string TempClonePrefix = ".tmp-clone-";

    // A conservative git transport allowlist: normal remote transports plus <c>file</c> (local/test
    // remotes), but NOT helper transports like <c>ext::</c> that can execute arbitrary commands during a
    // fetch. Defense in depth for the untrusted repository URL in clone mode.
    private const string AllowedGitProtocols = "https:http:file:ssh:git";

    // STRIPED per-repo locks: a fixed pool of gates hashed by canonical directory name. Two ensures for the
    // SAME repo always hash to the SAME gate (serialized ⇒ no directory race); distinct repos usually hash
    // to distinct gates (parallelism). A fixed-size pool BOUNDS memory against request-controlled repository
    // URLs — unlike a per-URL dictionary it never retains an entry per distinct (possibly hostile) URL.
    // (The service also serializes ALL production behind a single write gate, so in the real host this is
    // defense-in-depth; it is what makes the direct-parallel unit tests correct.)
    private const int LockStripeCount = 64;
    private readonly object[] _repoLocks = CreateStripes(LockStripeCount);

    private static object[] CreateStripes(int count)
    {
        var stripes = new object[count];
        for (var i = 0; i < count; i++)
            stripes[i] = new object();
        return stripes;
    }

    // Same directory name ⇒ same stripe within the process (GetHashCode is stable for the process lifetime).
    private object RepoGate(string dirName) =>
        _repoLocks[(int)((uint)StringComparer.Ordinal.GetHashCode(dirName) % LockStripeCount)];

    /// <param name="inner">The locate-only provider consulted first (cache hit) and again after a clone.</param>
    /// <param name="paths">The service volumes; clones land under <see cref="ServicePaths.CheckoutRoot"/>.</param>
    /// <param name="token">Optional access token for a private <c>https</c> clone; never logged.</param>
    /// <param name="gitExecutable">The git executable (overridable for tests); defaults to <c>git</c> on PATH.</param>
    /// <param name="log">Optional structured log sink (secrets are never passed to it).</param>
    public CloningCheckoutProvider(
        ICheckoutProvider inner,
        ServicePaths paths,
        string? token = null,
        string gitExecutable = "git",
        Action<string>? log = null)
    {
        _inner = inner;
        _paths = paths;
        _token = string.IsNullOrWhiteSpace(token) ? null : token;
        _gitExecutable = gitExecutable;
        _log = log;

        // Reclaim any temp-clone directories a previous run leaked (a crash, or a delete blocked by a
        // still-locked read-only pack file). The service constructs this provider ONCE at startup, so this
        // is the checkout-volume analogue of ServicePaths' scratch sweep; the locate provider never mistakes
        // a `.tmp-clone-*` dir for a canonical checkout, so only the disk leak needs reclaiming.
        SweepOrphanedTempClones();
    }

    public bool TryResolve(EnsureSnapshotRequest request, out CheckoutResolution resolution)
    {
        // Cache hit / idempotency: an already-provisioned checkout with a solution short-circuits with NO
        // git — BUT ONLY when it is at the requested commit. A checkout at a DIFFERENT commit must never be
        // indexed as the requested commit (that would publish a snapshot whose code is the wrong revision),
        // so a VERIFIED mismatch of our own cache falls through to re-provisioning below. An UNVERIFIABLE
        // checkout (e.g. an externally-provisioned non-git tree) keeps the locate provider's
        // trust-what-is-on-disk semantics — we neither re-clone nor clobber it.
        if (_inner.TryResolve(request, out resolution)
            && CheckoutCommitState(resolution.CheckoutDir, request.CommitSha) != CommitState.Mismatch)
            return true;

        resolution = null!;

        var dirName = ServicePaths.RepoDirectoryName(request.RepositoryRemoteUrl);
        var root = Path.GetFullPath(_paths.CheckoutRoot);
        var target = Path.GetFullPath(Path.Combine(root, dirName));
        // Containment guard (defense in depth with the URL sanitizer): never provision outside the volume.
        if (!IsContainedIn(root, target))
            return false;

        lock (RepoGate(dirName))
        {
            // Re-check under the lock: a concurrent ensure for the same repo may have just published it AT
            // the requested commit.
            if (_inner.TryResolve(request, out resolution)
                && CheckoutCommitState(resolution.CheckoutDir, request.CommitSha) != CommitState.Mismatch)
                return true;

            resolution = null!;

            // Decide whether a pre-existing canonical directory may be REPLACED. Only a VERIFIED commit
            // mismatch of a checkout we manage is safe to swap (the service serializes ALL production behind
            // one write gate, so nothing is mid-index of this checkout). A match with no solution, or an
            // unverifiable tree, must NOT be clobbered — cloning the same repo cannot conjure a solution the
            // repository lacks, and we must not destroy an externally-provisioned checkout. Degrade cleanly.
            var allowReplace = false;
            if (Directory.Exists(target))
            {
                if (CheckoutCommitState(target, request.CommitSha) != CommitState.Mismatch)
                    return false;
                allowReplace = true;
            }

            if (!TryProvision(request, root, target, allowReplace))
                return false;

            // Publish succeeded → the inner provider now locates the freshly-cloned checkout + solution.
            return _inner.TryResolve(request, out resolution);
        }
    }

    private enum CommitState { Match, Mismatch, Unverifiable }

    /// <summary>
    /// Compares the checked-out <c>HEAD</c> of <paramref name="checkoutDir"/> to the requested
    /// <paramref name="commitSha"/>. Returns <see cref="CommitState.Unverifiable"/> when HEAD cannot be read
    /// (a non-git or externally-provisioned tree) so callers preserve the locate provider's
    /// trust-what-is-on-disk semantics rather than clobbering a checkout we did not create.
    /// </summary>
    private CommitState CheckoutCommitState(string checkoutDir, string? commitSha)
    {
        var commit = commitSha?.Trim() ?? string.Empty;
        if (!IsHexObjectId(commit))
            return CommitState.Unverifiable;
        if (!Run(checkoutDir, out var head, out _, "rev-parse", "HEAD"))
            return CommitState.Unverifiable;
        head = head.Trim();
        if (head.Length == 0)
            return CommitState.Unverifiable;
        return string.Equals(head, commit, StringComparison.OrdinalIgnoreCase)
               || head.StartsWith(commit, StringComparison.OrdinalIgnoreCase)
            ? CommitState.Match
            : CommitState.Mismatch;
    }

    /// <summary>
    /// Clones <paramref name="request"/>'s repository at its exact commit into a temp dir under
    /// <paramref name="root"/>, verifies the checked-out HEAD, and publishes it to
    /// <paramref name="target"/> (replacing a stale checkout only when <paramref name="allowReplace"/>).
    /// Returns false (leaving no NEW canonical directory) on any failure so the job degrades to
    /// <c>unsupported</c> rather than indexing the wrong commit.
    /// </summary>
    private bool TryProvision(EnsureSnapshotRequest request, string root, string target, bool allowReplace)
    {
        var commit = request.CommitSha?.Trim() ?? string.Empty;
        // The commit id is an untrusted request input. Require a git object id (hex) so it can never be
        // parsed by git as an option/rev-expression (argument-injection hardening) and so we only ever
        // fetch a concrete, verifiable commit — never a branch name or ref we cannot pin.
        if (!IsHexObjectId(commit))
        {
            _log?.Invoke($"Clone skipped for '{SanitizeUrlForLog(request.RepositoryRemoteUrl)}': commit '{request.CommitSha}' is not a git object id.");
            return false;
        }

        var cleanUrl = request.RepositoryRemoteUrl;
        // A credential embedded in the REQUEST url (user[:pass]@host) would be written into `.git/config`
        // by `remote add` and could surface in diagnostics — and we cannot redact a secret we were not
        // told. Refuse it: credentials must arrive ONLY through SEXTANT_SERVICE_CHECKOUT_TOKEN, never in
        // the repository URL. (The redaction path only knows the configured token.)
        if (UrlHasUserInfo(cleanUrl))
        {
            _log?.Invoke($"Clone skipped for '{SanitizeUrlForLog(cleanUrl)}': repository URL must not embed credentials; use SEXTANT_SERVICE_CHECKOUT_TOKEN.");
            return false;
        }

        var temp = Path.GetFullPath(Path.Combine(root, $"{TempClonePrefix}{Guid.NewGuid():N}"));
        if (!IsContainedIn(root, temp))
            return false;

        // The token (if any) is injected ONLY into the transient fetch URL argument — never into the
        // `origin` remote written to `.git/config` — so the published checkout never carries a credential.
        var fetchUrl = AuthenticatedUrl(cleanUrl, _token);
        try
        {
            Directory.CreateDirectory(temp);

            var init = RunGit(temp, "init", "--quiet");
            if (!init.Ok)
                return FailProvision(GitStage.Init, "init", cleanUrl, init);
            // `--end-of-options` keeps a URL that starts with '-' from being parsed as an option
            // (argument-injection hardening); origin is the CLEAN url, so no token lands in .git/config.
            var remote = RunGit(temp, "remote", "add", "origin", "--end-of-options", cleanUrl);
            if (!remote.Ok)
                return FailProvision(GitStage.RemoteAdd, "remote add", cleanUrl, remote);

            // Prefer a shallow fetch of the EXACT commit (minimal transfer) using the authenticated URL on
            // the argv only. Some servers reject fetch-by-sha (uploadpack.allowReachableSHA1InWant off) —
            // fall back to a full fetch. CLASSIFY EACH attempt: the overall fetch is DETERMINISTIC only when
            // EVERY attempted path failed deterministically — a permanent shallow rejection followed by a
            // TRANSIENT full-fetch failure (a network blip) is still transient (retryable), so a real
            // connectivity failure can never be miscached as a permanent `unsupported`.
            var shallow = RunGit(temp, "fetch", "--depth", "1", "--end-of-options", fetchUrl, commit);
            if (!shallow.Ok)
            {
                var full = RunGit(temp, "fetch", "--end-of-options", fetchUrl);
                if (!full.Ok)
                {
                    var shallowTransient = IsTransientGitFailure(GitStage.Fetch, shallow.Kind, shallow.Stderr);
                    var fullTransient = IsTransientGitFailure(GitStage.Fetch, full.Kind, full.Stderr);
                    if (shallowTransient || fullTransient)
                        throw Transient("fetch", cleanUrl, fullTransient ? full : shallow);
                    return LogGitFailure(cleanUrl, "fetch", shallow.Stderr, full.Stderr);
                }
            }

            // `commit` is a validated hex object id (checked at the top of this method), so it can never be
            // parsed as an option or a pathspec. `--end-of-options` is therefore unnecessary here, and some
            // git builds (e.g. Debian git in the deployment image) reject it for `checkout --detach`, parsing
            // it as a pathspec, which `--detach` forbids ("--detach does not take a path argument"). Detach
            // directly to the verified sha. (`--end-of-options` stays on `remote add`/`fetch`, which guard
            // real URL args.) A failure AFTER a successful fetch is DETERMINISTIC (see IsTransientGitFailure):
            // a fresh temp repo that fetched but cannot check out the object means the commit is not present.
            var checkout = RunGit(temp, "checkout", "--detach", "--quiet", commit);
            if (!checkout.Ok)
                return FailProvision(GitStage.Checkout, "checkout", cleanUrl, checkout);

            var rev = RunGit(temp, "rev-parse", "HEAD");
            if (!rev.Ok)
                return FailProvision(GitStage.RevParse, "rev-parse", cleanUrl, rev);
            var head = rev.Stdout.Trim();
            if (!(string.Equals(head, commit, StringComparison.OrdinalIgnoreCase)
                  || head.StartsWith(commit, StringComparison.OrdinalIgnoreCase)))
            {
                _log?.Invoke($"Clone rejected for '{cleanUrl}': checked-out HEAD does not match requested commit.");
                return false;
            }

            // FETCH_HEAD records the fetch URL — which carried the token — so scrub it before publishing to
            // the durable volume. If it cannot be removed we FAIL CLOSED rather than cache a credential.
            if (_token is not null && !ScrubFetchHead(temp))
            {
                _log?.Invoke($"Clone rejected for '{cleanUrl}': could not scrub the fetch record before publish.");
                return false;
            }

            if (!Publish(temp, target, allowReplace))
            {
                _log?.Invoke($"Clone abandoned for '{cleanUrl}': a checkout was already published at the target.");
                return false;
            }
            _log?.Invoke($"Provisioned checkout for '{cleanUrl}' at {commit[..Math.Min(8, commit.Length)]}.");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"Clone failed for '{cleanUrl}': {Redact(ex.Message)}");
            return false;
        }
        finally
        {
            TryDeleteDirectory(temp);
        }
    }

    /// <summary>
    /// Publishes the staged <paramref name="temp"/> checkout to <paramref name="target"/> via a same-volume
    /// rename. When <paramref name="allowReplace"/> and a stale checkout (a VERIFIED commit mismatch) sits at
    /// the target, the stale tree is swapped out — moved aside under the sweepable temp prefix, the fresh
    /// tree moved in, then the stale tree deleted — so the target is only ever absent for the span of two
    /// renames. This is safe because the service serializes ALL production behind one write gate, so no
    /// reader is ever mid-index of the target. Returns false (without publishing) if the target is present
    /// and replacement is not allowed.
    /// </summary>
    private bool Publish(string temp, string target, bool allowReplace)
    {
        if (!Directory.Exists(target))
        {
            Directory.Move(temp, target);
            return true;
        }
        if (!allowReplace)
            return false;

        var root = Path.GetDirectoryName(target)!;
        var retired = Path.Combine(root, $"{TempClonePrefix}retired-{Guid.NewGuid():N}");
        Directory.Move(target, retired);
        try
        {
            Directory.Move(temp, target);
        }
        catch
        {
            // Roll back so a failed replace never leaves the target missing (fail-closed: keep the old cache).
            if (!Directory.Exists(target))
                Directory.Move(retired, target);
            throw;
        }
        TryDeleteDirectory(retired);
        return true;
    }

    /// <summary>
    /// Logs a git subcommand failure (with any captured stderr, token-redacted) and returns false so the
    /// caller degrades to <c>unsupported</c>. Surfacing stderr makes the common operational failures —
    /// bad token, repo-not-found, unreachable host, server rejecting fetch-by-sha — diagnosable instead of
    /// a silent <c>unsupported</c>.
    /// </summary>
    private bool LogGitFailure(string url, string step, params string[] stderrs)
    {
        var detail = string.Join(" | ", stderrs.Where(s => !string.IsNullOrWhiteSpace(s)).Select(Redact));
        _log?.Invoke($"Clone failed for '{url}' at git {step}: {(detail.Length > 0 ? detail.Trim() : "(no stderr)")}");
        return false;
    }

    /// <summary>
    /// Maps a single failed git invocation to the right outcome: THROW a
    /// <see cref="TransientProvisioningException"/> when the failure is classified TRANSIENT (retryable —
    /// the service requeues the identity), else log it and return false so the job degrades to a permanent
    /// <c>unsupported</c>. Used for the single-invocation stages (init / remote add / checkout / rev-parse);
    /// the fetch fallback classifies its two attempts together (deterministic only if BOTH are).
    /// </summary>
    private bool FailProvision(GitStage stage, string step, string url, GitResult result)
    {
        if (IsTransientGitFailure(stage, result.Kind, result.Stderr))
            throw Transient(step, url, result);
        return LogGitFailure(url, step, result.Stderr);
    }

    /// <summary>Builds a token-redacted, URL-sanitized <see cref="TransientProvisioningException"/> for a failed git step.</summary>
    private TransientProvisioningException Transient(string step, string url, GitResult result)
    {
        var reason = result.Kind switch
        {
            GitFailureKind.Timeout => "timed out",
            GitFailureKind.SpawnFailure => "git could not be started",
            _ => FirstLine(Redact(result.Stderr)) is { Length: > 0 } s ? s : "no stderr"
        };
        var message = $"git {step} failed transiently for '{SanitizeUrlForLog(url)}': {reason}";
        _log?.Invoke($"Clone will retry for '{SanitizeUrlForLog(url)}' — {message}");
        return new TransientProvisioningException(message);
    }

    private static string FirstLine(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        var nl = value.IndexOfAny(['\r', '\n']);
        return (nl >= 0 ? value[..nl] : value).Trim();
    }

    /// <summary>
    /// Decides whether a git failure at <paramref name="stage"/> is TRANSIENT (retryable) or DETERMINISTIC
    /// (permanent). A timeout is always transient; a spawn failure (git missing / not executable) is a
    /// permanent service-capability fault (retrying cannot conjure a git binary); the local setup stages
    /// (init / remote add) are treated as transient (an environmental glitch, bounded by the attempt cap); a
    /// fetch is deterministic ONLY when its stderr matches an unambiguous permanent-error pattern, else
    /// transient (an unknown/connectivity error → transient, safe because the attempt cap bounds the cost);
    /// a checkout / rev-parse AFTER a successful fetch is deterministic (a fresh temp repo that cannot resolve
    /// the requested object means the commit is not really present — a wrong-revision request, not a blip).
    /// </summary>
    internal static bool IsTransientGitFailure(GitStage stage, GitFailureKind kind, string stderr)
    {
        if (kind == GitFailureKind.Timeout)
            return true;
        if (kind == GitFailureKind.SpawnFailure)
            return false;
        return stage switch
        {
            GitStage.Init or GitStage.RemoteAdd => true,
            GitStage.Checkout or GitStage.RevParse => false,
            _ => !MatchesDeterministicGitError(stderr) // Fetch
        };
    }

    /// <summary>
    /// True when git stderr unambiguously indicates a PERMANENT condition — the repository or commit does not
    /// exist, or authentication was refused — so retrying the SAME request cannot succeed. Deliberately
    /// CONSERVATIVE: an ambiguous error (a bare <c>403</c>, an interrupted/aborted transfer, an unrecognized
    /// message) is left TRANSIENT so a recoverable commit is never permanently poisoned; the attempt cap
    /// bounds the cost of a mis-classified-transient permanent failure.
    /// </summary>
    internal static bool MatchesDeterministicGitError(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
            return false;
        foreach (var pattern in DeterministicGitErrors)
            if (stderr.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    // Permanent-error substrings (case-insensitive). Kept tight on purpose — see MatchesDeterministicGitError.
    private static readonly string[] DeterministicGitErrors =
    [
        "repository not found",
        "does not appear to be a git repository",
        "not found: did you run git update-server-info",
        "authentication failed",
        "invalid username or password",
        "could not read username",
        "terminal prompts disabled",
        "couldn't find remote ref",
        "reference is not a tree",
    ];

    /// <summary>Removes any occurrence of the token from a string before it is logged.</summary>
    private string Redact(string value) =>
        _token is null ? value : value.Replace(_token, "***", StringComparison.Ordinal);

    /// <summary>
    /// Deletes <c>.git/FETCH_HEAD</c> (which echoes the authenticated fetch URL). Returns true when it is
    /// gone afterward — regenerated on any later fetch — so a token can never be published on the volume.
    /// </summary>
    internal static bool ScrubFetchHead(string checkoutDir)
    {
        var fetchHead = Path.Combine(checkoutDir, ".git", "FETCH_HEAD");
        try
        {
            if (File.Exists(fetchHead))
                File.Delete(fetchHead);
            return !File.Exists(fetchHead);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Injects a token into an <c>https</c> URL as <c>https://x-access-token:&lt;token&gt;@host/...</c> for a
    /// private-repo clone. Returns the URL unchanged when there is no token, the URL is not <c>https</c>, or
    /// it already carries userinfo — public repos and non-https remotes need no credential.
    /// </summary>
    internal static string AuthenticatedUrl(string url, string? token)
    {
        if (token is null)
            return url;
        const string scheme = "https://";
        if (!url.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            return url;
        var rest = url[scheme.Length..];
        // Already has userinfo (user[:pass]@host) before the first path separator → don't double-inject.
        var firstSlash = rest.IndexOf('/');
        var authority = firstSlash >= 0 ? rest[..firstSlash] : rest;
        if (authority.Contains('@'))
            return url;
        return $"{scheme}x-access-token:{token}@{rest}";
    }

    /// <summary>
    /// True when an <c>http(s)</c> URL embeds userinfo (<c>user[:pass]@host</c>) in its authority. Such a
    /// caller-supplied credential must be refused (never persisted to <c>.git/config</c> or logged), since
    /// only the configured token is known to the redaction path.
    /// </summary>
    private static bool UrlHasUserInfo(string url)
    {
        var scheme = url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "https://"
            : url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "http://"
            : null;
        if (scheme is null)
            return false;
        var rest = url[scheme.Length..];
        var firstSlash = rest.IndexOf('/');
        var authority = firstSlash >= 0 ? rest[..firstSlash] : rest;
        return authority.Contains('@');
    }

    /// <summary>
    /// Strips any userinfo (<c>user[:pass]@</c>) from a URL's authority so a URL is safe to log even if a
    /// caller embedded a credential in it. Defense in depth for the (rejected) embedded-credential path.
    /// </summary>
    private static string SanitizeUrlForLog(string url)
    {
        var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0)
            return url;
        var authorityStart = schemeEnd + 3;
        var pathStart = url.IndexOf('/', authorityStart);
        var authorityEnd = pathStart >= 0 ? pathStart : url.Length;
        var at = url.LastIndexOf('@', authorityEnd - 1);
        return at >= authorityStart
            ? string.Concat(url.AsSpan(0, authorityStart), url.AsSpan(at + 1))
            : url;
    }

    /// <summary>A git object id is 7–64 hexadecimal characters (abbreviated/full SHA-1 or SHA-256).</summary>
    private static bool IsHexObjectId(string value)
    {
        if (value.Length is < 7 or > 64)
            return false;
        foreach (var c in value)
            if (!Uri.IsHexDigit(c))
                return false;
        return true;
    }

    // True when candidate is the root itself or a descendant of it, on normalized full paths (mirrors the
    // locate provider's guard) so "..", separator differences, and case (on Windows) cannot escape.
    private static bool IsContainedIn(string root, string candidate)
    {
        var rel = Path.GetRelativePath(root, candidate);
        return rel != ".."
            && !rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathRooted(rel);
    }

    private static void TryDeleteDirectory(string dir)
    {
        if (!Directory.Exists(dir))
            return;
        try
        {
            // Git writes read-only pack/object files; on Windows Directory.Delete throws on them, so clear
            // the read-only attribute across the tree first. Best-effort: a directory still locked by a dying
            // process is reclaimed by SweepOrphanedTempClones on the next startup rather than failing the
            // (already-failed) clone.
            ClearReadOnly(dir);
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* swept later */ }
    }

    private static void ClearReadOnly(string dir)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                var attrs = File.GetAttributes(file);
                if ((attrs & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best-effort */ }
    }

    /// <summary>
    /// Best-effort reclaim of any leaked <c>.tmp-clone-*</c> staging directories under the checkout root
    /// (a crashed or interrupted clone whose <c>finally</c> delete never completed). Each clone uses a fresh
    /// GUID name so a running clone never revisits an earlier temp dir; this startup sweep is what actually
    /// bounds the leak. Confined to the checkout root and never touches a canonical checkout.
    /// </summary>
    private void SweepOrphanedTempClones()
    {
        try
        {
            if (!Directory.Exists(_paths.CheckoutRoot))
                return;
            foreach (var dir in Directory.EnumerateDirectories(_paths.CheckoutRoot, TempClonePrefix + "*"))
            {
                // Skip a RECENTLY-touched staging dir: it may be an in-flight clone by another process
                // (defense for a misconfigured two-process start whose second process constructs this
                // provider before failing to acquire the single-writer lease). A genuinely orphaned dir is
                // untouched for longer than a git operation can run and ages into the sweep.
                try
                {
                    if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(dir) < GitTimeout)
                        continue;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* sweep anyway */ }
                TryDeleteDirectory(dir);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best-effort */ }
    }

    /// <summary>How a git invocation failed, so a caller can tell a TRANSIENT transport failure from a permanent one.</summary>
    internal enum GitFailureKind { None, NonZeroExit, Timeout, SpawnFailure }

    /// <summary>The git subcommand a failure occurred at (drives transient-vs-deterministic classification).</summary>
    internal enum GitStage { Init, RemoteAdd, Fetch, Checkout, RevParse }

    /// <summary>The outcome of a git invocation: success/kind plus the drained stdout/stderr.</summary>
    private readonly record struct GitResult(bool Ok, GitFailureKind Kind, string Stdout, string Stderr);

    /// <summary>
    /// Runs a git subcommand with an argument list (no shell, so no injection), draining both pipes under a
    /// hard deadline and killing the whole process tree on timeout. Reports WHY it failed (non-zero exit vs
    /// timeout vs spawn failure) so provisioning can distinguish a TRANSIENT transport failure (retryable)
    /// from a DETERMINISTIC one (permanent).
    /// </summary>
    private GitResult RunGit(string workingDir, params string[] args)
    {
        Process? process = null;
        try
        {
            var psi = new ProcessStartInfo(_gitExecutable)
            {
                WorkingDirectory = workingDir,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            // Never prompt for credentials on a private/invalid remote — fail fast instead of blocking.
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
            psi.Environment["GCM_INTERACTIVE"] = "never";
            // Force a stable C locale so git's stderr prose is English regardless of the host's LANG/LC_*.
            // The transient-vs-deterministic classifier substring-matches git's (untyped) fatal messages —
            // git exits 128 for nearly every fatal, so there is no code to switch on — and a localized
            // stderr would make a genuinely permanent "authentication failed"/"repository not found" miss the
            // allowlist and be (safely but wastefully) retried as transient. Pinning the locale keeps the
            // classification deterministic across deployments.
            psi.Environment["LC_ALL"] = "C";
            psi.Environment["LANG"] = "C";
            // Constrain transports to normal ones (+ file for local/test remotes); block helper transports
            // like ext:: that could execute arbitrary commands for a crafted repository URL.
            psi.Environment["GIT_ALLOW_PROTOCOL"] = AllowedGitProtocols;
            foreach (var a in args)
                psi.ArgumentList.Add(a);

            process = Process.Start(psi);
            if (process is null)
                return new GitResult(false, GitFailureKind.SpawnFailure, string.Empty, string.Empty);

            process.StandardInput.Close();

            using var cts = new CancellationTokenSource(GitTimeout);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

            if (!process.WaitForExit((int)GitTimeout.TotalMilliseconds))
            {
                cts.Cancel();
                KillTree(process);
                return new GitResult(false, GitFailureKind.Timeout, string.Empty, string.Empty);
            }

            var stdout = SafeResult(stdoutTask);
            var stderr = SafeResult(stderrTask);
            return process.ExitCode == 0
                ? new GitResult(true, GitFailureKind.None, stdout, stderr)
                : new GitResult(false, GitFailureKind.NonZeroExit, stdout, stderr);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // git missing / spawn failure ⇒ the provider cannot provision (a permanent capability fault).
            if (process is not null) KillTree(process);
            return new GitResult(false, GitFailureKind.SpawnFailure, string.Empty, string.Empty);
        }
        finally
        {
            process?.Dispose();
        }
    }

    /// <summary>Thin bool wrapper over <see cref="RunGit"/> for the callers that only need success + output.</summary>
    private bool Run(string workingDir, out string stdout, out string stderr, params string[] args)
    {
        var result = RunGit(workingDir, args);
        stdout = result.Stdout;
        stderr = result.Stderr;
        return result.Ok;
    }

    private static string SafeResult(Task<string> task)
    {
        try { return task.GetAwaiter().GetResult(); }
        catch { return string.Empty; }
    }

    private static void KillTree(Process process)
    {
        try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
    }
}
