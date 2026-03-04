using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace Sextant.Indexer;

public static class SolutionLoader
{
    public static async Task<Solution> LoadSolutionAsync(string solutionPath, Action<string>? onDiagnostic = null)
    {
        var workspace = MSBuildWorkspace.Create();
        workspace.RegisterWorkspaceFailedHandler(e =>
        {
            if (e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
                onDiagnostic?.Invoke(e.Diagnostic.Message);
        });

        return await workspace.OpenSolutionAsync(solutionPath);
    }
}
