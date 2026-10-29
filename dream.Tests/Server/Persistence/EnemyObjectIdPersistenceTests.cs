using System.Reflection;
using Dream.Server.Persistence;
using Dream.Simulation.Enemies;
using Dream.Simulation.Persistence;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Server.Persistence;

// Wave C (B): an enemy's durable identity is its ObjectId. A restore keeps it and hands the enemy a
// fresh actor id (replacing the wave 10 ReassignEnemyId re-identification), a respawn is a new
// object with a new ObjectId, and a legacy cube id that sits in the enemy actor range no longer
// collides with anything.
public sealed class EnemyObjectIdPersistenceTests
{
    private const string Owner = "legacy";

    [Fact]
    public void Restore_KeepsTheObjectId_AndGivesTheEnemyAFreshActorId_InObjectIdOrder()
    {
        using IncrementalCheckpointTests.TempDatabase database = new();
        SqliteWorldPersistenceRepository world = new(database.Schema);
        DateTime now = DateTime.UtcNow;
        world.SaveEnemyInstance(Enemy(4301, now));
        world.SaveEnemyInstance(Enemy(2006, now));

        using WorldSessionRuntime runtime = CreateRuntime(database, world);

        EnemyActor[] enemies = [.. runtime.Enemies.ActiveActors.OrderBy(enemy => enemy.Combatant.Actor.Id)];
        Assert.Equal(
            [WorldSessionRuntime.FirstEnemyActorId, WorldSessionRuntime.FirstEnemyActorId + 1],
            enemies.Select(enemy => enemy.Combatant.Actor.Id));
        Assert.True(runtime.ObjectIdentities.TryGetObjectId(enemies[0].Combatant.Actor.Id, out ObjectId first));
        Assert.True(runtime.ObjectIdentities.TryGetObjectId(enemies[1].Combatant.Actor.Id, out ObjectId second));
        Assert.Equal((new ObjectId(2006), new ObjectId(4301)), (first, second));

        // The checkpoint writes the rows back under the same ObjectIds.
        PersistenceCheckpoint checkpoint = runtime.CapturePersistenceCheckpoint(full: true)!;
        Assert.Equal([new ObjectId(2006), new ObjectId(4301)], checkpoint.Enemies.Select(enemy => enemy.ObjectId).Order());
        // The allocator is past every loaded id.
        Assert.True(runtime.ObjectIds.Next > 4301);
    }

    [Fact]
    public void ARespawn_IsANewObject_WithANewObjectId_AndTheDeadOnesRowIsDeleted()
    {
        using IncrementalCheckpointTests.TempDatabase database = new();
        SqliteWorldPersistenceRepository world = new(database.Schema);
        world.SaveEnemyInstance(Enemy(2006, DateTime.UtcNow));

        using WorldSessionRuntime runtime = CreateRuntime(database, world);
        EnemyActor wolf = Assert.Single(runtime.Enemies.ActiveActors);
        int wolfActorId = wolf.Combatant.Actor.Id;
        Assert.True(runtime.ObjectIdentities.TryGetObjectId(wolfActorId, out ObjectId before));

        Assert.True(runtime.Enemies.Remove(wolfActorId));
        typeof(WorldSessionRuntime)
            .GetMethod("DeletePersistedEnemy", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(runtime, [wolfActorId]);
        Assert.False(runtime.ObjectIdentities.TryGetObjectId(wolfActorId, out _));
        Assert.True(runtime.PersistenceWrites!.Flush(TimeSpan.FromSeconds(10)));
        Assert.Empty(world.LoadActiveEnemies());

        typeof(WorldSessionRuntime)
            .GetMethod("SpawnInitialWolf", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(runtime, []);
        EnemyActor respawned = Assert.Single(runtime.Enemies.ActiveActors);
        Assert.NotEqual(wolfActorId, respawned.Combatant.Actor.Id);
        Assert.True(runtime.ObjectIdentities.TryGetObjectId(respawned.Combatant.Actor.Id, out ObjectId after));
        Assert.True(after.Value > before.Value);
    }

    [Fact]
    public void ALegacyCubeIdInTheEnemyRange_RestoresWithTheEnemies_AndCollidesWithNothing()
    {
        using IncrementalCheckpointTests.TempDatabase database = new();
        new SqlitePlayerProfileRepository(database.Schema).GetOrCreate(Owner);
        SqliteWorldPersistenceRepository world = new(database.Schema);
        DateTime now = DateTime.UtcNow;
        (Vector3 firstCell, Vector3 secondCell) = FindTwoFreeCells();
        world.SavePlacedCube(Cube(1003, firstCell, now));
        world.SavePlacedCube(Cube(2004, secondCell, now));
        world.SaveEnemyInstance(Enemy(2006, now));

        using WorldSessionRuntime runtime = CreateRuntime(database, world);

        Assert.True(runtime.ObjectIdentities.TryGetActorId(new ObjectId(1003), out int firstCube));
        Assert.True(runtime.ObjectIdentities.TryGetActorId(new ObjectId(2004), out int secondCube));
        Assert.Equal(
            (ReplicatedActorIdRanges.FirstWorldObjectId, ReplicatedActorIdRanges.FirstWorldObjectId + 1),
            (firstCube, secondCube));
        EnemyActor wolf = Assert.Single(runtime.Enemies.ActiveActors);
        Assert.Equal(WorldSessionRuntime.FirstEnemyActorId, wolf.Combatant.Actor.Id);
        Assert.Equal(3, runtime.ObjectIdentities.Count);
    }

    // Two empty ground-level cells in the runtime's generated world.
    private static (Vector3 First, Vector3 Second) FindTwoFreeCells()
    {
        using WorldSessionRuntime probe = new(20, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        List<Vector3> free = [];
        for (int y = 1; y < probe.Map.Height && free.Count < 2; y++)
        {
            if (probe.Map.TryGetCubeAtCell(12, y, 12) == null)
            {
                free.Add(new Vector3(12f, y, 12f));
            }
        }

        Assert.Equal(2, free.Count);
        return (free[0], free[1]);
    }

    private static WorldSessionRuntime CreateRuntime(IncrementalCheckpointTests.TempDatabase database, IWorldPersistenceRepository world)
    {
        WorldSessionRuntime runtime = new(
            20,
            new Vector3(5f, 1.5f, 5f),
            new SceneEventHub(),
            playerProfiles: new SqlitePlayerProfileRepository(database.Schema),
            worldPersistence: world,
            persistenceWrites: PersistenceWriteQueue.Background());
        runtime.WriteCheckpointOnDispose = false;
        return runtime;
    }

    private static PersistedPlacedCube Cube(int id, Vector3 cell, DateTime now)
    {
        return new PersistedPlacedCube(new ObjectId(id), Owner, cell, GeometryShapeKind.SolidCube, GeometryMaterialId.Grass, 100, 100, now, now);
    }

    private static PersistedEnemyInstance Enemy(int id, DateTime now)
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
}
