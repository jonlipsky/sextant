using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Sextant.Service.Contributions;

/// <summary>
/// A REAL <see cref="IGitContentProvider"/> (#68) that verifies a contribution's declared file blob against
/// the authoritative Git content of an already-provisioned checkout, using the <c>git</c> CLI
/// (<c>git cat-file</c>). It replaces the dev-default <see cref="UnavailableGitContentProvider"/> so a
/// deployment that sets <see cref="ContributionPolicy.RequireGitContentVerification"/> can actually verify —
/// a declared blob that does not match the repository's content at the exact commit is rejected as a
/// tampered supply-chain input (acceptance criteria 1 &amp; 2).
///
/// Checkout PROVISIONING stays out of scope (Phase 14 / ProcessStack); this provider only READS an existing
/// checkout located through the same repo-url → directory mapping the indexing worker uses
/// (<see cref="ServicePaths.RepoDirectoryName"/>), so the two never disagree about where a repo lives.
///
/// HASH-DOMAIN RECONCILIATION (#68 point 2): a manifest fingerprint records EITHER the git blob OID
/// (<c>file_versions.git_blob_hash</c>) OR — the common case today — the raw SHA-256 of the file content
/// (<c>file_versions.content_hash</c>). This provider reads the blob's CONTENT at the commit and matches the
/// declared hash against BOTH the raw SHA-256 of that content and the git blob OID, so verification is
/// correct regardless of which domain the payload carried and no client change is required.
/// </summary>
public sealed class GitCliContentProvider : IGitContentProvider
{
    private readonly Func<string, string?> _checkoutResolver;
    private readonly string _gitExecutable;

    /// <summary>Upper bound on any single git invocation; a local blob read is near-instant.</summary>
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Ceiling on a single verified blob so a pathological/huge object cannot exhaust worker memory.</summary>
    private const long MaxBlobBytes = 512L * 1024 * 1024;

    /// <param name="checkoutResolver">
    /// Maps a repository remote URL to its local checkout directory, or null when none is provisioned.
    /// </param>
    /// <param name="gitExecutable">The git executable (overridable for tests); defaults to <c>git</c> on PATH.</param>
    public GitCliContentProvider(Func<string, string?> checkoutResolver, string gitExecutable = "git")
    {
        _checkoutResolver = checkoutResolver;
        _gitExecutable = gitExecutable;
    }

    /// <summary>
    /// Builds a provider that resolves each repository to its checkout under the service's persistent
    /// checkout volume, using the canonical <see cref="ServicePaths.RepoDirectoryName"/> mapping.
    /// </summary>
    public static GitCliContentProvider ForVolume(ServicePaths paths) =>
        new(url =>
        {
            var dir = Path.Combine(paths.CheckoutRoot, ServicePaths.RepoDirectoryName(url));
            return Directory.Exists(dir) ? dir : null;
        });

    public GitContentCheck VerifyBlob(
        string repositoryRemoteUrl, string commitSha, string repoRelativePath, string expectedBlobHash)
    {
        if (string.IsNullOrWhiteSpace(commitSha)
            || string.IsNullOrWhiteSpace(repoRelativePath)
            || string.IsNullOrWhiteSpace(expectedBlobHash))
            return GitContentCheck.Unavailable;

        // commitSha comes from an untrusted contribution manifest — the very supply-chain input this
        // verifier defends against. A git object id is hex; reject anything else so an attacker-controlled
        // value can never be parsed by git as an option/flag or an unexpected rev expression
        // (argument-injection hardening). We cannot verify against a non-oid "commit", so treat it as
        // Unavailable (the policy decides whether that is fatal).
        if (!IsHexObjectId(commitSha))
            return GitContentCheck.Unavailable;

        var checkoutDir = _checkoutResolver(repositoryRemoteUrl);
        if (string.IsNullOrEmpty(checkoutDir) || !Directory.Exists(checkoutDir))
            return GitContentCheck.Unavailable;

        // The commit itself must be present in this checkout; if it is not, we genuinely cannot verify
        // (Unavailable → the policy decides whether that is fatal), which is DIFFERENT from a commit we can
        // see but whose content disagrees (Mismatch → always fatal). '--end-of-options' guarantees the
        // (already hex-validated) commit id is never treated as an option (defense in depth).
        if (!TryRun(checkoutDir, out var commitType, out _, "cat-file", "-t", "--end-of-options", commitSha)
            || !string.Equals(commitType.Trim(), "commit", StringComparison.Ordinal))
            return GitContentCheck.Unavailable;

        // Repo paths are stored repo-relative with forward slashes; normalize defensively for git rev syntax.
        // The spec begins with the hex-validated commit id, so the whole token can never begin with '-'.
        var gitPath = repoRelativePath.Replace('\\', '/');
        var spec = $"{commitSha}:{gitPath}";

        // Read the blob CONTENT at the commit. A path absent at the commit means the declared file is not
        // part of the repository at that commit — an untrusted extra input — so it is a Mismatch, not
        // Unavailable (the commit WAS resolvable, the content simply is not there).
        if (!TryRunBytes(checkoutDir, out var content, "cat-file", "blob", "--end-of-options", spec))
            return GitContentCheck.Mismatch;

        var expected = expectedBlobHash.Trim();

        var rawSha256 = Convert.ToHexStringLower(SHA256.HashData(content));
        if (HashEquals(expected, rawSha256))
            return GitContentCheck.Match;

        // Fall back to the git blob OID domain (populated when the payload recorded git_blob_hash).
        if (TryRun(checkoutDir, out var oid, out _, "rev-parse", "--verify", "--end-of-options", spec)
            && HashEquals(expected, oid.Trim()))
            return GitContentCheck.Match;

        return GitContentCheck.Mismatch;
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

    private static bool HashEquals(string a, string b) =>
        a.Length == b.Length && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private bool TryRun(string workingDir, out string stdout, out string stderr, params string[] args)
    {
        if (TryRunBytes(workingDir, out var bytes, args, out stderr))
        {
            stdout = Encoding.UTF8.GetString(bytes);
            return true;
        }
        stdout = string.Empty;
        return false;
    }

    private bool TryRunBytes(string workingDir, out byte[] stdout, params string[] args) =>
        TryRunBytes(workingDir, out stdout, args, out _);

    private bool TryRunBytes(string workingDir, out byte[] stdout, string[] args, out string stderr)
    {
        stdout = [];
        stderr = string.Empty;
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
            foreach (var a in args)
                psi.ArgumentList.Add(a);

            process = Process.Start(psi);
            if (process is null)
                return false;

            // Close stdin immediately (EOF) so a git invocation that would otherwise read stdin — e.g. a
            // '--batch' style mode — can never wedge the worker waiting for input it will never get.
            process.StandardInput.Close();

            // Drain BOTH pipes asynchronously (so neither a full stdout nor a full stderr buffer can deadlock)
            // under a hard deadline, and cap stdout at MaxBlobBytes. Verifying a single blob in a local
            // checkout is near-instant, so a process still draining/running at the deadline is wedged or
            // pathological: cancel the drain, kill the whole process tree, and fail closed (Unavailable
            // upstream) rather than blocking the worker or buffering unbounded memory. The earlier version
            // copied stdout with a synchronous CopyTo BEFORE the timeout check, so a git process that left
            // stdout open could block forever — the async race below is what actually bounds it.
            using var cts = new CancellationTokenSource(GitTimeout);
            using var buffer = new MemoryStream();
            var stdoutTask = CopyBoundedAsync(process.StandardOutput.BaseStream, buffer, MaxBlobBytes, cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

            try
            {
                if (!process.WaitForExit((int)GitTimeout.TotalMilliseconds))
                {
                    cts.Cancel();
                    KillTree(process);
                    return false;
                }

                // The process has exited, so both pipes are at EOF and the drain tasks complete promptly.
                stdoutTask.GetAwaiter().GetResult();
                stderr = stderrTask.GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or IOException)
            {
                // Deadline hit mid-drain, or the blob exceeded MaxBlobBytes: kill and fail closed.
                cts.Cancel();
                KillTree(process);
                return false;
            }

            if (process.ExitCode != 0)
                return false;

            stdout = buffer.ToArray();
            return true;
        }
        catch
        {
            // git missing / not a repo / spawn failure ⇒ the provider cannot answer (Unavailable upstream).
            return false;
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static void KillTree(Process process)
    {
        try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
    }

    /// <summary>Copies <paramref name="source"/> into <paramref name="dest"/>, throwing once more than
    /// <paramref name="maxBytes"/> have been read so a pathological blob cannot exhaust memory.</summary>
    private static async Task CopyBoundedAsync(Stream source, Stream dest, long maxBytes, CancellationToken ct)
    {
        var buf = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > maxBytes)
                throw new InvalidOperationException("git blob exceeds the maximum verifiable size.");
            await dest.WriteAsync(buf.AsMemory(0, read), ct).ConfigureAwait(false);
        }
    }
}
