using System.ComponentModel;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Sextant.Core;
using Sextant.Mcp;
using Sextant.Service.Grants;

namespace Sextant.Service.Search;

/// <summary>
/// <c>search_symbols</c> (SVC-F): a name-prefix symbol search across every repository the verified caller may read.
/// It is a service-only tool (the local stdio server never registers it). It resolves the caller's grants on every
/// call, so a revoked grant stops being searched on the next page, and it declares its own <c>repository</c> and
/// <c>branch</c> narrowing arguments, so it is exempt from the reserved SVC-2 selector arguments. It parses its raw
/// arguments itself (see <see cref="InputSchema"/>): a wrong type or an unknown argument is <c>invalid_arguments</c>.
/// The result is a text block and the same JSON as <c>structuredContent</c>. The text fits
/// <see cref="ServiceOptions.MaxResponseChars"/> (see <see cref="ResponseBudget"/>): a page that would not keeps fewer
/// symbols, says so in <c>meta.page_truncated_by</c> and <c>message</c>, and its <c>next_cursor</c> resumes at the first
/// symbol left out.
/// </summary>
[McpServerToolType]
public static class SearchSymbolsTool
{
    /// <summary>The tool's name.</summary>
    public const string ToolName = "search_symbols";

    internal const string InvalidArgumentsCode = "invalid_arguments";
    internal const string InvalidSelectorCode = "invalid_selector";
    internal const string InvalidCursorCode = "invalid_cursor";
    internal const string NoVisibleRepositoriesCode = "no_visible_repositories";

    /// <summary>The longest <c>name_prefix</c> accepted, in characters.</summary>
    internal const int MaxNamePrefixLength = 256;

    private const string NamePrefixArgument = "name_prefix";
    private const string RepositoryArgument = "repository";
    private const string BranchArgument = "branch";
    private const string KindArgument = "kind";
    private const string CursorArgument = "cursor";
    private const string LimitArgument = "limit";

    private const string CallerRequiredMessage =
        "search_symbols searches the repositories visible to a verified caller; send a caller assertion.";

    private const string NoVisibleRepositoriesMessage =
        "There is no repository you can search here. Use list_repositories to see the repositories you can query.";

    private const string InvalidCursorMessage =
        "The cursor is not a cursor this search issued to you. Pass next_cursor back unchanged, with the same arguments.";

    private static readonly Dictionary<string, SymbolKind> Kinds = Enum.GetValues<SymbolKind>()
        .ToDictionary(k => k.ToString().ToLowerInvariant(), StringComparer.Ordinal);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        // As the remote output pass writes it, so the size budget measures the text the client receives.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>The advertised <c>inputSchema</c>, applied to <c>tools/list</c> by <see cref="ListToolsFilter"/>.</summary>
    internal static JsonElement InputSchema { get; } = BuildInputSchema();

    [McpServerTool(Name = ToolName), Description(
        "Symbols by name prefix in every repository you can query. Use when you do not know which repository has a type.")]
    public static CallToolResult SearchSymbols(
        RequestContext<CallToolRequestParams> context, SnapshotService service, CallerContext caller, ServiceOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(options);
        if (caller.Current is not { } principal)
            return Error(ListRepositoriesTool.CallerRequiredCode, CallerRequiredMessage);

        var parsed = Parse(context.Params?.Arguments, options.RepositoryUrlPolicy);
        if (parsed.Error is { } error)
            return error;
        var (query, kindName, cursor) = (parsed.Query!, parsed.KindName, parsed.Cursor);

        var binding = SymbolSearchCursor.Binding(principal, query.NamePrefix, kindName, query.RepositoryKey, query.Branch);
        SymbolSearchCursorState? resume = null;
        if (cursor is not null
            && !SymbolSearchCursor.TryDecode(cursor, binding, ServiceOptions.SearchMaxWidthCeiling, out resume))
            return Error(InvalidCursorCode, InvalidCursorMessage);

        // The request's cancellation (a closed connection or notifications/cancelled) stops the search: it is checked
        // between statements and interrupts the one running (issue #196).
        var maxChars = ResponseBudget.Clamp(options.MaxResponseChars);
        var queriedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var outcome = service.SearchSymbols(principal, query, resume,
            fits: candidate => Render(candidate, binding, queriedAt, maxChars).GetRawText().Length <= maxChars,
            cancellationToken: cancellationToken);
        if (outcome.NoTargets)
            return Error(NoVisibleRepositoriesCode, NoVisibleRepositoriesMessage);

        var body = Render(outcome, binding, queriedAt, maxChars);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = body.GetRawText() }],
            StructuredContent = body
        };
    }

    private static JsonElement Render(SymbolSearchOutcome outcome, string binding, long queriedAt, int maxChars) =>
        JsonSerializer.SerializeToElement(new SearchSymbolsResponse
        {
            Symbols = outcome.Symbols,
            NextCursor = outcome.Next is { } next ? SymbolSearchCursor.Encode(next, binding) : null,
            Pending = outcome.Pending,
            Unavailable = outcome.Unavailable,
            Truncated = outcome.Truncated,
            Message = outcome.CutBySize ? SizeCutMessage(outcome.Symbols.Count, maxChars) : null,
            Meta = new MetaObject
            {
                QueriedAt = queriedAt,
                IndexFreshness = outcome.IndexFreshness,
                ResultCount = outcome.Symbols.Count,
                PageTruncatedBy = outcome.CutBySize ? ResponseBudget.SizeTruncation : null
            }
        }, JsonOptions);

    private static string SizeCutMessage(int symbols, int maxChars) => string.Create(CultureInfo.InvariantCulture,
        $"Page cut to {symbols} symbols to stay under {maxChars:N0} characters. Pass next_cursor for the rest, or narrow with repository, branch or kind.");

    /// <summary>
    /// Replaces <c>search_symbols</c>'s generated <c>inputSchema</c> in a <c>tools/list</c> result with
    /// <see cref="InputSchema"/>. The SDK's tool definitions are shared across requests, so the tool is cloned.
    /// </summary>
    public static McpRequestFilter<ListToolsRequestParams, ListToolsResult> ListToolsFilter() =>
        next => async (context, cancellationToken) =>
        {
            var result = await next(context, cancellationToken);
            result.Tools = result.Tools.Select(t => t.Name == ToolName ? WithInputSchema(t) : t).ToList();
            return result;
        };

    private static Tool WithInputSchema(Tool tool)
    {
        var clone = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(tool, McpJsonUtilities.DefaultOptions), typeof(Tool), McpJsonUtilities.DefaultOptions) as Tool
            ?? throw new InvalidOperationException($"The tool '{tool.Name}' could not be copied.");
        clone.InputSchema = InputSchema;
        return clone;
    }

    private static JsonElement BuildInputSchema()
    {
        var kinds = new JsonArray();
        foreach (var name in Kinds.Keys.Order(StringComparer.Ordinal))
            kinds.Add(name);
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                [NamePrefixArgument] = new JsonObject
                {
                    ["type"] = "string",
                    ["minLength"] = 1,
                    ["maxLength"] = MaxNamePrefixLength,
                    ["description"] = "Start of the symbol name."
                },
                [RepositoryArgument] = Property("string",
                    "Only this repository."),
                [BranchArgument] = Property("string",
                    "Only this branch."),
                [KindArgument] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = kinds,
                    ["description"] = "Only this kind."
                },
                [CursorArgument] = new JsonObject
                {
                    ["type"] = "string",
                    ["maxLength"] = SymbolSearchCursor.MaxLength,
                    ["description"] = "next_cursor"
                },
                [LimitArgument] = Property("integer",
                    $"Max {SymbolSearchQuery.MaxLimit} per snapshot.")
            },
            ["required"] = new JsonArray(NamePrefixArgument),
            ["additionalProperties"] = false
        };
        return JsonSerializer.SerializeToElement(schema);

        static JsonObject Property(string type, string description) =>
            new() { ["type"] = type, ["description"] = description };
    }

    private static ParsedArguments Parse(IDictionary<string, JsonElement>? arguments, RepositoryUrlPolicy policy)
    {
        arguments ??= new Dictionary<string, JsonElement>();
        foreach (var name in arguments.Keys)
        {
            if (name is not (NamePrefixArgument or RepositoryArgument or BranchArgument or KindArgument
                or CursorArgument or LimitArgument))
                return Invalid($"Unknown argument '{Printable(name)}'.");
        }

        if (!TryString(arguments, NamePrefixArgument, out var prefix) || prefix is null)
            return Invalid($"'{NamePrefixArgument}' is required and must be a non-blank string.");
        if (prefix.Length > MaxNamePrefixLength)
            return Invalid($"'{NamePrefixArgument}' must be at most {MaxNamePrefixLength} characters.");
        if (!TryString(arguments, RepositoryArgument, out var repository)
            || !TryString(arguments, BranchArgument, out var branch)
            || !TryString(arguments, KindArgument, out var kind)
            || !TryString(arguments, CursorArgument, out var cursor))
            return Invalid($"'{RepositoryArgument}', '{BranchArgument}', '{KindArgument}' and '{CursorArgument}' must be strings.");

        string? repositoryKey = null;
        if (repository is not null)
        {
            var decision = policy.EvaluateSelector(repository);
            if (!decision.Ok)
                return new ParsedArguments(null, null, null, Error(InvalidSelectorCode,
                    $"The repository selector is not an accepted repository (reason: {decision.Reason})."));
            repositoryKey = RepositoryGrantKey.Of(decision.Canonical);
        }

        SymbolKind? symbolKind = null;
        if (kind is not null)
        {
            if (!Kinds.TryGetValue(kind.ToLowerInvariant(), out var parsedKind))
                return Invalid($"'{KindArgument}' must be one of: {string.Join(", ", Kinds.Keys.Order(StringComparer.Ordinal))}.");
            symbolKind = parsedKind;
            kind = kind.ToLowerInvariant();
        }

        if (!TryLimit(arguments, out var limit))
            return Invalid($"'{LimitArgument}' must be an integer.");

        var query = new SymbolSearchQuery
        {
            NamePrefix = prefix,
            RepositoryKey = repositoryKey,
            Branch = branch,
            Kind = symbolKind,
            Limit = limit
        };
        return new ParsedArguments(query, kind, cursor, null);
    }

    // False when the argument is present but neither a string nor null; the value is trimmed, and null when absent,
    // null or blank.
    private static bool TryString(IDictionary<string, JsonElement> arguments, string name, out string? value)
    {
        value = null;
        if (!arguments.TryGetValue(name, out var element) || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return true;
        if (element.ValueKind != JsonValueKind.String)
            return false;
        var text = element.GetString();
        value = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        return true;
    }

    private static bool TryLimit(IDictionary<string, JsonElement> arguments, out int limit)
    {
        limit = SymbolSearchQuery.DefaultLimit;
        if (!arguments.TryGetValue(LimitArgument, out var element) || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return true;
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out var value))
            return false;
        limit = (int)Math.Clamp(value, 1L, SymbolSearchQuery.MaxLimit);
        return true;
    }

    // An argument name echoed in an error, cut to a safe length and shape.
    private static string Printable(string name) =>
        name.Length <= 64 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.') ? name : "?";

    private static ParsedArguments Invalid(string message) =>
        new(null, null, null, Error(InvalidArgumentsCode, message));

    private static CallToolResult Error(string code, string message) =>
        new() { IsError = true, Content = [new TextContentBlock { Text = ResponseBuilder.BuildError(code, message) }] };

    private sealed record ParsedArguments(SymbolSearchQuery? Query, string? KindName, string? Cursor, CallToolResult? Error);

    private sealed record SearchSymbolsResponse
    {
        public required IReadOnlyList<SymbolSearchHit> Symbols { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public string? NextCursor { get; init; }

        public required IReadOnlyList<SymbolSearchTarget> Pending { get; init; }
        public required IReadOnlyList<SymbolSearchTarget> Unavailable { get; init; }
        public required IReadOnlyList<SymbolSearchTarget> Truncated { get; init; }
        public string? Message { get; init; }
        public required MetaObject Meta { get; init; }
    }
}
