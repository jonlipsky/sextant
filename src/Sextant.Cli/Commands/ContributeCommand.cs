using System.CommandLine;

namespace Sextant.Cli.Commands;

/// <summary>
/// <c>sextant contribute</c> — produce a deterministic semantic contribution for the current committed
/// source and either write it to a file or upload it to an index service (Phase 16). This is how an
/// environment that already possesses the required SDKs/workloads (a dev machine or, especially, CI after a
/// successful restore/build) does the repo/code indexing at compile time and hands a validated, immutable
/// contribution to the service, which authenticates, hash/capability-verifies, and publishes it.
///
/// Upload is OPT-IN and never required for a normal build: with no <c>--service</c> the command only
/// captures locally, and an unreachable service is non-blocking unless <c>--require</c> is set (the CI opt-in).
/// A dirty working tree is never uploaded — it is captured local-only and only with the explicit
/// <c>--allow-dirty</c> opt-in.
/// </summary>
internal static class ContributeCommand
{
    public static Command Build(Option<string?> dbOption, Option<string?> profileOption)
    {
        var solutionArg = new Argument<string>("solution-path") { Description = "Path to the .sln/.slnx to index" };

        var serviceOption = new Option<string?>("--service") { Description = "Index service base URL to upload to" };
        var outOption = new Option<string?>("--out", "-o") { Description = "Write the contribution artifact to this file instead of uploading" };
        var tokenOption = new Option<string?>("--token") { Description = "Contributor bearer token (or SEXTANT_CONTRIB_TOKEN)" };
        var tenantOption = new Option<string?>("--tenant") { Description = "Tenant/owner identity the contribution is published under" };
        var branchOption = new Option<string?>("--branch") { Description = "Branch to advance to the published snapshot (default: current)" };
        var defaultBranchOption = new Option<bool>("--default-branch") { Description = "Mark the branch as the repository default" };
        var allowDirtyOption = new Option<bool>("--allow-dirty") { Description = "Capture a dirty working tree (LOCAL-ONLY; never uploaded)" };
        var requireOption = new Option<bool>("--require") { Description = "Fail if the service is unavailable (CI opt-in; otherwise non-blocking)" };
        var noFinalizeOption = new Option<bool>("--no-finalize") { Description = "Upload without finalizing (multi-environment assembly)" };

        var command = new Command("contribute", "Produce and upload a semantic contribution for committed source")
        {
            solutionArg, serviceOption, outOption, tokenOption, tenantOption,
            branchOption, defaultBranchOption, allowDirtyOption, requireOption, noFinalizeOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
            await Handlers.ContributeHandler.RunAsync(new Handlers.ContributeOptions
            {
                SolutionPath = parseResult.GetValue(solutionArg)!,
                ServiceUrl = parseResult.GetValue(serviceOption),
                OutFile = parseResult.GetValue(outOption),
                Token = parseResult.GetValue(tokenOption),
                Tenant = parseResult.GetValue(tenantOption),
                Branch = parseResult.GetValue(branchOption),
                IsDefaultBranch = parseResult.GetValue(defaultBranchOption),
                AllowDirty = parseResult.GetValue(allowDirtyOption),
                Require = parseResult.GetValue(requireOption),
                Finalize = !parseResult.GetValue(noFinalizeOption),
                Db = parseResult.GetValue(dbOption),
                Profile = parseResult.GetValue(profileOption)
            }, cancellationToken));

        return command;
    }
}
