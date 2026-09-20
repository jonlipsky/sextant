using System.Security.Cryptography;
using System.Text;

namespace Sextant.Core;

public static class CanonicalIdGenerator
{
    /// <summary>
    /// Computes the logical project identity hash. Each evaluated target framework of a
    /// multi-targeted project is a distinct logical project, so the evaluated TFM is a first-class
    /// component of the identity alongside the normalized git remote URL and the repo-relative path.
    /// An unknown/absent framework is normalized to the empty string so it hashes deterministically
    /// (never null-keyed).
    /// </summary>
    public static string Generate(string normalizedGitRemoteUrl, string repoRelativePath, string targetFramework)
    {
        var input = $"{normalizedGitRemoteUrl}|{repoRelativePath}|{targetFramework ?? string.Empty}";
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(hashBytes)[..16];
    }
}
