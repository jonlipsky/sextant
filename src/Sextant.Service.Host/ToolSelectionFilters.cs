using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Sextant.Core;
using Sextant.Mcp;

namespace Sextant.Service.Host;

/// <summary>Where a <see cref="ToolCallSelection"/>'s repository came from.</summary>
internal enum ToolSelectionSource
{
    /// <summary>The call named no repository: the read uses the unselected default (or fails when one is required).</summary>
    None,

    /// <summary>The <c>X-Sextant-Repository</c> request header, as sent.</summary>
    Header,

    /// <summary>The reserved <c>repository</c> tool argument, canonicalized by the SVC-5 URL policy.</summary>
    Argument
}

/// <summary>
/// The repository/branch selection of ONE <c>tools/call</c> on the service's <c>/mcp</c> (SVC-2), resolved by
/// <see cref="ToolSelectionFilters"/> before the tool runs and kept in <see cref="HttpContext.Items"/>. The
/// stateless transport carries one JSON-RPC message per HTTP request, so the request's items belong to exactly
/// one call. The <see cref="DatabaseProvider"/> accessors read it, and later per-call checks (caller
/// verification, grants) can reuse it instead of re-parsing arguments.
/// </summary>
/// <param name="Repository">The effective repository selector, or null when the call names none.</param>
/// <param name="Branch">The named branch, or null for the repository's default branch.</param>
/// <param name="Source">Where <paramref name="Repository"/> came from.</param>
internal sealed record ToolCallSelection(string? Repository, string? Branch, ToolSelectionSource Source)
{
    private static readonly object ItemsKey = new();

    /// <summary>The selection recorded for the current request, or null when none was recorded.</summary>
    public static ToolCallSelection? Get(HttpContext? http) =>
        http is not null && http.Items.TryGetValue(ItemsKey, out var value) ? value as ToolCallSelection : null;

    internal static void Set(HttpContext http, ToolCallSelection selection) => http.Items[ItemsKey] = selection;
}

/// <summary>
/// SVC-2: per-call repository and branch selection through reserved tool arguments on the service's
/// stateless <c>/mcp</c>. A client that forwards tool arguments verbatim over a pooled connection with only
/// static headers can still name the repository (and branch) each call reads.
/// <list type="bullet">
///   <item><c>tools/list</c>: every repository-scoped tool advertises two optional string arguments,
///   <c>repository</c> and <c>branch</c>, except any name the tool already declares itself.</item>
///   <item><c>tools/call</c>: the reserved arguments are REMOVED before the tool binds its own arguments,
///   <c>repository</c> is canonicalized with the SVC-5 <see cref="RepositoryUrlPolicy"/>, and the result is
///   stored as the call's <see cref="ToolCallSelection"/>. The argument takes precedence over the
///   <c>X-Sextant-Repository</c> header; when both are sent and name different repositories the call fails
///   with <see cref="SelectorConflictCode"/>.</item>
/// </list>
/// The filters never change the target tool (that would re-run the SDK's authorization). They only decide
/// the selection: authorization, readiness and the not-found behavior stay in
/// <see cref="DatabaseProvider.TryBeginRead"/>, so a named branch without a repository fails there with
/// <c>repository_required</c> and a miss collapses to the existing uniform not-found. The local stdio server
/// registers no filters, so its tool list and calls are unchanged.
/// </summary>
internal static class ToolSelectionFilters
{
    /// <summary>The reserved tool argument naming the repository a call reads.</summary>
    public const string RepositoryArgument = "repository";

    /// <summary>The reserved tool argument naming the branch a call reads.</summary>
    public const string BranchArgument = "branch";

    /// <summary>The error code for a <c>repository</c> argument that names a different repository than the header.</summary>
    public const string SelectorConflictCode = "selector_conflict";

    /// <summary>The error code for a reserved argument that is not a string, or a repository the URL policy refuses.</summary>
    public const string InvalidSelectorCode = "invalid_selector";

    /// <summary>The reason code for an <c>owner/repo</c> short form when the policy has no single host to expand it with.</summary>
    public const string ShortFormHostRequired = RepositoryUrlRejection.HostRequired;

    /// <summary>
    /// Tools on the remote surface that are NOT repository-scoped, so the reserved arguments are neither advertised
    /// nor stripped for them: <c>list_repositories</c> (SVC-4) reads the caller's grants, not one index, and
    /// <c>search_symbols</c> (SVC-F) searches every visible repository and declares its own <c>repository</c> and
    /// <c>branch</c> narrowing arguments.
    /// </summary>
    internal static readonly IReadOnlySet<string> SelectionExemptTools =
        new HashSet<string>(StringComparer.Ordinal) { "list_repositories", "search_symbols" };

    private static readonly string[] ReservedArguments = [RepositoryArgument, BranchArgument];

    /// <summary>
    /// The names of every repository-scoped tool among <paramref name="toolTypes"/>: each
    /// <see cref="McpServerToolAttribute"/> method's tool name, minus <see cref="SelectionExemptTools"/>.
    /// </summary>
    public static IReadOnlySet<string> RepositoryScopedToolNames(IEnumerable<Type> toolTypes)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in toolTypes)
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
            {
                if (method.GetCustomAttribute<McpServerToolAttribute>() is not { } attribute)
                    continue;
                var name = attribute.Name ?? method.Name;
                if (!SelectionExemptTools.Contains(name))
                    names.Add(name);
            }
        }
        return names;
    }

    /// <summary>
    /// Adds the reserved arguments to every repository-scoped tool's <c>inputSchema</c> in a <c>tools/list</c>
    /// result. The SDK's tool definitions are shared across requests, so each one is cloned, never mutated.
    /// <paramref name="selectionRequired"/> (the operator switch) makes the description say it is required, and
    /// <paramref name="implicitSelection"/> (delegate callers) names the one exception: a caller that can read
    /// exactly one repository may omit it.
    /// </summary>
    public static McpRequestFilter<ListToolsRequestParams, ListToolsResult> ListToolsFilter(
        RepositoryUrlPolicy policy, IReadOnlySet<string> scopedTools, bool selectionRequired = false,
        bool implicitSelection = false)
    {
        var repositoryDescription = RepositoryDescription(policy, selectionRequired, implicitSelection);
        return next => async (context, cancellationToken) =>
        {
            var result = await next(context, cancellationToken);
            result.Tools = result.Tools
                .Select(tool => scopedTools.Contains(tool.Name) ? WithReservedArguments(tool, repositoryDescription) : tool)
                .ToList();
            return result;
        };
    }

    /// <summary>
    /// Strips and resolves a repository-scoped call's reserved arguments into its <see cref="ToolCallSelection"/>
    /// before the tool runs, or answers the call with a tool error when the selection is malformed or
    /// conflicts with the header.
    /// </summary>
    public static McpRequestFilter<CallToolRequestParams, CallToolResult> CallToolFilter(
        RepositoryUrlPolicy policy, IReadOnlySet<string> scopedTools)
    {
        var reservedByTool = new ConcurrentDictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        return next => async (context, cancellationToken) =>
        {
            var name = context.Params?.Name;
            if (name is null || !scopedTools.Contains(name))
                return await next(context, cancellationToken);

            // The SDK matches the target tool before any filter runs. A repository-scoped call without that
            // match cannot be told apart from its reserved arguments, so it fails closed instead of reading the
            // unselected default.
            if (context.MatchedPrimitive is not McpServerTool tool)
                throw new InvalidOperationException($"The tool '{name}' was not matched before selection.");
            var services = context.Services
                ?? throw new InvalidOperationException("Per-call selection requires the request services.");
            var http = services.GetService<IHttpContextAccessor>()?.HttpContext
                ?? throw new InvalidOperationException("Per-call selection requires the HTTP request.");

            var reserved = reservedByTool.GetOrAdd(name, _ => ReservedArgumentsFor(tool.ProtocolTool));
            var outcome = Resolve(context.Params!.Arguments, reserved, ServiceApp.RepositoryHeaderValue(http), policy);
            if (outcome.Error is { } error)
                return ErrorResult(error);

            ToolCallSelection.Set(http, outcome.Selection!);
            return await next(context, cancellationToken);
        };
    }

    /// <summary>The outcome of <see cref="Resolve"/>: a selection, or the error the call is answered with.</summary>
    internal readonly record struct SelectionOutcome(ToolCallSelection? Selection, string? Error);

    /// <summary>
    /// Resolves one call's selection. Removes the <paramref name="reserved"/> arguments from
    /// <paramref name="arguments"/> (in place, so the tool never binds them), canonicalizes a
    /// <c>repository</c> argument, and applies the precedence argument &gt; <paramref name="header"/>. A JSON
    /// <c>null</c> or blank value counts as absent.
    /// </summary>
    internal static SelectionOutcome Resolve(
        IDictionary<string, JsonElement>? arguments,
        IReadOnlySet<string> reserved,
        string? header,
        RepositoryUrlPolicy policy)
    {
        if (!TryTake(arguments, reserved, RepositoryArgument, out var repositoryArgument)
            || !TryTake(arguments, reserved, BranchArgument, out var branch))
            return Fail(InvalidSelectorCode, "The repository and branch selectors must be strings.");

        if (repositoryArgument is not null)
        {
            var decision = EvaluateRepository(repositoryArgument, policy);
            if (!decision.Ok)
                return Fail(InvalidSelectorCode,
                    $"The repository selector is not an accepted repository (reason: {decision.Reason}).");

            if (header is not null
                && !string.Equals(RemoteUrlIdentity.Normalize(header), decision.Canonical, StringComparison.Ordinal))
                return Fail(SelectorConflictCode,
                    $"The '{RepositoryArgument}' argument and the {ServiceApp.RepositoryHeader} header name different " +
                    "repositories. Send one of them, or make them name the same repository.");

            return new SelectionOutcome(
                new ToolCallSelection(decision.Canonical, branch, ToolSelectionSource.Argument), null);
        }

        if (header is not null)
            return new SelectionOutcome(new ToolCallSelection(header, branch, ToolSelectionSource.Header), null);

        return new SelectionOutcome(new ToolCallSelection(null, branch, ToolSelectionSource.None), null);
    }

    /// <summary>
    /// Evaluates a <c>repository</c> selector with the SVC-5 policy, accepting the short forms
    /// <c>host/owner/repo</c> and, when the policy allow-lists exactly one explicit host, <c>owner/repo</c>.
    /// </summary>
    internal static RepositoryUrlDecision EvaluateRepository(string selector, RepositoryUrlPolicy policy) =>
        policy.EvaluateSelector(selector);

    // Removes `key` from the call's arguments when it is reserved for this tool. False when it is present but
    // is neither a string nor null; `value` is the trimmed string, or null when absent/null/blank.
    private static bool TryTake(
        IDictionary<string, JsonElement>? arguments, IReadOnlySet<string> reserved, string key, out string? value)
    {
        value = null;
        if (arguments is null || !reserved.Contains(key) || !arguments.Remove(key, out var element))
            return true;
        switch (element.ValueKind)
        {
            case JsonValueKind.Null or JsonValueKind.Undefined:
                return true;
            case JsonValueKind.String:
                value = Blank(element.GetString());
                return true;
            default:
                return false;
        }
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // The reserved arguments a tool gets: both, minus any name its own schema already declares (so a tool whose
    // own `branch` argument means something else keeps that meaning).
    private static IReadOnlySet<string> ReservedArgumentsFor(Tool tool)
    {
        var declared = DeclaredProperties(tool.InputSchema);
        return ReservedArguments.Where(a => !declared.Contains(a)).ToHashSet(StringComparer.Ordinal);
    }

    private static HashSet<string> DeclaredProperties(JsonElement schema)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (schema.ValueKind == JsonValueKind.Object
            && schema.TryGetProperty("properties", out var properties)
            && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in properties.EnumerateObject())
                names.Add(property.Name);
        }
        return names;
    }

    private static Tool WithReservedArguments(Tool tool, string repositoryDescription)
    {
        var declared = DeclaredProperties(tool.InputSchema);
        if (ReservedArguments.All(declared.Contains))
            return tool;

        var schema = JsonNode.Parse(tool.InputSchema.GetRawText())!.AsObject();
        if (schema["properties"] is not JsonObject properties)
        {
            properties = [];
            schema["properties"] = properties;
        }
        if (!declared.Contains(RepositoryArgument))
            properties[RepositoryArgument] = StringProperty(repositoryDescription);
        // No description: an optional `branch` explains itself (omitted = the default branch), and this saves a
        // line on each of the scoped tools (issue #145). The repository description already says it is required.
        if (!declared.Contains(BranchArgument))
            properties[BranchArgument] = new JsonObject { ["type"] = "string" };

        var clone = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(tool, McpJsonUtilities.DefaultOptions), typeof(Tool), McpJsonUtilities.DefaultOptions) as Tool
            ?? throw new InvalidOperationException($"The tool '{tool.Name}' could not be copied.");
        clone.InputSchema = JsonSerializer.SerializeToElement(schema, McpJsonUtilities.DefaultOptions);
        return clone;
    }

    private static JsonObject StringProperty(string description) =>
        new() { ["type"] = "string", ["description"] = description };

    /// <summary>
    /// The advertised description of the reserved <c>repository</c> argument. It never says "Optional": it states
    /// the requirement when the operator requires a selection (<paramref name="selectionRequired"/>), and, when
    /// callers are verified (<paramref name="implicitSelection"/>), the one case in which a verified caller may omit it.
    /// </summary>
    internal static string RepositoryDescription(RepositoryUrlPolicy policy, bool selectionRequired, bool implicitSelection)
    {
        // Kept to one short line: it is repeated on every repository-scoped tool (issue #145). The forms and the
        // header default are spelled out once, in the server instructions and docs/service.md. A client may drop
        // those instructions, so the requirement itself is stated here. Implicit selection (delegate callers) lets
        // a verified caller that can read exactly one repository omit it, whether or not the operator switch is on.
        var form = policy.Hosts.Count == 1 ? "owner/repo" : "host/owner/repo";
        if (implicitSelection)
            return "Required unless exactly one is granted: " + form;
        return selectionRequired ? "Required: " + form : form;
    }

    private static SelectionOutcome Fail(string code, string message) =>
        new(null, ResponseBuilder.BuildError(code, message));

    private static CallToolResult ErrorResult(string body) =>
        new() { IsError = true, Content = [new TextContentBlock { Text = body }] };
}
