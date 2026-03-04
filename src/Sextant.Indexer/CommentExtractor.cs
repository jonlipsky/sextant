using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Sextant.Indexer;

public static class CommentExtractor
{
    private static readonly Regex TagPattern = new(
        @"//\s*(TODO|HACK|FIXME|BUG|NOTE|UNDONE)\b[:\s]*(.*)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex MultiLineTagPattern = new(
        @"/\*.*?(TODO|HACK|FIXME|BUG|NOTE|UNDONE)\b[:\s]*(.*?)(?:\*/|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    public static List<CommentEntry> ExtractComments(SyntaxTree syntaxTree)
    {
        var root = syntaxTree.GetRoot();
        var results = new List<CommentEntry>();

        foreach (var trivia in root.DescendantTrivia())
        {
            if (!trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) &&
                !trivia.IsKind(SyntaxKind.MultiLineCommentTrivia))
                continue;

            var text = trivia.ToString();
            var pattern = trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)
                ? TagPattern
                : MultiLineTagPattern;

            var match = pattern.Match(text);
            if (!match.Success) continue;

            var line = trivia.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            results.Add(new CommentEntry
            {
                FilePath = syntaxTree.FilePath,
                Line = line,
                Tag = match.Groups[1].Value.ToUpperInvariant(),
                Text = match.Groups[2].Value.Trim()
            });
        }

        return results;
    }
}

public sealed class CommentEntry
{
    public required string FilePath { get; init; }
    public int Line { get; init; }
    public required string Tag { get; init; }
    public required string Text { get; init; }
}
