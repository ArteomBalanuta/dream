using Dream.Multiplayer.Sessions;
using Dream.Server.Persistence;
using Dream.Simulation.Enemies;
using Dream.Simulation.Persistence;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Tests.Server.Persistence;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.BugHunt;

// BH-SIM2-3: the checkpoint capture was the only writer of _persistedCubes that could replace a
// known owner with null. Every other writer treats the owner as stable identity (the immediate
// write path explicitly falls back to the cached owner), so a capture taken while the live record
// had lost its owner silently un-owned the player's building in the database.
public class Sim2CheckpointCubeOwnerTests
{
    private static readonly DateTime Now = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ACaptureOfARecordThatLostItsOwner_KeepsThePersistedOwner()
    {
        using IncrementalCheckpointTests.TempDatabase database = new();
        new SqlitePlayerProfileRepository(database.Schema).GetOrCreate("builder");
        SqliteWorldPersistenceRepository world = new(database.Schema);
        world.SavePlacedCube(new PersistedPlacedCube(
            new ObjectId(10),
            "builder",
            new Vector3(3, 1, 3),
            GeometryShapeKind.SolidCube,
            GeometryMaterialId.Grass,
            100,
            100,
            Now,
            Now));

        Rig rig = new(world);
        rig.Bridge.RestoreWorld();

        Cube cube = rig.Host.Map.TryGetCubeAtCell(3, 1, 3)!;
        WorldObjectRecord restored = rig.WorldObjects.TryGet(cube)!;
        Assert.Equal("builder", restored.OwnerPlayerName);

        // The live record loses its owner while keeping its actor id, so its ObjectId binding (and
        // therefore the persisted row it maps to) survives.
        WorldObjectRecord stripped = rig.WorldObjects.ReplaceCubeRecordIdentity(
            cube,
            rig.Host.Map,
            restored.Actor.Id,
            restored.Health.MaxHealth,
            restored.Health.CurrentHealth,
            ownerPlayerName: null);
        Assert.Null(stripped.OwnerPlayerName);

        PersistenceCheckpoint checkpoint = rig.Bridge.CapturePersistenceCheckpoint(full: true)!;

        PersistedPlacedCube captured = Assert.Single(checkpoint.PlacedCubes);
        Assert.Equal(new ObjectId(10), captured.ObjectId);
        // Before the fix this captured null and wrote it back over the cached row.
        Assert.Equal("builder", captured.OwnerPlayerName);

        // A second capture still sees the owner: the cached row was not poisoned by the first.
        PersistenceCheckpoint again = rig.Bridge.CapturePersistenceCheckpoint(full: true)!;
        Assert.Equal("builder", Assert.Single(again.PlacedCubes).OwnerPlayerName);
    }

    [Fact]
    public void ACubeThatNeverHadAnOwner_IsStillCapturedWithoutOne()
    {
        using IncrementalCheckpointTests.TempDatabase database = new();
        new SqlitePlayerProfileRepository(database.Schema).GetOrCreate("builder");
        Rig rig = new(new SqliteWorldPersistenceRepository(database.Schema));
        Cube cube = rig.Host.Map.TryAddCube(new Vector3(3, 1, 3))!;
        WorldObjectRecord record = rig.WorldObjects.RegisterCube(cube, rig.Host.Map, ownerPlayerName: null);
        ObjectId id = rig.ObjectIds.Take();
        rig.Identities.Bind(id, record.Actor.Id);

        PersistenceCheckpoint checkpoint = rig.Bridge.CapturePersistenceCheckpoint(full: true)!;

        Assert.Null(Assert.Single(checkpoint.PlacedCubes).OwnerPlayerName);
    }

    private sealed class Rig
    {
        public Rig(IWorldPersistenceRepository? world)
        {
            Host = new FakeHost(new Map(generateStartupTerrain: true, width: 12, depth: 12, headless: true));
            ObjectIds = new ObjectIdAllocator();
            Identities = new ObjectIdentityMap();
            Bridge = new RuntimePersistenceBridge(
                Host,
                playerProfiles: null,
                world,
                checkpointWriter: null,
                PersistenceWriteQueue.Inline(),
                WorldObjects,
                Enemies,
                new SceneEventHub(),
                ObjectIds,
                Identities);
        }

        public FakeHost Host { get; }

        public ObjectIdAllocator ObjectIds { get; }

        public ObjectIdentityMap Identities { get; }

        public WorldObjectRegistry WorldObjects { get; } = new();

        public EnemyRegistry Enemies { get; } = new();

        public RuntimePersistenceBridge Bridge { get; }
    }

    private sealed class FakeHost(Map map) : IRuntimePersistenceHost
    {
        private int _nextEnemyId = 3000;

        public Map Map { get; } = map;

        public PlayerSessionRegistry Sessions { get; } = new();

        public bool IsDisposed => false;

        public PlayerInteractionMode GetSessionInteractionMode(PlayerSessionId sessionId) => PlayerInteractionMode.Building;

        public bool TryGetPlayerCheckpointState(
            PlayerSessionId sessionId,
            out Vector3 position,
            out Vector3 facing,
            out int currentHealth,
            out int maxHealth)
        {
            position = default;
            facing = default;
            currentHealth = 0;
            maxHealth = 0;
            return false;
        }

        public bool TryAllocateEnemyActorId(out int actorId)
        {
            actorId = _nextEnemyId++;
            return true;
        }

        public void SpawnInitialWolf()
        {
        }

        public bool IsPlayerInventoryLoaded(string playerName) => false;

        public void EvictPlayerInventory(string playerName)
        {
        }
    }
}
