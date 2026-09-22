using System.Reflection;
using Sextant.Core;
using Sextant.Core.Platform;
using Sextant.Indexer;
using Sextant.Service.Contributions;

namespace Sextant.Cli.Handlers;

/// <summary>Parsed <c>sextant contribute</c> options.</summary>
internal sealed record ContributeOptions
{
    public required string SolutionPath { get; init; }
    public string? ServiceUrl { get; init; }
    public string? OutFile { get; init; }
    public string? Token { get; init; }
    public string? Tenant { get; init; }
    public string? Branch { get; init; }
    public bool IsDefaultBranch { get; init; }
    public bool AllowDirty { get; init; }
    public bool Require { get; init; }
    public bool Finalize { get; init; } = true;
    public string? Db { get; init; }
    public string? Profile { get; init; }
}

/// <summary>Where a contribution run should send its artifact.</summary>
internal enum ContributionDestination
{
    Error,
    WriteLocal,
    Upload
}

/// <summary>The resolved decision for a contribution run (pure, so it is unit-testable without git/MSBuild).</summary>
internal sealed record ContributionCliDecision
{
    public required ContributionDestination Destination { get; init; }
    public string? Error { get; init; }

    public static ContributionCliDecision Fail(string error) =>
        new() { Destination = ContributionDestination.Error, Error = error };
}

/// <summary>
/// The PURE pre-capture decision for a contribution run. It encodes the two service-optional / dirty-tree
/// safety rules that must hold regardless of environment (acceptance criteria 5 and 6):
/// <list type="bullet">
///   <item>A contribution requires a committed git checkout (no root → error).</item>
///   <item>Cleanliness that could NOT be determined (git status failed, <c>isDirty == null</c>) is fatal for
///   an upload — the tree is never assumed clean — and is treated conservatively as DIRTY for a local write.</item>
///   <item>A DIRTY working tree is NEVER uploaded: without <c>--allow-dirty</c> it is an error, and with it
///   the artifact may only be written locally (<c>--out</c>), never sent to a service.</item>
///   <item>A clean tree uploads when a service is given, writes locally when only <c>--out</c> is given,
///   and errors when neither destination is specified.</item>
/// </list>
/// </summary>
internal static class ContributionCliPlan
{
    public static ContributionCliDecision Decide(bool hasGitRoot, bool? isDirty, ContributeOptions options)
    {
        if (!hasGitRoot)
            return ContributionCliDecision.Fail(
                "a contribution requires a committed git checkout; no .git root was found for the solution.");

        var hasOut = !string.IsNullOrWhiteSpace(options.OutFile);
        var hasService = !string.IsNullOrWhiteSpace(options.ServiceUrl);

        // Cleanliness unknown (git status could not be run). Never upload a tree we cannot prove is clean
        // (criterion 6); for a local write treat it conservatively as dirty so the artifact is stamped
        // dirty (local-only) rather than falsely clean.
        var dirty = isDirty ?? true;
        if (isDirty is null && hasService)
            return ContributionCliDecision.Fail(
                "could not determine whether the working tree is clean (git status failed); refusing to upload. Commit and retry, or write a local artifact with --out.");

        if (dirty && !options.AllowDirty)
            return ContributionCliDecision.Fail(
                "the working tree is dirty; commit your changes, or pass --allow-dirty to capture a LOCAL-ONLY artifact.");

        if (dirty)
        {
            // --allow-dirty (or unknown-cleanliness): dirty content stays local. Uploading it is forbidden (criterion 6).
            if (hasService)
                return ContributionCliDecision.Fail(
                    "a dirty working tree is never uploaded; --allow-dirty writes a local artifact only, so specify --out instead of --service.");
            if (!hasOut)
                return ContributionCliDecision.Fail("--allow-dirty requires --out (a dirty contribution is local-only).");
            return new ContributionCliDecision { Destination = ContributionDestination.WriteLocal };
        }

        if (hasService)
            return new ContributionCliDecision { Destination = ContributionDestination.Upload };
        if (hasOut)
            return new ContributionCliDecision { Destination = ContributionDestination.WriteLocal };

        return ContributionCliDecision.Fail("specify a destination: --service <url> to upload, or --out <file> to write locally.");
    }
}

/// <summary>Runs the <c>sextant contribute</c> command: resolve git facts, capture, then write or upload.</summary>
internal static class ContributeHandler
{
    public static async Task<int> RunAsync(ContributeOptions options, CancellationToken cancellationToken)
    {
        var solutionPath = Path.GetFullPath(options.SolutionPath);
        if (!File.Exists(solutionPath))
        {
            Console.Error.WriteLine($"Solution file not found: {solutionPath}");
            return 1;
        }

        var gitRoot = GitRemoteResolver.ResolveGitRoot(solutionPath);
        var changeSet = gitRoot is null ? null : GitChangeProvider.TryGetChangeSet(gitRoot);
        // null = cleanliness could not be determined (git status failed). Decide refuses to upload in that
        // case (criterion 6); resolve it conservatively to dirty for the artifact's provenance stamp.
        bool? isDirtyKnown = changeSet is null ? null : !changeSet.IsClean;

        var decision = ContributionCliPlan.Decide(gitRoot is not null, isDirtyKnown, options);
        if (decision.Destination == ContributionDestination.Error)
        {
            Console.Error.WriteLine(decision.Error);
            return 1;
        }

        var isDirty = isDirtyKnown ?? true;

        if (!TryResolveGitFacts(gitRoot!, out var commitSha, out var treeSha, out var currentBranch, out var gitError))
        {
            Console.Error.WriteLine(gitError);
            return 1;
        }

        // Resolve the branch ONCE (explicit --branch, else the checked-out branch) and thread the SAME value
        // into both capture and upload, so a default invocation still advances the current branch on publish.
        var branchName = options.Branch ?? currentBranch;

        var remoteUrl = ResolveRepositoryUrl(gitRoot!);
        var config = SextantConfiguration.Load();
        var capability = WorkerCapability.LocalDefault;
        var token = options.Token ?? Environment.GetEnvironmentVariable("SEXTANT_CONTRIB_TOKEN");

        var provenance = new ContributionProvenance
        {
            Tenant = options.Tenant ?? remoteUrl,
            Producer = Environment.MachineName,
            CliVersion = CliVersion(),
            WorkingTreeDirty = isDirty,
            ExecutionProvenance = Environment.GetEnvironmentVariable("GITHUB_RUN_ID") is { Length: > 0 } run
                ? $"github-actions:{run}" : null
        };

        Console.WriteLine("Sextant contribution capture");
        Console.WriteLine($"  Solution:   {solutionPath}");
        Console.WriteLine($"  Repository: {remoteUrl}");
        Console.WriteLine($"  Commit:     {commitSha}{(isDirty ? " (dirty — local only)" : string.Empty)}");
        Console.WriteLine($"  Capability: {capability.Fingerprint}");
        Console.WriteLine();
        Console.WriteLine("Indexing committed source (this uses the local SDK/workloads)...");

        ContributionArtifact artifact;
        try
        {
            artifact = await ContributionCapture.CaptureAsync(new ContributionCaptureRequest
            {
                SolutionPath = solutionPath,
                RepositoryRemoteUrl = remoteUrl,
                CommitSha = commitSha,
                TreeSha = treeSha,
                BranchName = branchName,
                IsDefaultBranch = options.IsDefaultBranch,
                WorkingTreeDirty = isDirty,
                Provenance = provenance,
                Capability = capability
            }, config, log: null, cancellationToken);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Capture failed: {ex.Message}");
            return 1;
        }

        Console.WriteLine($"  Artifact:   {artifact.ContentAddress} ({artifact.SizeBytes} bytes)");

        return decision.Destination == ContributionDestination.WriteLocal
            ? WriteLocal(artifact, options.OutFile!)
            : await UploadAsync(artifact, options, token, branchName, cancellationToken);
    }

    private static int WriteLocal(ContributionArtifact artifact, string outFile)
    {
        var fullPath = Path.GetFullPath(outFile);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(fullPath, artifact.ToArray());
        Console.WriteLine($"  Wrote contribution artifact to {fullPath}");
        return 0;
    }

    private static async Task<int> UploadAsync(
        ContributionArtifact artifact, ContributeOptions options, string? token, string? branchName,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(options.ServiceUrl, UriKind.Absolute, out var serviceUri))
        {
            Console.Error.WriteLine($"Invalid --service URL: {options.ServiceUrl}");
            return 1;
        }

        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var outcome = await ContributionUploader.UploadAsync(client, artifact, new ContributionUploadOptions
        {
            ServiceUrl = serviceUri,
            Token = token,
            Finalize = options.Finalize,
            BranchName = branchName,
            IsDefaultBranch = options.IsDefaultBranch
        }, cancellationToken);

        if (outcome.ServiceUnavailable)
        {
            // Service-optional: an unreachable service NEVER fails a normal build unless --require (the CI
            // opt-in) is set (acceptance criterion 5).
            if (options.Require)
            {
                Console.Error.WriteLine($"Service unavailable and --require was set: {outcome.Message}");
                return 1;
            }
            Console.WriteLine($"  Service unavailable (non-blocking): {outcome.Message}");
            return 0;
        }

        if (!outcome.Accepted)
        {
            Console.Error.WriteLine($"  Contribution rejected: {outcome.Result?.RejectionCode}: {outcome.Message}");
            foreach (var diagnostic in outcome.Result?.Diagnostics ?? [])
                Console.Error.WriteLine($"    - [{diagnostic.Code}] {diagnostic.ProjectPath}: {diagnostic.Message}");
            return 1;
        }

        Console.WriteLine($"  Contribution {outcome.Result?.Status}: snapshot {outcome.Result?.SnapshotId}");
        return 0;
    }

    private static bool TryResolveGitFacts(
        string gitRoot, out string commitSha, out string? treeSha, out string? branch, out string? error)
    {
        commitSha = string.Empty;
        treeSha = null;
        branch = null;
        error = null;

        var head = RunGit(gitRoot, "rev-parse HEAD");
        if (string.IsNullOrEmpty(head))
        {
            error = "could not resolve HEAD; the repository has no commits.";
            return false;
        }
        commitSha = head;
        treeSha = RunGit(gitRoot, "rev-parse HEAD^{tree}");
        // A detached HEAD (a CI PR checkout, an arbitrary commit) reports "HEAD" here. Leave the branch
        // UNRESOLVED (null) in that case rather than inventing a default: an unresolved branch advances NO
        // server pointer, so a detached checkout can never silently move a branch like `main`. A caller that
        // does want to publish to a branch passes --branch explicitly.
        var abbrev = RunGit(gitRoot, "rev-parse --abbrev-ref HEAD");
        if (!string.IsNullOrEmpty(abbrev) && abbrev != "HEAD")
            branch = abbrev;
        return true;
    }

    private static string ResolveRepositoryUrl(string gitRoot)
    {
        var raw = GitRemoteResolver.ReadOriginRemote(gitRoot);
        return raw is not null ? GitRemoteNormalizer.Normalize(raw) : $"local://{Environment.MachineName}";
    }

    private static string CliVersion() =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

    private static string? RunGit(string workingDirectory, string args)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git", args)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            return process.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }
}
