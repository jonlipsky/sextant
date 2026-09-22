using Microsoft.Data.Sqlite;

namespace Sextant.Store;

/// <summary>
/// An optional Phase-9 snapshot read scope for the semantic-query stores. It has three modes:
/// <list type="bullet">
/// <item><b>Unscoped</b> (<see cref="SnapshotId"/> null, not legacy-pinned): the query is unfiltered,
/// exactly as before Phase 9. This is the state of every write-path store, every direct-seed test, and a
/// pure legacy database that has no snapshot rows at all — so their behavior is byte-for-byte unchanged.</item>
/// <item><b>Scoped</b> (<see cref="SnapshotId"/> set): a read restricts its rows to the project versions
/// mapped to that snapshot (<c>snapshot_projects</c>), which is how a scope-less MCP query transparently
/// defaults to the selected current snapshot (criterion 6) and how whole-generation reader isolation is
/// enforced (criterion 7): a reader scoped to the selected snapshot never observes another (pending/
/// superseded) snapshot's rows.</item>
/// <item><b>Legacy-pinned</b>: a read restricts its rows to the pre-Phase-9 mutable rows
/// (<c>projects.snapshot_id IS NULL</c>). This is the transition state resolved by <see cref="ForSelected"/>
/// when a single-repo database already carries snapshot-tagged rows (a first/in-progress Phase-9 build,
/// or an all-pending/failed set) but has NOT yet selected a complete snapshot: pinning to the legacy rows
/// keeps a concurrent reader on the previous complete generation and hides the half-built pending rows,
/// preserving criterion 7 across the very first upgrade rebuild (on a fresh database with no legacy rows
/// this simply reads empty until the first snapshot is atomically selected).</item>
/// </list>
/// </summary>
public sealed class SnapshotReadScope
{
    /// <summary>The unscoped, no-op scope shared by every write path and legacy/direct-seed reader.</summary>
    public static readonly SnapshotReadScope Unscoped = new(null, legacyPinned: false);

    /// <summary>Pins reads to the pre-Phase-9 mutable rows (<c>projects.snapshot_id IS NULL</c>).</summary>
    public static readonly SnapshotReadScope LegacyPinned = new(null, legacyPinned: true);

    /// <summary>
    /// A fail-CLOSED scope that matches NO rows (Phase 11, criterion 6). Returned by the read planner when
    /// a read is not authorized, so a caller that reads the scope without first consulting the
    /// authorization verdict still leaks nothing — it reads empty rather than, as an unscoped scope would,
    /// reading the entire index. The gate (<c>ReadContextGate</c>) turns the denial into a structured
    /// <c>meta.error</c>; this scope is the belt-and-suspenders that makes fail-open impossible by
    /// construction for any future call site.
    /// </summary>
    public static readonly SnapshotReadScope DenyAll = new(null, legacyPinned: false, denyAll: true);

    /// <summary>
    /// The read scope for the current selected snapshot on <paramref name="connection"/> — the MCP
    /// default so a scope-less query transparently targets the current snapshot (criterion 6). Resolves
    /// to <see cref="LegacyPinned"/> for a single-repo database that has snapshot rows but no selected
    /// complete snapshot yet (a first/in-progress build — see criterion 7), and to <see cref="Unscoped"/>
    /// for a pure legacy database (no snapshots) or a multi-repo database (Phase 11 supplies explicit
    /// scope), where reads then behave exactly as before Phase 9.
    /// </summary>
    public static SnapshotReadScope ForSelected(SqliteConnection connection)
    {
        var store = new SnapshotStore(connection);
        var selected = store.GetSelectedSnapshotId();
        if (selected.HasValue)
            return new SnapshotReadScope(selected, legacyPinned: false);

        // No complete snapshot is selected. In a single-repo database that already carries snapshot-tagged
        // project rows, a full rebuild is in flight (or a prior one failed): pin to the legacy rows so a
        // concurrent reader sees the previous complete generation only, never the half-built pending rows.
        return store.HasUnselectedSnapshotProjectRows() ? LegacyPinned : Unscoped;
    }

    /// <summary>
    /// A <b>base-only</b> diagnostic scope (Phase-11 federation mode): restricts reads to the committed
    /// base snapshot's project versions. Given the selected overlay's <c>base_snapshot_id</c>, it reads
    /// exactly the rows the overlay layers on (the pre-edit state), which is how a differential test proves
    /// that a federated read shadows a touched base row (criteria 1/2). Equivalent to a plain snapshot
    /// scope on the base id; a null base (a clean base is its own selected snapshot) yields <see
    /// cref="Unscoped"/>.
    /// </summary>
    public static SnapshotReadScope ForBaseOf(long? baseSnapshotId) =>
        baseSnapshotId.HasValue ? new SnapshotReadScope(baseSnapshotId, legacyPinned: false) : Unscoped;

    /// <summary>
    /// An <b>overlay-local-only</b> diagnostic scope (Phase-11 federation mode): restricts reads to the
    /// project versions FRESHLY re-extracted into the overlay generation (<c>projects.snapshot_id =
    /// overlayId</c>), EXCLUDING the unchanged base project-versions the overlay shares via
    /// <c>snapshot_projects</c>. This is the "just my working-tree changes" view — the complement of <see
    /// cref="ForBaseOf"/> within a federated result.
    /// </summary>
    public static SnapshotReadScope ForOverlayLocalOnly(long overlayId) =>
        new(overlayId, legacyPinned: false, overlayLocalOnly: true);

    private SnapshotReadScope(long? snapshotId, bool legacyPinned, bool overlayLocalOnly = false, bool denyAll = false)
    {
        SnapshotId = snapshotId;
        _legacyPinned = legacyPinned;
        _overlayLocalOnly = overlayLocalOnly;
        _denyAll = denyAll;
    }

    public SnapshotReadScope(long? snapshotId) : this(snapshotId, legacyPinned: false) { }

    private readonly bool _legacyPinned;

    /// <summary>When true, every scoped fragment matches NO rows (fail-closed, see <see cref="DenyAll"/>).</summary>
    private readonly bool _denyAll;

    /// <summary>
    /// When true, a snapshot-scoped read filters by the OWNING project-version's <c>projects.snapshot_id</c>
    /// (only rows freshly extracted into this generation) instead of <c>snapshot_projects</c> membership
    /// (which also includes shared base rows). Set by <see cref="ForOverlayLocalOnly"/>.
    /// </summary>
    private readonly bool _overlayLocalOnly;

    /// <summary>The selected snapshot id, or null when the scope is a no-op or legacy-pinned.</summary>
    public long? SnapshotId { get; }

    /// <summary>Whether this scope actually restricts rows (a selected snapshot, the legacy pin, or a deny-all).</summary>
    public bool IsScoped => SnapshotId.HasValue || _legacyPinned || _denyAll;

    /// <summary>
    /// A SQL JOIN fragment restricting a query's rows to the scoped snapshot's project versions (or, when
    /// legacy-pinned, to the pre-Phase-9 mutable rows) via <paramref name="projectColumn"/> (the query's
    /// project-id column, e.g. <c>s.project_id</c> or <c>o.in_project_id</c>), or "" when unscoped. Each
    /// join pins one project row per source row (unique <c>snapshot_projects</c> key / <c>projects</c>
    /// primary key), so it never duplicates results. Pair with <see cref="Bind"/>. <paramref name="projectColumn"/>
    /// is a trusted internal column name, never user input.
    /// </summary>
    public string Join(string projectColumn)
    {
        if (_denyAll)
            return " JOIN (SELECT 1) __deny ON 1 = 0";
        if (SnapshotId.HasValue)
            return _overlayLocalOnly
                ? $" JOIN projects __olp ON __olp.id = {projectColumn} AND __olp.snapshot_id = @__snap"
                : $" JOIN snapshot_projects __sp ON __sp.project_id = {projectColumn} AND __sp.snapshot_id = @__snap";
        if (_legacyPinned)
            return $" JOIN projects __lp ON __lp.id = {projectColumn} AND __lp.snapshot_id IS NULL";
        return string.Empty;
    }

    /// <summary>
    /// A SQL predicate fragment (leading with " AND ") restricting <paramref name="projectColumn"/> to the
    /// scoped snapshot's project versions (or the legacy rows when legacy-pinned), or "" when unscoped.
    /// Insert it after the existing WHERE conditions and before any GROUP BY / ORDER BY / LIMIT, and pair
    /// with <see cref="Bind"/>.
    /// </summary>
    public string And(string projectColumn)
    {
        if (_denyAll)
            return " AND 1 = 0";
        if (SnapshotId.HasValue)
            return _overlayLocalOnly
                ? $" AND {projectColumn} IN (SELECT id FROM projects WHERE snapshot_id = @__snap)"
                : $" AND {projectColumn} IN (SELECT project_id FROM snapshot_projects WHERE snapshot_id = @__snap)";
        if (_legacyPinned)
            return $" AND {projectColumn} IN (SELECT id FROM projects WHERE snapshot_id IS NULL)";
        return string.Empty;
    }

    /// <summary>
    /// A standalone WHERE clause (leading with " WHERE ") restricting <paramref name="projectColumn"/> to the
    /// scoped snapshot's project versions (or the legacy rows when legacy-pinned), or "" when unscoped. For
    /// queries that otherwise have no WHERE.
    /// </summary>
    public string Where(string projectColumn)
    {
        if (_denyAll)
            return " WHERE 1 = 0";
        if (SnapshotId.HasValue)
            return _overlayLocalOnly
                ? $" WHERE {projectColumn} IN (SELECT id FROM projects WHERE snapshot_id = @__snap)"
                : $" WHERE {projectColumn} IN (SELECT project_id FROM snapshot_projects WHERE snapshot_id = @__snap)";
        if (_legacyPinned)
            return $" WHERE {projectColumn} IN (SELECT id FROM projects WHERE snapshot_id IS NULL)";
        return string.Empty;
    }

    /// <summary>Binds the scope parameter when scoped to a snapshot; a no-op otherwise. Safe to call on every query.</summary>
    public void Bind(SqliteCommand cmd)
    {
        if (SnapshotId.HasValue)
            cmd.Parameters.AddWithValue("@__snap", SnapshotId.Value);
    }
}
