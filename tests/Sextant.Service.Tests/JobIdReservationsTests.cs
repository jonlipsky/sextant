namespace Sextant.Service.Tests;

/// <summary>
/// Issue #158: <see cref="JobIdReservations"/> hands out job ids before their rows exist. An id handed to a caller is
/// covered by a persisted floor, so even a crash that loses the in-memory reservation never lets a restarted service
/// give that id to another identity's job.
/// </summary>
[TestClass]
public class JobIdReservationsTests
{
    private string _dir = "";

    [TestInitialize]
    public void Init()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"sextant_jobids_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string FloorPath => JobIdReservations.FloorPathFor(Path.Combine(_dir, "catalog.db"));

    [TestMethod]
    public void IdsStartAboveTheDurableRows_AndAReservationIsSharedByCallerAndRegistration()
    {
        var ids = new JobIdReservations(FloorPath, () => 7);
        ids.Load();

        var handed = ids.ForCaller("h1", "repo", "c1", "main", _ => null);
        Assert.AreEqual((8L, false), handed);
        Assert.AreEqual((8L, true), ids.ForCaller("h1", "repo", "c1", "main", _ => null), "a second caller gets the same id");
        Assert.AreEqual(8L, ids.ForRegistration("h1", "repo", "c1", "main"), "the row is inserted under the handed-out id");
        Assert.AreEqual("h1", ids.Find(8)!.IdentityHash);
        Assert.AreEqual(8L, ids.Find("h1")!.Id);

        Assert.AreEqual(9L, ids.ForRegistration("h2", "repo", "c2", null), "another identity never takes a reserved id");

        ids.Release("h1");
        Assert.IsNull(ids.Find(8));
        Assert.IsNull(ids.Find("h1"));
    }

    [TestMethod]
    public void ACallerOfADurableIdentity_GetsItsRowId_WithoutAReservation()
    {
        var ids = new JobIdReservations(FloorPath, () => 3);
        ids.Load();

        Assert.AreEqual((2L, true), ids.ForCaller("h1", "repo", "c1", null, _ => 2));
        Assert.IsNull(ids.Find("h1"));
        Assert.IsFalse(File.Exists(FloorPath), "no floor is needed for an id that is already durable");
    }

    [TestMethod]
    public void AnIdHandedToACaller_IsNeverReusedAfterACrashThatLostItsReservation()
    {
        var before = new JobIdReservations(FloorPath, () => 3);
        before.Load();
        Assert.AreEqual((4L, false), before.ForCaller("h1", "repo", "c1", null, _ => null));

        // The process dies before h1's row is written: the durable maximum is still 3.
        var after = new JobIdReservations(FloorPath, () => 3);
        after.Load();
        Assert.AreEqual(5L, after.ForRegistration("h2", "repo", "c2", null),
            "id 4 may still be polled by the caller it was handed to, so it is never given to another identity");
    }

    [TestMethod]
    public void AnIdOnlyUsedForRegistration_DoesNotMoveTheFloor()
    {
        var ids = new JobIdReservations(FloorPath, () => 0);
        ids.Load();
        Assert.AreEqual(1L, ids.ForRegistration("h1", "repo", "c1", null));
        Assert.IsFalse(File.Exists(FloorPath), "a registration that holds the writer writes its row before any caller sees the id");
    }

    [TestMethod]
    public void AMalformedFloor_FailsStartupClosed()
    {
        File.WriteAllText(FloorPath, "not-a-number");
        var ids = new JobIdReservations(FloorPath, () => 0);
        Assert.ThrowsExactly<InvalidOperationException>(ids.Load);
    }

    [TestMethod]
    public void AnUnwritableFloor_HandsOutNoNewId()
    {
        // The floor's directory does not exist, so the floor cannot be persisted: the caller keeps waiting rather
        // than receive an id a restart could reuse.
        var ids = new JobIdReservations(Path.Combine(_dir, "missing", "catalog.db.job-id-floor"), () => 0);
        ids.Load();
        Assert.IsNull(ids.ForCaller("h1", "repo", "c1", null, _ => null));
        Assert.IsNull(ids.Find("h1"));
    }
}
