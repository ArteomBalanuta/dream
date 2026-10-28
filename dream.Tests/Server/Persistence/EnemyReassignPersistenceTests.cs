using Dream.Server.Persistence;
using Dream.Simulation.Persistence;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Server.Persistence;

// Wave 10 fix loop (reviewer S3): a restore that re-identifies an enemy colliding with a legacy
// placed cube used to queue two writes (delete the old row, save the new one), which could land in
// different groups, so a failure or crash between them lost the enemy. The move is now one queued
// write (IWorldPersistenceRepository.ReassignEnemyId, one UPDATE under one savepoint), so the enemy
// is always present under exactly one id.
public sealed class EnemyReassignPersistenceTests
{
    private const string Owner = "legacy";
    private const int CollidingId = 2004;
    private const int OtherEnemyId = 2006;
    // Past every persisted id (cube 2010 is the highest).
    private const int MovedId = 2011;

    [Fact]
    public void ReassignEnemyId_MovesTheRow_IsANoOpWithoutOne_AndFailsAtomicallyOnATakenId()
    {
        using IncrementalCheckpointTests.TempDatabase database = new();
        SqliteWorldPersistenceRepository world = new(database.Schema);
        DateTime now = DateTime.UtcNow;
        world.SaveEnemyInstance(Enemy(CollidingId, now));
        world.SaveEnemyInstance(Enemy(OtherEnemyId, now));

        world.ReassignEnemyId(CollidingId, MovedId);
        Assert.Equal([OtherEnemyId, MovedId], EnemyIds(world));
        PersistedEnemyInstance moved = Assert.Single(world.LoadActiveEnemies(), enemy => enemy.EnemyId == MovedId);
        Assert.Equal(Enemy(CollidingId, now).Position, moved.Position);

        world.ReassignEnemyId(CollidingId, 3000);
        Assert.Equal([OtherEnemyId, MovedId], EnemyIds(world));

        Assert.ThrowsAny<Exception>(() => world.ReassignEnemyId(MovedId, OtherEnemyId));
        Assert.Equal([OtherEnemyId, MovedId], EnemyIds(world));
    }

    [Fact]
    public void Restore_WithACollision_MovesTheEnemyRowInOneWrite()
    {
        using IncrementalCheckpointTests.TempDatabase database = new();
        SeedLegacyWorld(database);
        SqliteWorldPersistenceRepository world = new(database.Schema);
        CountingWorld counting = new(world, failReassign: false);

        RestoreAndFlush(database, counting, out long failedWrites);

        Assert.Equal(0, failedWrites);
        Assert.Equal(1, counting.Reassigns);
        Assert.Equal(0, counting.EnemyDeletes);
        Assert.Equal(0, counting.EnemySaves);
        Assert.Equal([OtherEnemyId, MovedId], EnemyIds(world));
    }

    // The re-id write fails after its UPDATE ran (a crash or error mid-write): the queue rolls the
    // write back to its savepoint, so the enemy is still present, under its old id only. The next
    // restore re-identifies it again.
    [Fact]
    public void Restore_WithACollision_ThenAFailedWrite_LeavesTheEnemyUnderExactlyOneId()
    {
        using IncrementalCheckpointTests.TempDatabase database = new();
        SeedLegacyWorld(database);
        SqliteWorldPersistenceRepository world = new(database.Schema);

        RestoreAndFlush(database, new CountingWorld(world, failReassign: true), out long failedWrites);

        Assert.Equal(1, failedWrites);
        Assert.Equal([CollidingId, OtherEnemyId], EnemyIds(world));

        RestoreAndFlush(database, new CountingWorld(world, failReassign: false), out failedWrites);

        Assert.Equal(0, failedWrites);
        Assert.Equal([OtherEnemyId, MovedId], EnemyIds(world));
    }

    private static void RestoreAndFlush(
        IncrementalCheckpointTests.TempDatabase database,
        IWorldPersistenceRepository world,
        out long failedWrites)
    {
        using PersistenceWriteQueue writes = PersistenceWriteQueue.Background();
        using (WorldSessionRuntime runtime = new(
            20,
            new Vector3(5f, 1.5f, 5f),
            new SceneEventHub(),
            playerProfiles: new SqlitePlayerProfileRepository(database.Schema),
            worldPersistence: world,
            persistenceWrites: writes))
        {
            // Only the restore's own writes: no shutdown checkpoint that rewrites the live enemies.
            runtime.WriteCheckpointOnDispose = false;
            int[] liveIds = [.. runtime.Enemies.ActiveActors.Select(enemy => enemy.Combatant.Actor.Id).Order()];
            Assert.Equal([OtherEnemyId, MovedId], liveIds);
            Assert.True(writes.Flush(TimeSpan.FromSeconds(10)), "the persistence queue did not drain");
        }

        failedWrites = writes.FailedWrites;
    }

    private static void SeedLegacyWorld(IncrementalCheckpointTests.TempDatabase database)
    {
        new SqlitePlayerProfileRepository(database.Schema).GetOrCreate(Owner);
        SqliteWorldPersistenceRepository world = new(database.Schema);
        DateTime now = DateTime.UtcNow;
        world.SavePlacedCube(Cube(1003, new Vector3(7f, 1f, 5f), now));
        world.SavePlacedCube(Cube(CollidingId, new Vector3(5f, 1f, 7f), now));
        world.SavePlacedCube(Cube(2010, new Vector3(7f, 1f, 7f), now));
        world.SaveEnemyInstance(Enemy(CollidingId, now));
        world.SaveEnemyInstance(Enemy(OtherEnemyId, now));
    }

    private static int[] EnemyIds(IWorldPersistenceRepository world)
    {
        return [.. world.LoadActiveEnemies().Select(enemy => enemy.EnemyId).Order()];
    }

    private static PersistedPlacedCube Cube(int id, Vector3 cell, DateTime now)
    {
        return new PersistedPlacedCube(id, Owner, cell, GeometryShapeKind.SolidCube, GeometryMaterialId.Grass, 100, 100, now, now);
    }

    private static PersistedEnemyInstance Enemy(int id, DateTime now)
    {
        return new PersistedEnemyInstance(
            id,
            "Wolf",
            "Wolf",
            new Vector3(9f, 1f, 9f),
            Vector3.UnitZ,
            new PersistedCreatureStats(40, 100, 10, 10, 10, 0, 2.8f, 1f, 1),
            now,
            now);
    }

    // Counts the enemy writes, and can fail the re-id after its UPDATE has run.
    private sealed class CountingWorld(IWorldPersistenceRepository inner, bool failReassign) : IWorldPersistenceRepository
    {
        public int Reassigns { get; private set; }

        public int EnemySaves { get; private set; }

        public int EnemyDeletes { get; private set; }

        public PersistedPlayerWorldState? TryGetPlayerWorldState(string playerName) => inner.TryGetPlayerWorldState(playerName);

        public void SavePlayerWorldState(PersistedPlayerWorldState state) => inner.SavePlayerWorldState(state);

        public IReadOnlyList<PersistedPlacedCube> LoadPlacedCubes() => inner.LoadPlacedCubes();

        public void SavePlacedCube(PersistedPlacedCube cube) => inner.SavePlacedCube(cube);

        public void DeletePlacedCube(int cubeId) => inner.DeletePlacedCube(cubeId);

        public IReadOnlyList<PersistedCubeFracturePiece> LoadCubeFracturePieces() => inner.LoadCubeFracturePieces();

        public void SaveCubeFracturePiece(PersistedCubeFracturePiece piece) => inner.SaveCubeFracturePiece(piece);

        public void DeleteCubeFracturePieces(int sourceCubeId) => inner.DeleteCubeFracturePieces(sourceCubeId);

        public IReadOnlyList<PersistedEnemyInstance> LoadActiveEnemies() => inner.LoadActiveEnemies();

        public void SaveEnemyInstance(PersistedEnemyInstance enemy)
        {
            EnemySaves++;
            inner.SaveEnemyInstance(enemy);
        }

        public void DeleteEnemyInstance(int enemyId)
        {
            EnemyDeletes++;
            inner.DeleteEnemyInstance(enemyId);
        }

        public void ReassignEnemyId(int oldEnemyId, int newEnemyId)
        {
            Reassigns++;
            inner.ReassignEnemyId(oldEnemyId, newEnemyId);
            if (failReassign)
            {
                throw new IOException("simulated failure after the re-id UPDATE");
            }
        }

        public IReadOnlyList<PersistedItemInstance> LoadItemInstances() => inner.LoadItemInstances();

        public void SaveItemInstance(PersistedItemInstance item) => inner.SaveItemInstance(item);

        public void DeleteItemInstance(long itemInstanceId) => inner.DeleteItemInstance(itemInstanceId);

        public IReadOnlyList<PersistedInventorySlot> LoadInventorySlots() => inner.LoadInventorySlots();

        public void SaveInventorySlot(PersistedInventorySlot slot) => inner.SaveInventorySlot(slot);

        public void DeleteInventorySlots(string playerName) => inner.DeleteInventorySlots(playerName);
    }
}
