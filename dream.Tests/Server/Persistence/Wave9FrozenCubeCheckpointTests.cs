using System.Reflection;
using BulletSharp;
using Dream.Multiplayer.Sessions;
using Dream.Server.Persistence;
using Dream.Simulation.Persistence;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;

namespace Dream.Tests.Server.Persistence;

// Wave 9 tester, SCALE-4 (incremental checkpoints) x PHYS-2 (CubeRestFreezer), on the real
// authoritative runtime: a frozen cube a player pushes wakes, moves through the map (which marks it
// for the next incremental checkpoint), refreezes, and a restart from the incremental checkpoint
// restores exactly the live position. A restored cube starts dynamic and freezes where it was put.
[Collection(TimingSensitiveCollection.Name)]
public sealed class Wave9FrozenCubeCheckpointTests
{
    private const string PlayerName = "player-one";
    private const int FloorSize = 20;
    private static readonly PlayerSessionId SessionId = new(PlayerName);

    private readonly ITestOutputHelper _output;

    public Wave9FrozenCubeCheckpointTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void PushedFrozenCube_IsInTheNextIncrementalCheckpoint_AndRestoresAtItsLivePosition()
    {
        using IncrementalCheckpointTests.TempDatabase database = new();
        new SqlitePlayerProfileRepository(database.Schema).GetOrCreate(PlayerName);
        int cubeId;
        Vector3 livePosition;
        Vector3 placedPosition;
        using (WorldSessionRuntime runtime = CreateRuntime(database))
        using (RuntimeAuthoritativeSessionHost host = new(runtime))
        {
            runtime.WriteCheckpointOnDispose = false;
            runtime.FullCheckpointInterval = 1000;
            runtime.Physics.CubeRest.Enabled = true;
            host.JoinSession(SessionId.Value, PlayerName);
            runtime.Enemies.Clear();
            ulong tick = 0;
            // The world has startup terrain: walk along an empty lane (7 cells long, 3 wide).
            (int laneX, int laneZ) = FindEmptyLane(runtime.Map);
            runtime.Player.SetWorldPosition(new Vector3(laneX + 0.5f, 1.54f, laneZ + 0.5f));
            Advance(host, ref tick, 32, Vector3.UnitX, walk: false);
            Vector3 start = PlayerPosition(runtime);
            Vector3 cell = new(laneX + 2f, 1f, laneZ);
            Cube cube = AddOwnedCube(runtime, cell);
            Assert.True(runtime.Map.TryGetReplicatedId(cube, out cubeId) || TryGetActorId(runtime, cube, out cubeId));
            RigidBody body = BodyOf(runtime, cube);
            Advance(host, ref tick, 96, Vector3.UnitX, walk: false);
            Assert.True(runtime.Physics.CubeRest.IsFrozen(body), "the placed cube never froze");
            placedPosition = cube.Position;

            // Baseline: a full capture written, then the change tracking starts clean.
            PersistenceCheckpoint full = runtime.CapturePersistenceCheckpoint(full: true)!;
            new SqlitePersistenceCheckpointWriter(database.Schema).Write(full);
            long wokenBefore = runtime.Physics.CubeRest.TotalWoken;

            Advance(host, ref tick, 64, Vector3.UnitX, walk: true);
            Advance(host, ref tick, 96, Vector3.UnitX, walk: false);
            livePosition = cube.Position;
            _output.WriteLine($"player from {start} to {PlayerPosition(runtime)}; cube from {placedPosition} to {livePosition}; woken {runtime.Physics.CubeRest.TotalWoken - wokenBefore}; frozen again {runtime.Physics.CubeRest.IsFrozen(body)}");
            Assert.True(runtime.Physics.CubeRest.TotalWoken > wokenBefore, "the push woke nothing");
            Assert.True(livePosition.X - placedPosition.X > 0.3f, $"the cube moved only {livePosition.X - placedPosition.X} m");
            Assert.True(runtime.Physics.CubeRest.IsFrozen(body), "the pushed cube never refroze");

            PersistenceCheckpoint incremental = runtime.CapturePersistenceCheckpoint(full: false)!;
            Assert.True(incremental.IsIncremental);
            PersistedPlacedCube row = Assert.Single(incremental.PlacedCubes, candidate => candidate.CubeId == cubeId);
            Assert.Equal(livePosition, row.CenterPosition);
            new SqlitePersistenceCheckpointWriter(database.Schema).Write(incremental);

            // Refreezing (and staying frozen) is no change: the next capture holds no cube.
            Advance(host, ref tick, 64, Vector3.UnitX, walk: false);
            PersistenceCheckpoint idle = runtime.CapturePersistenceCheckpoint(full: false)!;
            Assert.True(idle.IsIncremental);
            Assert.DoesNotContain(idle.PlacedCubes, candidate => candidate.CubeId == cubeId);
        }

        // Restart: the cube is back at its live position, dynamic, and freezes there.
        using (WorldSessionRuntime restored = CreateRuntime(database))
        using (RuntimeAuthoritativeSessionHost host = new(restored))
        {
            restored.WriteCheckpointOnDispose = false;
            restored.Physics.CubeRest.Enabled = true;
            restored.Enemies.Clear();
            Cube cube = Assert.Single(restored.Map.SolidCubes, candidate =>
                (candidate.Position - livePosition).Length < 0.6f);
            Assert.Equal(livePosition, cube.Position);
            RigidBody body = BodyOf(restored, cube);
            Assert.False(restored.Physics.CubeRest.IsFrozen(body));
            Assert.False(body.IsStaticObject);

            ulong tick = 0;
            Advance(host, ref tick, 96, Vector3.UnitX, walk: false);

            Assert.True(restored.Physics.CubeRest.IsFrozen(body), "the restored cube never froze");
            Vector3 drift = cube.Position - livePosition;
            _output.WriteLine($"restored cube drift after freezing: {drift} ({drift.Length:E2} m)");
            Assert.True(drift.Length < 0.01f, $"the restored cube drifted {drift.Length} m");
        }
    }

    private static WorldSessionRuntime CreateRuntime(IncrementalCheckpointTests.TempDatabase database)
    {
        return new WorldSessionRuntime(
            FloorSize,
            new Vector3(5f, 1.5f, 5f),
            new SceneEventHub(),
            playerProfiles: new SqlitePlayerProfileRepository(database.Schema),
            worldPersistence: new SqliteWorldPersistenceRepository(database.Schema),
            checkpointWriter: new SqlitePersistenceCheckpointWriter(database.Schema));
    }

    private static void Advance(RuntimeAuthoritativeSessionHost host, ref ulong tick, int ticks, Vector3 facing, bool walk)
    {
        for (int i = 0; i < ticks; i++)
        {
            tick++;
            host.AdvanceTick(
                tick,
                SimulationClock.FixedDeltaSeconds,
                [new PlayerCommandFrame(
                    SessionId,
                    tick,
                    PlayerModeSwitchIntent.None,
                    walk ? new PlayerMoveIntent(1f, 0f) : new PlayerMoveIntent(0f, 0f),
                    new PlayerLookIntent(0f, 0f, facing),
                    PrimaryUsePressed: false)],
                PlayerSimulationTuning.AuthoritativeDefault,
                allowGameplaySimulation: false);
        }
    }

    private static (int X, int Z) FindEmptyLane(Map map)
    {
        for (int z = 2; z < FloorSize - 2; z++)
        {
            for (int x = 1; x < FloorSize - 8; x++)
            {
                bool empty = true;
                for (int dx = 0; dx < 7 && empty; dx++)
                {
                    for (int dz = -1; dz <= 1 && empty; dz++)
                    {
                        for (int y = 1; y <= 3 && empty; y++)
                        {
                            empty = map.TryGetCubeAtCell(x + dx, y, z + dz) == null &&
                                map.TryGetGeometryCellAtCell(x + dx, y, z + dz) == null &&
                                map.TryGetRubbleAtCell(new GeometryCellKey(x + dx, y, z + dz)) == null;
                        }
                    }
                }

                if (empty)
                {
                    return (x, z);
                }
            }
        }

        throw new InvalidOperationException("No empty lane in the startup world.");
    }

    private static Vector3 PlayerPosition(WorldSessionRuntime runtime)
    {
        return Assert.Single(runtime.CaptureActorSnapshots(), actor => actor.SessionId == SessionId.Value).Position;
    }

    private static RigidBody BodyOf(WorldSessionRuntime runtime, Cube cube)
    {
        return runtime.Physics.DynamicMapCubeBodies.Single(candidate =>
            candidate.UserObject is ValueTuple<Cube, Vector3> tuple && ReferenceEquals(tuple.Item1, cube));
    }

    private static WorldObjectRegistry WorldObjects(WorldSessionRuntime runtime)
    {
        FieldInfo field = typeof(WorldSessionRuntime).GetField("_worldObjects", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (WorldObjectRegistry)field.GetValue(runtime)!;
    }

    private static bool TryGetActorId(WorldSessionRuntime runtime, Cube cube, out int id)
    {
        id = WorldObjects(runtime).TryGet(cube)?.Actor.Id ?? 0;
        return id != 0;
    }

    // Places an owned cube and saves it the way a build does (as IncrementalCheckpointTests does).
    private static Cube AddOwnedCube(WorldSessionRuntime runtime, Vector3 cell)
    {
        Cube cube = Assert.IsType<Cube>(runtime.Map.TryAddCube(cell, materialId: GeometryMaterialId.Dirt));
        WorldObjectRecord record = WorldObjects(runtime).RegisterCube(cube, runtime.Map, ownerPlayerName: PlayerName);
        IWorldPersistenceRepository world = runtime.WorldPersistence!;
        world.SavePlacedCube(new PersistedPlacedCube(
            record.Actor.Id,
            PlayerName,
            cell,
            GeometryShapeKind.SolidCube,
            GeometryMaterialId.Dirt,
            record.Health.CurrentHealth,
            record.Health.MaxHealth,
            DateTime.UtcNow,
            DateTime.UtcNow,
            cube.Position));
        return cube;
    }
}
