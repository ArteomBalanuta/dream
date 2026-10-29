using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Sessions;
using Dream.Server.Persistence;
using Dream.Simulation.Inventory;
using Dream.Simulation.Persistence;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Tests.Server.Persistence;

namespace Dream.Tests.Simulation.Inventory;

// Wave C (C5): item instance ids come from the same allocator as cube, rubble and enemy object ids,
// so an id is never shared by two persisted objects. Without persistence nothing seeds the
// allocator and ids still count from 1.
public sealed class ItemObjectIdTests
{
    private static readonly int StarterCount = ToolCatalog.StarterItemKinds.Count;

    [Fact]
    public void NonPersistentRuntime_GrantsIds1ThroughN()
    {
        using WorldSessionRuntime runtime = new(16, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        using RuntimeAuthoritativeSessionHost host = new(runtime);
        host.JoinSession("solo", "Solo");

        Assert.Equal(Enumerable.Range(1, StarterCount).Select(id => (long)id), ItemIds(runtime, "solo").Order());
        Assert.Equal(StarterCount + 1, runtime.NextItemInstanceId);
    }

    [Fact]
    public void Service_TakesItsIdsFromTheSharedAllocator_AndReservesLoadedIds()
    {
        ObjectIdAllocator ids = new();
        PlayerInventoryService inventory = new(worldPersistence: null, writes: null, objectIds: ids);

        // Another kind of object takes id 1 first: the starter kit continues after it.
        Assert.Equal(1, ids.Take().Value);
        Assert.True(inventory.Ensure("Solo"));
        Assert.Equal(StarterCount + 2, ids.Next);
        Assert.Equal(ids.Next, inventory.NextItemInstanceId);
        Assert.Equal(
            Enumerable.Range(2, StarterCount).Select(id => (long)id),
            inventory.BuildSnapshot("Solo").Slots.Where(slot => slot.Item.HasValue)
                .Select(slot => slot.Item!.Value.ItemInstanceId).Distinct().Order());

        // A loaded inventory above the counter raises it.
        inventory.ApplyLoaded("Late", new PersistedPlayerInventory(
            [new PersistedInventorySlot("Late", 0, 500, DateTime.UtcNow)],
            []));
        Assert.Equal(501, ids.Next);
    }

    [Fact]
    public void PersistentRuntime_ItemIdsNeverCollideWithCubeOrEnemyIds()
    {
        using PersistenceTestDatabase database = new();
        const string Owner = "legacy";
        new SqlitePlayerProfileRepository(database.Schema).GetOrCreate(Owner);
        HashSet<long> objectIds = [1003, 1005, 2004];
        SqliteWorldPersistenceRepository seed = new(database.Schema);
        DateTime now = DateTime.UtcNow;
        seed.SavePlacedCube(Cube(1003, new Vector3(7f, 1f, 5f), Owner, now));
        seed.SavePlacedCube(Cube(1005, new Vector3(5f, 1f, 7f), Owner, now));
        seed.SaveEnemyInstance(Enemy(2004, now));

        long[] granted;
        using (WorldSessionRuntime runtime = CreateRuntime(database))
        {
            using RuntimeAuthoritativeSessionHost host = new(runtime);
            host.JoinSession("visitor", "Visitor");
            granted = ItemIds(runtime, "visitor");
        }

        Assert.Equal(StarterCount, granted.Length);
        Assert.DoesNotContain(granted, id => objectIds.Contains(id));
        Assert.All(granted, id => Assert.True(id > 2004, $"item id {id} is not above every cube and enemy id"));
    }

    [Fact]
    public void PersistentRuntime_ItemIdsAreUniqueAcrossARestart()
    {
        using PersistenceTestDatabase database = new();
        long[] first;
        using (WorldSessionRuntime runtime = CreateRuntime(database))
        {
            using RuntimeAuthoritativeSessionHost host = new(runtime);
            host.JoinSession("one", "One");
            first = ItemIds(runtime, "one");
        }

        long[] second;
        using (WorldSessionRuntime runtime = CreateRuntime(database))
        {
            using RuntimeAuthoritativeSessionHost host = new(runtime);
            Assert.True(runtime.NextItemInstanceId > first.Max());
            host.JoinSession("two", "Two");
            second = ItemIds(runtime, "two");
        }

        Assert.Equal(StarterCount, first.Length);
        Assert.Equal(StarterCount, second.Length);
        Assert.Empty(first.Intersect(second));
        Assert.True(second.Min() > first.Max());
        Assert.Equal(2L * StarterCount, database.Count("SELECT COUNT(DISTINCT item_instance_id) FROM item_instances;"));
    }

    private static WorldSessionRuntime CreateRuntime(PersistenceTestDatabase database)
    {
        return new WorldSessionRuntime(
            16,
            new Vector3(5f, 1.5f, 5f),
            new SceneEventHub(),
            playerProfiles: new SqlitePlayerProfileRepository(database.Schema),
            worldPersistence: new SqliteWorldPersistenceRepository(database.Schema));
    }

    private static long[] ItemIds(WorldSessionRuntime runtime, string sessionId)
    {
        PlayerInventorySnapshot snapshot = runtime.TryCaptureInventorySnapshot(new PlayerSessionId(sessionId))!.Value;
        return [.. snapshot.Slots
            .Where(slot => slot.Item.HasValue)
            .Select(slot => slot.Item!.Value.ItemInstanceId)
            .Distinct()];
    }

    private static PersistedPlacedCube Cube(int id, Vector3 cell, string owner, DateTime now)
    {
        return new PersistedPlacedCube(new ObjectId(id), owner, cell, GeometryShapeKind.SolidCube, GeometryMaterialId.Grass, 100, 100, now, now);
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
