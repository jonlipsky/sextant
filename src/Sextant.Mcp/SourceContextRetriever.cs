using System.Security.Cryptography;
using Sextant.Store;

namespace Sextant.Mcp;

/// <summary>
/// Query-time reproduction of reference source context (Phase 7 acceptance criterion 3). Phase 7 stops
/// storing a per-reference snippet string; a reference instead persists a (file_version, span) and the
/// snippet is reproduced on read ONLY from a byte-exact matching source. The local file is read only
/// when its current raw SHA-256 equals the hash recorded on the file's stored <c>file_version</c> — the
/// same hash the indexer computed (<see cref="FileStore.ComputeContentHash"/>: SHA-256 over the file's
/// bytes, no normalization). On any mismatch (file edited, moved, or absent) the snippet is unavailable
/// and null is returned, so callers keep the valid location + completeness metadata without a stale or
/// misleading snippet rather than reconstructing from drifted source.
/// </summary>
public sealed class SourceContextRetriever(FileStore files)
{
    /// <summary>The single trimmed source line at <paramref name="line"/>, or null when unavailable.</summary>
    public string? GetLineSnippet(long projectId, string filePath, int line)
    {
        if (!TryReadVerifiedLines(projectId, filePath, out var lines) || line < 1 || line > lines.Length)
            return null;
        return lines[line - 1].Trim();
    }

    /// <summary>
    /// The source lines within <paramref name="contextLines"/> above and below <paramref name="line"/>,
    /// joined with newlines, or null when the exact stored source version is unavailable.
    /// </summary>
    public string? GetContext(long projectId, string filePath, int line, int contextLines)
    {
        if (!TryReadVerifiedLines(projectId, filePath, out var lines))
            return null;
        var start = Math.Max(0, line - 1 - contextLines);
        var end = Math.Min(lines.Length - 1, line - 1 + contextLines);
        if (start > end)
            return null;
        return string.Join("\n", lines[start..(end + 1)]);
    }

    private bool TryReadVerifiedLines(long projectId, string filePath, out string[] lines)
    {
        lines = [];

        // File.Exists first: a file missing at index time stores a placeholder hash that a still-missing
        // file would re-derive to; checking existence up front means the gate can never pass for a file
        // that is not physically present and readable.
        if (!File.Exists(filePath))
            return false;

        var stored = files.TryGetStoredContentHash(projectId, filePath);
        if (stored == null)
            return false;

        byte[] actual;
        try
        {
            actual = SHA256.HashData(File.ReadAllBytes(filePath));
        }
        catch
        {
            return false;
        }

        if (!CryptographicOperations.FixedTimeEquals(actual, stored))
            return false;

        try
        {
            lines = File.ReadAllLines(filePath);
        }
        catch
        {
            return false;
        }
        return true;
    }
}
