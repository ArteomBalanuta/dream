using Dream.Server.Persistence.Store;
using Dream.Simulation.Persistence;
using Microsoft.Data.Sqlite;

namespace Dream.Server.Persistence.Repositories;

// player_inventory_slots: one row per occupied slot, keyed by (player_name, slot_index).
// player_starter_grants: the starter kit version each player has received (migration 0002).
// LoadPlayerInventory also reads the referenced item_instances rows and the player's grant row.
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

    // One statement, so the slots, the items and the starter grant come from one snapshot.
    // - The first SELECT gives one row per slot. Its WHERE uses the slot table's primary key
    //   (player_name, slot_index) and the join the item table's primary key, so a load reads one
    //   player's rows only (no new index is needed).
    // - The second gives the player's grant row, if any, by player_starter_grants' primary key, with
    //   NULL slot and item columns and the version in the last column. ORDER BY slot_index puts it
    //   first (NULL sorts first). A player with a grant but no slots still gets the grant back.
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
            i.updated_utc,
            NULL AS kit_version
        FROM player_inventory_slots AS s
        LEFT JOIN item_instances AS i ON i.item_instance_id = s.item_instance_id
        WHERE s.player_name = $playerName
        UNION ALL
        SELECT
            g.player_name,
            NULL, NULL, NULL,
            NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL,
            g.kit_version
        FROM player_starter_grants AS g
        WHERE g.player_name = $playerName
        ORDER BY 2;
        """;

    // LoadPlayerInventorySql's last column.
    private const int KitVersionOrdinal = 16;

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

    // The recorded version never goes down, so a stale write cannot undo a newer grant.
    private const string UpsertStarterGrantSql = """
        INSERT INTO player_starter_grants (
            player_name,
            kit_version,
            updated_utc
        )
        VALUES (
            $playerName,
            $kitVersion,
            $updatedUtc
        )
        ON CONFLICT(player_name) DO UPDATE SET
            kit_version = excluded.kit_version,
            updated_utc = excluded.updated_utc
        WHERE player_starter_grants.kit_version < excluded.kit_version;
        """;

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
        int? starterKitVersion = null;
        while (reader.Read())
        {
            if (reader.IsDBNull(1))
            {
                // The grant row: slot_index is NOT NULL in every slot row.
                starterKitVersion = reader.GetInt32(KitVersionOrdinal);
                continue;
            }

            slots.Add(ReadSlot(reader));
            if (reader.IsDBNull(4) || !itemIds.Add(reader.GetInt64(4)))
            {
                continue;
            }

            items.Add(SqliteItemRepository.ReadItem(reader, 4));
        }

        return new PersistedPlayerInventory(slots, items, starterKitVersion);
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

    // updated_utc is the write time.
    public void SaveStarterKitGrant(string playerName, int kitVersion) =>
        Execute(UpsertStarterGrantSql, (playerName, kitVersion), static (command, grant) =>
        {
            command.BindText("$playerName", grant.playerName.Trim());
            command.BindInt64("$kitVersion", grant.kitVersion);
            command.BindUtc("$updatedUtc", DateTime.UtcNow);
        });

    // The slot columns at ordinals 0 to 3.
    private static PersistedInventorySlot ReadSlot(SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetInt32(1), reader.GetInt64(2), reader.GetUtc(3));
}
