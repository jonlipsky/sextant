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

        var checkoutDir = _checkoutResolver(repositoryRemoteUrl);
        if (string.IsNullOrEmpty(checkoutDir) || !Directory.Exists(checkoutDir))
            return GitContentCheck.Unavailable;

        // The commit itself must be present in this checkout; if it is not, we genuinely cannot verify
        // (Unavailable → the policy decides whether that is fatal), which is DIFFERENT from a commit we can
        // see but whose content disagrees (Mismatch → always fatal).
        if (!TryRun(checkoutDir, out var commitType, out _, "cat-file", "-t", commitSha)
            || !string.Equals(commitType.Trim(), "commit", StringComparison.Ordinal))
            return GitContentCheck.Unavailable;

        // Repo paths are stored repo-relative with forward slashes; normalize defensively for git rev syntax.
        var gitPath = repoRelativePath.Replace('\\', '/');
        var spec = $"{commitSha}:{gitPath}";

        // Read the blob CONTENT at the commit. A path absent at the commit means the declared file is not
        // part of the repository at that commit — an untrusted extra input — so it is a Mismatch, not
        // Unavailable (the commit WAS resolvable, the content simply is not there).
        if (!TryRunBytes(checkoutDir, out var content, "cat-file", "blob", spec))
            return GitContentCheck.Mismatch;

        var expected = expectedBlobHash.Trim();

        var rawSha256 = Convert.ToHexStringLower(SHA256.HashData(content));
        if (HashEquals(expected, rawSha256))
            return GitContentCheck.Match;

        // Fall back to the git blob OID domain (populated when the payload recorded git_blob_hash).
        if (TryRun(checkoutDir, out var oid, out _, "rev-parse", "--verify", spec)
            && HashEquals(expected, oid.Trim()))
            return GitContentCheck.Match;

        return GitContentCheck.Mismatch;
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
        try
        {
            var psi = new ProcessStartInfo(_gitExecutable)
            {
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var a in args)
                psi.ArgumentList.Add(a);

            using var process = Process.Start(psi);
            if (process is null)
                return false;

            // Drain stderr asynchronously so a large stdout blob can never deadlock against a full stderr
            // pipe buffer, then copy the raw stdout bytes (blob content is binary — never decode it here).
            var errorTask = process.StandardError.ReadToEndAsync();
            using var buffer = new MemoryStream();
            process.StandardOutput.BaseStream.CopyTo(buffer);
            stderr = errorTask.GetAwaiter().GetResult();
            process.WaitForExit();

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
    }
}
