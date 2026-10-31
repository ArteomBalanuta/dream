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
            // Damage is the template's, not derived from the stored Strength (30 here: a derivation would give 32).
            Assert.Equal(12, restored.Combatant.AttackDamage);

            // Checkpoints never delete rows: one full checkpoint later, the Dragon row is still there.
            runtime.PersistCheckpoint();
            Assert.True(runtime.PersistenceWrites!.Flush(TimeSpan.FromSeconds(10)));
        }

        Assert.Contains(world.LoadActiveEnemies(), enemy => enemy.ObjectId == new ObjectId(2006) && enemy.EnemyKind == "Dragon");
        Assert.Contains(world.LoadActiveEnemies(), enemy => enemy.ObjectId == new ObjectId(2007) && enemy.EnemyKind == "Wolf");
    }

    [Fact]
    public void Restore_WhenEveryRowIsAnUnknownKind_KeepsTheRows_AndANewWolfGetsAFreshObjectId()
    {
        using Dream.Tests.Server.Persistence.IncrementalCheckpointTests.TempDatabase database = new();
        SqliteWorldPersistenceRepository world = new(database.Schema);
        DateTime now = DateTime.UtcNow;
        world.SaveEnemyInstance(Enemy(2006, "Dragon", now));
        world.SaveEnemyInstance(Enemy(2009, "Griffin", now));

        using (WorldSessionRuntime runtime = CreateRuntime(database, world))
        {
            Assert.Empty(runtime.Enemies.ActiveActors);

            typeof(WorldSessionRuntime)
                .GetMethod("SpawnInitialWolf", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(runtime, []);
            EnemyActor wolf = Assert.Single(runtime.Enemies.ActiveActors);
            Assert.True(runtime.ObjectIdentities.TryGetObjectId(wolf.Combatant.Actor.Id, out ObjectId wolfId));
            // Past both unknown rows: nothing collides with a kept row.
            Assert.True(wolfId.Value > 2009, wolfId.ToString());

            runtime.PersistCheckpoint();
            Assert.True(runtime.PersistenceWrites!.Flush(TimeSpan.FromSeconds(10)));
        }

        IReadOnlyList<PersistedEnemyInstance> rows = world.LoadActiveEnemies();
        Assert.Contains(rows, enemy => enemy.ObjectId == new ObjectId(2006) && enemy.EnemyKind == "Dragon");
        Assert.Contains(rows, enemy => enemy.ObjectId == new ObjectId(2009) && enemy.EnemyKind == "Griffin");
        Assert.Equal(3, rows.Count);
        Assert.Equal(3, rows.Select(enemy => enemy.ObjectId).Distinct().Count());
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
            new PersistedCreatureStats(40, 100, 30, 10, 10, 0, 2.8f, 1f, 1),
            now,
            now);
    }
}
