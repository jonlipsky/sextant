using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Sextant.Indexer;

/// <summary>
/// A pinned snapshot of the git state a single reconcile/index pass is bound to (issue #49). Capturing
/// it once at the start of a pass and re-verifying it immediately before the atomic publish detects a
/// HEAD / working-tree move (commit, checkout, rebase, stash, edit) that landed mid-pass, so a
/// generation is never published whose recorded commit disagrees with the status/sources it analyzed.
/// </summary>
/// <remarks>
/// <para><see cref="Head"/> and <see cref="Tree"/> catch structural moves (commit/checkout/rebase/reset).
/// <see cref="WorkingTreeToken"/> is a hash over the machine-readable <c>git status --porcelain=v2</c>
/// output, so it catches add/modify/delete/rename/untracked set changes. It is intentionally structural:
/// git status does not hash worktree blobs, so a pure in-place re-edit of an ALREADY-modified file that
/// leaves the same porcelain output is not caught here — that residual is already covered by the
/// content-folding working-tree delta digest (issues #43/#47/#48) and the periodic self-healing pass.
/// The guard is INERT on a stable tree: identical pins compare equal, so a stable pass behaves exactly
/// as before this fix (no new snapshot identity, no behavior change).</para>
/// </remarks>
public sealed record GitStatePin
{
    /// <summary>The resolved <c>HEAD</c> commit SHA.</summary>
    public required string Head { get; init; }

    /// <summary>The <c>HEAD^{tree}</c> SHA (null when unavailable).</summary>
    public string? Tree { get; init; }

    /// <summary>A stable token over the working-tree status (porcelain-v2 output hash).</summary>
    public required string WorkingTreeToken { get; init; }

    /// <summary>True when <paramref name="other"/> pins the identical git state (no mid-pass move).</summary>
    public bool Matches(GitStatePin other) =>
        string.Equals(Head, other.Head, StringComparison.Ordinal)
        && string.Equals(Tree, other.Tree, StringComparison.Ordinal)
        && string.Equals(WorkingTreeToken, other.WorkingTreeToken, StringComparison.Ordinal);
}

/// <summary>
/// Captures a <see cref="GitStatePin"/> for a repository. Abstracted so a pass can pin HEAD/status/
/// sources to one state and re-verify it before publishing, and so tests can inject a fake that
/// simulates a mid-pass move (issue #49).
/// </summary>
public interface IGitStateProbe
{
    /// <summary>
    /// Captures the current git state pin for <paramref name="repoRoot"/>, or null when git/HEAD is
    /// unavailable (not a work tree, no commit). A null capture at verify time is treated fail-closed by
    /// the caller (the pass aborts rather than publish an unverifiable state).
    /// </summary>
    GitStatePin? Capture(string repoRoot);
}

/// <summary>
/// The default <see cref="IGitStateProbe"/>: reads HEAD, the tree, and the porcelain working-tree status
/// from git. Every read is best-effort; a failed read yields a null pin (the caller fails closed).
/// </summary>
public sealed class GitStateProbe : IGitStateProbe
{
    /// <summary>A shared stateless instance (the probe holds no per-repo state).</summary>
    public static readonly GitStateProbe Default = new();

    public GitStatePin? Capture(string repoRoot)
    {
        if (string.IsNullOrEmpty(repoRoot)) return null;
        var head = RunGit(repoRoot, "rev-parse HEAD");
        if (string.IsNullOrEmpty(head)) return null;

        var tree = RunGit(repoRoot, "rev-parse HEAD^{tree}");
        var status = RunGitRaw(repoRoot, "status --porcelain=v2 -z --untracked-files=all --renames");
        // A null status read (git failed) yields a stable sentinel token: two failed reads still compare
        // equal, so a repo where status is persistently unavailable does not spuriously "move" — while a
        // read that later succeeds (or vice versa) is correctly seen as a change.
        var token = status == null
            ? "status-unavailable"
            : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(status)));

        return new GitStatePin { Head = head, Tree = tree, WorkingTreeToken = token };
    }

    private static string? RunGit(string repoRoot, string args)
    {
        var output = RunGitRaw(repoRoot, args)?.Trim();
        return string.IsNullOrEmpty(output) ? null : output;
    }

    private static string? RunGitRaw(string repoRoot, string args)
    {
        try
        {
            var psi = new ProcessStartInfo("git", args)
            {
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            using var process = Process.Start(psi);
            if (process == null) return null;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Thrown by the orchestrator when the git state moved between the start-of-pass <see cref="GitStatePin"/>
/// capture and the pre-publish re-verification (issue #49). It is raised BEFORE any generation/snapshot
/// pointer is flipped, so no mixed-state generation is ever published: the staging generation is
/// abandoned on dispose and a subsequent stable pass (bounded retry, else the periodic reconcile)
/// publishes the correct single-state snapshot.
/// </summary>
public sealed class GitStateMovedException : Exception
{
    public GitStateMovedException(string message) : base(message) { }
}
