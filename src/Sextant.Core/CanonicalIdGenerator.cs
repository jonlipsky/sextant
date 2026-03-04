using System.Security.Cryptography;
using System.Text;

namespace Sextant.Core;

public static class CanonicalIdGenerator
{
    public static string Generate(string normalizedGitRemoteUrl, string repoRelativePath)
    {
        var input = $"{normalizedGitRemoteUrl}|{repoRelativePath}";
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(hashBytes)[..16];
    }
}
