using Dream.Server.Persistence.Store;
using Dream.Simulation.Persistence;
using Dream.Simulation.World;
using Microsoft.Data.Sqlite;

namespace Dream.Server.Persistence.Repositories;

// player_inventory_slots: one row per occupied slot, keyed by (player_name, slot_index).
// player_starter_grants: the starter kit version each player has received (migration 0002).
// LoadPlayerInventory also reads the referenced item_instances rows and the player's grant row.
public sealed partial class SqliteInventoryRepository(IWorldStore store, WorldId worldId)
    : SqliteRepositoryBase(store, worldId), IInventoryRepository
{
    // World 1: the default world.
    public SqliteInventoryRepository(IWorldStore store)
        : this(store, WorldId.Default)
    {
    }

    private const string LoadSlotsSql = """
        SELECT
            player_name,
            slot_index,
            item_instance_id,
            updated_utc
        FROM player_inventory_slots
        WHERE world_id = $worldId
        ORDER BY player_name, slot_index;
        """;

    // One statement, so the slots, the items and the starter grant come from one snapshot.
    // - The first SELECT gives one row per slot. Its WHERE uses the slot table's primary key
    //   (world_id, player_name, slot_index) and the join the item table's primary key, so a load reads one
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
            i.rolled_modifiers,
            NULL AS kit_version,
            NULL AS gold
        FROM player_inventory_slots AS s
        LEFT JOIN item_instances AS i ON i.world_id = s.world_id AND i.item_instance_id = s.item_instance_id
        WHERE s.world_id = $worldId AND s.player_name = $playerName
        UNION ALL
        SELECT
            g.player_name,
            NULL, NULL, NULL,
            NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL,
            g.kit_version,
            NULL
        FROM player_starter_grants AS g
        WHERE g.world_id = $worldId AND g.player_name = $playerName
        UNION ALL
        SELECT
            w.player_name,
            NULL, NULL, NULL,
            NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL,
            NULL,
            w.gold
        FROM player_wallets AS w
        WHERE w.world_id = $worldId AND w.player_name = $playerName
        ORDER BY 2;
        """;

    // LoadPlayerInventorySql's last two columns. A row with a NULL slot_index is the grant row (kit
    // version set) or the wallet row (kit version NULL, gold set).
    private const int KitVersionOrdinal = 17;

    private const int GoldOrdinal = 18;

    // Adds the delta to the wallet row, creating it. A delta that would take the balance below zero
    // violates CHECK (gold >= 0) and fails the statement; so does a negative delta on a missing row. The
    // caller's closure then rolls back. SQLite checks the proposed insert row before it looks for the
    // conflict, so the proposed gold is 0 when the row exists (a negative delta must not fail there) and
    // the delta itself when it does not (a negative one then fails the CHECK, as it must).
    private const string ApplyWalletDeltaSql = """
        INSERT INTO player_wallets (
            world_id,
            player_name,
            gold,
            updated_utc
        )
        VALUES (
            $worldId,
            $playerName,
            CASE WHEN EXISTS (
                SELECT 1 FROM player_wallets AS w
                WHERE w.world_id = $worldId AND w.player_name = $playerName)
            THEN 0 ELSE $delta END,
            $updatedUtc
        )
        ON CONFLICT(world_id, player_name) DO UPDATE SET
            gold = player_wallets.gold + $delta,
            updated_utc = excluded.updated_utc;
        """;

    private const string LoadQuickbarSql = """
        SELECT player_name, slot, kind, ref_key, updated_utc
        FROM player_quickbar_slots
        WHERE world_id = $worldId AND player_name = $playerName
        ORDER BY slot;
        """;

    private const string UpsertQuickbarSql = """
        INSERT INTO player_quickbar_slots (
            world_id,
            player_name,
            slot,
            kind,
            ref_key,
            updated_utc
        )
        VALUES (
            $worldId,
            $playerName,
            $slot,
            $kind,
            $refKey,
            $updatedUtc
        )
        ON CONFLICT(world_id, player_name, slot) DO UPDATE SET
            kind = excluded.kind,
            ref_key = excluded.ref_key,
            updated_utc = excluded.updated_utc;
        """;

    private const string UpsertSlotSql = """
        INSERT INTO player_inventory_slots (
            world_id,
            player_name,
            slot_index,
            item_instance_id,
            updated_utc
        )
        VALUES (
            $worldId,
            $playerName,
            $slotIndex,
            $itemInstanceId,
            $updatedUtc
        )
        ON CONFLICT(world_id, player_name, slot_index) DO UPDATE SET
            item_instance_id = excluded.item_instance_id,
            updated_utc = excluded.updated_utc
        WHERE player_inventory_slots.item_instance_id = excluded.item_instance_id;
        """;

    private const string DeleteSlotsSql = "DELETE FROM player_inventory_slots WHERE world_id = $worldId AND player_name = $playerName;";

    private const string DeleteSlotSql = "DELETE FROM player_inventory_slots WHERE world_id = $worldId AND player_name = $playerName AND slot_index = $slotIndex AND item_instance_id = $expectedItemInstanceId;";

    private const string DestroyItemSlotsSql = "DELETE FROM player_inventory_slots WHERE world_id = $worldId AND player_name = $playerName AND item_instance_id = $itemInstanceId;";

    // The item row goes only when no slot (of any player) still references it.
    private const string DestroyItemRowSql = """
        DELETE FROM item_instances
        WHERE world_id = $worldId AND item_instance_id = $itemInstanceId
          AND NOT EXISTS (
              SELECT 1 FROM player_inventory_slots AS s
              WHERE s.world_id = $worldId AND s.item_instance_id = $itemInstanceId);
        """;

    // M5 trade: the item row moves to its new owner and goes back to the Inventory state; one row or the
    // closure throws.
    private const string AttachItemOwnerSql = """
        UPDATE item_instances
        SET owner_player_name = $toPlayer, state = 'Inventory', updated_utc = $updatedUtc
        WHERE world_id = $worldId AND item_instance_id = $itemInstanceId;
        """;

    // A plain INSERT: an occupied slot, or an item that already has a slot row (the unique index), throws.
    private const string AttachItemSlotSql = """
        INSERT INTO player_inventory_slots (world_id, player_name, slot_index, item_instance_id, updated_utc)
        VALUES ($worldId, $toPlayer, $slotIndex, $itemInstanceId, $updatedUtc);
        """;

    // The recorded version never goes down, so a stale write cannot undo a newer grant.
    private const string UpsertStarterGrantSql = """
        INSERT INTO player_starter_grants (
            world_id,
            player_name,
            kit_version,
            updated_utc
        )
        VALUES (
            $worldId,
            $playerName,
            $kitVersion,
            $updatedUtc
        )
        ON CONFLICT(world_id, player_name) DO UPDATE SET
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
        command.BindInt64("$worldId", WorldIdValue);
        command.BindText("$playerName", playerName.Trim());
        using SqliteDataReader reader = command.ExecuteReader();
        List<PersistedInventorySlot> slots = [];
        List<PersistedItemInstance> items = [];
        HashSet<long> itemIds = [];
        int? starterKitVersion = null;
        long gold = 0;
        while (reader.Read())
        {
            if (reader.IsDBNull(1))
            {
                // The grant row or the wallet row: slot_index is NOT NULL in every slot row.
                if (reader.IsDBNull(KitVersionOrdinal))
                {
                    gold = reader.GetInt64(GoldOrdinal);
                }
                else
                {
                    starterKitVersion = reader.GetInt32(KitVersionOrdinal);
                }

                continue;
            }

            slots.Add(ReadSlot(reader));
            if (reader.IsDBNull(4) || !itemIds.Add(reader.GetInt64(4)))
            {
                continue;
            }

            items.Add(SqliteItemRepository.ReadItem(reader, 4));
        }

        reader.Close();

        // The quickbar rows are a second query on the same session; only this player's own session
        // writes them, so they cannot diverge from the first read. Null when the player has no row.
        List<PersistedQuickbarSlot>? quickbar = null;
        using (SqliteCommand quickbarCommand = session.CreateCommand(LoadQuickbarSql))
        {
            quickbarCommand.BindInt64("$worldId", WorldIdValue);
            quickbarCommand.BindText("$playerName", playerName.Trim());
            using SqliteDataReader quickbarReader = quickbarCommand.ExecuteReader();
            while (quickbarReader.Read())
            {
                (quickbar ??= []).Add(new PersistedQuickbarSlot(
                    quickbarReader.GetString(0),
                    quickbarReader.GetInt32(1),
                    quickbarReader.GetInt32(2),
                    quickbarReader.GetString(3),
                    quickbarReader.GetUtc(4)));
            }
        }

        return new PersistedPlayerInventory(slots, items, starterKitVersion, gold, quickbar);
    }

    // Upserts one quickbar slot. updated_utc is the write time.
    public void SaveQuickbarSlot(PersistedQuickbarSlot slot) =>
        Execute(UpsertQuickbarSql, slot, static (command, s) =>
        {
            command.BindText("$playerName", s.PlayerName.Trim());
            command.BindInt64("$slot", s.Slot);
            command.BindInt64("$kind", s.Kind);
            command.BindText("$refKey", s.Key ?? string.Empty);
            command.BindUtc("$updatedUtc", DateTime.UtcNow);
        });

    // updated_utc is the write time. A row that holds a DIFFERENT item is never overwritten: the upsert
    // changes nothing and this throws, so the closure's savepoint rolls back and the occupant's row is
    // not orphaned (it is there because an earlier write failed; legitimate moves delete it first).
    public void SaveInventorySlot(PersistedInventorySlot slot)
    {
        int changed = Execute(UpsertSlotSql, slot, static (command, s) =>
        {
            command.BindText("$playerName", s.PlayerName.Trim());
            command.BindInt64("$slotIndex", s.SlotIndex);
            command.BindInt64("$itemInstanceId", s.ItemInstanceId);
            command.BindUtc("$updatedUtc", DateTime.UtcNow);
        });
        if (changed == 0)
        {
            throw new InvalidOperationException(
                $"Inventory slot {slot.SlotIndex} of {slot.PlayerName} holds a different item than {slot.ItemInstanceId}.");
        }
    }

    public void DeleteInventorySlots(string playerName) =>
        Execute(DeleteSlotsSql, playerName, static (command, name) => command.BindText("$playerName", name.Trim()));

    public void DeleteInventorySlot(string playerName, int slotIndex, long expectedItemInstanceId) =>
        Execute(DeleteSlotSql, (playerName, slotIndex, expectedItemInstanceId), static (command, d) =>
        {
            command.BindText("$playerName", d.playerName.Trim());
            command.BindInt64("$slotIndex", d.slotIndex);
            command.BindInt64("$expectedItemInstanceId", d.expectedItemInstanceId);
        });

    public void DestroyHeldInventoryItem(string playerName, long itemInstanceId)
    {
        int deleted = Execute(DestroyItemSlotsSql, (playerName, itemInstanceId), static (command, d) =>
        {
            command.BindText("$playerName", d.playerName.Trim());
            command.BindInt64("$itemInstanceId", d.itemInstanceId);
        });
        if (deleted == 0)
        {
            throw new InvalidOperationException($"{playerName} has no persisted slot row for item {itemInstanceId}.");
        }

        Execute(DestroyItemRowSql, itemInstanceId, static (command, id) => command.BindInt64("$itemInstanceId", id));
    }

    // M5 trade. Deletes the player's slot rows of the item and keeps the item row; throws when the player
    // had no row for it, so the trade's closure rolls back (a phantom item cannot be handed over).
    public void DetachHeldItem(string fromPlayer, long itemInstanceId)
    {
        int deleted = Execute(DestroyItemSlotsSql, (fromPlayer, itemInstanceId), static (command, d) =>
        {
            command.BindText("$playerName", d.fromPlayer.Trim());
            command.BindInt64("$itemInstanceId", d.itemInstanceId);
        });
        if (deleted == 0)
        {
            throw new InvalidOperationException($"{fromPlayer} has no persisted slot row for item {itemInstanceId}.");
        }
    }

    // M5 trade. Gives the item row to toPlayer and inserts its slot row. Throws unless exactly one item row
    // changed, and on an occupied slot (never an upsert), so the closure rolls back whole.
    public void AttachItem(string toPlayer, long itemInstanceId, int slotIndex, DateTime utc)
    {
        int moved = Execute(AttachItemOwnerSql, (toPlayer, itemInstanceId, utc), static (command, d) =>
        {
            command.BindText("$toPlayer", d.toPlayer.Trim());
            command.BindInt64("$itemInstanceId", d.itemInstanceId);
            command.BindUtc("$updatedUtc", d.utc);
        });
        if (moved != 1)
        {
            throw new InvalidOperationException($"Item {itemInstanceId} has no item row to hand to {toPlayer}.");
        }

        Execute(AttachItemSlotSql, (toPlayer, itemInstanceId, slotIndex, utc), static (command, d) =>
        {
            command.BindText("$toPlayer", d.toPlayer.Trim());
            command.BindInt64("$itemInstanceId", d.itemInstanceId);
            command.BindInt64("$slotIndex", d.slotIndex);
            command.BindUtc("$updatedUtc", d.utc);
        });
    }

    public void DestroyInventoryItem(string playerName, long itemInstanceId)
    {
        Execute(DestroyItemSlotsSql, (playerName, itemInstanceId), static (command, d) =>
        {
            command.BindText("$playerName", d.playerName.Trim());
            command.BindInt64("$itemInstanceId", d.itemInstanceId);
        });
        Execute(DestroyItemRowSql, itemInstanceId, static (command, id) => command.BindInt64("$itemInstanceId", id));
    }

    // updated_utc is the write time.
    public void SaveStarterKitGrant(string playerName, int kitVersion) =>
        Execute(UpsertStarterGrantSql, (playerName, kitVersion), static (command, grant) =>
        {
            command.BindText("$playerName", grant.playerName.Trim());
            command.BindInt64("$kitVersion", grant.kitVersion);
            command.BindUtc("$updatedUtc", DateTime.UtcNow);
        });

    // updated_utc is the write time.
    public void ApplyWalletDelta(string playerName, long delta) =>
        Execute(ApplyWalletDeltaSql, (playerName, delta), static (command, d) =>
        {
            command.BindText("$playerName", d.playerName.Trim());
            command.BindInt64("$delta", d.delta);
            command.BindUtc("$updatedUtc", DateTime.UtcNow);
        });

    // The slot columns at ordinals 0 to 3.
    private static PersistedInventorySlot ReadSlot(SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetInt32(1), reader.GetInt64(2), reader.GetUtc(3));
}
