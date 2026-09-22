using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Sextant.Core;

/// <summary>
/// A stable fingerprint of the toolchain that produced an index, folded into a Phase-9 snapshot
/// identity (see <see cref="SnapshotIdentity"/>). Two indexes built by a materially different runtime
/// — a different OS, architecture, or .NET runtime version — must not be treated as interchangeable
/// for reuse, so the fingerprint captures the coarse toolchain surface without being so specific that
/// it needlessly invalidates on every patch. It is deterministic for a given runtime and independent
/// of machine name, paths, and culture.
/// </summary>
public static class ToolchainFingerprint
{
    /// <summary>
    /// The fingerprint of the current runtime: a SHA-256 (hex) over the OS platform + architecture +
    /// the .NET framework description. Stable across runs on the same toolchain, distinct across
    /// materially different ones.
    /// </summary>
    public static string Current { get; } = Compute();

    private static string Compute()
    {
        var canonical = string.Join(
            ';',
            "v=1",
            $"os={RuntimeInformation.OSDescription}",
            $"arch={RuntimeInformation.OSArchitecture}",
            $"framework={RuntimeInformation.FrameworkDescription}");
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexStringLower(bytes);
    }
}
