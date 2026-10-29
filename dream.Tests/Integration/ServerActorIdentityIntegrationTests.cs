using System.Reflection;
using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Replication;
using Dream.Client.World;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server.Persistence;
using Dream.Simulation.Determinism;
using Dream.Simulation.Enemies;
using Dream.Simulation.Persistence;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Tests.Integration;

// Wave 6 fix loop 1, server-side identity and live cube state:
//   - a placed cube keeps its damage between hits on a persistent world (identity repair no longer
//     restores health), and checkpoints save the damaged health;
//   - the authoritative map carries the same replicated cube ids the server sends to clients
//     (NET-9), on every registration path, so server and client checksums agree;
//   - every enemy spawn takes a fresh, deterministic actor id, also after a persistence restore.
public class ServerActorIdentityIntegrationTests
{
    private const int FloorSize = 12;
    private static readonly Vector3 SpawnPosition = new(5f, 1.5f, 5f);
    private static readonly PlayerSessionId Session = new("session-a");

    [Fact]
    public void PersistentWorld_NonFatalHit_IsSavedByTheNextCheckpoint()
    {
        RecordingWorldPersistence world = new();
        using WorldSessionRuntime runtime = CreateRuntime(world, out RuntimeAuthoritativeSessionHost host);
        using (host)
        {
            Build(runtime, new Vector3(7f, 1f, 5f), sequence: 1);
            PersistedPlacedCube placed = Assert.Single(world.LoadPlacedCubes());
            Assert.Equal(placed.MaxHealth, placed.CurrentHealth);

            CombatResolutionResult hit = HitAlongX(runtime, sequence: 2);
            Assert.Equal(ActorOf(runtime, placed.ObjectId), hit.TargetActorId);
            Assert.False(hit.TargetDied);
            Assert.True(hit.CurrentHealth < placed.MaxHealth);

            runtime.PersistCheckpoint();

            PersistedPlacedCube saved = Assert.Single(world.LoadPlacedCubes());
            Assert.Equal(placed.ObjectId, saved.ObjectId);
            Assert.Equal(hit.CurrentHealth, saved.CurrentHealth);
        }
    }

    // The identity repair still runs when the registry link is lost (the resolver then registers
    // a fresh record under a new id): it restores the persisted id and owner, and the health the
    // cube had after its last hit, not its placement-time health.
    [Fact]
    public void PersistentWorld_LostRegistryLink_IsRepairedWithTheCubesCurrentHealth()
    {
        RecordingWorldPersistence world = new();
        using WorldSessionRuntime runtime = CreateRuntime(world, out RuntimeAuthoritativeSessionHost host);
        using (host)
        {
            Build(runtime, new Vector3(7f, 1f, 5f), sequence: 1);
            PersistedPlacedCube placed = Assert.Single(world.LoadPlacedCubes());

            CombatResolutionResult first = HitAlongX(runtime, sequence: 2);
            Assert.False(first.TargetDied);

            Cube cube = Assert.IsType<Cube>(runtime.Map.TryGetCubeAtCell(7, 1, 5));
            Assert.True(GetWorldObjects(runtime).Unregister(cube, preserveDetachedRecord: false));

            // (A hit that destroys the cube drops its binding, so resolve the actor id first.)
            int placedActorId = ActorOf(runtime, placed.ObjectId);
            WaitOutTheAttackCooldown(runtime);
            CombatResolutionResult second = HitAlongX(runtime, sequence: 3);
            Assert.Equal(placedActorId, second.TargetActorId);
            Assert.True(
                second.CurrentHealth < first.CurrentHealth,
                $"The second hit left {second.CurrentHealth} hp after the first left {first.CurrentHealth} hp.");
            WorldObjectRecord? repaired = GetWorldObjects(runtime).TryGet(cube);
            if (!second.TargetDied)
            {
                Assert.NotNull(repaired);
                Assert.Equal(placedActorId, repaired.Actor.Id);
                Assert.Equal(placed.OwnerPlayerName, repaired.OwnerPlayerName);
            }
        }
    }

    [Fact]
    public void ServerMap_ResolvesPlacedCubesByTheActorIdsItReplicates()
    {
        using WorldSessionRuntime runtime = CreateRuntime(worldPersistence: null, out RuntimeAuthoritativeSessionHost host);
        using (host)
        {
            Build(runtime, new Vector3(7f, 1f, 5f), sequence: 1);
            Build(runtime, new Vector3(5f, 1f, 7f), sequence: 2);

            WorldMutationDelta[] placements = runtime.ExportServerSnapshotEnvelope().WorldMutations
                .Where(mutation => mutation.Kind == WorldMutationKind.CubePlaced)
                .ToArray();
            Assert.Equal(2, placements.Length);
            foreach (WorldMutationDelta placement in placements)
            {
                int actorId = Assert.IsType<int>(placement.ActorId);
                Cube cube = Assert.IsType<Cube>(runtime.Map.TryGetCubeAtCell(
                    (int)placement.CellPosition.X,
                    (int)placement.CellPosition.Y,
                    (int)placement.CellPosition.Z));
                Assert.True(runtime.Map.TryGetReplicatedId(cube, out int replicatedId));
                Assert.Equal(actorId, replicatedId);
                Assert.True(runtime.Map.TryGetCubeByReplicatedId(actorId, out Cube? byId));
                Assert.Same(cube, byId);
            }

            Assert.Empty(runtime.Map.FindInvariantViolations());
        }
    }

    [Fact]
    public void ServerMap_KeepsReplicatedIds_AcrossAPersistenceRestoreAndAMapRebuild()
    {
        RecordingWorldPersistence world = new();
        world.SavePlacedCube(new PersistedPlacedCube(
            new ObjectId(4242),
            "Player A",
            new Vector3(7f, 1f, 7f),
            GeometryShapeKind.SolidCube,
            GeometryMaterialId.Grass,
            CurrentHealth: 60,
            MaxHealth: 100,
            DateTime.UtcNow,
            DateTime.UtcNow));

        using WorldSessionRuntime runtime = CreateRuntime(world, out RuntimeAuthoritativeSessionHost host);
        using (host)
        {
            Cube restored = Assert.IsType<Cube>(runtime.Map.TryGetCubeAtCell(7, 1, 7));
            Assert.True(runtime.Map.TryGetReplicatedId(restored, out int restoredId));
            // A restore gives the cube a fresh actor id; 4242 stays its ObjectId.
            Assert.Equal(ReplicatedActorIdRanges.FirstWorldObjectId, restoredId);
            Assert.Equal(restoredId, ActorOf(runtime, new ObjectId(4242)));

            runtime.RebuildMapForFloorSize(FloorSize + 2);

            Cube rebuilt = Assert.IsType<Cube>(runtime.Map.TryGetCubeAtCell(7, 1, 7));
            Assert.NotSame(restored, rebuilt);
            Assert.True(runtime.Map.TryGetReplicatedId(rebuilt, out int rebuiltId));
            Assert.Equal(restoredId, rebuiltId);
            Assert.Equal(restoredId, ActorOf(runtime, new ObjectId(4242)));
            Assert.Empty(runtime.Map.FindInvariantViolations());
        }
    }

    // The same two cubes in the same cells, with their ids swapped between the two servers: the
    // cell-only world checksum cannot tell them apart, the Cubes part now can (before the fix the
    // server map had no ids and both servers used the cell fallback).
    [Fact]
    public void TwoServers_GivingTheSameCubesDifferentIds_HaveDifferentCubesParts()
    {
        Vector3 first = new(7f, 1f, 5f);
        Vector3 second = new(5f, 1f, 7f);
        using WorldSessionRuntime left = CreateRuntime(worldPersistence: null, out RuntimeAuthoritativeSessionHost leftHost);
        using WorldSessionRuntime right = CreateRuntime(worldPersistence: null, out RuntimeAuthoritativeSessionHost rightHost);
        using (leftHost)
        using (rightHost)
        {
            Build(left, first, sequence: 1);
            Build(left, second, sequence: 2);
            Build(right, second, sequence: 1);
            Build(right, first, sequence: 2);

            Cube leftCube = Assert.IsType<Cube>(left.Map.TryGetCubeAtCell(7, 1, 5));
            Cube rightCube = Assert.IsType<Cube>(right.Map.TryGetCubeAtCell(7, 1, 5));
            Assert.True(left.Map.TryGetReplicatedId(leftCube, out int leftId));
            Assert.True(right.Map.TryGetReplicatedId(rightCube, out int rightId));
            Assert.NotEqual(leftId, rightId);

            SimulationStateChecksum.Parts leftParts = SimulationStateChecksum.ComputeParts(left.Map, [], 0);
            SimulationStateChecksum.Parts rightParts = SimulationStateChecksum.ComputeParts(right.Map, [], 0);
            Assert.Equal(leftParts.World, rightParts.World);
            Assert.NotEqual(leftParts.Cubes, rightParts.Cubes);
        }
    }

    // Client/server parity: a client that joins after one cube is built (world snapshot with ids)
    // and then receives a second build as a delta (CubePlaced with its id) holds the same cubes
    // under the same ids, so its Cubes part equals the server's.
    [Fact]
    public void ServerMap_AndItsReplicatedClientMap_HaveEqualCubesParts()
    {
        using WorldSessionRuntime server = CreateRuntime(worldPersistence: null, out RuntimeAuthoritativeSessionHost host);
        using (host)
        {
            Build(server, new Vector3(7f, 1f, 5f), sequence: 1);
            _ = server.ExportServerSnapshotEnvelope();

            JoinAcceptedMessage accepted = host.JoinSession("session-b", "B");
            using ClientWorldSession client = new(FloorSize, SpawnPosition, new SceneEventHub());
            ClientJoinAcceptedApplier.ApplyToRuntime(client, accepted);

            Build(server, new Vector3(5f, 1f, 7f), sequence: 2);
            server.AdvanceAuthoritativeTick(
                WorldSessionRuntime.WorldChecksumIntervalTicks,
                SimulationClock.FixedDeltaSeconds,
                (PlayerCommandFrame?)null,
                PlayerSimulationTuning.AuthoritativeDefault);
            RemoteWorldMutationApplier.ApplySnapshotToRuntime(client, server.ExportServerSnapshotEnvelope());

            foreach (Vector3 cell in new[] { new Vector3(7f, 1f, 5f), new Vector3(5f, 1f, 7f) })
            {
                Cube serverCube = Assert.IsType<Cube>(server.Map.TryGetCubeAtCell((int)cell.X, (int)cell.Y, (int)cell.Z));
                Cube clientCube = Assert.IsType<Cube>(client.Map.TryGetCubeAtCell((int)cell.X, (int)cell.Y, (int)cell.Z));
                Assert.True(server.Map.TryGetReplicatedId(serverCube, out int serverId));
                Assert.True(client.Map.TryGetReplicatedId(clientCube, out int clientId));
                Assert.Equal(serverId, clientId);
            }

            SimulationStateChecksum.Parts serverParts = SimulationStateChecksum.ComputeParts(server.Map, [], 0);
            SimulationStateChecksum.Parts clientParts = SimulationStateChecksum.ComputeParts(client.Map, [], 0);
            Assert.Equal(serverParts.World, clientParts.World);
            Assert.Equal(serverParts.Cubes, clientParts.Cubes);
        }
    }

    [Fact]
    public void EveryEnemySpawn_TakesTheNextActorId()
    {
        using WorldSessionRuntime runtime = new(FloorSize, SpawnPosition, new SceneEventHub());
        EnemyActor first = Assert.Single(runtime.Enemies.ActiveActors);
        Assert.Equal(WorldSessionRuntime.FirstEnemyActorId, first.Combatant.Actor.Id);

        for (int life = 1; life <= 3; life++)
        {
            runtime.Enemies.Clear();
            Tick(runtime, (ulong)life);
            EnemyActor respawned = Assert.Single(runtime.Enemies.ActiveActors);
            Assert.Equal(WorldSessionRuntime.FirstEnemyActorId + life, respawned.Combatant.Actor.Id);
        }
    }

    [Fact]
    public void PersistedEnemies_GetFreshActorIds_AndARespawnNeverReusesAPersistedObjectId()
    {
        RecordingWorldPersistence world = new();
        world.SaveEnemyInstance(new PersistedEnemyInstance(
            new ObjectId(2007),
            "Wolf",
            "Wolf",
            new Vector3(6f, 1f, 6f),
            Vector3.UnitZ,
            new PersistedCreatureStats(40, 100, 10, 10, 10, 0, 2.8f, 1f, 1),
            DateTime.UtcNow,
            DateTime.UtcNow));

        using WorldSessionRuntime runtime = CreateRuntime(world, out RuntimeAuthoritativeSessionHost host);
        using (host)
        {
            Assert.Equal(WorldSessionRuntime.FirstEnemyActorId, Assert.Single(runtime.Enemies.ActiveActors).Combatant.Actor.Id);
            Assert.Equal(WorldSessionRuntime.FirstEnemyActorId, ActorOf(runtime, new ObjectId(2007)));

            runtime.Enemies.Clear();
            Tick(runtime, 1);

            EnemyActor respawned = Assert.Single(runtime.Enemies.ActiveActors);
            Assert.Equal(WorldSessionRuntime.FirstEnemyActorId + 1, respawned.Combatant.Actor.Id);
            Assert.Equal(WorldSessionRuntime.FirstEnemyActorId + 1, ActorOf(runtime, new ObjectId(2008)));
            Assert.Contains(world.LoadActiveEnemies(), enemy => enemy.ObjectId == new ObjectId(2008));
        }
    }

    [Fact]
    public void TwoSameSeedServers_AllocateTheSameEnemyIds()
    {
        using WorldSessionRuntime left = new(FloorSize, SpawnPosition, new SceneEventHub());
        using WorldSessionRuntime right = new(FloorSize, SpawnPosition, new SceneEventHub());
        for (ulong tick = 1; tick <= 4; tick++)
        {
            left.Enemies.Clear();
            right.Enemies.Clear();
            Tick(left, tick);
            Tick(right, tick);
            Assert.Equal(
                Assert.Single(left.Enemies.ActiveActors).Combatant.Actor.Id,
                Assert.Single(right.Enemies.ActiveActors).Combatant.Actor.Id);
            Assert.Equal(SimulationStateChecksum.ComputeParts(left).Actors, SimulationStateChecksum.ComputeParts(right).Actors);
        }
    }

    private static int ActorOf(WorldSessionRuntime runtime, ObjectId objectId)
    {
        Assert.True(runtime.ObjectIdentities.TryGetActorId(objectId, out int actorId));
        return actorId;
    }

    private static void WaitOutTheAttackCooldown(WorldSessionRuntime runtime)
    {
        for (int i = 0; i < SimulationClock.AuthoritativeTicksPerSecond * 2; i++)
        {
            Tick(runtime, runtime.CurrentServerTick + 1);
        }
    }

    private static void Tick(WorldSessionRuntime runtime, ulong tick)
    {
        runtime.AdvanceAuthoritativeTick(
            tick,
            SimulationClock.FixedDeltaSeconds,
            (PlayerCommandFrame?)null,
            PlayerSimulationTuning.AuthoritativeDefault);
    }

    private static WorldSessionRuntime CreateRuntime(
        IWorldPersistenceRepository? worldPersistence,
        out RuntimeAuthoritativeSessionHost host)
    {
        WorldSessionRuntime runtime = worldPersistence == null
            ? new(FloorSize, SpawnPosition, new SceneEventHub())
            : new(
                FloorSize,
                SpawnPosition,
                new SceneEventHub(),
                playerProfiles: new RecordingPlayerProfiles(),
                worldPersistence: worldPersistence);
        host = new RuntimeAuthoritativeSessionHost(runtime);
        host.JoinSession(Session.Value, "Player A");
        return runtime;
    }

    private static void Build(WorldSessionRuntime runtime, Vector3 cell, ulong sequence)
    {
        runtime.SetAuthoritativeInteractionMode(PlayerInteractionMode.Building);
        AuthoritativeActionResult result = runtime.ProcessAuthoritativeAction(
            new PlayerActionMessage(Session, sequence, new BuildUseIntent(cell)));
        Assert.True(result.Accepted, result.Rejection?.ToString());
    }

    // The primary stands west of the cube at (7, 1, 5) and hits it along +X.
    private static CombatResolutionResult HitAlongX(WorldSessionRuntime runtime, ulong sequence)
    {
        runtime.Enemies.Clear();
        runtime.SetAuthoritativeInteractionMode(PlayerInteractionMode.Combat);
        Vector3 origin = new(5.5f, 1.5f, 5.5f);
        runtime.Player.SetWorldPosition(origin);
        runtime.Player.SetFacing(Vector3.UnitX);
        AuthoritativeActionResult result = runtime.ProcessAuthoritativeAction(
            new PlayerActionMessage(Session, sequence, new CombatUseIntent(origin, Vector3.UnitX)));
        Assert.True(result.Accepted, result.Rejection?.ToString());
        CombatResolutionResult combat = Assert.IsType<CombatResolutionResult>(result.Payload);
        Assert.True(combat.DamageApplied > 0, $"The hit missed: target {combat.TargetActorId}.");
        return combat;
    }

    private static WorldObjectRegistry GetWorldObjects(WorldSessionRuntime runtime)
    {
        FieldInfo field = typeof(WorldSessionRuntime).GetField("_worldObjects", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (WorldObjectRegistry)field.GetValue(runtime)!;
    }

    private sealed class RecordingPlayerProfiles : IPlayerProfileRepository
    {
        private readonly Dictionary<string, PersistedPlayerProfile> _profiles = new(StringComparer.OrdinalIgnoreCase);

        public PersistedPlayerProfile GetOrCreate(string playerName)
        {
            if (!_profiles.TryGetValue(playerName, out PersistedPlayerProfile profile))
            {
                profile = new PersistedPlayerProfile(
                    playerName,
                    playerName,
                    PlayerInteractionMode.Building,
                    new PersistedCreatureStats(100, 100, 10, 10, 10, 10, 6f, 2f, 1),
                    DateTime.UtcNow,
                    DateTime.UtcNow);
                _profiles[playerName] = profile;
            }

            return profile;
        }

        public PersistedPlayerProfile? TryGet(string playerName)
        {
            return _profiles.TryGetValue(playerName, out PersistedPlayerProfile profile) ? profile : null;
        }

        public void Save(PersistedPlayerProfile profile)
        {
            _profiles[profile.PlayerName] = profile;
        }
    }

    private sealed class RecordingWorldPersistence : IWorldPersistenceRepository
    {
        private readonly Dictionary<string, PersistedPlayerWorldState> _players = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<ObjectId, PersistedPlacedCube> _placedCubes = [];
        private readonly Dictionary<ObjectId, PersistedEnemyInstance> _enemies = [];
        private readonly List<PersistedCubeFracturePiece> _fracturePieces = [];

        public PersistedPlayerWorldState? TryGetPlayerWorldState(string playerName)
        {
            return _players.TryGetValue(playerName, out PersistedPlayerWorldState state) ? state : null;
        }

        public void SavePlayerWorldState(PersistedPlayerWorldState state) => _players[state.PlayerName] = state;

        public IReadOnlyList<PersistedPlacedCube> LoadPlacedCubes() => _placedCubes.Values.ToArray();

        public void SavePlacedCube(PersistedPlacedCube cube) => _placedCubes[cube.ObjectId] = cube;

        public void DeletePlacedCube(ObjectId objectId) => _placedCubes.Remove(objectId);

        public IReadOnlyList<PersistedCubeFracturePiece> LoadCubeFracturePieces() => _fracturePieces.ToArray();

        public void SaveCubeFracturePiece(PersistedCubeFracturePiece piece)
        {
            _fracturePieces.RemoveAll(existing => piece.ObjectId.IsNone ? existing.OriginObjectId == piece.OriginObjectId && existing.PieceIndex == piece.PieceIndex : existing.ObjectId == piece.ObjectId);
            _fracturePieces.Add(piece);
        }

        public void DeleteCubeFracturePieces(ObjectId originObjectId) => _fracturePieces.RemoveAll(piece => piece.OriginObjectId == originObjectId);

        public IReadOnlyList<PersistedEnemyInstance> LoadActiveEnemies() => _enemies.Values.ToArray();

        public void SaveEnemyInstance(PersistedEnemyInstance enemy) => _enemies[enemy.ObjectId] = enemy;

        public void DeleteEnemyInstance(ObjectId objectId) => _enemies.Remove(objectId);

        public long LoadNextObjectId() => 1;


        public IReadOnlyList<PersistedItemInstance> LoadItemInstances() => Array.Empty<PersistedItemInstance>();

        public void SaveItemInstance(PersistedItemInstance item)
        {
        }

        public void DeleteItemInstance(long itemInstanceId)
        {
        }

        public IReadOnlyList<PersistedInventorySlot> LoadInventorySlots() => Array.Empty<PersistedInventorySlot>();

        public PersistedPlayerInventory LoadPlayerInventory(string playerName) => PersistedPlayerInventory.Empty;

        public void SaveInventorySlot(PersistedInventorySlot slot)
        {
        }

        public void DeleteInventorySlots(string playerName)
        {
        }

        public void SaveStarterKitGrant(string playerName, int kitVersion)
        {
        }
    }
}
