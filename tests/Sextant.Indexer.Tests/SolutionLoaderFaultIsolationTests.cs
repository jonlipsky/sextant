using Microsoft.CodeAnalysis;
using Sextant.Indexer;

namespace Sextant.Indexer.Tests;

/// <summary>
/// Proves the per-project isolation seam of <see cref="SolutionLoader"/> (issue #90): when one project
/// fails to load, the loadable projects are still returned and the failure is reported as a skipped
/// project rather than aborting the whole load. Uses a fake loader (backed by an in-memory
/// <see cref="AdhocWorkspace"/>) so a "BuildHost crash" can be simulated deterministically without a
/// real MSBuild toolchain.
/// </summary>
[TestClass]
public sealed class SolutionLoaderFaultIsolationTests
{
    private static readonly string ProjA = Path.Combine("repo", "A", "A.csproj");
    private static readonly string ProjB = Path.Combine("repo", "B", "B.csproj");
    private static readonly string ProjC = Path.Combine("repo", "C", "C.csproj");

    [TestMethod]
    public async Task OneFailingProject_IsSkipped_WhileTheRestLoad()
    {
        var loader = new FakeProjectLoader(throwFor: [ProjB]);
        var diagnostics = new List<string>();

        var (solution, skipped) = await SolutionLoader.LoadProjectsIndividuallyAsync(
            [ProjA, ProjB, ProjC], loader, diagnostics.Add, CancellationToken.None);

        // The two loadable projects produce a NON-empty solution — not an empty index (issue #90).
        CollectionAssert.AreEquivalent(
            new[] { ProjA, ProjC },
            solution.Projects.Select(p => p.FilePath).ToArray());

        // The single failure is surfaced as a skipped project naming it and why.
        Assert.AreEqual(1, skipped.Count);
        Assert.AreEqual(Path.GetFullPath(ProjB), Path.GetFullPath(skipped[0].ProjectPath));
        StringAssert.Contains(skipped[0].Reason, "simulated BuildHost crash");

        Assert.IsTrue(diagnostics.Any(d => d.Contains("B.csproj")),
            "a diagnostic naming the skipped project must be emitted");
    }

    [TestMethod]
    public async Task AllProjectsLoad_NoSkips()
    {
        var loader = new FakeProjectLoader();

        var (solution, skipped) = await SolutionLoader.LoadProjectsIndividuallyAsync(
            [ProjA, ProjB, ProjC], loader, null, CancellationToken.None);

        Assert.AreEqual(3, solution.Projects.Count());
        Assert.AreEqual(0, skipped.Count);
    }

    [TestMethod]
    public async Task AlreadyLoadedProject_IsNotReopened()
    {
        var loader = new FakeProjectLoader();

        // The same project appears twice (e.g. once directly, once already pulled in transitively).
        var (_, skipped) = await SolutionLoader.LoadProjectsIndividuallyAsync(
            [ProjA, ProjA], loader, null, CancellationToken.None);

        Assert.AreEqual(0, skipped.Count);
        Assert.AreEqual(1, loader.OpenCount, "an already-loaded project must not be opened twice");
    }

    [TestMethod]
    public async Task Cancellation_Propagates_AndIsNotRecordedAsASkip()
    {
        var loader = new FakeProjectLoader(cancelFor: [ProjB]);
        var skippedSoFar = new List<string>();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await SolutionLoader.LoadProjectsIndividuallyAsync(
                [ProjA, ProjB, ProjC], loader, skippedSoFar.Add, CancellationToken.None));

        // A cancellation is not a per-project load fault, so it must never be swallowed as a skip.
        Assert.IsFalse(skippedSoFar.Any(d => d.StartsWith("Skipped project", StringComparison.Ordinal)));
    }

    private sealed class FakeProjectLoader : SolutionLoader.IWorkspaceProjectLoader
    {
        private readonly AdhocWorkspace _workspace = new();
        private readonly HashSet<string> _throwFor;
        private readonly HashSet<string> _cancelFor;

        public FakeProjectLoader(IEnumerable<string>? throwFor = null, IEnumerable<string>? cancelFor = null)
        {
            _throwFor = new HashSet<string>(throwFor ?? [], StringComparer.OrdinalIgnoreCase);
            _cancelFor = new HashSet<string>(cancelFor ?? [], StringComparer.OrdinalIgnoreCase);
        }

        public int OpenCount { get; private set; }

        public Solution CurrentSolution => _workspace.CurrentSolution;

        public bool IsLoaded(string projectPath) =>
            _workspace.CurrentSolution.Projects.Any(
                p => string.Equals(p.FilePath, projectPath, StringComparison.OrdinalIgnoreCase));

        public Task OpenProjectAsync(string projectPath, CancellationToken cancellationToken)
        {
            OpenCount++;
            if (_cancelFor.Contains(projectPath))
                throw new OperationCanceledException();
            if (_throwFor.Contains(projectPath))
                throw new InvalidOperationException(
                    $"simulated BuildHost crash for {Path.GetFileName(projectPath)}");

            var name = Path.GetFileNameWithoutExtension(projectPath);
            var info = ProjectInfo.Create(
                ProjectId.CreateNewId(), VersionStamp.Create(), name, name, LanguageNames.CSharp,
                filePath: projectPath);
            _workspace.AddProject(info);
            return Task.CompletedTask;
        }
    }
}
