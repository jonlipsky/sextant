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
    /// assume-unchanged/skip-worktree, has no staged or unstaged change, and its <see cref="CheckoutFileContent.Content"/>
    /// — the exact bytes the caller read and will journal — is the committed content; otherwise why not. Never throws.
    /// </summary>
    string? Problem(string checkoutDir, string head, IReadOnlyList<CheckoutFileContent> files);
}

/// <summary>A file the SDK-pin override would journal: its absolute path and the exact bytes read from it.</summary>
public readonly record struct CheckoutFileContent(string Path, ReadOnlyMemory<byte> Content);

/// <summary>
/// The git-backed <see cref="ICheckoutContentVerifier"/>: <c>rev-parse HEAD^{commit}</c>, <c>ls-files -v</c> and
/// <c>status --porcelain</c> over just the candidate paths, then <c>ls-tree</c> of the verified commit,
/// <c>check-attr filter</c> and <c>cat-file</c> of each blob. <c>status</c> alone is not proof: git may call a file clean from its cached
/// stat data (e.g. <c>core.checkStat=minimal</c> and a same-size edit that kept its mtime), so the bytes to be
/// journaled must equal, byte for byte, the commit's blob as stored or git's checkout rendering (smudge/eol
/// conversion) of it. git is run hardened against the checkout's own
/// configuration and the worker's environment — inherited <c>GIT_*</c> variables are dropped, replace refs are
/// ignored, repository discovery cannot climb above the checkout, pathspecs are literal, <c>core.fsmonitor</c>
/// is disabled, and <c>GIT_OPTIONAL_LOCKS=0</c> keeps <c>status</c> from rewriting the index. Any failure — git missing, a
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

    public string? Problem(string checkoutDir, string head, IReadOnlyList<CheckoutFileContent> files)
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

    private string? ProblemCore(string checkout, string head, IReadOnlyList<CheckoutFileContent> files)
    {
        var relative = new List<string>(files.Count);
        foreach (var file in files)
        {
            var path = Path.GetRelativePath(checkout, Path.GetFullPath(file.Path));
            if (path == "." || path.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(path))
                return $"'{file.Path}' is not inside the checkout";
            relative.Add(path.Replace('\\', '/'));
        }
        if (relative.Count == 0)
            return null;

        var rev = Run(checkout, ["rev-parse", "--verify", "--quiet", "HEAD^{commit}"]);
        if (rev.Problem is not null)
            return rev.Problem.Length > 0 ? rev.Problem : "git cannot resolve the checkout's HEAD to a commit";
        var resolved = rev.Text.Trim();
        if (!string.Equals(resolved, head, StringComparison.OrdinalIgnoreCase))
            return $"git resolves HEAD to '{FirstLine(resolved)}', not the commit {head} the checkout's metadata names";

        var listed = Run(checkout, ["ls-files", "-z", "-v", "--", .. relative]);
        if (listed.Problem is not null)
            return listed.Problem.Length > 0 ? listed.Problem : "git ls-files failed";
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var record in listed.Text.Split('\0', StringSplitOptions.RemoveEmptyEntries))
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
        var changed = status.Text.Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (changed is not null)
            return $"'{(changed.Length > 3 ? changed[3..] : changed)}' differs from its committed content (git status '{changed[..Math.Min(2, changed.Length)]}')";

        return ContentProblem(checkout, resolved, files, relative);
    }

    /// <summary>
    /// Requires each file's bytes to be, byte for byte, the verified commit's blob as stored or git's checkout
    /// rendering of it (smudge/eol conversion under the current configuration — the stored form also covers a
    /// file checked out before, say, <c>core.autocrlf</c> was turned on). Hashing the bytes through the clean
    /// filter would not do: clean conversions are many-to-one (<c>ident</c>, custom filters), so an edit they
    /// normalize away would pass. The rendering is only trusted from git's own deterministic conversions
    /// (eol, <c>ident</c>, <c>working-tree-encoding</c>): a <c>filter</c> driver's smudge output is whatever the
    /// configured program emits, not a function of the blob, so a path with one must match the stored blob.
    /// </summary>
    private string? ContentProblem(string checkout, string commit, IReadOnlyList<CheckoutFileContent> files, List<string> relative)
    {
        var tree = Run(checkout, ["ls-tree", "-z", "--full-tree", commit, "--", .. relative]);
        if (tree.Problem is not null)
            return tree.Problem.Length > 0 ? tree.Problem : "git ls-tree failed";
        var blobs = new Dictionary<string, (string Mode, string Type, string Id)>(StringComparer.Ordinal);
        foreach (var record in tree.Text.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            // "<mode> SP <type> SP <object> TAB <path>"
            var tab = record.IndexOf('\t', StringComparison.Ordinal);
            var meta = tab > 0 ? record[..tab].Split(' ') : [];
            if (meta.Length == 3)
                blobs[record[(tab + 1)..]] = (meta[0], meta[1], meta[2]);
        }

        for (var i = 0; i < files.Count; i++)
        {
            var path = relative[i];
            if (!blobs.TryGetValue(path, out var blob))
                return $"'{path}' is not in the commit {commit}";
            if (blob.Type != "blob" || blob.Mode is not ("100644" or "100755"))
                return $"'{path}' is not a regular file in the commit {commit} (mode {blob.Mode} {blob.Type})";
            var problem = MatchesBlob(checkout, path, blob.Id, files[i].Content.Span);
            if (problem is not null)
                return problem;
        }
        return null;
    }

    private string? MatchesBlob(string checkout, string path, string blobId, ReadOnlySpan<byte> content)
    {
        var stored = Run(checkout, ["cat-file", "blob", blobId]);
        if (stored.Problem is not null)
            return stored.Problem.Length > 0 ? stored.Problem : "git cat-file failed";
        if (content.SequenceEqual(stored.Stdout))
            return null;
        var attribute = Run(checkout, ["check-attr", "-z", "filter", "--", path]);
        if (attribute.Problem is not null)
            return attribute.Problem.Length > 0 ? attribute.Problem : "git check-attr failed";
        // "<path> NUL filter NUL <value> NUL"
        var fields = attribute.Text.Split('\0');
        var driver = fields.Length >= 3 ? fields[2] : "unspecified";
        if (driver is not ("unspecified" or "unset"))
            return $"the bytes read from '{path}' are not its committed blob {blobId}, and git would check it out " +
                $"through the '{FirstLine(driver)}' filter driver, whose output cannot vouch for the commit";
        var rendered = Run(checkout, ["cat-file", "--filters", $"--path={path}", blobId]);
        if (rendered.Problem is not null)
            return rendered.Problem.Length > 0 ? rendered.Problem : "git cat-file failed";
        return content.SequenceEqual(rendered.Stdout)
            ? null
            : $"the bytes read from '{path}' are not a checkout of its committed content (blob {blobId})";
    }

    /// <summary>The drained stdout, or a problem: empty for a silent non-zero exit, else a description.</summary>
    private readonly record struct GitRun(byte[] Stdout, string? Problem)
    {
        public string Text => Encoding.UTF8.GetString(Stdout);
    }

    private GitRun Run(string checkout, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(_gitExecutable)
        {
            WorkingDirectory = checkout,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
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
        // A local refs/replace entry would make git read another commit's tree under the journaled commit's id.
        psi.Environment["GIT_NO_REPLACE_OBJECTS"] = "1";
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
            return new GitRun([], "git could not be started");
        process.StandardInput.Close();
        using var stdout = new MemoryStream();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(stdout);
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(_timeout))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already exited */ }
            // Let the killed pipes drain before the buffer and process are disposed; never leave a fault unobserved.
            if (process.WaitForExit(DrainTimeout))
                Task.WaitAny([Task.WhenAll(copy, stderr)], DrainTimeout);
            Observe(copy);
            Observe(stderr);
            return new GitRun([], $"git {args[0]} timed out");
        }
        process.WaitForExit();
        copy.GetAwaiter().GetResult();
        var error = stderr.GetAwaiter().GetResult();
        return process.ExitCode == 0
            ? new GitRun(stdout.ToArray(), null)
            : new GitRun(stdout.ToArray(), string.IsNullOrWhiteSpace(error) ? string.Empty : $"git {args[0]} failed ({FirstLine(error)})");
    }

    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    private static void Observe(Task task) =>
        task.ContinueWith(t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private static string FirstLine(string value)
    {
        var line = value.Trim().Split('\n', 2)[0].Trim();
        return line.Length > 200 ? line[..200] + "…" : line;
    }
}
