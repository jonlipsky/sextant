namespace Sextant.Core;

public sealed record ProjectIdentity
{
    public required string CanonicalId { get; init; }
    public required string GitRemoteUrl { get; init; }
    public required string RepoRelativePath { get; init; }
    public string? DiskPath { get; init; }
    public string? AssemblyName { get; init; }
    public string? TargetFramework { get; init; }
    public bool IsTestProject { get; init; }
}
