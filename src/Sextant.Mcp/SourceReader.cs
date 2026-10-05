namespace Sextant.Mcp;

internal static class SourceReader
{
    public static object? ReadContext(string filePath, int targetLine, int contextLines = 3)
    {
        if (!File.Exists(filePath)) return null;
        var allLines = File.ReadAllLines(filePath);
        var start = Math.Max(0, targetLine - 1 - contextLines);
        var end = Math.Min(allLines.Length, targetLine + contextLines);
        return new
        {
            start_line = start + 1,
            end_line = end,
            lines = allLines[start..end]
                .Select((text, i) => new { line_number = start + i + 1, content = text })
                .ToList()
        };
    }
}
