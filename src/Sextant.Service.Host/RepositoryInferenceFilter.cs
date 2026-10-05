using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Sextant.Mcp;

namespace Sextant.Service.Host;

/// <summary>
/// Infers the repository of a symbol or path call that names none (<see cref="RepositoryInference"/>). It acts only on a
/// verified delegate caller's call of an inferable tool that named neither a repository (argument or header) nor a
/// branch, when the caller can read more than one repository (with exactly one, the SVC-4 implicit selection already
/// answers). It probes each repository the caller can read, at most <see cref="MaxRepositories"/> of them (with more,
/// it does nothing), and:
/// <list type="bullet">
///   <item>exactly one holds the argument: the call reads that repository (<see cref="ToolSelectionSource.Inferred"/>),
///   and the answer says so in <c>meta.snapshot.repository_selection: "inferred"</c>;</item>
///   <item>several hold it: the call fails with <c>repository_required</c>, listing only those repositories;</item>
///   <item>none holds it: the call goes on unchanged (the read then fails with <c>repository_required</c>).</item>
/// </list>
/// It never widens access. The candidates are the caller's own visible repositories
/// (<see cref="SnapshotService.ListSelectableRepositories"/>); each probe goes through the request's read authorizer
/// (<see cref="DatabaseProvider.ProbeRepositories"/>), so a repository the caller cannot read is never probed; the
/// final read is authorized again like any named read; and the error lists only repositories that passed both.
/// </summary>
internal static class RepositoryInferenceFilter
{
    /// <summary>
    /// The most repositories one call probes. A caller that can read more gets no inference and no probe at all,
    /// whatever their order: the call goes on unchanged, so it gets the ordinary <c>repository_required</c>.
    /// </summary>
    public const int MaxRepositories = 25;

    public static McpRequestFilter<CallToolRequestParams, CallToolResult> CallToolFilter(
        SnapshotService service, RepositoryUrlPolicy policy, RepositoryInferenceProbeCount probes)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(probes);
        return next => async (context, cancellationToken) =>
        {
            if (context.Params is not { Name: { } name } parameters || !RepositoryInference.Tools.ContainsKey(name))
                return await next(context, cancellationToken);
            var services = context.Services
                ?? throw new InvalidOperationException("Repository inference requires the request services.");
            var http = services.GetService<IHttpContextAccessor>()?.HttpContext
                ?? throw new InvalidOperationException("Repository inference requires the HTTP request.");
            if (ToolCallSelection.Get(http) is not { Source: ToolSelectionSource.None, Repository: null, Branch: null }
                || CallerRequest.Get(http) is not { IsDelegate: true, Principal: not null }
                || CallerVisibility.VisibleKeys(http, service) is not { Count: > 1 } keys
                || RepositoryInference.ProbeFor(name, parameters.Arguments) is not { } probe)
                return await next(context, cancellationToken);

            var candidates = service.ListSelectableRepositories(keys, MaxRepositories + 1);
            if (candidates.Count is < 2 or > MaxRepositories)
                return await next(context, cancellationToken);

            var provider = services.GetRequiredService<DatabaseProvider>();
            var holders = RepositoryInference.Holders(provider.ProbeRepositories(candidates, (db, read) =>
            {
                probes.Increment();
                return probe(db, read);
            }, cancellationToken));
            if (holders.Count == 1)
            {
                ToolCallSelection.Set(http, new ToolCallSelection(holders[0], null, ToolSelectionSource.Inferred));
                return await next(context, cancellationToken);
            }
            if (holders.Count > 1)
                return ToolSelectionFilters.ErrorResult(
                    ResponseBuilder.BuildRepositoryRequired(HoldersGuidance(name, holders, policy)));
            return await next(context, cancellationToken);
        };
    }

    /// <summary>
    /// The guidance of the <c>repository_required</c> error for an argument held by several repositories: those
    /// repositories, in the short form the <c>repository</c> argument accepts.
    /// </summary>
    internal static string HoldersGuidance(string tool, IReadOnlyList<string> holders, RepositoryUrlPolicy policy)
    {
        var names = holders.Select(url => ServiceApp.ShortRepositoryName(url, policy)).Distinct(StringComparer.Ordinal).ToList();
        var what = tool == "get_file_symbols" ? "This file" : "This symbol";
        return $"{what} is in {names.Count} repositories this caller can read: " +
               $"{string.Join(", ", names.Select(n => $"'{n}'"))}. Pass the 'repository' argument (e.g. '{names[0]}').";
    }
}

/// <summary>
/// How many repositories <see cref="RepositoryInferenceFilter"/> has looked an argument up in, over one app's
/// lifetime. A diagnostic: the tests read it to prove that a caller past the limit is never probed.
/// </summary>
internal sealed class RepositoryInferenceProbeCount
{
    private long _value;

    public long Value => Interlocked.Read(ref _value);

    public void Increment() => Interlocked.Increment(ref _value);
}
