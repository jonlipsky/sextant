using System.Security.Cryptography;
using Sextant.Store;

namespace Sextant.Mcp;

/// <summary>
/// Query-time reproduction of reference source context (Phase 7 acceptance criterion 3). Phase 7 stops
/// storing a per-reference snippet string; a reference instead persists a (file_version, span) and the
/// snippet is reproduced on read ONLY from a byte-exact matching source. That source is the stored text of
/// the file's recorded <c>file_version</c> hash (<see cref="SourceTextStore"/>, issue #244: the service keeps
/// the bytes it indexed, so a published snapshot serves its own text even once the shared checkout has moved
/// to another commit), else the local file, read only when its current raw SHA-256 equals that hash — the
/// same hash the indexer computed (<see cref="FileStore.ComputeContentHash"/>: SHA-256 over the file's
/// bytes, no normalization). On any mismatch (file edited, moved, or absent, and no stored text) the snippet
/// is unavailable and null is returned, so callers keep the valid location + completeness metadata without a
/// stale or misleading snippet rather than reconstructing from drifted source.
/// </summary>
public sealed class SourceContextRetriever(FileStore files, SourceTextStore? texts = null)
{
    // Per-instance memo of verified source lines keyed by (project, file). A SourceContextRetriever is
    // created per query, so this caches for the lifetime of ONE request: a symbol referenced N times in
    // one file costs a single lookup + hash + read instead of N. A null value caches a miss.
    private readonly Dictionary<(long ProjectId, string FilePath), string[]?> _verifiedLines = [];

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

    /// <summary>
    /// The declaration's lines <paramref name="lineStart"/>..<paramref name="lineEnd"/> of the exact stored source
    /// version as <c>{start_line, end_line, lines[{line_number, content}]}</c>, or null when it is unavailable.
    /// </summary>
    public object? GetDeclaration(long projectId, string filePath, int lineStart, int lineEnd)
    {
        if (!TryReadVerifiedLines(projectId, filePath, out var lines))
            return null;
        var start = Math.Max(0, lineStart - 1);
        var end = Math.Min(lines.Length, lineEnd);
        return start > end ? null : LineBlock(lines, lineStart, lineEnd, start, end);
    }

    /// <summary>
    /// The lines within <paramref name="contextLines"/> above and below <paramref name="line"/> of the exact stored
    /// source version, in the <see cref="GetDeclaration"/> shape, or null when it is unavailable.
    /// </summary>
    public object? GetContextBlock(long projectId, string filePath, int line, int contextLines)
    {
        if (!TryReadVerifiedLines(projectId, filePath, out var lines))
            return null;
        var start = Math.Max(0, line - 1 - contextLines);
        var end = Math.Min(lines.Length, line + contextLines);
        return start > end ? null : LineBlock(lines, start + 1, end, start, end);
    }

    private static object LineBlock(string[] lines, int startLine, int endLine, int start, int end) => new
    {
        start_line = startLine,
        end_line = endLine,
        lines = lines[start..end]
            .Select((text, i) => new { line_number = start + i + 1, content = text })
            .ToList()
    };

    private bool TryReadVerifiedLines(long projectId, string filePath, out string[] lines)
    {
        if (_verifiedLines.TryGetValue((projectId, filePath), out var cached))
        {
            lines = cached ?? [];
            return cached != null;
        }

        var verified = ReadVerifiedLines(projectId, filePath);
        _verifiedLines[(projectId, filePath)] = verified;
        lines = verified ?? [];
        return verified != null;
    }

    private string[]? ReadVerifiedLines(long projectId, string filePath)
    {
        var stored = files.TryGetStoredContentHash(projectId, filePath);
        if (stored == null)
            return null;

        // The stored text is verified against the same hash, so it is exactly the indexed version whatever the
        // working tree now holds (issue #244).
        if (texts?.TryGet(stored) is { } indexed)
            return DecodeLines(indexed);

        // File.Exists first: a file missing at index time stores a placeholder hash that a still-missing
        // file would re-derive to; checking existence up front means the gate can never pass for a file
        // that is not physically present and readable.
        if (!File.Exists(filePath))
            return null;

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(filePath);
        }
        catch
        {
            return null;
        }

        // Hash and decode the SAME buffer: a second File.ReadAllLines could observe a concurrent edit
        // whose bytes were never verified, returning source that does not match the gated hash.
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), stored))
            return null;

        return DecodeLines(bytes);
    }

    private static string[] DecodeLines(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes), detectEncodingFromByteOrderMarks: true);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
            lines.Add(line);
        return [.. lines];
    }
}
