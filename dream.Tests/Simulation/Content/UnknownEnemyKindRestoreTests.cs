using Dream.Server.Persistence;
using Dream.Simulation.Enemies;
using Dream.Simulation.Persistence;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Simulation.Content;

// F2: an enemy row whose kind the catalog does not know is logged and skipped on restore, and its
// row is kept.
public sealed class UnknownEnemyKindRestoreTests
{
    [Fact]
    public void Restore_SkipsAnUnknownEnemyKind_KeepsItsRow_AndRestoresTheKnownOne()
    {
        using Dream.Tests.Server.Persistence.IncrementalCheckpointTests.TempDatabase database = new();
        SqliteWorldPersistenceRepository world = new(database.Schema);
        DateTime now = DateTime.UtcNow;
        world.SaveEnemyInstance(Enemy(2006, "Dragon", now));
        world.SaveEnemyInstance(Enemy(2007, "Wolf", now));

        using (WorldSessionRuntime runtime = CreateRuntime(database, world))
        {
            EnemyActor restored = Assert.Single(runtime.Enemies.ActiveActors);
            Assert.Equal("Wolf", restored.EnemyKind);
            Assert.True(runtime.ObjectIdentities.TryGetObjectId(restored.Combatant.Actor.Id, out ObjectId objectId));
            Assert.Equal(new ObjectId(2007), objectId);
            // Wolf numbers still come from the template: range and aggro are not the row's.
            Assert.Equal(10f, restored.AggroRange);
            Assert.Equal(1.35f, restored.AttackRange);
        }

        Assert.Contains(world.LoadActiveEnemies(), enemy => enemy.ObjectId == new ObjectId(2006) && enemy.EnemyKind == "Dragon");
    }

    private static WorldSessionRuntime CreateRuntime(Dream.Tests.Server.Persistence.IncrementalCheckpointTests.TempDatabase database, IWorldPersistenceRepository world)
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

    private static PersistedEnemyInstance Enemy(int id, string kind, DateTime now)
    {
        return new PersistedEnemyInstance(
            new ObjectId(id),
            kind,
            kind,
            new Vector3(9f + (id % 3), 1f, 9f),
            Vector3.UnitZ,
            new PersistedCreatureStats(40, 100, 10, 10, 10, 0, 2.8f, 1f, 1),
            now,
            now);
    }
}
