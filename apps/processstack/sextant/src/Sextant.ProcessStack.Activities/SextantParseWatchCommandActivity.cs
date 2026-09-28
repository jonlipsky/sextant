using System.Text;
using ProcessStack.Abstractions;

namespace Sextant.ProcessStack.Activities;

/// <summary>
/// Parses a chat command for the <c>configure-watched-repos</c> flow: <c>watch</c>/<c>unwatch</c> one or more
/// repositories (optionally on a branch), <c>list</c> the watches, or <c>help</c>. The command set is v1's.
/// Pure computation: repositories are shape-checked with the service's SVC-5 rules and returned normalized;
/// whether the workspace's GitHub connection can see them is the flow's check.
/// </summary>
[ActivityName(TypeName)]
[ActivityDescription("Parses a watch/unwatch/list/help chat command into a verb, normalized repositories and a branch (pure computation).")]
public sealed class SextantParseWatchCommandActivity : AbstractActivity
{
    /// <summary>The activity name flows use (<c>activity:SextantParseWatchCommand</c>).</summary>
    public const string TypeName = "SextantParseWatchCommand";

    public const string VerbWatch = "watch";
    public const string VerbUnwatch = "unwatch";
    public const string VerbList = "list";
    public const string VerbHelp = "help";

    public const int DefaultMaxRepositories = 10;
    public const int MaxRepositoriesLimit = 50;
    public const int MaxErrors = 10;
    public const int MaxEchoLength = 64;

    private static readonly string[] UnwatchPrefixes = ["stop watching ", "stop tracking ", "unwatch ", "untrack "];
    private static readonly string[] WatchPrefixes = ["start watching ", "watch ", "track "];
    private static readonly string[] ListPrefixes =
    [
        "what am i watching", "what am i tracking", "what repos am i watching", "list watched", "list my watched",
        "list my watches", "show my watched", "show watched",
    ];
    private static readonly string[] BranchMarkers = [" on branch ", " on "];

    [ActivityInput("prompt", Required = true, Description = "The chat message.")]
    public string? Prompt { get; set; }

    [ActivityInput("defaultHost", Description = "The host an owner/repo names (default github.com).", DefaultValue = RepositoryReference.DefaultHost)]
    public string? DefaultHost { get; set; } = RepositoryReference.DefaultHost;

    [ActivityInput("maxRepositories", Description = "At most this many repositories per command (default 10, at most 50).", DefaultValue = DefaultMaxRepositories)]
    public object? MaxRepositories { get; set; }

    [ActivityOutput("verb", Description = "watch | unwatch | list | help.")]
    public string Verb { get; set; } = VerbHelp;

    [ActivityOutput("repositories", Description = "The named repositories, de-duplicated: {input, host, owner, repo, repositoryKey, remoteUrl}.")]
    public List<object?> Repositories { get; set; } = [];

    [ActivityOutput("branch", Description = "The branch named with \"on\"/\"on branch\"; \"\" for the default branch.")]
    public string Branch { get; set; } = string.Empty;

    [ActivityOutput("errors", Description = "Human-readable problems with the command; empty when it parsed cleanly.")]
    public List<string> Errors { get; set; } = [];

    public SextantParseWatchCommandActivity() => Name = TypeName;

    public override Task ExecuteAsync(ActivityContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var text = Clean(ActivityValues.Text(ActivityValues.Input(this, "prompt", Prompt)));
        var host = ActivityValues.Text(ActivityValues.Input(this, "defaultHost", DefaultHost)).ToLowerInvariant();
        var maxRepositories = ActivityValues.Limit(
            ActivityValues.Input(this, "maxRepositories", MaxRepositories), DefaultMaxRepositories, MaxRepositoriesLimit);

        Verb = VerbHelp;
        Repositories = [];
        Branch = string.Empty;
        Errors = [];

        if (TryMatchPrefix(text, UnwatchPrefixes, out var rest))
            ParseTargets(VerbUnwatch, rest, host.Length > 0 ? host : RepositoryReference.DefaultHost, maxRepositories);
        else if (TryMatchPrefix(text, WatchPrefixes, out rest))
            ParseTargets(VerbWatch, rest, host.Length > 0 ? host : RepositoryReference.DefaultHost, maxRepositories);
        else if (ListPrefixes.Any(prefix => text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            Verb = VerbList;

        // Log the verb and counts only: the message itself stays in the chat.
        context.LogInfo($"{TypeName}: {Verb}, {Repositories.Count} repositor{(Repositories.Count == 1 ? "y" : "ies")}, {Errors.Count} error(s).");
        return Task.CompletedTask;
    }

    private void ParseTargets(string verb, string rest, string host, int maxRepositories)
    {
        Verb = verb;
        var (repositories, branch) = SplitBranch(ResolveSlackLinks(rest));
        if (branch is not null)
        {
            var name = GitRefs.StripHeadsPrefix(StripDecoration(branch));
            if (GitRefs.IsValidBranchName(name))
                Branch = name;
            else
                AddError(name.Length == 0 ? "Name the branch after \"on\"." : $"{Echo(name)} is not a valid branch name.");
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in Tokenize(repositories))
        {
            var verdict = RepositoryReference.Parse(token, host);
            if (!verdict.Ok)
            {
                AddError(DescribeRefusal(token, verdict.Reason));
                continue;
            }
            if (!keys.Add(verdict.Canonical))
                continue;
            if (Repositories.Count >= maxRepositories)
            {
                AddError($"At most {maxRepositories} repositories per command; the rest were left out.");
                break;
            }
            var repository = ActivityValues.NewMap();
            repository["input"] = token;
            repository["host"] = verdict.Host;
            repository["owner"] = verdict.SpelledOwner;
            repository["repo"] = verdict.SpelledRepo;
            repository["repositoryKey"] = verdict.Canonical;
            repository["remoteUrl"] = RepositoryReference.CloneUrlFor(verdict.Host, verdict.SpelledOwner, verdict.SpelledRepo);
            Repositories.Add(repository);
        }

        if (Repositories.Count == 0 && Errors.Count == 0)
            AddError($"Name a repository, for example: {verb} owner/repo on main.");
    }

    // Whitespace collapsed, leading mentions (`<@U123>`, `@sextant`) and trailing sentence punctuation removed.
    private static string Clean(string prompt)
    {
        var text = string.Join(' ', prompt.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        while (text.StartsWith('@') || text.StartsWith("<@", StringComparison.Ordinal))
        {
            var end = text[0] == '<' ? text.IndexOf('>') : text.IndexOf(' ');
            text = end < 0 ? string.Empty : text[(end + 1)..].TrimStart();
        }
        return text == "?" ? text : text.TrimEnd('.', '!', '?');
    }

    // Matches `prefix` at a word boundary (so a bare "watch" is a watch with no repository).
    private static bool TryMatchPrefix(string text, string[] prefixes, out string rest)
    {
        var padded = text + " ";
        foreach (var prefix in prefixes)
        {
            if (padded.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                rest = padded[prefix.Length..].Trim();
                return true;
            }
        }
        rest = string.Empty;
        return false;
    }

    // The last " on branch " (else the last " on ") splits the repositories from the branch.
    private static (string Repositories, string? Branch) SplitBranch(string rest)
    {
        var padded = " " + rest + " ";
        foreach (var marker in BranchMarkers)
        {
            var index = padded.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
                return (padded[..index].Trim(), padded[(index + marker.Length)..].Trim());
        }
        return (rest, null);
    }

    // Repository references separated by commas, whitespace, "and" or "&" (Slack escapes it as "&amp;").
    private static IEnumerable<string> Tokenize(string repositories)
    {
        foreach (var raw in repositories.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw.Equals("and", StringComparison.OrdinalIgnoreCase) || raw is "&" or "&amp;")
                continue;
            var token = StripDecoration(raw);
            if (token.Length > 0)
                yield return token;
        }
    }

    // Replaces every Slack link `<url>`/`<url|label>` with what it names, before the text is split (a label
    // may hold spaces or "on").
    private static string ResolveSlackLinks(string text)
    {
        var builder = new StringBuilder(text.Length);
        var index = 0;
        while (index < text.Length)
        {
            var open = text.IndexOf('<', index);
            var close = open < 0 ? -1 : text.IndexOf('>', open + 1);
            if (close < 0)
            {
                builder.Append(text, index, text.Length - index);
                break;
            }
            builder.Append(text, index, open - index).Append(SlackLinkTarget(text[(open + 1)..close]));
            index = close + 1;
        }
        return builder.ToString();
    }

    // A link names its url, except an auto-link: Slack turns a typed `github.com/o/r` into
    // `<http://github.com/o/r|github.com/o/r>`, so when the url is only a scheme plus the label, the label is
    // what the user wrote.
    private static string SlackLinkTarget(string link)
    {
        var bar = link.IndexOf('|');
        if (bar < 0)
            return link;
        var url = link[..bar];
        var label = link[(bar + 1)..];
        var autoLink = label.Length > 0
            && (url.Equals("http://" + label, StringComparison.OrdinalIgnoreCase)
                || url.Equals("https://" + label, StringComparison.OrdinalIgnoreCase));
        return autoLink ? label : url;
    }

    private static string StripDecoration(string token) =>
        token.Trim().TrimStart('"', '\'', '`', '<', '(', '[').TrimEnd('"', '\'', '`', '>', ')', ']', '.', ',', ';', ':', '!', '?');

    private static string DescribeRefusal(string token, string reason) => reason switch
    {
        RepositoryUrlShape.SchemeNotAllowed when token.StartsWith("http://", StringComparison.OrdinalIgnoreCase) =>
            "Only https repository URLs are supported.",
        RepositoryUrlShape.UrlComponentNotAllowed =>
            "A repository URL must not carry credentials, a port, a query or a fragment.",
        _ => $"{Echo(token)} is not a repository; use owner/repo, host/owner/repo or an https URL.",
    };

    // Quotes a user-supplied value back, truncated; anything with an '@' (possible credentials) is not echoed.
    private static string Echo(string value)
    {
        if (value.Contains('@'))
            return "That reference";
        return value.Length > MaxEchoLength ? $"'{value[..MaxEchoLength]}…'" : $"'{value}'";
    }

    private void AddError(string message)
    {
        if (Errors.Count < MaxErrors)
            Errors.Add(message);
    }
}
