using System.Reflection;
using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Replication;
using Dream.Client.World;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Persistence;
using Dream.Simulation.Combat;
using Dream.Simulation.Determinism;
using Dream.Simulation.Enemies;
using Dream.Simulation.Inventory;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Tests.Integration;

// Wave 10 ID-1, the two replicated-id follow-ups from wave 6:
//   - a generated terrain cube gets its replicated id on the server when it is first hit; the id
//     now reaches clients in a sequenced CubeIdentified delta, so a client that joined earlier
//     keys (and checksums) the cube by the same id without waiting for a full world snapshot;
//   - placed-cube and enemy ids come from disjoint ranges (ReplicatedActorIdRanges), and a
//     restart derives both counters from every persisted id, so neither kind reuses the other's.
public class ReplicatedIdIntegrationTests
{
    private const int FloorSize = 12;
    // Raised terrain (cubes above the floor) is generated only on floors of 20 and more.
    private const int TerrainFloorSize = 32;
    private static readonly Vector3 SpawnPosition = new(5f, 1.5f, 5f);
    private static readonly PlayerSessionId Session = new("session-a");

    [Fact]
    public void TerrainCubeFirstHit_SendsItsIdInADelta_AndAnEarlierJoinedClientStaysInSync()
    {
        using WorldSessionRuntime server = new(TerrainFloorSize, SpawnPosition, new SceneEventHub());
        using RuntimeAuthoritativeSessionHost host = new(server);
        JoinAcceptedMessage accepted = host.JoinSession(Session.Value, "A");
        using ClientWorldSession client = new(TerrainFloorSize, SpawnPosition, new SceneEventHub());
        ClientJoinAcceptedApplier.ApplyToRuntime(client, accepted);
        // The client is caught up before the hit: its join snapshot is the only full snapshot.
        RemoteWorldMutationApplier.ApplySnapshotToRuntime(client, server.ExportServerSnapshotEnvelope());

        Cube terrainCube = FindExposedTerrainCube(server.Map, out GeometryCellKey cell);
        Assert.False(server.Map.TryGetReplicatedId(terrainCube, out _));

        CombatResolutionResult hit = HitFromAbove(server, cell, sequence: 1);
        Assert.False(hit.TargetDied);
        Assert.NotNull(hit.TargetActorId);
        int cubeId = hit.TargetActorId!.Value;
        Assert.True(ReplicatedActorIdRanges.IsWorldObjectId(cubeId));

        server.AdvanceAuthoritativeTick(
            WorldSessionRuntime.WorldChecksumIntervalTicks,
            SimulationClock.FixedDeltaSeconds,
            (PlayerCommandFrame?)null,
            PlayerSimulationTuning.AuthoritativeDefault);
        ServerSnapshotEnvelope snapshot = server.ExportServerSnapshotEnvelope();
        Assert.Null(snapshot.World);
        Assert.NotNull(snapshot.WorldChecksum);
        WorldMutationDelta identified = Assert.Single(
            snapshot.WorldMutations,
            mutation => mutation.Kind == WorldMutationKind.CubeIdentified);
        Assert.Equal(cubeId, identified.ActorId);
        Assert.Equal(new Vector3(cell.X, cell.Y, cell.Z), identified.CellPosition);

        // Without the delta the client still keys the cube by its cell while the server keys it
        // by id, so the id-keyed Cubes part of the checksum differs (the wave-6 behaviour, healed
        // only by a full world snapshot) and id-resolved deltas for that cube miss on the client.
        using (ClientWorldSession withoutDelta = new(TerrainFloorSize, SpawnPosition, new SceneEventHub()))
        {
            ClientJoinAcceptedApplier.ApplyToRuntime(withoutDelta, accepted);
            ServerSnapshotEnvelope stripped = snapshot with
            {
                WorldMutations = snapshot.WorldMutations
                    .Where(mutation => mutation.Kind != WorldMutationKind.CubeIdentified)
                    .ToArray()
            };
            RemoteWorldMutationApplier.ApplySnapshotToRuntime(withoutDelta, stripped);
            Assert.NotEqual(
                SimulationStateChecksum.ComputeParts(server.Map, [], 0).Cubes,
                SimulationStateChecksum.ComputeParts(withoutDelta.Map, [], 0).Cubes);
            Assert.False(withoutDelta.Map.TryGetCubeByReplicatedId(cubeId, out _));
        }

        RemoteWorldMutationApplier.ApplySnapshotToRuntime(client, snapshot);

        Assert.False(RemoteWorldMutationApplier.IsWorldOutOfSync(client, snapshot));
        SimulationStateChecksum.Parts serverParts = SimulationStateChecksum.ComputeParts(server.Map, [], 0);
        SimulationStateChecksum.Parts clientParts = SimulationStateChecksum.ComputeParts(client.Map, [], 0);
        Assert.Equal(serverParts.World, clientParts.World);
        Assert.Equal(serverParts.Cubes, clientParts.Cubes);
        Cube clientCube = Assert.IsType<Cube>(client.Map.TryGetCubeAtCell(cell.X, cell.Y, cell.Z));
        Assert.True(client.Map.TryGetReplicatedId(clientCube, out int clientId));
        Assert.Equal(cubeId, clientId);
    }

    [Fact]
    public void CubeIdentified_RoundTripsThroughTheCodec_AndAppliesToTheCubeInItsCell()
    {
        WorldMutationDelta delta = WorldMutationDelta.Identified(new Vector3(3f, 2f, 3f), 1234) with { Sequence = 9 };
        ServerSnapshotEnvelope envelope = new(
            ServerTick: 1,
            LastAcceptedCommandSequences: new Dictionary<string, ulong>(),
            Actors: [],
            InteractionMode: PlayerInteractionMode.Combat,
            World: null,
            WorldMutations: [delta],
            DamageEvents: []);
        TransportJsonCodec codec = new();
        ServerSnapshotMessage message = new(envelope);
        byte[] payload = codec.Encode(new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, message.MessageKind, 1), message);
        ServerSnapshotMessage decoded = Assert.IsType<ServerSnapshotMessage>(codec.Decode(payload).Message);
        WorldMutationDelta roundTripped = Assert.Single(decoded.Snapshot.WorldMutations);
        Assert.Equal(delta, roundTripped);

        Map map = new(width: 10, depth: 10);
        Cube cube = Assert.IsType<Cube>(map.TryAddCube(new Vector3(3f, 2f, 3f), withPhysics: false));
        RemoteWorldMutationApplier.ApplyWorldMutations(map, [roundTripped]);
        Assert.True(map.TryGetCubeByReplicatedId(1234, out Cube? byId));
        Assert.Same(cube, byId);

        // An empty cell is left alone (the world checksum catches that divergence).
        RemoteWorldMutationApplier.ApplyWorldMutations(map, [WorldMutationDelta.Identified(new Vector3(5f, 2f, 5f), 77)]);
        Assert.False(map.TryGetCubeByReplicatedId(77, out _));
    }

    // A CubeIdentified carries no position, so it must not let an earlier same-cell move of the
    // same cube be coalesced away.
    [Fact]
    public void CoalesceSameCellMoves_KeepsAMoveFollowedOnlyByAnIdentification()
    {
        WorldMutationDelta move = new(
            WorldMutationKind.CubeMoved,
            new Vector3(1f, 1f, 1f),
            new Vector3(1f, 1f, 1f),
            new Vector3(1.5f, 1.4f, 1.5f),
            Sequence: 1,
            ActorId: 7);
        WorldMutationDelta identified = WorldMutationDelta.Identified(new Vector3(1f, 1f, 1f), 7) with { Sequence = 2 };

        WorldMutationDelta[] coalesced = WorldSessionRuntime.CoalesceSameCellMoves([move, identified]);

        Assert.Equal([1ul, 2ul], coalesced.Select(mutation => mutation.Sequence).ToArray());
    }

    // A build's CubePlaced already carries its id; no separate CubeIdentified is sent for it.
    [Fact]
    public void Placement_DoesNotAlsoExportACubeIdentified()
    {
        using WorldSessionRuntime server = new(FloorSize, SpawnPosition, new SceneEventHub());
        using RuntimeAuthoritativeSessionHost host = new(server);
        host.JoinSession(Session.Value, "A");
        _ = server.ExportServerSnapshotEnvelope();

        server.SetAuthoritativeInteractionMode(PlayerInteractionMode.Building);
        Assert.True(server.ProcessAuthoritativeAction(
            new PlayerActionMessage(Session, 1, new BuildUseIntent(new Vector3(7f, 1f, 5f)))).Accepted);

        ServerSnapshotEnvelope snapshot = server.ExportServerSnapshotEnvelope();
        Assert.Contains(snapshot.WorldMutations, mutation => mutation.Kind == WorldMutationKind.CubePlaced && mutation.ActorId.HasValue);
        Assert.DoesNotContain(snapshot.WorldMutations, mutation => mutation.Kind == WorldMutationKind.CubeIdentified);
    }

    // Before the partition, placed cubes counted up from 1000 and enemies from 2000, so the 1001st
    // cube registration took the first wolf's id.
    [Fact]
    public void FifteenHundredPlacedCubes_AndEnemyRespawns_NeverShareAnId()
    {
        const int Count = 1500;
        using WorldSessionRuntime runtime = new(48, SpawnPosition, new SceneEventHub());
        WorldObjectRegistry registry = GetWorldObjects(runtime);
        HashSet<int> cubeIds = [];
        HashSet<int> enemyIds = [Assert.Single(runtime.Enemies.ActiveActors).Combatant.Actor.Id];
        IEnumerator<Vector3> cells = FreeRaisedCells(runtime.Map).GetEnumerator();

        for (int i = 0; i < Count; i++)
        {
            Assert.True(cells.MoveNext(), "The map ran out of free cells.");
            Cube cube = Assert.IsType<Cube>(runtime.Map.TryAddCube(cells.Current, withPhysics: false));
            WorldObjectRecord record = registry.RegisterCube(cube, runtime.Map, actorId: null);
            Assert.True(cubeIds.Add(record.Actor.Id), $"Cube id {record.Actor.Id} was handed out twice.");

            runtime.Enemies.Clear();
            Tick(runtime, (ulong)i + 1);
            int enemyId = Assert.Single(runtime.Enemies.ActiveActors).Combatant.Actor.Id;
            Assert.True(enemyIds.Add(enemyId), $"Enemy id {enemyId} was handed out twice.");
        }

        Assert.Empty(cubeIds.Intersect(enemyIds));
        Assert.All(cubeIds, id => Assert.True(ReplicatedActorIdRanges.IsWorldObjectId(id), $"cube id {id}"));
        Assert.All(enemyIds, id => Assert.True(ReplicatedActorIdRanges.IsEnemyId(id), $"enemy id {id}"));
        // The cube block below the enemy range ran out and allocation moved above it.
        Assert.Contains(ReplicatedActorIdRanges.FirstOverflowWorldObjectId, cubeIds);
        Assert.Equal(WorldSessionRuntime.FirstEnemyActorId + Count, enemyIds.Max());

        // Every id still resolves to its own kind.
        int lastEnemyId = Assert.Single(runtime.Enemies.ActiveActors).Combatant.Actor.Id;
        Assert.True(runtime.TryResolveCombatActor(lastEnemyId, out CombatActorHandle enemy));
        Assert.Equal(CreatureKind.Enemy, enemy.Kind);
        foreach (int cubeId in new[] { ReplicatedActorIdRanges.FirstWorldObjectId, ReplicatedActorIdRanges.FirstEnemyId - 1, ReplicatedActorIdRanges.FirstOverflowWorldObjectId })
        {
            Assert.True(runtime.TryResolveCombatActor(cubeId, out CombatActorHandle cubeActor));
            Assert.Equal(CreatureKind.Object, cubeActor.Kind);
        }
    }

    [Fact]
    public void Restart_RestoresBothIdCounters_AndTheObjectIdCounter_PastEveryPersistedId()
    {
        InMemoryWorldPersistence world = new();
        ObjectId[] placedIds;
        int lastEnemyId;
        using (WorldSessionRuntime first = CreatePersistentRuntime(world, out RuntimeAuthoritativeSessionHost host))
        using (host)
        {
            first.SetAuthoritativeInteractionMode(PlayerInteractionMode.Building);
            Vector3[] buildCells = [new(7f, 1f, 5f), new(5f, 1f, 7f), new(7f, 1f, 7f)];
            for (int i = 0; i < buildCells.Length; i++)
            {
                AuthoritativeActionResult result = first.ProcessAuthoritativeAction(
                    new PlayerActionMessage(Session, (ulong)i + 1, new BuildUseIntent(buildCells[i])));
                Assert.True(result.Accepted, result.Rejection?.ToString());
            }

            // Three respawns, each persisted by its spawn.
            for (ulong tick = 1; tick <= 3; tick++)
            {
                first.Enemies.Clear();
                Tick(first, tick);
            }

            placedIds = world.LoadPlacedCubes().Select(cube => cube.ObjectId).Order().ToArray();
            lastEnemyId = Assert.Single(first.Enemies.ActiveActors).Combatant.Actor.Id;
            Assert.True(first.ObjectIdentities.TryGetObjectId(lastEnemyId, out ObjectId lastEnemyObject));
            Assert.Contains(world.LoadActiveEnemies(), enemy => enemy.ObjectId == lastEnemyObject);
        }

        Assert.Equal(3, placedIds.Length);
        Assert.Equal(WorldSessionRuntime.FirstEnemyActorId + 3, lastEnemyId);
        ObjectId highestPersisted = world.LoadPlacedCubes().Select(cube => cube.ObjectId)
            .Concat(world.LoadActiveEnemies().Select(enemy => enemy.ObjectId)).Max();
        int persistedEnemies = world.LoadActiveEnemies().Count;

        using WorldSessionRuntime restarted = CreatePersistentRuntime(world, out RuntimeAuthoritativeSessionHost restartedHost);
        using (restartedHost)
        {
            // Restored cubes take compact actor ids, in ObjectId order, from the start of the range.
            WorldObjectRegistry registry = GetWorldObjects(restarted);
            Assert.Equal(ReplicatedActorIdRanges.FirstWorldObjectId + 3, registry.NextActorId);
            for (int i = 0; i < placedIds.Length; i++)
            {
                Assert.True(restarted.ObjectIdentities.TryGetActorId(placedIds[i], out int cubeActorId));
                Assert.Equal(ReplicatedActorIdRanges.FirstWorldObjectId + i, cubeActorId);
            }

            restarted.SetAuthoritativeInteractionMode(PlayerInteractionMode.Building);
            Assert.True(restarted.ProcessAuthoritativeAction(
                new PlayerActionMessage(Session, 10, new BuildUseIntent(new Vector3(5f, 1f, 3f)))).Accepted);
            ObjectId newCube = world.LoadPlacedCubes().Select(cube => cube.ObjectId).Except(placedIds).Single();
            Assert.True(newCube.Value > highestPersisted.Value);

            restarted.Enemies.Clear();
            Tick(restarted, 1);
            EnemyActor respawned = Assert.Single(restarted.Enemies.ActiveActors);
            Assert.Equal(WorldSessionRuntime.FirstEnemyActorId + persistedEnemies, respawned.Combatant.Actor.Id);
            Assert.True(restarted.ObjectIdentities.TryGetObjectId(respawned.Combatant.Actor.Id, out ObjectId respawnedObject));
            Assert.True(respawnedObject.Value > newCube.Value);
            Assert.True(ReplicatedActorIdRanges.IsEnemyId(respawned.Combatant.Actor.Id));
        }
    }

    // Worlds saved before the partition: a placed cube can hold a number in today's enemy range, and a
    // fractured cube's id lives on only in its pieces. Persisted ids are ObjectIds now, so none of
    // that collides with an actor id: every object restores under a fresh actor id.
    [Fact]
    public void LegacyWorld_RestoresWithoutCollisions()
    {
        DateTime now = DateTime.UtcNow;
        InMemoryWorldPersistence world = new();
        world.SavePlacedCube(LegacyCube(1003, new Vector3(7f, 1f, 5f), now));
        world.SavePlacedCube(LegacyCube(2004, new Vector3(5f, 1f, 7f), now));
        world.SavePlacedCube(LegacyCube(2010, new Vector3(7f, 1f, 7f), now));
        world.SaveEnemyInstance(LegacyEnemy(2005, now));
        world.SaveEnemyInstance(LegacyEnemy(2006, now));
        world.SaveCubeFracturePiece(new PersistedCubeFracturePiece(
            new ObjectId(2011), new ObjectId(1500), 0, "legacy", new Vector3(3f, 1f, 7f), new Vector3(3.25f, 1.25f, 7.25f), 0.25f,
            GeometryMaterialId.Grass, now, now));

        using WorldSessionRuntime runtime = CreatePersistentRuntime(world, out RuntimeAuthoritativeSessionHost host);
        using (host)
        {
            int[] cubeActorIds = new[] { 1003L, 2004L, 2010L }
                .Select(id => runtime.ObjectIdentities.TryGetActorId(new ObjectId(id), out int actorId) ? actorId : -1)
                .ToArray();
            int[] enemyIds = runtime.Enemies.ActiveActors.Select(enemy => enemy.Combatant.Actor.Id).Order().ToArray();
            Assert.Equal([1000, 1001, 1002], cubeActorIds);
            Assert.Empty(cubeActorIds.Intersect(enemyIds));
            Assert.Equal([2000, 2001], enemyIds);
            Assert.Equal([new ObjectId(2005), new ObjectId(2006)], world.LoadActiveEnemies().Select(enemy => enemy.ObjectId).Order());
            Assert.True(runtime.TryResolveCombatActor(1001, out CombatActorHandle cube));
            Assert.Equal(CreatureKind.Object, cube.Kind);

            // Actor ids continue after the restored ones; ObjectIds continue past every persisted one
            // (cubes, enemies and the fracture group's origin and piece).
            Assert.Equal(1003, GetWorldObjects(runtime).NextActorId);
            // The joined player's starter items take the ids after them.
            Assert.Equal(2012 + ToolCatalog.StarterItemKinds.Count, runtime.ObjectIds.Next);
            runtime.Enemies.Clear();
            Tick(runtime, 1);
            Assert.Equal(2002, Assert.Single(runtime.Enemies.ActiveActors).Combatant.Actor.Id);
        }
    }

    [Theory]
    [InlineData(999, 1000)]
    [InlineData(1000, 1001)]
    [InlineData(1998, 1999)]
    [InlineData(1999, ReplicatedActorIdRanges.FirstOverflowWorldObjectId)]
    [InlineData(ReplicatedActorIdRanges.FirstOverflowWorldObjectId, ReplicatedActorIdRanges.FirstOverflowWorldObjectId + 1)]
    public void NextWorldObjectId_SkipsTheEnemyRange(int current, int expected)
    {
        Assert.Equal(expected, ReplicatedActorIdRanges.NextWorldObjectId(current));
    }

    [Fact]
    public void IdSpaces_ReportExhaustionInsteadOfOverlapping()
    {
        Assert.Throws<InvalidOperationException>(() => ReplicatedActorIdRanges.NextWorldObjectId(int.MaxValue));
        Assert.Throws<InvalidOperationException>(
            () => ReplicatedActorIdRanges.NextEnemyId(ReplicatedActorIdRanges.FirstOverflowWorldObjectId - 1));
        Assert.False(ReplicatedActorIdRanges.IsWorldObjectId(ReplicatedActorIdRanges.FirstEnemyId));
        Assert.True(ReplicatedActorIdRanges.IsEnemyId(ReplicatedActorIdRanges.FirstOverflowWorldObjectId - 1));
        Assert.False(ReplicatedActorIdRanges.IsEnemyId(ReplicatedActorIdRanges.FirstOverflowWorldObjectId));
        Assert.Throws<InvalidOperationException>(() => ReplicatedActorIdRanges.NextWorldObjectId(ReplicatedActorIdRanges.LastWorldObjectId));
    }

    // Reserving the last id of a range saturates the counter; only handing out an id from the
    // exhausted counter fails. The last id itself can still be handed out once.
    [Fact]
    public void Counters_SaturateOnReserve_AndFailOnlyWhenAllocatingFromAnExhaustedRange()
    {
        const int Exhausted = ReplicatedActorIdRanges.ExhaustedCounter;
        Assert.Equal(Exhausted, ReplicatedActorIdRanges.ReserveWorldObjectId(1000, ReplicatedActorIdRanges.LastWorldObjectId));
        Assert.Equal(Exhausted, ReplicatedActorIdRanges.ReserveEnemyId(2000, ReplicatedActorIdRanges.LastEnemyId));
        // An exhausted counter stays exhausted whatever is reserved after it.
        Assert.Equal(Exhausted, ReplicatedActorIdRanges.ReserveWorldObjectId(Exhausted, 1500));
        Assert.Equal(Exhausted, ReplicatedActorIdRanges.ReserveEnemyId(Exhausted, 2500));
        // Ids of other kinds leave a counter alone.
        Assert.Equal(1000, ReplicatedActorIdRanges.ReserveWorldObjectId(1000, int.MaxValue));
        Assert.Equal(2000, ReplicatedActorIdRanges.ReserveEnemyId(2000, 5));

        int cubes = ReplicatedActorIdRanges.LastWorldObjectId;
        Assert.Equal(ReplicatedActorIdRanges.LastWorldObjectId, ReplicatedActorIdRanges.TakeWorldObjectId(ref cubes));
        Assert.Equal(Exhausted, cubes);
        Assert.Throws<InvalidOperationException>(() => ReplicatedActorIdRanges.TakeWorldObjectId(ref cubes));

        int enemies = ReplicatedActorIdRanges.LastEnemyId;
        Assert.Equal(ReplicatedActorIdRanges.LastEnemyId, ReplicatedActorIdRanges.TakeEnemyId(ref enemies));
        Assert.Equal(Exhausted, enemies);
        Assert.Throws<InvalidOperationException>(() => ReplicatedActorIdRanges.TakeEnemyId(ref enemies));

        int low = 1999;
        Assert.Equal(1999, ReplicatedActorIdRanges.TakeWorldObjectId(ref low));
        Assert.Equal(ReplicatedActorIdRanges.FirstOverflowWorldObjectId, low);
    }

    // A world whose persisted ObjectIds are far beyond the int actor id space still restores (the
    // server starts): its objects get fresh, compact actor ids, and later ids continue past them.
    [Fact]
    public void Restore_WithObjectIdsBeyondTheActorIdSpace_StartsAndAllocatesFreshActorIds()
    {
        DateTime now = DateTime.UtcNow;
        InMemoryWorldPersistence world = new();
        world.SavePlacedCube(LegacyCube(5_000_000_000L, new Vector3(7f, 1f, 5f), now));
        world.SaveEnemyInstance(LegacyEnemy(6_000_000_000L, now));

        using WorldSessionRuntime runtime = CreatePersistentRuntime(world, out RuntimeAuthoritativeSessionHost host);
        using (host)
        {
            WorldObjectRegistry registry = GetWorldObjects(runtime);
            Assert.Equal(ReplicatedActorIdRanges.FirstWorldObjectId + 1, registry.NextActorId);
            Assert.Equal(WorldSessionRuntime.FirstEnemyActorId, Assert.Single(runtime.Enemies.ActiveActors).Combatant.Actor.Id);
            // The joined player's starter items take the first ids after the stored ones.
            long firstFree = 6_000_000_001L + ToolCatalog.StarterItemKinds.Count;
            Assert.Equal(firstFree, runtime.ObjectIds.Next);

            runtime.Enemies.Clear();
            Tick(runtime, 1);
            EnemyActor respawned = Assert.Single(runtime.Enemies.ActiveActors);
            Assert.Equal(WorldSessionRuntime.FirstEnemyActorId + 1, respawned.Combatant.Actor.Id);
            Assert.True(runtime.ObjectIdentities.TryGetObjectId(respawned.Combatant.Actor.Id, out ObjectId respawnedObject));
            Assert.Equal(new ObjectId(firstFree), respawnedObject);
        }
    }

    private static PersistedPlacedCube LegacyCube(long id, Vector3 cell, DateTime now)
    {
        return new PersistedPlacedCube(new ObjectId(id), "legacy", cell, GeometryShapeKind.SolidCube, GeometryMaterialId.Grass, 100, 100, now, now);
    }

    private static PersistedEnemyInstance LegacyEnemy(long id, DateTime now)
    {
        return new PersistedEnemyInstance(
            new ObjectId(id),
            "Wolf",
            "Wolf",
            new Vector3(9f, 1f, 9f),
            Vector3.UnitZ,
            new PersistedCreatureStats(40, 100, 10, 10, 10, 0, 2.8f, 1f, 1),
            now,
            now);
    }

    // The topmost cube of a generated column (nothing above it), away from the spawn point.
    private static Cube FindExposedTerrainCube(Map map, out GeometryCellKey cell)
    {
        foreach (Cube candidate in map.SolidCubes.OrderBy(cube => cube.Position.X).ThenBy(cube => cube.Position.Z).ThenBy(cube => cube.Position.Y))
        {
            if (!map.TryGetCellForCube(candidate, out GeometryCellKey key) ||
                key.Y < 1 ||
                key.Y + 2 >= map.Height ||
                map.TryGetCubeAtCell(key.X, key.Y + 1, key.Z) != null ||
                map.TryGetCubeAtCell(key.X, key.Y + 2, key.Z) != null ||
                map.TryGetReplicatedId(candidate, out _))
            {
                continue;
            }

            cell = key;
            return candidate;
        }

        throw new InvalidOperationException("The generated terrain has no exposed cube above the floor.");
    }

    private static CombatResolutionResult HitFromAbove(WorldSessionRuntime runtime, GeometryCellKey cell, ulong sequence)
    {
        runtime.Enemies.Clear();
        runtime.SetAuthoritativeInteractionMode(PlayerInteractionMode.Combat);
        Vector3 origin = new(cell.X + 0.5f, cell.Y + 2.2f, cell.Z + 0.5f);
        runtime.Player.SetWorldPosition(origin);
        runtime.Player.SetFacing(-Vector3.UnitY);
        AuthoritativeActionResult result = runtime.ProcessAuthoritativeAction(
            new PlayerActionMessage(Session, sequence, new CombatUseIntent(origin, -Vector3.UnitY)));
        Assert.True(result.Accepted, result.Rejection?.ToString());
        CombatResolutionResult combat = Assert.IsType<CombatResolutionResult>(result.Payload);
        Assert.True(combat.DamageApplied > 0, $"The hit missed: target {combat.TargetActorId}.");
        return combat;
    }

    private static IEnumerable<Vector3> FreeRaisedCells(Map map)
    {
        for (int y = 1; y < map.Height; y++)
        {
            for (int x = 0; x < map.Width; x++)
            {
                for (int z = 0; z < map.Depth; z++)
                {
                    if (map.TryGetCubeAtCell(x, y, z) == null)
                    {
                        yield return new Vector3(x, y, z);
                    }
                }
            }
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

    private static WorldSessionRuntime CreatePersistentRuntime(
        IWorldPersistenceRepository world,
        out RuntimeAuthoritativeSessionHost host)
    {
        WorldSessionRuntime runtime = new(
            FloorSize,
            SpawnPosition,
            new SceneEventHub(),
            playerProfiles: new InMemoryPlayerProfiles(),
            worldPersistence: world);
        host = new RuntimeAuthoritativeSessionHost(runtime);
        host.JoinSession(Session.Value, "Player A");
        return runtime;
    }

    private static WorldObjectRegistry GetWorldObjects(WorldSessionRuntime runtime)
    {
        FieldInfo field = typeof(WorldSessionRuntime).GetField("_worldObjects", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (WorldObjectRegistry)field.GetValue(runtime)!;
    }

    private sealed class InMemoryPlayerProfiles : IPlayerProfileRepository
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

    private sealed class InMemoryWorldPersistence : IWorldPersistenceRepository
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

        // Terrain overrides are not exercised by this fake.
        public IReadOnlyList<PersistedTerrainCellOverride> LoadTerrainCellOverrides() => [];

        public void SaveTerrainCellOverride(PersistedTerrainCellOverride cellOverride)
        {
        }


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
