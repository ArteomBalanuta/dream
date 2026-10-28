using Dream.Server.Persistence.Store;
using Dream.Simulation.Persistence;
using Microsoft.Data.Sqlite;

namespace Dream.Server.Persistence.Repositories;

// player_inventory_slots: one row per occupied slot, keyed by (player_name, slot_index).
// LoadPlayerInventory also reads the referenced item_instances rows.
public sealed partial class SqliteInventoryRepository(IWorldStore store)
    : SqliteRepositoryBase(store), IInventoryRepository
{
    private const string LoadSlotsSql = """
        SELECT
            player_name,
            slot_index,
            item_instance_id,
            updated_utc
        FROM player_inventory_slots
        ORDER BY player_name, slot_index;
        """;

    // One statement, so the slots and items come from one snapshot. The WHERE uses the slot table's
    // primary key (player_name, slot_index) and the join the item table's primary key, so a load
    // reads one player's rows only (no new index is needed).
    internal const string LoadPlayerInventorySql = """
        SELECT
            s.player_name,
            s.slot_index,
            s.item_instance_id,
            s.updated_utc,
            i.item_instance_id,
            i.item_kind,
            i.display_name,
            i.visual_id,
            i.owner_player_name,
            i.world_position_x,
            i.world_position_y,
            i.world_position_z,
            i.state,
            i.stack_count,
            i.created_utc,
            i.updated_utc
        FROM player_inventory_slots AS s
        LEFT JOIN item_instances AS i ON i.item_instance_id = s.item_instance_id
        WHERE s.player_name = $playerName
        ORDER BY s.slot_index;
        """;

    private const string UpsertSlotSql = """
        INSERT INTO player_inventory_slots (
            player_name,
            slot_index,
            item_instance_id,
            updated_utc
        )
        VALUES (
            $playerName,
            $slotIndex,
            $itemInstanceId,
            $updatedUtc
        )
        ON CONFLICT(player_name, slot_index) DO UPDATE SET
            item_instance_id = excluded.item_instance_id,
            updated_utc = excluded.updated_utc;
        """;

    private const string DeleteSlotsSql = "DELETE FROM player_inventory_slots WHERE player_name = $playerName;";

    public IReadOnlyList<PersistedInventorySlot> LoadInventorySlots() =>
        QueryList(LoadSlotsSql, static reader => ReadSlot(reader));

    public PersistedPlayerInventory LoadPlayerInventory(string playerName)
    {
        using SqliteSession session = Store.OpenSession();
        using SqliteCommand command = session.CreateCommand(LoadPlayerInventorySql);
        command.BindText("$playerName", playerName.Trim());
        using SqliteDataReader reader = command.ExecuteReader();
        List<PersistedInventorySlot> slots = [];
        List<PersistedItemInstance> items = [];
        HashSet<long> itemIds = [];
        while (reader.Read())
        {
            slots.Add(ReadSlot(reader));
            if (reader.IsDBNull(4) || !itemIds.Add(reader.GetInt64(4)))
            {
                continue;
            }

            items.Add(SqliteItemRepository.ReadItem(reader, 4));
        }

        return new PersistedPlayerInventory(slots, items);
    }

    // updated_utc is the write time.
    public void SaveInventorySlot(PersistedInventorySlot slot) =>
        Execute(UpsertSlotSql, slot, static (command, s) =>
        {
            command.BindText("$playerName", s.PlayerName.Trim());
            command.BindInt64("$slotIndex", s.SlotIndex);
            command.BindInt64("$itemInstanceId", s.ItemInstanceId);
            command.BindUtc("$updatedUtc", DateTime.UtcNow);
        });

    public void DeleteInventorySlots(string playerName) =>
        Execute(DeleteSlotsSql, playerName, static (command, name) => command.BindText("$playerName", name.Trim()));

    // The slot columns at ordinals 0 to 3.
    private static PersistedInventorySlot ReadSlot(SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetInt32(1), reader.GetInt64(2), reader.GetUtc(3));
}
