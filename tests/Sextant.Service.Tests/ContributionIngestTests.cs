using Sextant.Core.Platform;
using Sextant.Service.Contributions;
using Sextant.Store;

namespace Sextant.Service.Tests;

/// <summary>
/// Hermetic Phase-16 contribution-ingest tests. They drive <see cref="SnapshotService.IngestContributionAsync"/>
/// directly (no HTTP, no network, no MSBuild): a real content-addressed artifact built by
/// <see cref="ContributionTestFixtures"/> is fed to the service, which authenticates/authorizes,
/// hash/capability-verifies, imports, and publishes it through the SAME catalog path as Phase-13 ensure.
/// Together they cover all six acceptance criteria.
/// </summary>
[TestClass]
public sealed class ContributionIngestTests
{
    private const string Repo = "https://github.com/octo/app";
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private const string Commit2 = "89abcdef0123456789abcdef0123456789abcdef";
    private const string CapLinux = "linux-x64|net8.0|sdk-8.0.400";
    private const string CapWindows = "win-x64|net8.0-windows|sdk-8.0.400";

    private string _dbPath = string.Empty;
    private IndexDatabase? _db;
    private SnapshotService? _service;

    [TestCleanup]
    public void Cleanup()
    {
        _service?.Dispose();
        SqliteTestDatabase.Delete(_dbPath, _db);
    }

    // ---- criterion 1: authenticated client publishes a deterministic contribution for a clean commit ----

    [TestMethod]
    public async Task Clean_commit_contribution_publishes_a_complete_snapshot()
    {
        var service = Start();
        var artifact = ContributionTestFixtures.BuildArtifact(Repo, Commit, CapLinux, [new PayloadProjectSpec("src/App/App.csproj", "net8.0")]);

        var result = await service.IngestContributionAsync(new IngestContributionRequest { Artifact = artifact.ToArray() });

        Assert.IsTrue(result.Accepted, result.Message);
        Assert.AreEqual(ContributionIngestStatus.Complete, result.Status);
        Assert.IsNotNull(result.SnapshotId);

        var snapshots = new SnapshotStore(_db!.GetConnection());
        var published = snapshots.GetById(result.SnapshotId!.Value)!;
        Assert.AreEqual(SnapshotStatus.Complete, published.Status);
        Assert.AreEqual(Commit, snapshots.GetCommitSha(published.CommitId));
    }

    [TestMethod]
    public void Contribution_manifest_derivation_is_deterministic_for_identical_inputs()
    {
        var a = ContributionTestFixtures.BuildArtifact(Repo, Commit, CapLinux, [new PayloadProjectSpec("src/App/App.csproj", "net8.0")]);
        var b = ContributionTestFixtures.BuildArtifact(Repo, Commit, CapLinux, [new PayloadProjectSpec("src/App/App.csproj", "net8.0")]);

        // The manifest is a pure derivation of the committed inputs (repo/commit/capability/project graph +
        // fingerprints), so identical inputs yield a byte-identical manifest and therefore an identical
        // ManifestHash. (The full content address also folds in the raw payload SQLite bytes, which are not
        // guaranteed byte-stable across two independent index builds; literal-bytes idempotency is pinned
        // separately by Re_uploading_the_same_artifact_is_a_duplicate_no_op.)
        Assert.AreEqual(a.Manifest.ManifestHash, b.Manifest.ManifestHash,
            "the manifest derivation must be deterministic for identical committed inputs");
    }

    // ---- criterion 2: re-uploading the same contribution is a content-addressed no-op --------------------

    [TestMethod]
    public async Task Re_uploading_the_same_artifact_is_a_duplicate_no_op()
    {
        var service = Start();
        var artifact = ContributionTestFixtures.BuildArtifact(Repo, Commit, CapLinux, [new PayloadProjectSpec("src/App/App.csproj", "net8.0")]);

        var first = await service.IngestContributionAsync(new IngestContributionRequest { Artifact = artifact.ToArray() });
        var second = await service.IngestContributionAsync(new IngestContributionRequest { Artifact = artifact.ToArray() });

        Assert.AreEqual(ContributionIngestStatus.Complete, first.Status);
        Assert.IsTrue(second.Accepted);
        Assert.AreEqual(ContributionIngestStatus.Duplicate, second.Status);
        Assert.AreEqual(first.SnapshotId, second.SnapshotId);
        Assert.AreEqual(first.ContentHash, second.ContentHash);
    }

    // ---- criterion 3: bad contributions are rejected with structured reasons ----------------------------

    [TestMethod]
    public async Task Dirty_tree_contribution_is_rejected()
    {
        var service = Start();
        var artifact = ContributionTestFixtures.BuildArtifact(
            Repo, Commit, CapLinux, [new PayloadProjectSpec("src/App/App.csproj", "net8.0")], workingTreeDirty: true);

        var result = await service.IngestContributionAsync(new IngestContributionRequest { Artifact = artifact.ToArray() });

        AssertRejected(result, ContributionRejectionCode.DirtyTree);
    }

    [TestMethod]
    public async Task Schema_mismatch_contribution_is_rejected()
    {
        var service = Start();
        var artifact = ContributionTestFixtures.BuildArtifact(
            Repo, Commit, CapLinux, [new PayloadProjectSpec("src/App/App.csproj", "net8.0")],
            mutateManifest: m => m with { SchemaVersion = m.SchemaVersion + 100 });

        var result = await service.IngestContributionAsync(new IngestContributionRequest { Artifact = artifact.ToArray() });

        AssertRejected(result, ContributionRejectionCode.SchemaMismatch);
    }

    [TestMethod]
    public async Task Analyzer_mismatch_contribution_is_rejected()
    {
        var service = Start();
        var artifact = ContributionTestFixtures.BuildArtifact(
            Repo, Commit, CapLinux, [new PayloadProjectSpec("src/App/App.csproj", "net8.0")],
            mutateManifest: m => m with { AnalyzerVersion = m.AnalyzerVersion + "-tampered" });

        var result = await service.IngestContributionAsync(new IngestContributionRequest { Artifact = artifact.ToArray() });

        AssertRejected(result, ContributionRejectionCode.AnalyzerMismatch);
    }

    [TestMethod]
    public async Task Capability_mismatch_between_declared_and_built_is_rejected()
    {
        var service = Start();
        // The payload was genuinely built under CapLinux, but the manifest DECLARES CapWindows.
        var artifact = ContributionTestFixtures.BuildArtifact(
            Repo, Commit, CapLinux, [new PayloadProjectSpec("src/App/App.csproj", "net8.0")],
            mutateManifest: m => m with { CapabilityFingerprint = CapWindows });

        var result = await service.IngestContributionAsync(new IngestContributionRequest { Artifact = artifact.ToArray() });

        AssertRejected(result, ContributionRejectionCode.CapabilityMismatch);
    }

    [TestMethod]
    public async Task Project_graph_mismatch_is_rejected()
    {
        var service = Start();
        // Payload has TWO projects but the manifest declares only ONE — the graph no longer matches.
        var artifact = ContributionTestFixtures.BuildArtifact(
            Repo, Commit, CapLinux,
            [new PayloadProjectSpec("src/A/A.csproj", "net8.0"), new PayloadProjectSpec("src/B/B.csproj", "net8.0")],
            mutateManifest: m => m with { Projects = [m.Projects[0]] });

        var result = await service.IngestContributionAsync(new IngestContributionRequest { Artifact = artifact.ToArray() });

        AssertRejected(result, ContributionRejectionCode.ProjectGraphMismatch);
    }

    [TestMethod]
    public async Task Duplicate_project_canonical_id_is_rejected()
    {
        var service = Start();
        // Payload has TWO distinct project versions, but the manifest declares the SAME one twice. The count
        // still matches (2 == 2), so this exercises the uniqueness guard specifically — a duplicate canonical
        // id would otherwise crash the importer's per-project capability map.
        var artifact = ContributionTestFixtures.BuildArtifact(
            Repo, Commit, CapLinux,
            [new PayloadProjectSpec("src/App/App.csproj", "net8.0"), new PayloadProjectSpec("src/Lib/Lib.csproj", "net8.0")],
            mutateManifest: m => m with { Projects = [m.Projects[0], m.Projects[0]] });

        var result = await service.IngestContributionAsync(new IngestContributionRequest { Artifact = artifact.ToArray() });

        AssertRejected(result, ContributionRejectionCode.ProjectGraphMismatch);
    }

    [TestMethod]
    public async Task Payload_from_a_different_repository_than_declared_is_rejected()
    {
        // Supply-chain attack: the tenant is authorized for `Repo`, but ships a payload built for a DIFFERENT
        // repository and declares `Repo` in the manifest. The payload snapshot's own repository row must be
        // bound to the declared identity, so this imports nothing under the authorized assembly identity.
        var service = Start();
        var artifact = ContributionTestFixtures.BuildArtifact(
            Repo, Commit, CapLinux, [new PayloadProjectSpec("src/App/App.csproj", "net8.0")],
            mutateManifest: m => m with { RepositoryRemoteUrl = "https://github.com/evil/substitute" });

        var result = await service.IngestContributionAsync(new IngestContributionRequest { Artifact = artifact.ToArray() });

        AssertRejected(result, ContributionRejectionCode.RepositoryMismatch);
    }

    [TestMethod]
    public async Task Payload_from_a_different_commit_than_declared_is_rejected()
    {
        // The payload snapshot's own commit row must equal the manifest's declared (authorized) commit, so a
        // manifest that relabels the commit while shipping another commit's content is rejected.
        var service = Start();
        var artifact = ContributionTestFixtures.BuildArtifact(
            Repo, Commit, CapLinux, [new PayloadProjectSpec("src/App/App.csproj", "net8.0")],
            mutateManifest: m => m with { CommitSha = Commit2 });

        var result = await service.IngestContributionAsync(new IngestContributionRequest { Artifact = artifact.ToArray() });

        AssertRejected(result, ContributionRejectionCode.CommitMismatch);
    }

    [TestMethod]
    public async Task Payload_built_for_a_different_tree_than_the_manifest_declares_is_rejected()
    {
        // The payload's recorded tree must equal the manifest's declared commit tree. A manifest that keeps an
        // authorized commit but relabels the tree (shipping other content) is bound and rejected.
        var service = Start();
        var artifact = ContributionTestFixtures.BuildArtifact(
            Repo, Commit, CapLinux, [new PayloadProjectSpec("src/App/App.csproj", "net8.0")],
            mutateManifest: m => m with { TreeSha = "tampered-tree-sha" });

        var result = await service.IngestContributionAsync(new IngestContributionRequest { Artifact = artifact.ToArray() });

        AssertRejected(result, ContributionRejectionCode.CommitMismatch);
    }

    [TestMethod]
    public async Task Payload_built_under_a_different_config_than_the_manifest_declares_is_rejected()
    {
        // The payload's recorded config hash must equal the manifest's declared config. A manifest that
        // relabels the profile/config it was built under (so it assembles under a different identity than it
        // actually built) is bound and rejected with a structured ConfigMismatch reason.
        var service = Start();
        var artifact = ContributionTestFixtures.BuildArtifact(
            Repo, Commit, CapLinux, [new PayloadProjectSpec("src/App/App.csproj", "net8.0")],
            mutateManifest: m => m with { ConfigHash = "tampered-config-hash" });

        var result = await service.IngestContributionAsync(new IngestContributionRequest { Artifact = artifact.ToArray() });

        AssertRejected(result, ContributionRejectionCode.ConfigMismatch);
    }

    [TestMethod]
    public async Task Manifest_declaring_an_unsupported_version_is_rejected_as_malformed()
    {
        // Only the v1 wire format is understood. A manifest declaring another version must not be interpreted
        // with v1 semantics; the parse-boundary shape check turns it into a structured MalformedArtifact.
        var service = Start();
        var artifact = ContributionTestFixtures.BuildArtifact(
            Repo, Commit, CapLinux, [new PayloadProjectSpec("src/App/App.csproj", "net8.0")],
            mutateManifest: m => m with { Version = "999" });

        var result = await service.IngestContributionAsync(new IngestContributionRequest { Artifact = artifact.ToArray() });

        Assert.IsFalse(result.Accepted);
        Assert.AreEqual(ContributionIngestStatus.Rejected, result.Status);
        Assert.AreEqual(ContributionRejectionCode.MalformedArtifact, result.RejectionCode);
    }

    [TestMethod]
    public async Task Per_project_capability_differing_from_the_contribution_capability_is_rejected()
    {
        // Every project version in ONE contribution was built by ONE environment, so a project whose declared
        // capability differs from the contribution's built capability is a mismatched-inputs rejection.
        var service = Start();
        var artifact = ContributionTestFixtures.BuildArtifact(
            Repo, Commit, CapLinux, [new PayloadProjectSpec("src/App/App.csproj", "net8.0")],
            mutateManifest: m => m with { Projects = [m.Projects[0] with { CapabilityFingerprint = CapWindows }] });

        var result = await service.IngestContributionAsync(new IngestContributionRequest { Artifact = artifact.ToArray() });

        AssertRejected(result, ContributionRejectionCode.CapabilityMismatch);
    }

    [TestMethod]
    public async Task Empty_source_fingerprints_are_rejected_when_git_verification_required()
    {
        // Under required verification an EMPTY fingerprint set would pass zero checks and bypass the gate, so
        // a project that declares no source fingerprints is itself fatal. A real provider is wired to satisfy
        // the fail-closed Start guard.
        var service = Start(
            policy: new ContributionPolicy { RequireGitContentVerification = true },
            gitContent: new FixedGitContentProvider(GitContentCheck.Match));
        var artifact = ContributionTestFixtures.BuildArtifact(
            Repo, Commit, CapLinux, [new PayloadProjectSpec("src/App/App.csproj", "net8.0")],
            mutateManifest: m => m with { Projects = [m.Projects[0] with { SourceFingerprints = [] }] });

        var result = await service.IngestContributionAsync(new IngestContributionRequest { Artifact = artifact.ToArray() });

        AssertRejected(result, ContributionRejectionCode.ContentMismatch);
    }

    [TestMethod]
    public async Task Null_projects_in_a_tampered_manifest_are_rejected_as_malformed()
    {
        // A hand-crafted manifest can null a required collection (STJ's `required` enforces presence, not
        // non-null). The parse-boundary shape check turns it into a structured MalformedArtifact rejection
        // instead of an NRE 500 deep in authorization.
        var service = Start();
        var artifact = ContributionTestFixtures.BuildArtifact(
            Repo, Commit, CapLinux, [new PayloadProjectSpec("src/App/App.csproj", "net8.0")],
            mutateManifest: m => m with { Projects = null! });

        var result = await service.IngestContributionAsync(new IngestContributionRequest { Artifact = artifact.ToArray() });

        Assert.IsFalse(result.Accepted);
        Assert.AreEqual(ContributionIngestStatus.Rejected, result.Status);
        Assert.AreEqual(ContributionRejectionCode.MalformedArtifact, result.RejectionCode);
    }

    [TestMethod]
    public async Task Overlapping_contributions_into_the_same_pending_snapshot_are_rejected()
    {
        // Assembly requires each contribution to import DISJOINT project versions into the one pending
        // snapshot. A second contribution that re-imports a logical project already assembled is rejected
        // rather than silently reusing/overwriting the earlier contribution's row.
        var service = Start();
        var first = ContributionTestFixtures.BuildArtifact(
            Repo, Commit, CapLinux, [new PayloadProjectSpec("src/Shared/Shared.csproj", "net8.0")], stableCanonicalIds: true);
        var overlap = ContributionTestFixtures.BuildArtifact(
            Repo, Commit, CapWindows, [new PayloadProjectSpec("src/Shared/Shared.csproj", "net8.0")], stableCanonicalIds: true);

        var assembling = await service.IngestContributionAsync(new IngestContributionRequest { Artifact = first.ToArray(), Finalize = false });
        Assert.AreEqual(ContributionIngestStatus.Assembling, assembling.Status);

        var result = await service.IngestContributionAsync(new IngestContributionRequest { Artifact = overlap.ToArray(), Finalize = true });

        AssertRejected(result, ContributionRejectionCode.ProjectGraphMismatch);

        // The earlier contribution's pending snapshot is untouched — it still carries exactly its one project.
        var (_, projectCount, _) = ContributionTestFixtures.ReadAssembly(_db!, assembling.IdentityHash);
        Assert.AreEqual(1, projectCount, "the overlapping contribution rolled back without mutating the pending snapshot");
    }

    [TestMethod]
    public async Task Contributing_to_a_superseded_snapshot_never_resurrects_or_mutates_it()
    {
        // A published snapshot that a branch later moved past is Superseded — still immutable. A new
        // contribution for that committed state must NOT re-import into it, re-complete it, or re-advance a
        // branch to it; it records provenance only and leaves the superseded snapshot byte-for-byte unchanged.
        var service = Start();

        var v1 = ContributionTestFixtures.BuildArtifact(Repo, Commit, CapLinux, [new PayloadProjectSpec("src/App/App.csproj", "net8.0")]);
        var published = await service.IngestContributionAsync(new IngestContributionRequest
        { Artifact = v1.ToArray(), Finalize = true, BranchName = "main", IsDefaultBranch = true });
        Assert.AreEqual(ContributionIngestStatus.Complete, published.Status);

        // A newer commit advances main → the first snapshot becomes Superseded.
        var v2 = ContributionTestFixtures.BuildArtifact(Repo, Commit2, CapLinux, [new PayloadProjectSpec("src/App/App.csproj", "net8.0")]);
        await service.IngestContributionAsync(new IngestContributionRequest
        { Artifact = v2.ToArray(), Finalize = true, BranchName = "main", IsDefaultBranch = true });

        var snapshots = new SnapshotStore(_db!.GetConnection());
        Assert.AreEqual(SnapshotStatus.Superseded, snapshots.GetById(published.SnapshotId!.Value)!.Status);

        // A NEW distinct contribution (different capability/projects → different content hash) arrives for the
        // first commit, whose assembly snapshot is now Superseded.
        var late = ContributionTestFixtures.BuildArtifact(Repo, Commit, CapWindows, [new PayloadProjectSpec("src/Win/Win.csproj", "net8.0-windows")]);
        var result = await service.IngestContributionAsync(new IngestContributionRequest { Artifact = late.ToArray(), Finalize = true });

        Assert.IsTrue(result.Accepted);
        Assert.AreEqual(published.SnapshotId, result.SnapshotId);
        var after = snapshots.GetById(published.SnapshotId!.Value)!;
        Assert.AreEqual(SnapshotStatus.Superseded, after.Status, "a superseded snapshot is never resurrected to complete");
        var (_, projectCount, _) = ContributionTestFixtures.ReadAssembly(_db!, result.IdentityHash);
        Assert.AreEqual(1, projectCount, "a superseded snapshot is immutable — the late contribution never mutates it");
    }

    // ---- streaming ingest path (HTTP host): OUR size cap governs, and parity with the byte[] path ---------

    [TestMethod]
    public async Task Streaming_ingest_publishes_a_clean_contribution()
    {
        var service = Start();
        var artifact = ContributionTestFixtures.BuildArtifact(Repo, Commit, CapLinux, [new PayloadProjectSpec("src/App/App.csproj", "net8.0")]);

        using var stream = new MemoryStream(artifact.ToArray());
        var result = await service.IngestContributionAsync(stream, token: null, finalize: true, branchName: null, isDefaultBranch: false);

        Assert.IsTrue(result.Accepted, result.Message);
        Assert.AreEqual(ContributionIngestStatus.Complete, result.Status);
        Assert.IsNotNull(result.SnapshotId);
    }

    [TestMethod]
    public async Task Streaming_ingest_rejects_oversized_artifact_before_buffering_it_whole()
    {
        var service = Start(policy: new ContributionPolicy { MaxArtifactBytes = 32 });
        var artifact = ContributionTestFixtures.BuildArtifact(Repo, Commit, CapLinux, [new PayloadProjectSpec("src/App/App.csproj", "net8.0")]);

        using var stream = new MemoryStream(artifact.ToArray());
        var result = await service.IngestContributionAsync(stream, token: null, finalize: true, branchName: null, isDefaultBranch: false);

        Assert.IsFalse(result.Accepted);
        Assert.AreEqual(ContributionRejectionCode.SizeLimit, result.RejectionCode);
    }

    [TestMethod]
    public async Task Unauthorized_tenant_is_rejected_when_authorization_required()
    {
        var service = Start(
            policy: new ContributionPolicy { RequireAuthorization = true },
            authorizer: new DenyingAuthorizer("tenant not entitled to this repository"));
        var artifact = ContributionTestFixtures.BuildArtifact(Repo, Commit, CapLinux, [new PayloadProjectSpec("src/App/App.csproj", "net8.0")]);

        var result = await service.IngestContributionAsync(new IngestContributionRequest { Artifact = artifact.ToArray(), Token = "bearer-xyz" });

        AssertRejected(result, ContributionRejectionCode.Unauthorized);
    }

    [TestMethod]
    public async Task Missing_token_is_rejected_when_authorization_required()
    {
        // A REAL authorizer is wired (satisfying the fail-closed Start guard); the token-presence gate must
        // still reject a contribution that arrives with no authenticated token under a require-auth policy.
        var service = Start(
            policy: new ContributionPolicy { RequireAuthorization = true },
            authorizer: new AllowingAuthorizer());
        var artifact = ContributionTestFixtures.BuildArtifact(Repo, Commit, CapLinux, [new PayloadProjectSpec("src/App/App.csproj", "net8.0")]);

        var result = await service.IngestContributionAsync(new IngestContributionRequest { Artifact = artifact.ToArray(), Token = null });

        AssertRejected(result, ContributionRejectionCode.Unauthorized);
    }

    [TestMethod]
    public void Start_refuses_to_run_fail_open_when_authorization_required_but_authorizer_is_open()
    {
        // CRITICAL 1 fail-closed guard: opting into RequireAuthorization without wiring a real authorizer
        // would silently wave every contribution through the dev-open authorizer, so Start must refuse.
        _dbPath = ServiceTestFixtures.NewDbPath();
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => _service = SnapshotService.Start(
            ServiceTestFixtures.NewOptions(_dbPath), worker: null, database: _db,
            authorizer: null, gitContent: null,
            contributionPolicy: new ContributionPolicy { RequireAuthorization = true }));
        StringAssert.Contains(ex.Message, "RequireAuthorization");
    }

    [TestMethod]
    public void Start_refuses_to_run_fail_open_when_git_verification_required_but_provider_is_unavailable()
    {
        _dbPath = ServiceTestFixtures.NewDbPath();
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => _service = SnapshotService.Start(
            ServiceTestFixtures.NewOptions(_dbPath), worker: null, database: _db,
            authorizer: null, gitContent: null,
            contributionPolicy: new ContributionPolicy { RequireGitContentVerification = true }));
        StringAssert.Contains(ex.Message, "RequireGitContentVerification");
    }

    [TestMethod]
    public async Task Git_content_mismatch_is_rejected_when_verification_required()
    {
        var service = Start(
            policy: new ContributionPolicy { RequireGitContentVerification = true },
            gitContent: new FixedGitContentProvider(GitContentCheck.Mismatch));
        var artifact = ContributionTestFixtures.BuildArtifact(Repo, Commit, CapLinux, [new PayloadProjectSpec("src/App/App.csproj", "net8.0")]);

        var result = await service.IngestContributionAsync(new IngestContributionRequest { Artifact = artifact.ToArray() });

        AssertRejected(result, ContributionRejectionCode.ContentMismatch);
    }

    [TestMethod]
    public async Task Git_content_unavailable_is_accepted_when_verification_not_required()
    {
        // Dev-default posture: unwired git provider ⇒ content check skipped ⇒ contribution still publishes.
        var service = Start();
        var artifact = ContributionTestFixtures.BuildArtifact(Repo, Commit, CapLinux, [new PayloadProjectSpec("src/App/App.csproj", "net8.0")]);

        var result = await service.IngestContributionAsync(new IngestContributionRequest { Artifact = artifact.ToArray() });

        Assert.IsTrue(result.Accepted);
        Assert.AreEqual(ContributionIngestStatus.Complete, result.Status);
    }

    [TestMethod]
    public async Task Malformed_artifact_is_rejected_before_touching_the_catalog()
    {
        var service = Start();
        var result = await service.IngestContributionAsync(new IngestContributionRequest { Artifact = "not-a-sextant-artifact-blob"u8.ToArray() });

        Assert.IsFalse(result.Accepted);
        Assert.AreEqual(ContributionIngestStatus.Rejected, result.Status);
        Assert.AreEqual(ContributionRejectionCode.MalformedArtifact, result.RejectionCode);
    }

    [TestMethod]
    public async Task Oversized_artifact_is_rejected()
    {
        var service = Start(policy: new ContributionPolicy { MaxArtifactBytes = 32 });
        var artifact = ContributionTestFixtures.BuildArtifact(Repo, Commit, CapLinux, [new PayloadProjectSpec("src/App/App.csproj", "net8.0")]);

        var result = await service.IngestContributionAsync(new IngestContributionRequest { Artifact = artifact.ToArray() });

        Assert.IsFalse(result.Accepted);
        Assert.AreEqual(ContributionRejectionCode.SizeLimit, result.RejectionCode);
    }

    // ---- criterion 4: CI publishes native project versions assembled into ONE snapshot ------------------

    [TestMethod]
    public async Task Two_capability_contributions_assemble_into_one_snapshot()
    {
        var service = Start();

        // A Linux CI runner contributes the cross-platform project version; a Windows CI runner contributes
        // the win-specific one. They target the SAME capability-less assembly snapshot for the same commit.
        var linux = ContributionTestFixtures.BuildArtifact(Repo, Commit, CapLinux, [new PayloadProjectSpec("src/Core/Core.csproj", "net8.0")]);
        var windows = ContributionTestFixtures.BuildArtifact(Repo, Commit, CapWindows, [new PayloadProjectSpec("src/Win/Win.csproj", "net8.0-windows")]);

        // First contribution does NOT finalize (assembly still in progress) — snapshot stays pending.
        var first = await service.IngestContributionAsync(new IngestContributionRequest { Artifact = linux.ToArray(), Finalize = false });
        Assert.AreEqual(ContributionIngestStatus.Assembling, first.Status);
        Assert.AreEqual(SnapshotStatus.Pending, new SnapshotStore(_db!.GetConnection()).GetById(first.SnapshotId!.Value)!.Status);

        // Last contribution finalizes → publishes ONE snapshot carrying BOTH project versions.
        var second = await service.IngestContributionAsync(new IngestContributionRequest { Artifact = windows.ToArray(), Finalize = true });
        Assert.AreEqual(ContributionIngestStatus.Complete, second.Status);
        Assert.AreEqual(first.SnapshotId, second.SnapshotId, "both contributions assemble into ONE snapshot");

        var (snapshotId, projectCount, capabilities) = ContributionTestFixtures.ReadAssembly(_db!, second.IdentityHash);
        Assert.AreEqual(second.SnapshotId, snapshotId);
        Assert.AreEqual(2, projectCount, "the assembled snapshot carries both project versions");
        CollectionAssert.AreEquivalent(new[] { CapLinux, CapWindows }, capabilities,
            "each imported project version keeps its producing capability fingerprint");
    }

    [TestMethod]
    public async Task Contributing_to_an_already_complete_snapshot_records_provenance_without_mutation()
    {
        var service = Start();
        var first = ContributionTestFixtures.BuildArtifact(Repo, Commit, CapLinux, [new PayloadProjectSpec("src/App/App.csproj", "net8.0")]);
        var published = await service.IngestContributionAsync(new IngestContributionRequest { Artifact = first.ToArray(), Finalize = true });
        Assert.AreEqual(ContributionIngestStatus.Complete, published.Status);

        // A DISTINCT contribution (different capability) arrives for the already-published assembly snapshot.
        var late = ContributionTestFixtures.BuildArtifact(Repo, Commit, CapWindows, [new PayloadProjectSpec("src/Win/Win.csproj", "net8.0-windows")]);
        var result = await service.IngestContributionAsync(new IngestContributionRequest { Artifact = late.ToArray(), Finalize = true });

        Assert.IsTrue(result.Accepted);
        Assert.AreEqual(ContributionIngestStatus.Complete, result.Status);
        Assert.AreEqual(published.SnapshotId, result.SnapshotId);
        // The complete snapshot is NOT mutated: it still carries only the originally-published project.
        var (_, projectCount, _) = ContributionTestFixtures.ReadAssembly(_db!, result.IdentityHash);
        Assert.AreEqual(1, projectCount, "a complete snapshot is immutable — the late contribution never mutates it");
    }

    // ---- helpers ----------------------------------------------------------------------------------------

    private static void AssertRejected(IngestContributionResult result, string expectedCode)
    {
        Assert.IsFalse(result.Accepted, $"expected rejection {expectedCode} but was accepted ({result.Status})");
        Assert.AreEqual(ContributionIngestStatus.Rejected, result.Status);
        Assert.AreEqual(expectedCode, result.RejectionCode);
        Assert.IsTrue(result.Diagnostics.Count > 0, "a rejection must record structured diagnostics (criterion 3)");
        Assert.IsNotNull(result.JobId, "a rejection is recorded against the durable job ledger");
    }

    private SnapshotService Start(
        ContributionPolicy? policy = null,
        IContributionAuthorizer? authorizer = null,
        IGitContentProvider? gitContent = null)
    {
        _dbPath = ServiceTestFixtures.NewDbPath();
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        _service = SnapshotService.Start(
            ServiceTestFixtures.NewOptions(_dbPath), worker: null, database: _db,
            authorizer: authorizer, gitContent: gitContent, contributionPolicy: policy ?? ContributionPolicy.Default);
        return _service;
    }

    private sealed class DenyingAuthorizer(string reason) : IContributionAuthorizer
    {
        public ContributionAuthorization Authorize(ContributionAuthContext context) => ContributionAuthorization.Deny(reason);
    }

    private sealed class AllowingAuthorizer : IContributionAuthorizer
    {
        public ContributionAuthorization Authorize(ContributionAuthContext context) => ContributionAuthorization.Allow();
    }

    private sealed class FixedGitContentProvider(GitContentCheck answer) : IGitContentProvider
    {
        public GitContentCheck VerifyBlob(string repositoryRemoteUrl, string commitSha, string repoRelativePath, string expectedBlobHash) => answer;
    }
}
