using Microsoft.Data.Sqlite;
using Sextant.Indexer;
using Sextant.Store;

namespace Sextant.Benchmarks.Tests;

/// <summary>
/// Phase 5 differential parity: index the same generated corpus twice — once through the legacy
/// declaration-driven extractor (flag OFF) and once through the document-oriented extractor (flag ON)
/// — and compare the two canonical databases table-by-table. Every intentional difference between the
/// extractors (acceptance criterion 4) is encoded as an explicit, directional assertion here:
/// <list type="bullet">
///   <item>Type <b>relationships</b> (inherits/implements/overrides/returns/parameterOf/instantiates)
///   must be <b>identical</b> — both paths reuse <see cref="RelationshipExtractor"/>.</item>
///   <item><b>Call edges</b> (caller→callee) from the new extractor must be a <b>superset</b> of the
///   legacy edges: usage-site + <c>IOperation</c> resolution recovers reduced-extension/constructed
///   calls the legacy <c>GetSymbolInfo</c>-only path could drop, and loses none.</item>
///   <item><b>Reference</b> ownership flips to the usage site, so kinds are reclassified by precise
///   syntactic role. The new extractor must (a) emit <b>no</b> <c>Override</c> reference kind (folded
///   into the real occurrence kind) and (b) introduce <b>no spurious type target</b> — every type the
///   new extractor records as referenced is also recorded by the legacy path.</item>
/// </list>
/// These load real projects through MSBuildWorkspace (a NuGet restore per corpus), so this is a heavy
/// integration test.
/// </summary>
[TestClass]
public sealed class ExtractorParityTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task DocumentExtractor_MatchesLegacyExtractor_OnSharedFixtureCorpus()
    {
        var root = NewTempDir("parity");
        var legacyDir = NewTempDir("parity-legacy");
        var newDir = NewTempDir("parity-new");
        try
        {
            var slnx = CorpusGenerator.GenerateCorrectnessCorpus(root);
            Restore(slnx);

            // Two from-scratch full indexes of the identical on-disk corpus, one per extractor.
            using var legacyDb = new IndexDatabase(Path.Combine(legacyDir, "index.db"));
            legacyDb.RunMigrations();
            var legacySolution = await SolutionLoader.LoadSolutionAsync(slnx);
            await new IndexOrchestrator(legacyDb, useDocumentExtractor: false).IndexSolutionAsync(legacySolution);

            using var newDb = new IndexDatabase(Path.Combine(newDir, "index.db"));
            newDb.RunMigrations();
            var newSolution = await SolutionLoader.LoadSolutionAsync(slnx);
            await new IndexOrchestrator(newDb, useDocumentExtractor: true).IndexSolutionAsync(newSolution);

            var legacyDump = CanonicalIndexDump.Dump(legacyDb.GetConnection());
            var newDump = CanonicalIndexDump.Dump(newDb.GetConnection());

            // Sanity: both extractors produced a non-trivial graph over the same corpus.
            var legacySymbols = Section(legacyDump, "symbols");
            var newSymbols = Section(newDump, "symbols");
            Assert.IsTrue(legacySymbols.Count > 0, "legacy index produced no symbols");
            CollectionAssert.AreEqual(legacySymbols, newSymbols,
                "symbol extraction is shared and must be byte-identical between the two extractors");

            // --- Relationships: must be identical (both reuse RelationshipExtractor) ---------------
            var legacyRels = new HashSet<string>(Section(legacyDump, "relationships"));
            var newRels = new HashSet<string>(Section(newDump, "relationships"));
            var relsOnlyLegacy = legacyRels.Except(newRels).OrderBy(x => x).ToList();
            var relsOnlyNew = newRels.Except(legacyRels).OrderBy(x => x).ToList();
            TestContext.WriteLine($"relationships: legacy={legacyRels.Count} new={newRels.Count} " +
                                  $"legacy-only={relsOnlyLegacy.Count} new-only={relsOnlyNew.Count}");
            Assert.AreEqual(0, relsOnlyLegacy.Count,
                "relationships present in legacy but missing from the new extractor:\n" + string.Join("\n", relsOnlyLegacy.Take(20)));
            Assert.AreEqual(0, relsOnlyNew.Count,
                "relationships present in the new extractor but missing from legacy:\n" + string.Join("\n", relsOnlyNew.Take(20)));

            // --- Call edges: new must be a superset of legacy (caller→callee, ignoring call site) ---
            var legacyCalls = CallEdgeSet(Section(legacyDump, "call_graph"));
            var newCalls = CallEdgeSet(Section(newDump, "call_graph"));
            var callsOnlyLegacy = legacyCalls.Except(newCalls).OrderBy(x => x).ToList();
            var callsOnlyNew = newCalls.Except(legacyCalls).OrderBy(x => x).ToList();
            TestContext.WriteLine($"call edges: legacy={legacyCalls.Count} new={newCalls.Count} " +
                                  $"legacy-only={callsOnlyLegacy.Count} new-only(recovered)={callsOnlyNew.Count}");
            Assert.AreEqual(0, callsOnlyLegacy.Count,
                "call edges present in legacy but dropped by the new extractor (a regression):\n" + string.Join("\n", callsOnlyLegacy.Take(20)));

            // --- References: usage-site reclassification (criterion 4) ------------------------------
            var legacyRefRows = Section(legacyDump, "references");
            var newRefRows = Section(newDump, "references");

            // (a) The new extractor emits no Override reference kind (folded into the real occurrence).
            var overrideKind = ((int)Sextant.Core.ReferenceKind.Override).ToString();
            Assert.IsFalse(newRefRows.Any(r => ReferenceKindOf(r) == overrideKind),
                "the document-oriented extractor must not emit the legacy Override reference kind");

            // (b) No spurious type target: every type the new extractor references, legacy references too.
            var legacyTypeTargets = TypeTargets(legacyRefRows);
            var newTypeTargets = TypeTargets(newRefRows);
            var typeTargetsOnlyNew = newTypeTargets.Except(legacyTypeTargets).OrderBy(x => x).ToList();
            var typeTargetsOnlyLegacy = legacyTypeTargets.Except(newTypeTargets).OrderBy(x => x).ToList();
            TestContext.WriteLine($"referenced type targets: legacy={legacyTypeTargets.Count} new={newTypeTargets.Count} " +
                                  $"new-only={typeTargetsOnlyNew.Count} legacy-only={typeTargetsOnlyLegacy.Count}");
            if (typeTargetsOnlyLegacy.Count > 0)
                TestContext.WriteLine("legacy-only type targets (positions the usage-site pass intentionally skips, e.g. cref):\n"
                                      + string.Join("\n", typeTargetsOnlyLegacy.Take(20)));
            Assert.AreEqual(0, typeTargetsOnlyNew.Count,
                "the new extractor referenced a type the legacy extractor did not — a spurious target:\n"
                + string.Join("\n", typeTargetsOnlyNew.Take(20)));

            // (c) Cross-project reference pairs must be IDENTICAL. This is the connectivity Phase 4's
            // undirected closure + target-cascade deletion depend on: for every cross-project usage the
            // reference stores symbol_id=target (dependency project) and in_project_id=consumer. If the
            // usage-site extractor dropped or mis-targeted a cross-project edge, the pair set would
            // diverge from the legacy declaration-driven path here. (On an ambiguous target — a key
            // present in several projects, e.g. a multi-TFM dependency consumed by one TFM — the
            // catalog binds a deterministic pick rather than the exact producer; this corpus is
            // single-TFM so no such ambiguity arises and the sets must match exactly.)
            var legacyPairs = CrossProjectPairs(legacyRefRows);
            var newPairs = CrossProjectPairs(newRefRows);
            var pairsOnlyLegacy = legacyPairs.Except(newPairs).OrderBy(x => x).ToList();
            var pairsOnlyNew = newPairs.Except(legacyPairs).OrderBy(x => x).ToList();
            TestContext.WriteLine($"cross-project reference pairs: legacy={legacyPairs.Count} new={newPairs.Count} " +
                                  $"legacy-only={pairsOnlyLegacy.Count} new-only={pairsOnlyNew.Count}");
            Assert.IsTrue(legacyPairs.Count > 0, "the corpus is expected to contain cross-project references");
            Assert.AreEqual(0, pairsOnlyLegacy.Count,
                "cross-project reference pairs present in legacy but missing from the new extractor "
                + "(a broken Phase-4 closure edge):\n" + string.Join("\n", pairsOnlyLegacy.Take(20)));
            Assert.AreEqual(0, pairsOnlyNew.Count,
                "cross-project reference pairs the new extractor introduced but legacy did not:\n"
                + string.Join("\n", pairsOnlyNew.Take(20)));
        }
        finally
        {
            TryDelete(root);
            TryDelete(legacyDir);
            TryDelete(newDir);
        }
    }

    // Fields in a canonical-dump row are separated by the unit-separator control character.
    private const char Fs = '\u001f';

    /// <summary>Returns the row lines of one canonical-dump section (excluding the header and count).</summary>
    private static List<string> Section(string dump, string table)
    {
        var rows = new List<string>();
        using var reader = new StringReader(dump);
        string? line;
        var inSection = false;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.StartsWith("=== ", StringComparison.Ordinal))
            {
                inSection = line == $"=== {table} ===";
                continue;
            }
            if (!inSection) continue;
            if (line.StartsWith("(rows:", StringComparison.Ordinal)) { inSection = false; continue; }
            if (line.Length > 0) rows.Add(line);
        }
        return rows;
    }

    /// <summary>Projects call_graph rows to their (caller_canon, caller_key, callee_canon, callee_key)
    /// identity, discarding the volatile call-site file/line so dedup collapses same-edge call sites.</summary>
    private static HashSet<string> CallEdgeSet(IEnumerable<string> rows)
    {
        var set = new HashSet<string>();
        foreach (var row in rows)
        {
            var f = row.Split(Fs);
            if (f.Length >= 4) set.Add(string.Join(Fs, f[0], f[1], f[2], f[3]));
        }
        return set;
    }

    private static string ReferenceKindOf(string referenceRow)
    {
        var f = referenceRow.Split(Fs);
        return f.Length >= 6 ? f[5] : string.Empty;
    }

    /// <summary>The set of distinct (target project canonical id, target symbol key) among references
    /// whose target is a type (documentation-id keys for types start with <c>T:</c>).</summary>
    private static HashSet<string> TypeTargets(IEnumerable<string> referenceRows)
    {
        var set = new HashSet<string>();
        foreach (var row in referenceRows)
        {
            var f = row.Split(Fs);
            if (f.Length >= 2 && f[1].StartsWith("T:", StringComparison.Ordinal))
                set.Add(string.Join(Fs, f[0], f[1]));
        }
        return set;
    }

    /// <summary>The set of distinct (consumer project canonical id, target/dependency project
    /// canonical id) pairs implied by references whose target lives in a different project. Mirrors
    /// <c>ReferenceStore.GetCrossProjectPairs</c> but keyed on canonical project ids so it is
    /// comparable across two independently-built databases. Row fields:
    /// [0]=target project, [1]=target key, [2]=consumer project.</summary>
    private static HashSet<string> CrossProjectPairs(IEnumerable<string> referenceRows)
    {
        var set = new HashSet<string>();
        foreach (var row in referenceRows)
        {
            var f = row.Split(Fs);
            if (f.Length >= 3 && f[2] != f[0])
                set.Add(string.Join(Fs, f[2], f[0]));
        }
        return set;
    }

    private static string NewTempDir(string tag)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"sextant-parity-{tag}-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private static void Restore(string solutionPath)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("dotnet", $"restore \"{solutionPath}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = System.Diagnostics.Process.Start(psi)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEnd();
        stdoutTask.GetAwaiter().GetResult();
        process.WaitForExit();
        if (process.ExitCode != 0)
            Assert.Inconclusive($"restore of the generated corpus failed (exit {process.ExitCode}): {stderr}");
    }
}
