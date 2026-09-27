using System.Diagnostics;
using System.Text;

namespace Sextant.Service.SdkPin;

/// <summary>
/// Confirms, before the SDK-pin override journals and rewrites a <c>global.json</c> (issue #113), that the file
/// holds exactly its COMMITTED content. The journal's original bytes are then the commit's bytes, so a
/// same-commit crash recovery can only ever put the committed file back — never a stale local edit or an
/// untracked file onto a tree that was re-provisioned at the same commit.
/// </summary>
public interface ICheckoutContentVerifier
{
    /// <summary>
    /// Null when <paramref name="checkoutDir"/>'s HEAD resolves to the commit <paramref name="head"/> and every file
    /// in <paramref name="files"/> (absolute paths inside the checkout) is tracked, is not flagged
    /// assume-unchanged/skip-worktree, and has no staged or unstaged change; otherwise why not. Never throws.
    /// </summary>
    string? Problem(string checkoutDir, string head, IReadOnlyList<string> files);
}

/// <summary>
/// The git-backed <see cref="ICheckoutContentVerifier"/>: <c>rev-parse HEAD^{commit}</c>, <c>ls-files -v</c> and
/// <c>status --porcelain</c> over just the candidate paths. git is run hardened against the checkout's own
/// configuration and the worker's environment — inherited <c>GIT_*</c> variables are dropped, repository
/// discovery cannot climb above the checkout, pathspecs are literal, <c>core.fsmonitor</c> is disabled, and
/// <c>GIT_OPTIONAL_LOCKS=0</c> keeps <c>status</c> from rewriting the index. Any failure — git missing, a
/// timeout, git's safe-directory ownership check — is a problem, so the override is refused (fail closed).
/// </summary>
public sealed class GitCheckoutContentVerifier : ICheckoutContentVerifier
{
    private readonly string _gitExecutable;
    private readonly TimeSpan _timeout;

    /// <param name="gitExecutable">The git executable (overridable for tests); defaults to <c>git</c> on PATH.</param>
    /// <param name="timeout">Upper bound on each git invocation; defaults to one minute.</param>
    public GitCheckoutContentVerifier(string gitExecutable = "git", TimeSpan? timeout = null)
    {
        _gitExecutable = gitExecutable;
        _timeout = timeout ?? TimeSpan.FromMinutes(1);
    }

    public static GitCheckoutContentVerifier Instance { get; } = new();

    public string? Problem(string checkoutDir, string head, IReadOnlyList<string> files)
    {
        try
        {
            return ProblemCore(Path.TrimEndingDirectorySeparator(Path.GetFullPath(checkoutDir)), head, files);
        }
        catch (Exception ex)
        {
            return $"git could not verify it ({ex.GetType().Name}: {ex.Message})";
        }
    }

    private string? ProblemCore(string checkout, string head, IReadOnlyList<string> files)
    {
        var relative = new List<string>(files.Count);
        foreach (var file in files)
        {
            var path = Path.GetRelativePath(checkout, Path.GetFullPath(file));
            if (path == "." || path.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(path))
                return $"'{file}' is not inside the checkout";
            relative.Add(path.Replace('\\', '/'));
        }
        if (relative.Count == 0)
            return null;

        var rev = Run(checkout, ["rev-parse", "--verify", "--quiet", "HEAD^{commit}"]);
        if (rev.Problem is not null)
            return rev.Problem.Length > 0 ? rev.Problem : "git cannot resolve the checkout's HEAD to a commit";
        var resolved = rev.Stdout.Trim();
        if (!string.Equals(resolved, head, StringComparison.OrdinalIgnoreCase))
            return $"git resolves HEAD to '{FirstLine(resolved)}', not the commit {head} the checkout's metadata names";

        var listed = Run(checkout, ["ls-files", "-z", "-v", "--", .. relative]);
        if (listed.Problem is not null)
            return listed.Problem.Length > 0 ? listed.Problem : "git ls-files failed";
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var record in listed.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            // "<tag> <path>": H = tracked; lowercase = assume-unchanged, S = skip-worktree, M = unmerged (one
            // record per stage). Any non-H record for a path wins.
            if (record.Length > 2 && record[1] == ' '
                && (!tags.TryGetValue(record[2..], out var seen) || seen == "H"))
                tags[record[2..]] = record[..1];
        }
        foreach (var path in relative)
        {
            if (!tags.TryGetValue(path, out var tag))
                return $"'{path}' is not tracked by git";
            if (tag != "H")
                return $"'{path}' is flagged in the git index (ls-files tag '{tag}'), so git cannot vouch for its content";
        }

        var status = Run(checkout, ["status", "--porcelain=v1", "-z", "--untracked-files=no", "--ignore-submodules=all", "--", .. relative]);
        if (status.Problem is not null)
            return status.Problem.Length > 0 ? status.Problem : "git status failed";
        var changed = status.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return changed is null
            ? null
            : $"'{(changed.Length > 3 ? changed[3..] : changed)}' differs from its committed content (git status '{changed[..Math.Min(2, changed.Length)]}')";
    }

    /// <summary>The drained stdout, or a problem: empty for a silent non-zero exit, else a description.</summary>
    private readonly record struct GitRun(string Stdout, string? Problem);

    private GitRun Run(string checkout, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(_gitExecutable)
        {
            WorkingDirectory = checkout,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        // Inherited GIT_DIR/GIT_WORK_TREE/GIT_INDEX_FILE/... would point git at some other repository or index.
        foreach (var name in psi.Environment.Keys.Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToList())
            psi.Environment.Remove(name);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GCM_INTERACTIVE"] = "never";
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        psi.Environment["GIT_LITERAL_PATHSPECS"] = "1";
        if (Path.GetDirectoryName(checkout) is { Length: > 0 } parent)
            psi.Environment["GIT_CEILING_DIRECTORIES"] = parent;
        psi.Environment["LC_ALL"] = "C";
        psi.Environment["LANG"] = "C";
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("core.fsmonitor=false");
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi);
        if (process is null)
            return new GitRun(string.Empty, "git could not be started");
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(_timeout))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already exited */ }
            return new GitRun(string.Empty, $"git {args[0]} timed out");
        }
        process.WaitForExit();
        var output = stdout.GetAwaiter().GetResult();
        var error = stderr.GetAwaiter().GetResult();
        return process.ExitCode == 0
            ? new GitRun(output, null)
            : new GitRun(output, string.IsNullOrWhiteSpace(error) ? string.Empty : $"git {args[0]} failed ({FirstLine(error)})");
    }

    private static string FirstLine(string value)
    {
        var line = value.Trim().Split('\n', 2)[0].Trim();
        return line.Length > 200 ? line[..200] + "…" : line;
    }
}
