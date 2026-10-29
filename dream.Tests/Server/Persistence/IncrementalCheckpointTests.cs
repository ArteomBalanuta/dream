using System.Globalization;
using System.Reflection;
using System.Text;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server;
using Dream.Server.Persistence;
using Dream.Simulation.Combat;
using Dream.Simulation.Enemies;
using Dream.Simulation.Persistence;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Tests.Utilities;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;

namespace Dream.Tests.Server.Persistence;

// SCALE-4: checkpoints capture only what changed since the previous capture (with a full sweep
// every FullCheckpointInterval captures and on shutdown), and the background writer merges queued
// incremental checkpoints. The database must end up exactly where full checkpoints would put it.
[Collection(TimingSensitiveCollection.Name)]
public sealed class IncrementalCheckpointTests
{
    private const string PlayerName = "player-one";
    private const int TicksPerCheckpoint = 320;
    private const int TicksPerAdvance = 8;
    private const int FloorSize = 16;

    private readonly ITestOutputHelper _output;

    public IncrementalCheckpointTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // ---- Merge ----

    [Fact]
    public void Merge_LastWriteWinsPerKey_AndDeleteThenReAddKeepsReAdd()
    {
        PersistenceCheckpoint first = Incremental(
            cubes: [Cube(1, health: 90), Cube(2, health: 80)],
            enemies: [Enemy(50, hp: 30)],
            pieces: [Piece(8, 0, x: 1f), Piece(8, 1, x: 1f)]);
        PersistenceCheckpoint second = Incremental(
            cubes: [Cube(1, health: 70)],
            removedCubes: [2],
            removedEnemies: [50],
            removedGroups: [8]);
        PersistenceCheckpoint third = Incremental(
            cubes: [Cube(2, health: 60), Cube(3, health: 50)],
            enemies: [Enemy(51, hp: 20)],
            pieces: [Piece(8, 1, x: 2f)]);

        PersistenceCheckpoint merged = PersistenceCheckpoint.Merge(PersistenceCheckpoint.Merge(first, second), third);

        Assert.True(merged.IsIncremental);
        Assert.Equal([(1, 70), (2, 60), (3, 50)], merged.PlacedCubes.Select(cube => ((int)cube.ObjectId.Value, cube.CurrentHealth)));
        Assert.Empty(merged.RemovedCubeObjectIds);
        Assert.Equal([51], merged.Enemies.Select(enemy => (int)enemy.ObjectId.Value));
        Assert.Equal(new[] { new ObjectId(50) }, merged.RemovedEnemyObjectIds);
        // The group was removed and then got a piece again: the re-add wins, the older pieces stay dropped.
        PersistedCubeFracturePiece piece = Assert.Single(merged.FracturePieces);
        Assert.Equal((8, 1, 2f), ((int)piece.OriginObjectId.Value, piece.PieceIndex, piece.Center.X));
        Assert.Empty(merged.RemovedFractureOriginObjectIds);
    }

    [Fact]
    public void Merge_TombstoneDropsOlderRow_AndFullNewerKeepsOlderRowsItDoesNotHold()
    {
        PersistenceCheckpoint first = Incremental(cubes: [Cube(1, health: 90), Cube(2, health: 80)]);
        PersistenceCheckpoint removal = Incremental(removedCubes: [1]);

        PersistenceCheckpoint merged = PersistenceCheckpoint.Merge(first, removal);
        Assert.Equal([2], merged.PlacedCubes.Select(cube => (int)cube.ObjectId.Value));
        Assert.Equal(new[] { new ObjectId(1) }, merged.RemovedCubeObjectIds);

        PersistenceCheckpoint full = new([], [], [], [Cube(2, health: 10), Cube(5, health: 10)], []);
        PersistenceCheckpoint mergedFull = PersistenceCheckpoint.Merge(merged, full);
        Assert.False(mergedFull.IsIncremental);
        Assert.Equal([(2, 10), (5, 10)], mergedFull.PlacedCubes.Select(cube => ((int)cube.ObjectId.Value, cube.CurrentHealth)));
        Assert.Same(full, PersistenceCheckpoint.Merge(PersistenceCheckpoint.Empty, full));

        // An incremental checkpoint folded into a full one keeps the result full.
        PersistenceCheckpoint fullThenIncremental = PersistenceCheckpoint.Merge(full, Incremental(cubes: [Cube(5, health: 4)]));
        Assert.False(fullThenIncremental.IsIncremental);
        Assert.Equal([(2, 10), (5, 4)], fullThenIncremental.PlacedCubes.Select(cube => ((int)cube.ObjectId.Value, cube.CurrentHealth)));
    }

    [Fact]
    public void Merge_PlayerRowsAreKeyedByNameIgnoringCase()
    {
        PersistenceCheckpoint first = Incremental() with
        {
            PlayerVitals = [new PlayerCheckpointVitals("Alice", PlayerInteractionMode.Building, 50, 100)]
        };
        PersistenceCheckpoint second = Incremental() with
        {
            PlayerVitals = [new PlayerCheckpointVitals("alice ", PlayerInteractionMode.Combat, 40, 100)]
        };

        PlayerCheckpointVitals vitals = Assert.Single(PersistenceCheckpoint.Merge(first, second).PlayerVitals);
        Assert.Equal(40, vitals.CurrentHealth);
    }

    [Fact]
    public void BackgroundWriter_MergesQueuedIncrementalCheckpoints_IntoOneWrite()
    {
        BlockingWriter writer = new();
        using BackgroundCheckpointWriter background = new(writer);
        PersistenceCheckpoint first = Incremental(cubes: [Cube(1, health: 90)]);

        background.Enqueue(first);
        Assert.True(writer.Started.Wait(TimeSpan.FromSeconds(5)));
        background.Enqueue(Incremental(cubes: [Cube(1, health: 80), Cube(2, health: 70)]));
        background.Enqueue(Incremental(removedCubes: [2], cubes: [Cube(1, health: 60)]));
        background.Enqueue(Incremental(cubes: [Cube(2, health: 50)]));
        writer.Release.Set();
        Assert.True(background.Flush(TimeSpan.FromSeconds(5)));

        Assert.Equal(2, background.CompletedWrites);
        Assert.Equal(2, background.MergedCheckpoints);
        Assert.Equal(2, background.SupersededCheckpoints);
        Assert.Same(first, writer.Written[0]);
        PersistenceCheckpoint merged = writer.Written[1];
        Assert.Equal([(1, 60), (2, 50)], merged.PlacedCubes.Select(cube => ((int)cube.ObjectId.Value, cube.CurrentHealth)));
        Assert.Empty(merged.RemovedCubeObjectIds);
        Assert.Equal(2, background.LastWriteRows);
        Assert.Equal(3, background.TotalWrittenRows);
        Assert.Equal(2, background.IncrementalWrites);
    }

    [Fact]
    public void BackgroundWriter_FailedIncrementalWrite_IsWrittenUnderTheNextCheckpoint()
    {
        FailingOnceWriter writer = new();
        using BackgroundCheckpointWriter background = new(writer);

        background.Enqueue(Incremental(cubes: [Cube(1, health: 90), Cube(2, health: 80)]));
        Assert.True(background.Flush(TimeSpan.FromSeconds(5)));
        background.Enqueue(Incremental(cubes: [Cube(2, health: 70)]));
        Assert.True(background.Flush(TimeSpan.FromSeconds(5)));

        Assert.Equal(1, background.FailedWrites);
        PersistenceCheckpoint written = Assert.Single(writer.Written);
        Assert.Equal([(1, 90), (2, 70)], written.PlacedCubes.Select(cube => ((int)cube.ObjectId.Value, cube.CurrentHealth)));
    }

    [Fact]
    public void BackgroundWriter_WaitsForImmediateWritesQueuedBeforeTheCapture()
    {
        BlockingWriter writer = new();
        writer.Release.Set();
        using BackgroundCheckpointWriter background = new(writer);
        TaskCompletionSource committed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        background.Enqueue(Incremental(cubes: [Cube(1, health: 90)]) with { ImmediateWritesCommitted = committed.Task });
        Assert.False(writer.Started.Wait(TimeSpan.FromMilliseconds(200)), "The checkpoint was written before the immediate writes committed.");

        committed.SetResult();
        Assert.True(background.Flush(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, background.CompletedWrites);
    }

    [Fact]
    public void SqliteWriter_MergedCheckpoint_UpdatesRowsAndIgnoresTombstones()
    {
        using TempDatabase database = new();
        SqliteWorldPersistenceRepository world = new(database.Schema);
        new SqlitePlayerProfileRepository(database.Schema).GetOrCreate(PlayerName);
        world.SavePlacedCube(Cube(1, health: 100));
        world.SavePlacedCube(Cube(2, health: 100));
        // Cube 2 is destroyed by the immediate queue and re-placed with the same id before the
        // merged checkpoint lands: the tombstone must not delete the re-added row.
        world.DeletePlacedCube(new ObjectId(2));
        world.SavePlacedCube(Cube(2, health: 100));

        PersistenceCheckpoint merged = PersistenceCheckpoint.Merge(
            Incremental(cubes: [Cube(1, health: 40)], removedCubes: [2]),
            Incremental(cubes: [Cube(1, health: 30)], removedCubes: [2]));
        new SqlitePersistenceCheckpointWriter(database.Schema).Write(merged);

        Dictionary<int, int> health = world.LoadPlacedCubes().ToDictionary(cube => (int)cube.ObjectId.Value, cube => cube.CurrentHealth);
        Assert.Equal(30, health[1]);
        Assert.Equal(100, health[2]);
    }

    // ---- Dirty tracking ----

    [Fact]
    public void Registry_RemoveThenReAddSameId_ClearsTheTombstone_AndDamageMarksDirty()
    {
        Map map = new(physics: null, generateStartupTerrain: false, width: 10, depth: 10);
        WorldObjectRegistry registry = new();
        registry.EnableCheckpointTracking();
        Cube first = Assert.IsType<Cube>(map.TryAddCube(new Vector3(3f, 1f, 3f)));
        WorldObjectRecord record = registry.RegisterCube(first, map, actorId: 4000, ownerPlayerName: PlayerName);
        registry.ClearCheckpointChanges();

        record.ApplyDamage(5);
        Assert.Contains(first, registry.CheckpointDirtyCubes);
        registry.ClearCheckpointChanges();

        Assert.True(registry.TryRemove(record));
        Assert.Contains(4000, registry.CheckpointRemovedCubeIds);

        Cube second = Assert.IsType<Cube>(map.TryAddCube(new Vector3(3f, 1f, 3f)));
        registry.RegisterCube(second, map, actorId: 4000, ownerPlayerName: PlayerName);
        Assert.DoesNotContain(4000, registry.CheckpointRemovedCubeIds);
        Assert.Contains(second, registry.CheckpointDirtyCubes);

        // A removed record's damage no longer marks anything.
        registry.ClearCheckpointChanges();
        record.ApplyDamage(1);
        Assert.Empty(registry.CheckpointDirtyCubes);
    }

    [Fact]
    public void IncrementalCapture_HoldsOnlyChangedRows()
    {
        using TempDatabase database = new();
        using ScriptedSession session = new(database, fullCheckpointInterval: 12, joinPlayer: false);
        for (int i = 0; i < 6; i++)
        {
            session.AddOwnedCube(new Vector3(2f + i, 1f, 12f));
        }

        WorldSessionRuntime runtime = session.Runtime;
        PersistenceCheckpoint full = Assert.IsType<PersistenceCheckpoint>(runtime.CapturePersistenceCheckpoint());
        Assert.False(full.IsIncremental);
        Assert.Equal(6, full.PlacedCubes.Count);

        // Nothing changed (the resting cubes and the wolf, which stands still with no player).
        session.Advance(ticks: 1);
        PersistenceCheckpoint? idle = runtime.CapturePersistenceCheckpoint();
        Assert.True(idle == null || (idle.PlacedCubes.Count == 0 && idle.FracturePieces.Count == 0),
            $"Idle capture held {idle?.PlacedCubes.Count} cubes.");

        Cube moved = Assert.IsType<Cube>(runtime.Map.TryGetCubeAtCell(4, 1, 12));
        runtime.Map.UpdateCubePosition(moved, moved.Position + new Vector3(0.1f, 0f, 0f), syncPhysics: false);
        Cube damaged = Assert.IsType<Cube>(runtime.Map.TryGetCubeAtCell(6, 1, 12));
        WorldObjectRegistry registry = GetWorldObjects(runtime);
        Assert.IsType<WorldObjectRecord>(registry.TryGet(damaged)).ApplyDamage(3);
        WorldObjectRecord removed = Assert.IsType<WorldObjectRecord>(registry.TryGet(runtime.Map.TryGetCubeAtCell(7, 1, 12)!));
        Assert.True(runtime.ObjectIdentities.TryGetObjectId(removed.Actor.Id, out ObjectId removedObjectId));
        DeletePersistedPlacedCube(runtime, removed.Actor.Id);
        Assert.True(registry.TryRemove(removed));

        PersistenceCheckpoint incremental = Assert.IsType<PersistenceCheckpoint>(runtime.CapturePersistenceCheckpoint());
        Assert.True(incremental.IsIncremental);
        Assert.Equal(2, incremental.PlacedCubes.Count);
        Assert.Contains(incremental.PlacedCubes, cube => cube.CenterPosition == moved.Position);
        Assert.Contains(incremental.PlacedCubes, cube => cube.CurrentHealth == 97);
        Assert.Equal(new[] { removedObjectId }, incremental.RemovedCubeObjectIds);
        Assert.Equal(1, runtime.FullCheckpointCaptures);
        Assert.Equal(2, runtime.IncrementalCheckpointCaptures);
    }

    [Fact]
    public void EveryFullCheckpointIntervalCaptures_IsAFullSweep()
    {
        using TempDatabase database = new();
        using ScriptedSession session = new(database, fullCheckpointInterval: 3, joinPlayer: false);
        session.AddOwnedCube(new Vector3(3f, 1f, 12f));
        WorldSessionRuntime runtime = session.Runtime;

        bool[] kinds = new bool[7];
        for (int i = 0; i < kinds.Length; i++)
        {
            Cube cube = Assert.IsType<Cube>(runtime.Map.TryGetCubeAtCell(3, 1, 12));
            runtime.Map.UpdateCubePosition(cube, new Vector3(3.5f + (i % 2 == 0 ? 0.1f : 0f), 1.5f, 12.5f), syncPhysics: false);
            kinds[i] = Assert.IsType<PersistenceCheckpoint>(runtime.CapturePersistenceCheckpoint()).IsIncremental;
        }

        Assert.Equal([false, true, true, false, true, true, false], kinds);
    }

    // ---- Restart equivalence ----

    // A scripted session (builds, falls, moves, damage, fractures, removals and re-adds in the same
    // cell, wolf kills and respawns) checkpoints through the dedicated host every 5 seconds. At a
    // checkpoint tick, the rows the incremental checkpoints left in the database must equal the
    // rows a full checkpoint writes at that moment, and a restart from that database must restore
    // the same world as a run that checkpointed fully every time. With a slow writer the queued
    // incremental checkpoints are merged, and the result must still match.
    [Theory]
    [InlineData(WorldSessionRuntime.DefaultFullCheckpointInterval, false)]
    [InlineData(3, false)]
    [InlineData(WorldSessionRuntime.DefaultFullCheckpointInterval, true)]
    [InlineData(3, true)]
    public void IncrementalCheckpoints_RestoreTheSameWorldAsFullCheckpoints(int fullCheckpointInterval, bool slowWriter)
    {
        using TempDatabase incrementalDatabase = new();
        using TempDatabase fullDatabase = new();
        ScriptOutcome incremental;
        ScriptOutcome full;
        List<string> incrementalRows;
        List<string> rowsAfterFullCheckpoint;
        using (ScriptedSession session = new(
            incrementalDatabase,
            fullCheckpointInterval,
            checkpointWriteDelay: slowWriter ? TimeSpan.FromMilliseconds(60) : default)
        {
            FlushEachPhase = !slowWriter
        })
        {
            incremental = session.RunScript();
            _output.WriteLine(string.Join(Environment.NewLine, session.Log));
            incrementalRows = ReadDatabase(incrementalDatabase);
            session.Runtime.PersistCheckpoint();
            rowsAfterFullCheckpoint = ReadDatabase(incrementalDatabase);
            Assert.True(session.Runtime.IncrementalCheckpointCaptures > 0);
            if (slowWriter)
            {
                Assert.True(session.Writes.MergedCheckpoints > 0, "The slow writer never fell behind, so nothing was merged.");
            }

            session.Runtime.WriteCheckpointOnDispose = false;
        }

        using (ScriptedSession session = new(fullDatabase, fullCheckpointInterval: 1) { FlushEachPhase = true })
        {
            full = session.RunScript();
            Assert.Equal(0, session.Runtime.IncrementalCheckpointCaptures);
            session.Runtime.WriteCheckpointOnDispose = false;
        }

        _output.WriteLine($"incremental: {incremental}");
        _output.WriteLine($"full:        {full}");
        Assert.True(incremental.Fractures >= 3, $"Only {incremental.Fractures} fractures.");
        Assert.True(incremental.WolfKills >= 1, $"Only {incremental.WolfKills} wolf kills.");
        Assert.True(incremental.ReAddsInFracturedCell >= 2, $"Only {incremental.ReAddsInFracturedCell} re-adds.");
        Assert.True(incremental.FullWrites >= 1);
        // A slow writer can fold every incremental checkpoint into a waiting full one.
        Assert.True(slowWriter || incremental.IncrementalWrites > 0, "No incremental checkpoint was written.");

        foreach (string row in rowsAfterFullCheckpoint.Except(incrementalRows))
        {
            _output.WriteLine($"full only:        {row}");
        }

        foreach (string row in incrementalRows.Except(rowsAfterFullCheckpoint))
        {
            _output.WriteLine($"incremental only: {row}");
        }

        Assert.Equal(rowsAfterFullCheckpoint, incrementalRows);
        List<string> fullRows = ReadDatabase(fullDatabase);
        Assert.Equal(fullRows, incrementalRows);

        string incrementalWorld = DescribeRestoredWorld(incrementalDatabase);
        string fullWorld = DescribeRestoredWorld(fullDatabase);
        _output.WriteLine($"restored world checksum: incremental {Checksum(incrementalWorld):X16}, full {Checksum(fullWorld):X16}");
        Assert.Equal(fullWorld, incrementalWorld);
    }

    [Fact]
    public void CrashBetweenIncrementalCheckpoints_RestoresTheLastCommittedCheckpoint()
    {
        using TempDatabase database = new();
        List<string> committedRows;
        string committedWorld;
        using (ScriptedSession session = new(database, fullCheckpointInterval: 12))
        {
            session.RunScript();
            committedRows = ReadDatabase(database);
            committedWorld = DescribeRestoredWorld(database);

            // Volatile changes only (no immediate writes): moves, damage and a few seconds of
            // simulation, then a checkpoint whose write fails, then the process dies.
            WorldSessionRuntime runtime = session.Runtime;
            WorldObjectRegistry registry = GetWorldObjects(runtime);
            foreach (WorldObjectRecord record in registry.GetCubeRecords().Take(3))
            {
                record.ApplyDamage(1);
            }

            session.Advance(ticks: TicksPerCheckpoint - TicksPerAdvance);
            PersistenceCheckpoint? lost = runtime.CapturePersistenceCheckpoint();
            Assert.NotNull(lost);
            Assert.True(lost.IsIncremental);
            Assert.ThrowsAny<Exception>(() => new SqlitePersistenceCheckpointWriter(database.Schema).Write(
                lost with { PlacedCubes = [.. lost.PlacedCubes, Cube((int)lost.PlacedCubes[0].ObjectId.Value, health: 999)] }));
            runtime.WriteCheckpointOnDispose = false;
        }

        Assert.Equal(committedRows, ReadDatabase(database));
        Assert.Equal(committedWorld, DescribeRestoredWorld(database));
        Assert.Equal("ok", database.IntegrityCheck());
    }

    [Fact]
    public void Shutdown_WritesAFinalFullCheckpoint()
    {
        using TempDatabase database = new();
        Vector3 movedCenter;
        ObjectId cubeId;
        using (ScriptedSession session = new(database, fullCheckpointInterval: 12, joinPlayer: false))
        {
            session.AddOwnedCube(new Vector3(3f, 1f, 12f));
            session.Advance(TicksPerCheckpoint);
            Cube cube = Assert.IsType<Cube>(session.Runtime.Map.TryGetCubeAtCell(3, 1, 12));
            Assert.True(session.Runtime.Map.TryGetReplicatedId(cube, out int cubeActorId));
            Assert.True(session.Runtime.ObjectIdentities.TryGetObjectId(cubeActorId, out cubeId));
            movedCenter = cube.Position + new Vector3(0.2f, 0f, 0.1f);
            session.Runtime.Map.UpdateCubePosition(cube, movedCenter, syncPhysics: false);
        }

        PersistedPlacedCube saved = new SqliteWorldPersistenceRepository(database.Schema)
            .LoadPlacedCubes()
            .Single(cube => cube.ObjectId == cubeId);
        Assert.Equal(movedCenter, saved.CenterPosition);
    }

    // ---- Allocation ----

    [Fact]
    public void IdleTick_AndIdleCapture_AllocateNothing_AndDirtyCaptureIsBounded()
    {
        using TempDatabase database = new();
        using ScriptedSession session = new(database, fullCheckpointInterval: int.MaxValue, joinPlayer: false);
        List<Cube> cubes = [];
        for (int i = 0; i < 12; i++)
        {
            cubes.Add(session.AddOwnedCube(new Vector3(1f + i, 1f, 13f)));
        }

        WorldSessionRuntime runtime = session.Runtime;
        session.Advance(ticks: 64);
        Assert.NotNull(runtime.CapturePersistenceCheckpoint());

        // An idle host tick with tracking on: nothing is marked, nothing is allocated.
        long tickBytes = AllocationAssert.AtMost(0, () => session.Advance(ticks: 1));
        long idleCaptureBytes = AllocationAssert.AtMost(0, () => runtime.CapturePersistenceCheckpoint());

        // k moved cubes: the capture copies k rows into one exact-size array plus a constant.
        const int Moved = 8;
        int flip = 0;
        long dirtyCaptureBytes = AllocationAssert.MeasureBytesPerIteration(() =>
        {
            flip ^= 1;
            for (int i = 0; i < Moved; i++)
            {
                Cube cube = cubes[i];
                runtime.Map.UpdateCubePosition(cube, cube.Position + new Vector3(flip == 0 ? -0.01f : 0.01f, 0f, 0f), syncPhysics: false);
            }

            PersistenceCheckpoint captured = runtime.CapturePersistenceCheckpoint()!;
            if (captured.PlacedCubes.Count != Moved)
            {
                Assert.Fail($"captured {captured.PlacedCubes.Count}: full={!captured.IsIncremental} " + string.Join(";", captured.PlacedCubes.Select(c => $"{(int)c.ObjectId.Value}@{c.CenterPosition}")));
            }
        });
        long budget = 512 + (Moved * 128);
        _output.WriteLine($"idle tick {tickBytes} B, idle capture {idleCaptureBytes} B, capture of {Moved} moved cubes {dirtyCaptureBytes} B (budget {budget} B)");
        Assert.True(dirtyCaptureBytes <= budget, $"A capture of {Moved} cubes allocated {dirtyCaptureBytes} B; budget {budget} B.");
    }

    // ---- Helpers ----

    private static PersistenceCheckpoint Incremental(
        PersistedPlacedCube[]? cubes = null,
        int[]? removedCubes = null,
        PersistedEnemyInstance[]? enemies = null,
        int[]? removedEnemies = null,
        PersistedCubeFracturePiece[]? pieces = null,
        int[]? removedGroups = null)
    {
        return new PersistenceCheckpoint([], [], enemies ?? [], cubes ?? [], pieces ?? [])
        {
            IsIncremental = true,
            RemovedCubeObjectIds = ToObjectIds(removedCubes),
            RemovedEnemyObjectIds = ToObjectIds(removedEnemies),
            RemovedFractureOriginObjectIds = ToObjectIds(removedGroups)
        };
    }

    private static ObjectId[] ToObjectIds(int[]? ids) =>
        ids == null ? [] : ids.Select(id => new ObjectId(id)).ToArray();

    private static PersistedPlacedCube Cube(int id, int health)
    {
        return new PersistedPlacedCube(
            new ObjectId(id),
            PlayerName,
            new Vector3(id, 1f, 3f),
            GeometryShapeKind.SolidCube,
            GeometryMaterialId.Dirt,
            health,
            MaxHealth: 100,
            DateTime.UtcNow,
            DateTime.UtcNow,
            new Vector3(id + 0.5f, 1.5f, 3.5f));
    }

    private static PersistedEnemyInstance Enemy(int id, int hp)
    {
        return new PersistedEnemyInstance(
            new ObjectId(id),
            "wolf",
            "Wolf",
            new Vector3(1f, 1f, 1f),
            Vector3.UnitZ,
            new PersistedCreatureStats(hp, 30, 10, 10, 10, 0, 2.8f, 0.95f, 2),
            DateTime.UtcNow,
            DateTime.UtcNow);
    }

    private static PersistedCubeFracturePiece Piece(int sourceCubeId, int index, float x)
    {
        return new PersistedCubeFracturePiece(
            new ObjectId(1000 + sourceCubeId * 10 + index), new ObjectId(sourceCubeId),
            index,
            PlayerName,
            new Vector3(2f, 1f, 2f),
            new Vector3(x, 1.1f, 2.2f),
            0.2f,
            GeometryMaterialId.Dirt,
            DateTime.UtcNow,
            DateTime.UtcNow);
    }

    private static WorldObjectRegistry GetWorldObjects(WorldSessionRuntime runtime)
    {
        FieldInfo field = typeof(WorldSessionRuntime).GetField("_worldObjects", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (WorldObjectRegistry)field.GetValue(runtime)!;
    }

    // Deletes a cube's persisted row the way the combat resolver does (the runtime's private forwarder).
    private static void DeletePersistedPlacedCube(WorldSessionRuntime runtime, int cubeActorId) =>
        typeof(WorldSessionRuntime)
            .GetMethod("DeletePersistedPlacedCube", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(runtime, [cubeActorId]);

    private static string F(float value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static string V(Vector3 value) => $"({F(value.X)},{F(value.Y)},{F(value.Z)})";

    // Every persisted row, timestamps excluded, in a stable order.
    private static List<string> ReadDatabase(TempDatabase database)
    {
        SqliteWorldPersistenceRepository world = new(database.Schema);
        List<string> rows = [];
        foreach (PersistedPlacedCube cube in world.LoadPlacedCubes().OrderBy(cube => (int)cube.ObjectId.Value))
        {
            rows.Add($"cube {(int)cube.ObjectId.Value} {cube.OwnerPlayerName} {V(cube.CellPosition)} {cube.MaterialId} {cube.CurrentHealth}/{cube.MaxHealth} " +
                $"{(cube.CenterPosition is { } center ? V(center) : "-")}");
        }

        foreach (PersistedCubeFracturePiece piece in world.LoadCubeFracturePieces().OrderBy(piece => (int)piece.OriginObjectId.Value).ThenBy(piece => piece.PieceIndex))
        {
            rows.Add($"piece {(int)piece.OriginObjectId.Value}/{piece.PieceIndex} {piece.OwnerPlayerName} {V(piece.ParentCell)} {V(piece.Center)} {F(piece.SideLength)} {piece.MaterialId}");
        }

        foreach (PersistedEnemyInstance enemy in world.LoadActiveEnemies().OrderBy(enemy => (int)enemy.ObjectId.Value))
        {
            rows.Add($"enemy {(int)enemy.ObjectId.Value} {enemy.EnemyKind} {V(enemy.Position)} {V(enemy.Facing)} {enemy.Stats.CurrentHealth}/{enemy.Stats.MaxHealth} L{enemy.Stats.Level}");
        }

        if (world.TryGetPlayerWorldState(PlayerName) is { } state)
        {
            rows.Add($"player {state.PlayerName} {V(state.Position)} {V(state.Facing)} {state.CurrentHealth} {state.CurrentEnergy}");
        }

        if (new SqlitePlayerProfileRepository(database.Schema).TryGet(PlayerName) is { } profile)
        {
            rows.Add($"profile {profile.PlayerName} {profile.InteractionMode} {profile.Stats.CurrentHealth}/{profile.Stats.MaxHealth}");
        }

        return rows;
    }

    // The world a server restores from the database: every registered cube with its exact center
    // and health, every rubble piece and every enemy.
    private static string DescribeRestoredWorld(TempDatabase database)
    {
        using WorldSessionRuntime runtime = new(
            FloorSize,
            new Vector3(5f, 1.5f, 5f),
            new SceneEventHub(),
            playerProfiles: new SqlitePlayerProfileRepository(database.Schema),
            worldPersistence: new SqliteWorldPersistenceRepository(database.Schema),
            checkpointWriter: new SqlitePersistenceCheckpointWriter(database.Schema));
        runtime.WriteCheckpointOnDispose = false;
        StringBuilder text = new();
        foreach (WorldObjectRecord record in GetWorldObjects(runtime).GetCubeRecords().OrderBy(record => record.Actor.Id))
        {
            Cube cube = (Cube)record.SourceObject;
            text.AppendLine($"cube {record.Actor.Id} {record.OwnerPlayerName} {V(cube.Position)} {record.Health.CurrentHealth}/{record.Health.MaxHealth}");
        }

        foreach ((GeometryCellKey cell, int index, GeometryFracturePiece piece, GeometryMaterialId material) in runtime.Map.CaptureFracturePieces()
            .OrderBy(piece => piece.Cell.X).ThenBy(piece => piece.Cell.Y).ThenBy(piece => piece.Cell.Z).ThenBy(piece => piece.PieceIndex))
        {
            text.AppendLine($"piece {cell} {index} {V(piece.Center)} {F(piece.SideLength)} {material}");
        }

        foreach (EnemyActor enemy in runtime.Enemies.ActiveActors.OrderBy(enemy => enemy.Combatant.Actor.Id))
        {
            text.AppendLine($"enemy {enemy.Combatant.Actor.Id} {V(enemy.Position)} {V(enemy.Forward)} {enemy.Combatant.Health.CurrentHealth}");
        }

        return text.ToString();
    }

    private static ulong Checksum(string text)
    {
        ulong hash = 14695981039346656037UL;
        foreach (char c in text)
        {
            hash = unchecked((hash ^ c) * 1099511628211UL);
        }

        return hash;
    }

    private sealed record ScriptOutcome(
        int Builds,
        int Fractures,
        int WolfKills,
        int ReAddsInFracturedCell,
        long IncrementalWrites,
        long FullWrites,
        long RowsWritten)
    {
        public override string ToString() =>
            $"{Builds} builds, {Fractures} fractures, {WolfKills} wolf kills, {ReAddsInFracturedCell} re-adds, " +
            $"{IncrementalWrites} incremental + {FullWrites} full writes, {RowsWritten} rows";
    }

    // A dedicated host over a SQLite-backed runtime, with the production checkpoint path: the host
    // captures every 5 seconds and the background writer writes.
    private sealed class ScriptedSession : IDisposable
    {
        private static readonly PlayerSessionId SessionId = new("session-one");
        private readonly RuntimeAuthoritativeSessionHost _authority;
        private uint _actionSequence;
        private int _builds;
        private int _fractures;
        private int _wolfKills;
        private int _reAdds;

        public List<string> Log { get; } = [];

        // Waits for each phase's checkpoint write before the next phase, so no checkpoint is
        // superseded. A superseded FULL checkpoint loses the rows of keys it held that the next
        // one no longer has (for example the pieces of a rubble group replaced in the same cell),
        // so a full run is only reproducible when every capture is written.
        public bool FlushEachPhase { get; init; }

        public ScriptedSession(
            TempDatabase database,
            int fullCheckpointInterval,
            bool joinPlayer = true,
            TimeSpan checkpointWriteDelay = default)
        {
            // Placed cubes reference their owner's profile.
            new SqlitePlayerProfileRepository(database.Schema).GetOrCreate(PlayerName);
            IPersistenceCheckpointWriter writer = new SqlitePersistenceCheckpointWriter(database.Schema);
            if (checkpointWriteDelay > TimeSpan.Zero)
            {
                writer = new SlowWriter(writer, checkpointWriteDelay);
            }

            Runtime = new WorldSessionRuntime(
                FloorSize,
                new Vector3(5f, 1.5f, 5f),
                new SceneEventHub(),
                playerProfiles: new SqlitePlayerProfileRepository(database.Schema),
                worldPersistence: new SqliteWorldPersistenceRepository(database.Schema),
                checkpointWriter: writer)
            {
                FullCheckpointInterval = fullCheckpointInterval
            };
            _authority = new RuntimeAuthoritativeSessionHost(Runtime);
            Host = new DedicatedServerHost(_authority, new ProtocolRouter(
                [new ClientHelloHandler(), new JoinRequestHandler(), new PlayerCommandHandler(), new PlayerActionHandler()],
                Array.Empty<IClientMessageHandler>()));
            if (joinPlayer)
            {
                _authority.JoinSession(SessionId.Value, PlayerName);
                Act(new PlayerStatsUpdateIntent(new PlayerCombatStatsSnapshot(
                    Strength: PlayerCombatStats.MaxStrength,
                    AttackSpeed: 2f,
                    Vitality: 10)));
            }
        }

        public WorldSessionRuntime Runtime { get; }

        public DedicatedServerHost Host { get; }

        public BackgroundCheckpointWriter Writes => Host.CheckpointWrites!;

        public void Advance(int ticks)
        {
            for (int done = 0; done < ticks; done += TicksPerAdvance)
            {
                Host.AdvanceTime(SimulationClock.FixedDeltaSeconds * Math.Min(TicksPerAdvance, ticks - done));
            }
        }

        // Places an owned cube directly (no player needed), as a build would.
        public Cube AddOwnedCube(Vector3 cell)
        {
            Cube cube = Assert.IsType<Cube>(Runtime.Map.TryAddCube(cell, materialId: GeometryMaterialId.Dirt));
            WorldObjectRecord record = GetWorldObjects(Runtime).RegisterCube(cube, Runtime.Map, ownerPlayerName: PlayerName);
            new SqliteWorldPersistenceRepositoryAccessor(Runtime).Save(record, cube);
            return cube;
        }

        // Eight 5-second phases, each ending on the host's checkpoint tick, with actions at three
        // points inside each phase. Ends right after the last checkpoint has been written.
        public ScriptOutcome RunScript()
        {
            Vector3[] row = [new(8f, 1f, 3f), new(8f, 1f, 5f), new(8f, 1f, 7f), new(8f, 1f, 9f), new(8f, 1f, 11f)];
            Vector3 stacked = new(8f, 2f, 5f);
            Vector3 floating = new(11f, 4f, 7f);
            Vector3 reAddCell = row[2];
            for (int phase = 0; phase < 8; phase++)
            {
                for (int slot = 0; slot < 3; slot++)
                {
                    switch ((phase, slot))
                    {
                        case (0, 0):
                            foreach (Vector3 cell in row)
                            {
                                Build(cell);
                            }

                            break;
                        case (0, 1):
                            Build(stacked);
                            Build(floating);
                            break;
                        case (1, 0):
                            DamageCubeAt(row[0], 7);
                            MoveCubeAt(row[1], new Vector3(0.12f, 0f, 0.05f));
                            break;
                        case (1, 1):
                        case (3, 0):
                        case (5, 1):
                            AttackCubeAt(reAddCell);
                            break;
                        case (2, 0):
                        case (3, 2):
                        case (6, 0):
                            if (Build(reAddCell))
                            {
                                _reAdds++;
                            }

                            break;
                        case (2, 1):
                        case (4, 1):
                        case (6, 1):
                            KillWolf();
                            break;
                        case (2, 2):
                        case (4, 2):
                        case (6, 2):
                            KillWolf();
                            break;
                        case (3, 1):
                            MoveCubeAt(row[3], new Vector3(1f, 0f, 0f));
                            DamageCubeAt(stacked, 11);
                            break;
                        case (5, 0):
                            AttackCubeAt(row[4]);
                            break;
                        case (7, 0):
                            DamageCubeAt(row[0], 2);
                            break;
                    }

                    Advance(slot == 2 ? TicksPerCheckpoint - (2 * 96) : 96);
                }

                if (FlushEachPhase)
                {
                    Assert.True(Writes.Flush(TimeSpan.FromSeconds(30)));
                }
            }

            Assert.True(Writes.Flush(TimeSpan.FromSeconds(30)));
            Assert.Equal(0, Writes.FailedWrites);
            return new ScriptOutcome(
                _builds,
                _fractures,
                _wolfKills,
                _reAdds,
                Writes.IncrementalWrites,
                Writes.FullWrites,
                Writes.TotalWrittenRows);
        }

        private bool Build(Vector3 cell)
        {
            Runtime.SetAuthoritativeInteractionMode(PlayerInteractionMode.Building);
            Runtime.Player.SetWorldPosition(new Vector3(cell.X - 2.5f, 1.5f, cell.Z + 0.5f));
            bool accepted = Act(new BuildUseIntent(cell)).Accepted;
            if (accepted)
            {
                _builds++;
            }

            return accepted;
        }

        private void AttackCubeAt(Vector3 cell)
        {
            Runtime.SetAuthoritativeInteractionMode(PlayerInteractionMode.Combat);
            Vector3 origin = new(cell.X - 2f, cell.Y + 0.5f, cell.Z + 0.5f);
            Runtime.Player.SetWorldPosition(origin);
            Runtime.Player.SetFacing(Vector3.UnitX);
            AuthoritativeActionResult result = Act(new CombatUseIntent(origin, Vector3.UnitX));
            if (result.Payload is CombatResolutionResult { TargetDied: true } resolution)
            {
                if (resolution.TargetKind == CreatureKind.Object)
                {
                    _fractures++;
                }
                else
                {
                    _wolfKills++;
                }
            }
        }

        private void KillWolf()
        {
            EnemyActor? wolf = Runtime.Enemies.ActiveActors.FirstOrDefault(enemy => enemy.IsActive);
            if (wolf == null)
            {
                return;
            }

            Runtime.SetAuthoritativeInteractionMode(PlayerInteractionMode.Combat);
            Vector3 target = wolf.Combatant.Actor.GetWorldAnchor();
            Vector3 origin = target - new Vector3(1.5f, 0f, 0f);
            Runtime.Player.SetWorldPosition(origin);
            Runtime.Player.SetFacing(Vector3.UnitX);
            AuthoritativeActionResult result = Act(new CombatUseIntent(origin, Vector3.UnitX));
            if (result.Payload is CombatResolutionResult { TargetDied: true } resolution)
            {
                if (resolution.TargetKind == CreatureKind.Object)
                {
                    _fractures++;
                }
                else
                {
                    _wolfKills++;
                }
            }
        }

        private void DamageCubeAt(Vector3 cell, int amount)
        {
            if (Runtime.Map.TryGetCubeAtCell((int)cell.X, (int)cell.Y, (int)cell.Z) is { } cube &&
                GetWorldObjects(Runtime).TryGet(cube) is { } record)
            {
                record.ApplyDamage(amount);
            }
        }

        private void MoveCubeAt(Vector3 cell, Vector3 offset)
        {
            if (Runtime.Map.TryGetCubeAtCell((int)cell.X, (int)cell.Y, (int)cell.Z) is { } cube)
            {
                Runtime.Map.UpdateCubePosition(cube, cube.Position + offset);
            }
        }

        private AuthoritativeActionResult Act(IPlayerActionPayload payload)
        {
            AuthoritativeActionResult result = _authority.ProcessAction(new PlayerActionMessage(SessionId, ++_actionSequence, payload));
            Log.Add($"{payload.GetType().Name}: accepted={result.Accepted} {result.Payload} {result.Rejection}");
            return result;
        }

        public void Dispose()
        {
            Host.Dispose();
        }
    }

    // Saves a directly registered cube the way a build does, through the runtime's repository.
    private readonly struct SqliteWorldPersistenceRepositoryAccessor(WorldSessionRuntime runtime)
    {
        public void Save(WorldObjectRecord record, Cube cube)
        {
            IWorldPersistenceRepository world = runtime.WorldPersistence!;
            Vector3 cell = new(MathF.Floor(cube.Position.X), MathF.Floor(cube.Position.Y), MathF.Floor(cube.Position.Z));
            ObjectId objectId = runtime.ObjectIds.Take();
            runtime.ObjectIdentities.Bind(objectId, record.Actor.Id);
            world.SavePlacedCube(new PersistedPlacedCube(
                objectId,
                PlayerName,
                cell,
                GeometryShapeKind.SolidCube,
                GeometryMaterialId.Dirt,
                record.Health.CurrentHealth,
                record.Health.MaxHealth,
                DateTime.UtcNow,
                DateTime.UtcNow,
                cube.Position));
        }
    }

    private sealed class SlowWriter(IPersistenceCheckpointWriter inner, TimeSpan delay) : IPersistenceCheckpointWriter
    {
        public void Write(PersistenceCheckpoint checkpoint)
        {
            Thread.Sleep(delay);
            inner.Write(checkpoint);
        }
    }

    private sealed class BlockingWriter : IPersistenceCheckpointWriter
    {
        public ManualResetEventSlim Started { get; } = new();

        public ManualResetEventSlim Release { get; } = new();

        public List<PersistenceCheckpoint> Written { get; } = [];

        public void Write(PersistenceCheckpoint checkpoint)
        {
            Started.Set();
            Release.Wait(TimeSpan.FromSeconds(30));
            lock (Written)
            {
                Written.Add(checkpoint);
            }
        }
    }

    private sealed class FailingOnceWriter : IPersistenceCheckpointWriter
    {
        private int _calls;

        public List<PersistenceCheckpoint> Written { get; } = [];

        public void Write(PersistenceCheckpoint checkpoint)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                throw new InvalidOperationException("disk full");
            }

            Written.Add(checkpoint);
        }
    }

    internal sealed class TempDatabase : IDisposable
    {
        private readonly string _rootDirectory;

        public TempDatabase()
        {
            _rootDirectory = Path.Combine(Path.GetTempPath(), $"dream-incremental-checkpoint-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_rootDirectory);
            string schemaPath = Path.Combine(FindRepoRoot(), "dream", "config", "player-profiles.init.sql");
            Schema = new SqliteSchemaBootstrapper(Path.Combine(_rootDirectory, "persistence.sqlite"), schemaPath);
            Schema.EnsureSchema();
        }

        public SqliteSchemaBootstrapper Schema { get; }

        public string IntegrityCheck()
        {
            using Microsoft.Data.Sqlite.SqliteConnection connection = Schema.CreateOpenConnection();
            using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA integrity_check;";
            return (string)command.ExecuteScalar()!;
        }

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(_rootDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        private static string FindRepoRoot()
        {
            DirectoryInfo? current = new(AppContext.BaseDirectory);
            while (current != null)
            {
                if (Directory.Exists(Path.Combine(current.FullName, "dream")) &&
                    Directory.Exists(Path.Combine(current.FullName, "dream.Tests")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            throw new DirectoryNotFoundException("Could not locate repository root.");
        }
    }
}
