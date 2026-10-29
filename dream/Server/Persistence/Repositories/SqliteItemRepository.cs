using Dream.Server.Persistence.Store;
using Dream.Simulation.Persistence;
using Dream.Simulation.World;
using Microsoft.Data.Sqlite;
using OpenTK.Mathematics;

namespace Dream.Server.Persistence.Repositories;

// item_instances: every item, in the world or in a slot.
public sealed partial class SqliteItemRepository(IWorldStore store, WorldId worldId)
    : SqliteRepositoryBase(store, worldId), IItemRepository
{
    // World 1: the default world.
    public SqliteItemRepository(IWorldStore store)
        : this(store, WorldId.Default)
    {
    }

    private const string LoadSql = """
        SELECT
            item_instance_id,
            item_kind,
            display_name,
            visual_id,
            owner_player_name,
            world_position_x,
            world_position_y,
            world_position_z,
            state,
            stack_count,
            created_utc,
            updated_utc
        FROM item_instances
        WHERE world_id = $worldId
        ORDER BY item_instance_id;
        """;

    private const string UpsertSql = """
        INSERT INTO item_instances (
            world_id,
            item_instance_id,
            item_kind,
            display_name,
            visual_id,
            owner_player_name,
            world_position_x,
            world_position_y,
            world_position_z,
            state,
            stack_count,
            created_utc,
            updated_utc
        )
        VALUES (
            $worldId,
            $itemInstanceId,
            $itemKind,
            $displayName,
            $visualId,
            $ownerPlayerName,
            $worldPositionX,
            $worldPositionY,
            $worldPositionZ,
            $state,
            $stackCount,
            $createdUtc,
            $updatedUtc
        )
        ON CONFLICT(world_id, item_instance_id) DO UPDATE SET
            item_kind = excluded.item_kind,
            display_name = excluded.display_name,
            visual_id = excluded.visual_id,
            owner_player_name = excluded.owner_player_name,
            world_position_x = excluded.world_position_x,
            world_position_y = excluded.world_position_y,
            world_position_z = excluded.world_position_z,
            state = excluded.state,
            stack_count = excluded.stack_count,
            updated_utc = excluded.updated_utc;
        """;

    private const string DeleteSql = "DELETE FROM item_instances WHERE world_id = $worldId AND item_instance_id = $itemInstanceId;";

    public IReadOnlyList<PersistedItemInstance> LoadItemInstances() =>
        QueryList(LoadSql, static reader => ReadItem(reader, 0));

    // created_utc keeps item.CreatedUtc (UtcNow when unset); updated_utc is the write time.
    public void SaveItemInstance(PersistedItemInstance item) =>
        Execute(UpsertSql, item, static (command, i) =>
        {
            command.BindInt64("$itemInstanceId", i.ItemInstanceId);
            command.BindText("$itemKind", i.ItemKind);
            command.BindText("$displayName", i.DisplayName);
            command.BindText("$visualId", i.VisualId);
            command.BindNullableText("$ownerPlayerName", i.OwnerPlayerName);
            command.BindNullableVector3("$worldPositionX", "$worldPositionY", "$worldPositionZ", i.WorldPosition);
            command.BindText("$state", i.State);
            command.BindInt64("$stackCount", i.StackCount);
            command.BindUtc("$createdUtc", i.CreatedUtc == default ? DateTime.UtcNow : i.CreatedUtc);
            command.BindUtc("$updatedUtc", DateTime.UtcNow);
        });

    public void DeleteItemInstance(long itemInstanceId) =>
        Execute(DeleteSql, itemInstanceId, static (command, id) => command.BindInt64("$itemInstanceId", id));

    // The item columns in LoadSql's order, starting at firstOrdinal. The world position is null when
    // its x column is NULL (the three are written together).
    internal static PersistedItemInstance ReadItem(SqliteDataReader reader, int firstOrdinal)
    {
        int o = firstOrdinal;
        Vector3? worldPosition = reader.IsDBNull(o + 5) ? null : reader.GetVector3(o + 5);
        return new PersistedItemInstance(
            reader.GetInt64(o),
            reader.GetString(o + 1),
            reader.GetString(o + 2),
            reader.GetString(o + 3),
            reader.GetNullableString(o + 4),
            worldPosition,
            reader.GetString(o + 8),
            reader.GetInt32(o + 9),
            reader.GetUtc(o + 10),
            reader.GetUtc(o + 11));
    }
}
