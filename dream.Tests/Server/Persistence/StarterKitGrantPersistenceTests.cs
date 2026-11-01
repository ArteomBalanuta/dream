using Dream.Simulation.World;
using Microsoft.Data.Sqlite;
using Xunit;
using Dream.Server.Persistence;
using Dream.Simulation.Inventory;
using Dream.Simulation.Persistence;

namespace Dream.Tests.Server.Persistence;

// Fix loop 1 after waves T2 and A: player_starter_grants (migration 0002) records the starter kit
// version each player has received. It is read with the inventory in LoadPlayerInventory's one
// statement and written in the same write-queue group as the granted items.
public sealed class StarterKitGrantPersistenceTests
{
    [Fact]
    public void LoadPlayerInventory_ReturnsNoVersion_UntilAGrantIsSaved()
    {
        using PersistenceTestDatabase database = new();
        CreateProfiles(database, "ann");
        SqliteWorldPersistenceRepository world = new(database.Schema);
        SaveItemInSlot(world, "ann", itemInstanceId: 1, slotIndex: 0);

        Assert.Null(world.LoadPlayerInventory("ann").StarterKitVersion);

        world.SaveStarterKitGrant(" ann ", 2);

        PersistedPlayerInventory inventory = world.LoadPlayerInventory("ann");
        Assert.Equal(2, inventory.StarterKitVersion);
        PersistedInventorySlot slot = Assert.Single(inventory.Slots);
        Assert.Equal(0, slot.SlotIndex);
        Assert.Equal(1, Assert.Single(inventory.Items).ItemInstanceId);
    }

    [Fact]
    public void LoadPlayerInventory_ReturnsTheGrant_ForAPlayerWithNoSlots_AndOnlyThatPlayersGrant()
    {
        using PersistenceTestDatabase database = new();
        CreateProfiles(database, "ben", "cat");
        SqliteWorldPersistenceRepository world = new(database.Schema);
        world.SaveStarterKitGrant("ben", 1);
        world.SaveStarterKitGrant("cat", 2);
        SaveItemInSlot(world, "cat", itemInstanceId: 7, slotIndex: 3);

        PersistedPlayerInventory ben = world.LoadPlayerInventory("ben");
        Assert.Equal(1, ben.StarterKitVersion);
        Assert.Empty(ben.Slots);
        Assert.Empty(ben.Items);
        Assert.Equal(2, world.LoadPlayerInventory("cat").StarterKitVersion);
        Assert.Null(world.LoadPlayerInventory("nobody").StarterKitVersion);
    }

    [Fact]
    public void SaveStarterKitGrant_NeverLowersTheVersion_AndSlotDeletesKeepIt()
    {
        using PersistenceTestDatabase database = new();
        CreateProfiles(database, "dan");
        SqliteWorldPersistenceRepository world = new(database.Schema);

        world.SaveStarterKitGrant("dan", 2);
        world.SaveStarterKitGrant("dan", 1);
        world.DeleteInventorySlots("dan");

        Assert.Equal(2, world.LoadPlayerInventory("dan").StarterKitVersion);
        Assert.Equal(1L, database.Count("SELECT COUNT(*) FROM player_starter_grants;"));
        world.SaveStarterKitGrant("dan", 3);
        Assert.Equal(3, world.LoadPlayerInventory("dan").StarterKitVersion);
    }

    [Fact]
    public void LoadPlayerInventory_ReadsTheGrantByItsPrimaryKey_InTheSameStatement()
    {
        using PersistenceTestDatabase database = new();
        using SqliteConnection connection = database.Schema.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + SqliteWorldPersistenceRepository.LoadPlayerInventorySql;
        command.Parameters.AddWithValue("$playerName", "Player A");
        command.Parameters.AddWithValue("$worldId", 1L);
        List<string> plan = [];
        using (SqliteDataReader reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                plan.Add(reader.GetString(3));
            }
        }

        Assert.DoesNotContain(plan, step => step.StartsWith("SCAN", StringComparison.Ordinal));
        Assert.Contains(plan, step => step.StartsWith("SEARCH g USING", StringComparison.Ordinal));
        Assert.Contains(plan, step => step.StartsWith("SEARCH s USING", StringComparison.Ordinal));
    }

    [Fact]
    public void NewPlayerSeeding_WritesTheItemsAndTheGrant_InOneGroupCommit()
    {
        using PersistenceTestDatabase database = new();
        CreateProfiles(database, "fay");
        SqliteWorldPersistenceRepository world = new(database.Schema);
        using PersistenceWriteQueue writes = PersistenceWriteQueue.Background();
        PlayerInventoryService inventory = new(world, writes, new ObjectIdAllocator());
        // As a join prefetch would: the new player's (empty) inventory is loaded.
        inventory.ApplyLoaded("fay", world.LoadPlayerInventory("fay"));

        // The worker runs the gate as a group of its own, so every write Ensure queues behind it
        // lands in the next group.
        using ManualResetEventSlim gateRunning = new(initialState: false);
        using ManualResetEventSlim gate = new(initialState: false);
        long groupsBefore = writes.CommittedGroups;
        writes.Enqueue(() =>
        {
            gateRunning.Set();
            gate.Wait(TimeSpan.FromSeconds(30));
        });
        Assert.True(gateRunning.Wait(TimeSpan.FromSeconds(30)));
        Assert.True(inventory.Ensure("fay"));
        gate.Set();
        Assert.True(writes.Flush(TimeSpan.FromSeconds(30)));

        Assert.Equal(0, writes.FailedWrites);
        // The gate, then ONE write for the whole seed (every kit item with its slot, the equipped
        // slot and the grant), so they land together or not at all.
        Assert.Equal(1 + 1, writes.CompletedWrites);
        // The gate's group touches no database; one group commits all of the seeding's writes, the
        // grant included.
        Assert.Equal(1, writes.CommittedGroups - groupsBefore);
        Assert.Equal((long)ToolCatalog.CurrentStarterKitVersion, database.Count("SELECT kit_version FROM player_starter_grants WHERE player_name = 'fay';"));
        Assert.Equal((long)ToolCatalog.StarterKit.Count, database.Count("SELECT COUNT(*) FROM item_instances WHERE owner_player_name = 'fay';"));
        Assert.Equal(ToolCatalog.CurrentStarterKitVersion, world.LoadPlayerInventory("fay").StarterKitVersion);
    }

    [Fact]
    public void Grants_AreDeletedWithTheirProfile()
    {
        using PersistenceTestDatabase database = new();
        CreateProfiles(database, "gil");
        SqliteWorldPersistenceRepository world = new(database.Schema);
        world.SaveStarterKitGrant("gil", 2);

        using (SqliteConnection connection = database.Schema.CreateOpenConnection())
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA foreign_keys = ON; DELETE FROM player_profiles WHERE player_name = 'gil';";
            command.ExecuteNonQuery();
        }

        Assert.Equal(0L, database.Count("SELECT COUNT(*) FROM player_starter_grants;"));
    }

    private static void CreateProfiles(PersistenceTestDatabase database, params string[] names)
    {
        using SqlitePlayerProfileRepository profiles = new(database.Schema);
        foreach (string name in names)
        {
            profiles.GetOrCreate(name);
        }
    }

    private static void SaveItemInSlot(SqliteWorldPersistenceRepository world, string owner, long itemInstanceId, int slotIndex)
    {
        ToolDefinition tool = ToolCatalog.Resolve("tool.builder-hammer");
        world.SaveItemInstance(new PersistedItemInstance(
            itemInstanceId,
            tool.ItemKind,
            tool.DisplayName,
            tool.VisualId,
            owner,
            WorldPosition: null,
            State: "Inventory",
            StackCount: 1,
            CreatedUtc: DateTime.UtcNow,
            UpdatedUtc: DateTime.UtcNow));
        world.SaveInventorySlot(new PersistedInventorySlot(owner, slotIndex, itemInstanceId, DateTime.UtcNow));
    }
}
