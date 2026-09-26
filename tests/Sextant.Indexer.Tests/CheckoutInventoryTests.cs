using Sextant.Indexer;

namespace Sextant.Indexer.Tests;

/// <summary>
/// Issue #119 — the file-system inventory that coverage is computed over: project files on disk (pruning
/// build output) and the submodules a checkout declares, with a git-config-faithful <c>.gitmodules</c>
/// parser and a scan-error sink so an incomplete scan is never mistaken for a complete one.
/// </summary>
[TestClass]
public class CheckoutInventoryTests
{
    private string _root = null!;

    [TestInitialize]
    public void Init()
    {
        _root = Path.Combine(Path.GetTempPath(), $"sextant_inv_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [TestMethod]
    public void FindProjectFiles_FindsAllProjectKinds_AndPrunesBuildOutput()
    {
        Touch("src/A/A.csproj");
        Touch("src/B/B.fsproj");
        Touch("src/C/C.vbproj");
        Touch("src/A/obj/Stale.csproj");
        Touch("src/A/bin/Debug/Copied.csproj");
        Touch(".git/modules/x/Hidden.csproj");
        Touch("src/A/A.cs");

        var errors = new List<string>();
        var found = CheckoutInventory.FindProjectFiles(_root, errors)
            .Select(p => Path.GetRelativePath(_root, p).Replace('\\', '/'))
            .ToList();

        CollectionAssert.AreEqual(new[] { "src/A/A.csproj", "src/B/B.fsproj", "src/C/C.vbproj" }, found);
        Assert.AreEqual(0, errors.Count);
    }

    [TestMethod]
    public void ParseGitmodulesPaths_ReadsOnlySubmoduleSections_WithQuotesEscapesAndComments()
    {
        string[] lines =
        [
            "# leading comment",
            "[submodule \"lib/one\"]",
            "\tpath = lib/one",
            "\turl = https://example.com/one.git",
            "[SubModule \"two\"]",
            "   PATH = \"lib/with space\" ; trailing comment",
            "[submodule \"three\"]",
            "path = lib/three # comment",
            "[submodule \"quoted-hash\"]",
            "path = \"lib/has#hash\"",
            "[submodule \"escaped\"]",
            "path = lib/esc\\\"aped",
            "[core]",
            "path = not/a/submodule",
            "[submodules]",
            "path = also/not",
            "[submodule \"empty\"]",
            "path =",
            "; path = commented/out"
        ];

        var paths = CheckoutInventory.ParseGitmodulesPaths(lines);

        CollectionAssert.AreEqual(
            new[] { "lib/one", "lib/with space", "lib/three", "lib/has#hash", "lib/esc\"aped" }, paths);
    }

    [TestMethod]
    public void FindDeclaredSubmodules_PopulatedRequiresGitEntry_NotJustANonEmptyDirectory()
    {
        WriteGitmodules("", "vendor/gitfile", "vendor/gitdir", "vendor/plain", "vendor/missing");
        Touch("vendor/gitfile/.git", "gitdir: ../../.git/modules/gitfile");
        Directory.CreateDirectory(Path.Combine(_root, "vendor/gitdir/.git"));
        Touch("vendor/plain/readme.txt"); // non-empty but not a git worktree

        var errors = new List<string>();
        var subs = CheckoutInventory.FindDeclaredSubmodules(_root, errors);

        CollectionAssert.AreEqual(
            new[]
            {
                new DeclaredSubmodule("vendor/gitdir", true),
                new DeclaredSubmodule("vendor/gitfile", true),
                new DeclaredSubmodule("vendor/missing", false),
                new DeclaredSubmodule("vendor/plain", false)
            },
            subs.ToArray());
        Assert.AreEqual(0, errors.Count);
    }

    [TestMethod]
    public void FindDeclaredSubmodules_RecursesThroughPopulatedSubmodules_Only()
    {
        WriteGitmodules("", "outer", "cold");
        Touch("outer/.git", "gitdir: x");
        WriteGitmodules("outer", "inner");
        // An unpopulated submodule cannot reveal nested ones, even if stray files claim them.
        Touch("cold/.gitmodules", "[submodule \"n\"]\n\tpath = nested\n");

        var subs = CheckoutInventory.FindDeclaredSubmodules(_root);

        CollectionAssert.AreEqual(
            new[]
            {
                new DeclaredSubmodule("cold", false),
                new DeclaredSubmodule("outer", true),
                new DeclaredSubmodule("outer/inner", false)
            },
            subs.ToArray());
    }

    [TestMethod]
    public void FindDeclaredSubmodules_EntryEscapingTheCheckout_IsAScanError()
    {
        WriteGitmodules("", "../outside", "ok");

        var errors = new List<string>();
        var subs = CheckoutInventory.FindDeclaredSubmodules(_root, errors);

        CollectionAssert.AreEqual(new[] { new DeclaredSubmodule("ok", false) }, subs.ToArray());
        Assert.AreEqual(1, errors.Count);
        StringAssert.Contains(errors[0], "escapes the checkout");
    }

    [TestMethod]
    public void FindDeclaredSubmodules_NoGitmodules_IsEmptyWithoutErrors()
    {
        var errors = new List<string>();
        Assert.AreEqual(0, CheckoutInventory.FindDeclaredSubmodules(_root, errors).Count);
        Assert.AreEqual(0, errors.Count);
    }

    [TestMethod]
    public void FindDeclaredSubmodules_IntermediateLinkOutsideTheCheckout_IsAScanError_NotPopulated()
    {
        // `link/sub` is lexically inside the checkout, but `link` is a junction/symlink to a directory
        // OUTSIDE it that holds a real git worktree. That external content must never count as a populated
        // submodule (the project walk never follows the link, so its projects would silently vanish).
        var outside = Path.Combine(Path.GetTempPath(), $"sextant_inv_out_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(outside, "sub", ".git"));
        try
        {
            CreateDirectoryLink(Path.Combine(_root, "link"), outside);
            WriteGitmodules("", "link/sub");

            var errors = new List<string>();
            var subs = CheckoutInventory.FindDeclaredSubmodules(_root, errors);

            CollectionAssert.AreEqual(new[] { new DeclaredSubmodule("link/sub", false) }, subs.ToArray());
            Assert.AreEqual(1, errors.Count);
            StringAssert.Contains(errors[0], "symbolic link or junction");
        }
        finally
        {
            try { Directory.Delete(Path.Combine(_root, "link")); } catch { /* best effort */ }
            try { Directory.Delete(outside, recursive: true); } catch { /* best effort */ }
        }
    }

    [TestMethod]
    public void PathComparer_MatchesHostFileSystemCaseSemantics()
    {
        var caseInsensitiveHost = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
        Assert.AreEqual(caseInsensitiveHost, CheckoutInventory.PathComparer.Equals("/r/A.csproj", "/r/a.csproj"));
    }

    [TestMethod]
    public void FindProjectFiles_CaseDistinctProjects_OnCaseSensitiveFileSystem_AreBothCounted()
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
            Assert.Inconclusive("Needs a case-sensitive file system (runs on Linux CI).");

        Touch("src/A.csproj");
        Touch("src/a.csproj");

        var found = CheckoutInventory.FindProjectFiles(_root)
            .Select(p => Path.GetRelativePath(_root, p).Replace('\\', '/'))
            .ToList();

        CollectionAssert.AreEqual(new[] { "src/A.csproj", "src/a.csproj" }, found,
            "case-folding would hide one real project from coverage");
    }

    private static void CreateDirectoryLink(string link, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            // A junction needs no elevation/developer mode, unlike a directory symlink.
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
            })!;
            p.WaitForExit();
            if (p.ExitCode != 0)
                Assert.Inconclusive($"Could not create a junction: {p.StandardError.ReadToEnd()}");
        }
        else
        {
            Directory.CreateSymbolicLink(link, target);
        }
    }

    private void Touch(string relative, string content = "")
    {
        var full = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private void WriteGitmodules(string repoRelative, params string[] paths)
    {
        var body = string.Concat(paths.Select(p => $"[submodule \"{p}\"]\n\tpath = {p}\n\turl = https://example.com/{p}.git\n"));
        Touch(Path.Combine(repoRelative, ".gitmodules"), body);
    }
}
