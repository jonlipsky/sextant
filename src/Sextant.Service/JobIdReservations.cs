using System.Globalization;

namespace Sextant.Service;

/// <summary>
/// Hands out <c>snapshot_jobs</c> ids before their rows can be written (issue #158). Registering a new identity's
/// job needs the single writer, which a running production holds for its whole run, so a
/// <c>POST /control/ensure?wait=false</c> that cannot get the writer within <see cref="ServiceOptions.ControlWriteWait"/>
/// answers with a RESERVED id instead: the id the identity's row is inserted with once its turn on the writer comes.
/// <para>
/// Every new job row takes its id from here (<see cref="ForRegistration"/>), so a reserved id can never be taken by
/// another identity's row. A reservation lives in memory until its row commits, or until no pending registration of
/// the identity is left (<see cref="Release"/>; a failed registration's id then reads as unknown). An id that is
/// handed to a caller before its row exists is first covered by a floor persisted next to the catalog (a small file,
/// written atomically): ids start above it after a restart, so a crash that loses the reservation never lets another
/// identity reuse the id the caller is polling. The caller then sees <c>404</c> for it, and its next ensure registers
/// the identity again.
/// </para>
/// Thread-safe. The lock is a leaf: it is never held while waiting for the writer.
/// </summary>
internal sealed class JobIdReservations(string floorPath, Func<long> maxDurableId)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Reservation> _byHash = new(StringComparer.Ordinal);
    private readonly Dictionary<long, Reservation> _byId = [];

    // The next id to allocate, and the floor persisted on disk (every id below it may have been handed out).
    private long _next = 1;
    private long _persistedFloor;

    /// <summary>The file holding the persisted id floor: next to the catalog, so it lives on the same volume.</summary>
    public static string FloorPathFor(string catalogDbPath) => catalogDbPath + ".job-id-floor";

    /// <summary>
    /// Reads the persisted floor. A missing file is floor 0; an unreadable or malformed one throws, so the service
    /// refuses to start rather than risk handing out an id a caller may still be polling.
    /// </summary>
    public void Load()
    {
        var floor = ReadFloor(floorPath);
        lock (_lock)
        {
            _persistedFloor = floor;
            _next = Math.Max(_next, floor);
        }
    }

    /// <summary>
    /// The id to insert <paramref name="identityHash"/>'s new job row with. The caller holds the writer and has
    /// checked that no row exists. A reservation already handed to a caller is used as is; otherwise a fresh id is
    /// allocated and recorded as a reservation first, so a caller asking concurrently gets the same id.
    /// </summary>
    public long ForRegistration(string identityHash, string repositoryUrl, string commitSha, string? branchName)
    {
        lock (_lock)
        {
            if (_byHash.TryGetValue(identityHash, out var reserved))
                return reserved.Id;
            return AddLocked(Allocate(), identityHash, repositoryUrl, commitSha, branchName).Id;
        }
    }

    /// <summary>
    /// The id a caller can poll for <paramref name="identityHash"/> while its registration waits for the writer: the
    /// existing reservation, else the durable row's id (<paramref name="durableId"/>), else a new reservation.
    /// <c>Existed</c> is false only for a new reservation. Returns null when a not-yet-durable id cannot be covered
    /// by the persisted floor (the file could not be written), so no id is ever handed out that a restart could reuse.
    /// </summary>
    public (long Id, bool Existed)? ForCaller(
        string identityHash, string repositoryUrl, string commitSha, string? branchName, Func<string, long?> durableId)
    {
        lock (_lock)
        {
            if (_byHash.TryGetValue(identityHash, out var reserved))
                return PersistFloorLocked(reserved.Id + 1) ? (reserved.Id, true) : null;
            if (durableId(identityHash) is long existing)
                return (existing, true);

            var id = Allocate();
            if (!PersistFloorLocked(id + 1))
                return null;
            AddLocked(id, identityHash, repositoryUrl, commitSha, branchName);
            return (id, false);
        }
    }

    /// <summary>Drops <paramref name="identityHash"/>'s reservation once its job row has committed, or once nothing is left to register it.</summary>
    public void Release(string identityHash)
    {
        lock (_lock)
        {
            if (_byHash.Remove(identityHash, out var reserved))
                _byId.Remove(reserved.Id);
        }
    }

    /// <summary>The pending reservation for a job id, or null.</summary>
    public Reservation? Find(long id)
    {
        lock (_lock)
            return _byId.GetValueOrDefault(id);
    }

    /// <summary>The pending reservation for an identity, or null.</summary>
    public Reservation? Find(string identityHash)
    {
        lock (_lock)
            return _byHash.GetValueOrDefault(identityHash);
    }

    // Above every committed row (re-read each time, so a row written by any other path is never collided with) and
    // every id this instance or a crashed predecessor may have handed out.
    private long Allocate()
    {
        _next = Math.Max(_next, maxDurableId() + 1);
        return _next++;
    }

    private Reservation AddLocked(long id, string identityHash, string repositoryUrl, string commitSha, string? branchName)
    {
        var reservation = new Reservation(
            id, identityHash, repositoryUrl, commitSha, branchName, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _byHash[identityHash] = reservation;
        _byId[id] = reservation;
        return reservation;
    }

    private bool PersistFloorLocked(long floor)
    {
        if (floor <= _persistedFloor)
            return true;
        var temp = $"{floorPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(floor.ToString(CultureInfo.InvariantCulture));
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, floorPath, overwrite: true);
            _persistedFloor = floor;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temp); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            return false;
        }
    }

    private static long ReadFloor(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return 0;
        }
        if (long.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var floor))
            return floor;
        throw new InvalidOperationException(
            $"The job-id floor file '{path}' is malformed. It must hold one non-negative integer: an id above every job id " +
            "the service may have handed out (issue #158). Set it to a value above the highest job id clients may still be " +
            "polling, then restart.");
    }

    /// <summary>A job id handed out before its row exists, with what a status read needs to describe it.</summary>
    internal sealed record Reservation(
        long Id, string IdentityHash, string RepositoryUrl, string CommitSha, string? BranchName, long CreatedAt);
}
