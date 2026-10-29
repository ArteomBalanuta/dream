using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;
using Dream.Multiplayer.Diagnostics;
using Dream.Server;
using Dream.Server.Persistence;
using static Dream.Tests.Integration.ObjectIdTestSupport;
using static Dream.Tests.Integration.PersistentWorldSeedDeterminismIntegrationTests;

namespace Dream.Tests.Integration;

// Wave C tester: "never reused" under adversarial crashes, on the production persistent host over
// SQLite. Every phase runs the scripted session (build, fracture storm, a wolf death and respawn,
// starter item grants for 3 players) with WriteCheckpointOnDispose = false, so the final
// checkpoint is lost like after a kill. Every id ever seen in the database is remembered across
// phases.
public sealed class WaveCTesterCrashAdversarialTests(ITestOutputHelper output)
{
    private const int PhaseTicks = CheckpointTicks + 40;

    // Phase 1 + a gracefully drained crash, then the newest row of every kind is deleted, as if the
    // tail of the write queue never committed. Counter kept (the AFTER INSERT triggers raised it
    // with the lost rows only if those rows had committed, so this is the strict variant).
    [Fact]
    public void LostNewestRows_WithTheCounterKept_NeverReissueAnyIdEverPersisted_AcrossKinds()
    {
        using TempDatabase database = new();
        HashSet<long> everSeen = [];

        RunCrashedPhase(database, everSeen, "phase 1");
        PersistedIds first = ReadIds(database.DatabasePath);
        AssertNoSharedIdsAcrossKinds(first);
        DeleteNewestRows(database.DatabasePath, perKind: 3);
        SqliteConnection.ClearAllPools();
        output.WriteLine($"after deleting newest rows: next_object_id {ReadIds(database.DatabasePath).NextObjectId}, ever seen {everSeen.Count} ids, max {everSeen.Max()}");

        long counterBefore = ReadIds(database.DatabasePath).NextObjectId;
        long maxEver = everSeen.Max();
        Assert.True(counterBefore > maxEver, "the counter must stay above every id ever persisted, even the deleted rows");

        RunCrashedPhase(database, everSeen, "phase 2", mustStartAbove: maxEver);
        PersistedIds second = ReadIds(database.DatabasePath);
        AssertNoSharedIdsAcrossKinds(second);
        foreach (long id in second.AllObjects.Concat(second.Origins).Where(id => !first.AllObjects.Contains(id) && !first.Origins.Contains(id)))
        {
            Assert.True(id > maxEver, $"new id {id} is not above every id ever persisted ({maxEver})");
        }
    }

    // The realistic kill: the lost rows and their counter bump roll back together. Ids of rows that
    // never committed may then be issued again. What must hold: no id of a surviving row is
    // issued again, and no two kinds share an id.
    [Fact]
    public void LostNewestRows_WithTheCounterRolledBackWithThem_NeverCollideWithSurvivingRows()
    {
        using TempDatabase database = new();
        HashSet<long> everSeen = [];

        RunCrashedPhase(database, everSeen, "phase 1");
        DeleteNewestRows(database.DatabasePath, perKind: 3);
        SqliteConnection.ClearAllPools();
        PersistedIds survivors = ReadIds(database.DatabasePath);
        Dictionary<long, (long Origin, long X, long Y, long Z)> survivingPieces = ReadPieceGroups(database.DatabasePath);
        long rolledBack = survivors.MaxId + 1;
        using (SqliteConnection connection = Open(database.DatabasePath, readOnly: false))
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"UPDATE worlds SET next_object_id = {rolledBack} WHERE world_id = 1;";
            command.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        RunCrashedPhase(database, everSeen, "phase 2", mustStartAbove: survivors.MaxId);
        PersistedIds second = ReadIds(database.DatabasePath);
        AssertNoSharedIdsAcrossKinds(second);
        // Every surviving row is still there with its id and was not overwritten by a different kind;
        // a rubble group may only be gone because phase 2 fractured its cell again (wave D).
        IReadOnlyList<long> replaced = AssertMissingPiecesWereReplaced(survivingPieces, ReadPieceGroups(database.DatabasePath));
        output.WriteLine($"rubble pieces replaced by a second fracture of their cell: {replaced.Count}");
        Assert.Empty(survivors.Items.Except(second.Items));
    }

    // The counter is corrupted downward between runs (1, a value inside the id range, and a
    // negative one). The heal guard must raise it above every id in the database and log that it did.
    [Theory]
    [InlineData(1L)]
    [InlineData(0L)]
    [InlineData(-7L)]
    [InlineData(0L, true)]
    public void CorruptedCounter_IsHealedAtStartup_AndLogged_BeforeAnyIdIsIssued(long corrupt, bool middleOfRange = false)
    {
        using TempDatabase database = new();
        HashSet<long> everSeen = [];
        RunCrashedPhase(database, everSeen, "phase 1");
        PersistedIds before = ReadIds(database.DatabasePath);
        long target = middleOfRange ? before.MaxId / 2 : corrupt;
        using (SqliteConnection connection = Open(database.DatabasePath, readOnly: false))
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"UPDATE worlds SET next_object_id = {target} WHERE world_id = 1;";
            command.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        List<(string Category, string Message)> lines = [];
        long maxInDb = before.MaxId;
        using (NetworkDiagnosticsLog.CaptureOnCurrentThread((category, message) => lines.Add((category, message))))
        using (ServerPersistenceBundle bundle = database.OpenBundle())
        using (DedicatedServerHost host = ServerBootstrap.CreateAuthoritativeHost(bundle))
        {
            Assert.True(host.Runtime!.ObjectIds.Next > maxInDb, $"allocator starts at {host.Runtime.ObjectIds.Next}, not above {maxInDb}");
        }

        SqliteConnection.ClearAllPools();
        (string Category, string Message) healed = Assert.Single(lines, line => line.Category == "server.persistence.object-id-counter-healed");
        output.WriteLine($"corrupted to {target}: {healed.Message}");
        Assert.Contains($"stored={target}", healed.Message, StringComparison.Ordinal);
        Assert.Contains($"healed={maxInDb + 1}", healed.Message, StringComparison.Ordinal);
        Assert.True(ReadIds(database.DatabasePath).NextObjectId > maxInDb);

        // And a full crashed run afterwards only adds ids above the healed counter.
        RunCrashedPhase(database, everSeen, "phase 2", mustStartAbove: maxInDb);
    }

    // A real kill with writes pending: the queue worker is stalled behind a blocked read, the build
    // and fracture inserts pile up behind it, and the host is disposed. The 30 s shutdown drain
    // deadline passes and the pending inserts are dropped (FailedWrites > 0). The restart must find
    // a consistent database and issue only ids above everything that committed.
    [Fact]
    public async Task KilledWithPendingInserts_RestartsConsistent_AndIssuesOnlyIdsAboveWhatCommitted()
    {
        using TempDatabase database = new();
        HashSet<long> everSeen = [];
        RunCrashedPhase(database, everSeen, "phase 1");
        PersistedIds committed = ReadIds(database.DatabasePath);

        using ManualResetEventSlim gate = new(false);
        Dream.Simulation.Persistence.PersistenceWriteQueue queue;
        Task disposal;
        PersistentScriptedServer server = new(database.OpenBundle());
        try
        {
            server.Runtime.WriteCheckpointOnDispose = false;
            queue = server.Runtime.PersistenceWrites!;
            Assert.True(queue.EnqueueRead(() => gate.Wait(TimeSpan.FromMinutes(2))));
            server.RunScript(Script, ticks: CheckpointTicks - 20);
            Assert.True(queue.PendingCount > 0, "the stalled queue should hold pending writes");
            output.WriteLine($"pending writes at the kill: {queue.PendingCount}");
        }
        catch
        {
            gate.Set();
            server.Dispose();
            throw;
        }

        disposal = Task.Run(server.Dispose);
        await Task.Delay(TimeSpan.FromSeconds(32));
        Assert.False(disposal.IsCompleted, "dispose should be waiting on the stalled worker");
        gate.Set();
        await disposal.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(queue.FailedWrites > 0, "the shutdown drain should have dropped the pending writes");
        output.WriteLine($"dropped writes: {queue.FailedWrites}");

        SqliteConnection.ClearAllPools();
        PersistedIds afterKill = ReadIds(database.DatabasePath);
        AssertNoSharedIdsAcrossKinds(afterKill);
        Assert.True(afterKill.NextObjectId > afterKill.MaxId);
        Assert.Equal("ok", Text(database.DatabasePath, "PRAGMA integrity_check;"));
        Assert.Equal(0L, Scalar(database.DatabasePath, "SELECT COUNT(*) FROM pragma_foreign_key_check;"));
        // The killed run's inserts did not commit: nothing above the committed counter appeared.
        Assert.Equal(committed.NextObjectId, afterKill.NextObjectId);

        RunCrashedPhase(database, everSeen, "phase 3", mustStartAbove: afterKill.MaxId);
    }

    private void RunCrashedPhase(TempDatabase database, HashSet<long> everSeen, string label, long mustStartAbove = 0)
    {
        using (PersistentScriptedServer server = new(database.OpenBundle()))
        {
            server.Runtime.WriteCheckpointOnDispose = false;
            Assert.True(server.Runtime.ObjectIds.Next > mustStartAbove, $"{label}: allocator at {server.Runtime.ObjectIds.Next}, not above {mustStartAbove}");
            server.RunScript(Script, ticks: PhaseTicks);
        }

        SqliteConnection.ClearAllPools();
        PersistedIds ids = ReadIds(database.DatabasePath);
        output.WriteLine($"{label}: cubes {ids.Cubes.Count} pieces {ids.Pieces.Count} origins {ids.Origins.Count} enemies {ids.Enemies.Count} items {ids.Items.Count} next_object_id {ids.NextObjectId} max {ids.MaxId}");
        foreach (long id in ids.AllObjects.Concat(ids.Origins))
        {
            everSeen.Add(id);
        }

        Assert.All(ids.AllObjects.Concat(ids.Origins), id => Assert.True(id < ids.NextObjectId, $"{label}: id {id} is not below the stored counter {ids.NextObjectId}"));
        AssertNoSharedIdsAcrossKinds(ids);
    }

    // Cube, piece, enemy and item ids are one namespace; rubble origins are retired cube ids, so
    // they must not equal a live id of any kind either.
    private static void AssertNoSharedIdsAcrossKinds(PersistedIds ids)
    {
        Assert.Equal(ids.AllObjects.Count, ids.AllObjects.Distinct().Count());
        Assert.Empty(ids.Origins.Intersect(ids.AllObjects));
    }

    private static void DeleteNewestRows(string databasePath, int perKind)
    {
        SqliteConnection.ClearAllPools();
        using SqliteConnection connection = Open(databasePath, readOnly: false);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            PRAGMA foreign_keys = OFF;
            DELETE FROM player_inventory_slots WHERE item_instance_id IN (SELECT item_instance_id FROM item_instances ORDER BY item_instance_id DESC LIMIT {perKind});
            DELETE FROM item_instances WHERE item_instance_id IN (SELECT item_instance_id FROM item_instances ORDER BY item_instance_id DESC LIMIT {perKind});
            DELETE FROM cube_fracture_pieces WHERE object_id IN (SELECT object_id FROM cube_fracture_pieces ORDER BY object_id DESC LIMIT {perKind});
            DELETE FROM enemy_instances WHERE object_id IN (SELECT object_id FROM enemy_instances ORDER BY object_id DESC LIMIT {perKind});
            DELETE FROM placed_cube_instances WHERE object_id IN (SELECT object_id FROM placed_cube_instances ORDER BY object_id DESC LIMIT {perKind});
            """;
        command.ExecuteNonQuery();
    }

    private static string Text(string databasePath, string sql)
    {
        using SqliteConnection connection = Open(databasePath);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return (string)command.ExecuteScalar()!;
    }
}
