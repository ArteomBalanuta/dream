using Dream.Simulation.Persistence;
using Dream.Simulation.World.Geometry;
using Microsoft.Data.Sqlite;
using OpenTK.Mathematics;

namespace Dream.Server.Persistence;

public sealed class SqliteWorldPersistenceRepository : IWorldPersistenceRepository, IDisposable
{
    private readonly SqliteSchemaBootstrapper _schema;

    public SqliteWorldPersistenceRepository(SqliteSchemaBootstrapper schema)
    {
        _schema = schema;
    }

    public PersistedPlayerWorldState? TryGetPlayerWorldState(string playerName)
    {
        using SqliteConnectionLease lease = _schema.OpenConnection();
        SqliteConnection connection = lease.Connection;
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                player_name,
                position_x,
                position_y,
                position_z,
                facing_x,
                facing_y,
                facing_z,
                current_hp,
                current_energy,
                updated_utc
            FROM player_world_state
            WHERE player_name = $playerName;
            """;
        command.Parameters.AddWithValue("$playerName", playerName.Trim());
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new PersistedPlayerWorldState(
            reader.GetString(0),
            new Vector3(reader.GetFloat(1), reader.GetFloat(2), reader.GetFloat(3)),
            new Vector3(reader.GetFloat(4), reader.GetFloat(5), reader.GetFloat(6)),
            reader.GetInt32(7),
            reader.GetInt32(8),
            SqliteSchemaBootstrapper.ParseUtc(reader.GetString(9)));
    }

    public void SavePlayerWorldState(PersistedPlayerWorldState state)
    {
        using SqliteConnectionLease lease = _schema.OpenConnection();
        SqliteConnection connection = lease.Connection;
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO player_world_state (
                player_name,
                position_x,
                position_y,
                position_z,
                facing_x,
                facing_y,
                facing_z,
                current_hp,
                current_energy,
                updated_utc
            )
            VALUES (
                $playerName,
                $positionX,
                $positionY,
                $positionZ,
                $facingX,
                $facingY,
                $facingZ,
                $currentHp,
                $currentEnergy,
                $updatedUtc
            )
            ON CONFLICT(player_name) DO UPDATE SET
                position_x = excluded.position_x,
                position_y = excluded.position_y,
                position_z = excluded.position_z,
                facing_x = excluded.facing_x,
                facing_y = excluded.facing_y,
                facing_z = excluded.facing_z,
                current_hp = excluded.current_hp,
                current_energy = excluded.current_energy,
                updated_utc = excluded.updated_utc;
            """;
        command.Parameters.AddWithValue("$playerName", state.PlayerName.Trim());
        command.Parameters.AddWithValue("$positionX", state.Position.X);
        command.Parameters.AddWithValue("$positionY", state.Position.Y);
        command.Parameters.AddWithValue("$positionZ", state.Position.Z);
        command.Parameters.AddWithValue("$facingX", state.Facing.X);
        command.Parameters.AddWithValue("$facingY", state.Facing.Y);
        command.Parameters.AddWithValue("$facingZ", state.Facing.Z);
        command.Parameters.AddWithValue("$currentHp", state.CurrentHealth);
        command.Parameters.AddWithValue("$currentEnergy", state.CurrentEnergy);
        command.Parameters.AddWithValue("$updatedUtc", SqliteSchemaBootstrapper.FormatUtc(DateTime.UtcNow));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<PersistedPlacedCube> LoadPlacedCubes()
    {
        using SqliteConnectionLease lease = _schema.OpenConnection();
        SqliteConnection connection = lease.Connection;
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                cube_id,
                owner_player_name,
                cell_x,
                cell_y,
                cell_z,
                center_x,
                center_y,
                center_z,
                shape_kind,
                material_id,
                current_health,
                max_health,
                created_utc,
                updated_utc
            FROM placed_cube_instances
            ORDER BY cube_id;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        List<PersistedPlacedCube> cubes = [];
        while (reader.Read())
        {
            cubes.Add(new PersistedPlacedCube(
                reader.GetInt32(0),
                reader.GetString(1),
                new Vector3(reader.GetFloat(2), reader.GetFloat(3), reader.GetFloat(4)),
                ShapeKind: Enum.Parse<GeometryShapeKind>(reader.GetString(8), ignoreCase: true),
                MaterialId: Enum.Parse<GeometryMaterialId>(reader.GetString(9), ignoreCase: true),
                CurrentHealth: reader.GetInt32(10),
                MaxHealth: reader.GetInt32(11),
                CreatedUtc: SqliteSchemaBootstrapper.ParseUtc(reader.GetString(12)),
                UpdatedUtc: SqliteSchemaBootstrapper.ParseUtc(reader.GetString(13)),
                CenterPosition: reader.IsDBNull(5) || reader.IsDBNull(6) || reader.IsDBNull(7)
                    ? null
                    : new Vector3(reader.GetFloat(5), reader.GetFloat(6), reader.GetFloat(7))));
        }

        return cubes;
    }

    public void SavePlacedCube(PersistedPlacedCube cube)
    {
        using SqliteConnectionLease lease = _schema.OpenConnection();
        SqliteConnection connection = lease.Connection;
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO placed_cube_instances (
                cube_id,
                owner_player_name,
                cell_x,
                cell_y,
                cell_z,
                center_x,
                center_y,
                center_z,
                shape_kind,
                material_id,
                current_health,
                max_health,
                created_utc,
                updated_utc
            )
            VALUES (
                $cubeId,
                $ownerPlayerName,
                $cellX,
                $cellY,
                $cellZ,
                $centerX,
                $centerY,
                $centerZ,
                $shapeKind,
                $materialId,
                $currentHealth,
                $maxHealth,
                $createdUtc,
                $updatedUtc
            )
            ON CONFLICT(cube_id) DO UPDATE SET
                owner_player_name = excluded.owner_player_name,
                cell_x = excluded.cell_x,
                cell_y = excluded.cell_y,
                cell_z = excluded.cell_z,
                center_x = excluded.center_x,
                center_y = excluded.center_y,
                center_z = excluded.center_z,
                shape_kind = excluded.shape_kind,
                material_id = excluded.material_id,
                current_health = excluded.current_health,
                max_health = excluded.max_health,
                updated_utc = excluded.updated_utc;
            """;
        command.Parameters.AddWithValue("$cubeId", cube.CubeId);
        command.Parameters.AddWithValue("$ownerPlayerName", cube.OwnerPlayerName);
        command.Parameters.AddWithValue("$cellX", cube.CellPosition.X);
        command.Parameters.AddWithValue("$cellY", cube.CellPosition.Y);
        command.Parameters.AddWithValue("$cellZ", cube.CellPosition.Z);
        command.Parameters.AddWithValue("$centerX", (object?)cube.CenterPosition?.X ?? DBNull.Value);
        command.Parameters.AddWithValue("$centerY", (object?)cube.CenterPosition?.Y ?? DBNull.Value);
        command.Parameters.AddWithValue("$centerZ", (object?)cube.CenterPosition?.Z ?? DBNull.Value);
        command.Parameters.AddWithValue("$shapeKind", cube.ShapeKind.ToString());
        command.Parameters.AddWithValue("$materialId", cube.MaterialId.ToString());
        command.Parameters.AddWithValue("$currentHealth", cube.CurrentHealth);
        command.Parameters.AddWithValue("$maxHealth", cube.MaxHealth);
        command.Parameters.AddWithValue("$createdUtc", SqliteSchemaBootstrapper.FormatUtc(cube.CreatedUtc == default ? DateTime.UtcNow : cube.CreatedUtc));
        command.Parameters.AddWithValue("$updatedUtc", SqliteSchemaBootstrapper.FormatUtc(DateTime.UtcNow));
        command.ExecuteNonQuery();
    }

    public void DeletePlacedCube(int cubeId)
    {
        using SqliteConnectionLease lease = _schema.OpenConnection();
        SqliteConnection connection = lease.Connection;
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM placed_cube_instances WHERE cube_id = $cubeId;";
        command.Parameters.AddWithValue("$cubeId", cubeId);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<PersistedCubeFracturePiece> LoadCubeFracturePieces()
    {
        using SqliteConnectionLease lease = _schema.OpenConnection();
        SqliteConnection connection = lease.Connection;
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                source_cube_id,
                piece_index,
                owner_player_name,
                parent_cell_x,
                parent_cell_y,
                parent_cell_z,
                center_x,
                center_y,
                center_z,
                side_length,
                material_id,
                created_utc,
                updated_utc
            FROM cube_fracture_pieces
            ORDER BY source_cube_id, piece_index;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        List<PersistedCubeFracturePiece> pieces = [];
        while (reader.Read())
        {
            pieces.Add(new PersistedCubeFracturePiece(
                SourceCubeId: reader.GetInt32(0),
                PieceIndex: reader.GetInt32(1),
                OwnerPlayerName: reader.GetString(2),
                ParentCell: new Vector3(reader.GetFloat(3), reader.GetFloat(4), reader.GetFloat(5)),
                Center: new Vector3(reader.GetFloat(6), reader.GetFloat(7), reader.GetFloat(8)),
                SideLength: reader.GetFloat(9),
                MaterialId: Enum.Parse<GeometryMaterialId>(reader.GetString(10), ignoreCase: true),
                CreatedUtc: SqliteSchemaBootstrapper.ParseUtc(reader.GetString(11)),
                UpdatedUtc: SqliteSchemaBootstrapper.ParseUtc(reader.GetString(12))));
        }

        return pieces;
    }

    public void SaveCubeFracturePiece(PersistedCubeFracturePiece piece)
    {
        using SqliteConnectionLease lease = _schema.OpenConnection();
        SqliteConnection connection = lease.Connection;
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO cube_fracture_pieces (
                source_cube_id,
                piece_index,
                owner_player_name,
                parent_cell_x,
                parent_cell_y,
                parent_cell_z,
                center_x,
                center_y,
                center_z,
                side_length,
                material_id,
                created_utc,
                updated_utc
            )
            VALUES (
                $sourceCubeId,
                $pieceIndex,
                $ownerPlayerName,
                $parentCellX,
                $parentCellY,
                $parentCellZ,
                $centerX,
                $centerY,
                $centerZ,
                $sideLength,
                $materialId,
                $createdUtc,
                $updatedUtc
            )
            ON CONFLICT(source_cube_id, piece_index) DO UPDATE SET
                owner_player_name = excluded.owner_player_name,
                parent_cell_x = excluded.parent_cell_x,
                parent_cell_y = excluded.parent_cell_y,
                parent_cell_z = excluded.parent_cell_z,
                center_x = excluded.center_x,
                center_y = excluded.center_y,
                center_z = excluded.center_z,
                side_length = excluded.side_length,
                material_id = excluded.material_id,
                updated_utc = excluded.updated_utc;
            """;
        command.Parameters.AddWithValue("$sourceCubeId", piece.SourceCubeId);
        command.Parameters.AddWithValue("$pieceIndex", piece.PieceIndex);
        command.Parameters.AddWithValue("$ownerPlayerName", piece.OwnerPlayerName.Trim());
        command.Parameters.AddWithValue("$parentCellX", piece.ParentCell.X);
        command.Parameters.AddWithValue("$parentCellY", piece.ParentCell.Y);
        command.Parameters.AddWithValue("$parentCellZ", piece.ParentCell.Z);
        command.Parameters.AddWithValue("$centerX", piece.Center.X);
        command.Parameters.AddWithValue("$centerY", piece.Center.Y);
        command.Parameters.AddWithValue("$centerZ", piece.Center.Z);
        command.Parameters.AddWithValue("$sideLength", piece.SideLength);
        command.Parameters.AddWithValue("$materialId", piece.MaterialId.ToString());
        command.Parameters.AddWithValue("$createdUtc", SqliteSchemaBootstrapper.FormatUtc(piece.CreatedUtc == default ? DateTime.UtcNow : piece.CreatedUtc));
        command.Parameters.AddWithValue("$updatedUtc", SqliteSchemaBootstrapper.FormatUtc(DateTime.UtcNow));
        command.ExecuteNonQuery();
    }

    public void DeleteCubeFracturePieces(int sourceCubeId)
    {
        using SqliteConnectionLease lease = _schema.OpenConnection();
        SqliteConnection connection = lease.Connection;
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM cube_fracture_pieces WHERE source_cube_id = $sourceCubeId;";
        command.Parameters.AddWithValue("$sourceCubeId", sourceCubeId);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<PersistedEnemyInstance> LoadActiveEnemies()
    {
        using SqliteConnectionLease lease = _schema.OpenConnection();
        SqliteConnection connection = lease.Connection;
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                enemy_id,
                enemy_kind,
                display_name,
                position_x,
                position_y,
                position_z,
                facing_x,
                facing_y,
                facing_z,
                current_hp,
                max_hp,
                strength,
                agility,
                vitality,
                energy,
                movement_speed,
                attack_speed,
                level,
                created_utc,
                updated_utc
            FROM enemy_instances
            ORDER BY enemy_id;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        List<PersistedEnemyInstance> enemies = [];
        while (reader.Read())
        {
            enemies.Add(new PersistedEnemyInstance(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                new Vector3(reader.GetFloat(3), reader.GetFloat(4), reader.GetFloat(5)),
                new Vector3(reader.GetFloat(6), reader.GetFloat(7), reader.GetFloat(8)),
                new PersistedCreatureStats(
                    CurrentHealth: reader.GetInt32(9),
                    MaxHealth: reader.GetInt32(10),
                    Strength: reader.GetInt32(11),
                    Agility: reader.GetInt32(12),
                    Vitality: reader.GetInt32(13),
                    Energy: reader.GetInt32(14),
                    MovementSpeed: reader.GetFloat(15),
                    AttackSpeed: reader.GetFloat(16),
                    Level: reader.GetInt32(17)),
                SqliteSchemaBootstrapper.ParseUtc(reader.GetString(18)),
                SqliteSchemaBootstrapper.ParseUtc(reader.GetString(19))));
        }

        return enemies;
    }

    public void SaveEnemyInstance(PersistedEnemyInstance enemy)
    {
        using SqliteConnectionLease lease = _schema.OpenConnection();
        SqliteConnection connection = lease.Connection;
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO enemy_instances (
                enemy_id,
                enemy_kind,
                display_name,
                position_x,
                position_y,
                position_z,
                facing_x,
                facing_y,
                facing_z,
                current_hp,
                max_hp,
                strength,
                agility,
                vitality,
                energy,
                movement_speed,
                attack_speed,
                level,
                created_utc,
                updated_utc
            )
            VALUES (
                $enemyId,
                $enemyKind,
                $displayName,
                $positionX,
                $positionY,
                $positionZ,
                $facingX,
                $facingY,
                $facingZ,
                $currentHealth,
                $maxHealth,
                $strength,
                $agility,
                $vitality,
                $energy,
                $movementSpeed,
                $attackSpeed,
                $level,
                $createdUtc,
                $updatedUtc
            )
            ON CONFLICT(enemy_id) DO UPDATE SET
                enemy_kind = excluded.enemy_kind,
                display_name = excluded.display_name,
                position_x = excluded.position_x,
                position_y = excluded.position_y,
                position_z = excluded.position_z,
                facing_x = excluded.facing_x,
                facing_y = excluded.facing_y,
                facing_z = excluded.facing_z,
                current_hp = excluded.current_hp,
                max_hp = excluded.max_hp,
                strength = excluded.strength,
                agility = excluded.agility,
                vitality = excluded.vitality,
                energy = excluded.energy,
                movement_speed = excluded.movement_speed,
                attack_speed = excluded.attack_speed,
                level = excluded.level,
                updated_utc = excluded.updated_utc;
            """;
        command.Parameters.AddWithValue("$enemyId", enemy.EnemyId);
        command.Parameters.AddWithValue("$enemyKind", enemy.EnemyKind);
        command.Parameters.AddWithValue("$displayName", enemy.DisplayName);
        command.Parameters.AddWithValue("$positionX", enemy.Position.X);
        command.Parameters.AddWithValue("$positionY", enemy.Position.Y);
        command.Parameters.AddWithValue("$positionZ", enemy.Position.Z);
        command.Parameters.AddWithValue("$facingX", enemy.Facing.X);
        command.Parameters.AddWithValue("$facingY", enemy.Facing.Y);
        command.Parameters.AddWithValue("$facingZ", enemy.Facing.Z);
        command.Parameters.AddWithValue("$currentHealth", enemy.Stats.CurrentHealth);
        command.Parameters.AddWithValue("$maxHealth", enemy.Stats.MaxHealth);
        command.Parameters.AddWithValue("$strength", enemy.Stats.Strength);
        command.Parameters.AddWithValue("$agility", enemy.Stats.Agility);
        command.Parameters.AddWithValue("$vitality", enemy.Stats.Vitality);
        command.Parameters.AddWithValue("$energy", enemy.Stats.Energy);
        command.Parameters.AddWithValue("$movementSpeed", enemy.Stats.MovementSpeed);
        command.Parameters.AddWithValue("$attackSpeed", enemy.Stats.AttackSpeed);
        command.Parameters.AddWithValue("$level", enemy.Stats.Level);
        command.Parameters.AddWithValue("$createdUtc", SqliteSchemaBootstrapper.FormatUtc(enemy.CreatedUtc == default ? DateTime.UtcNow : enemy.CreatedUtc));
        command.Parameters.AddWithValue("$updatedUtc", SqliteSchemaBootstrapper.FormatUtc(DateTime.UtcNow));
        command.ExecuteNonQuery();
    }

    public void DeleteEnemyInstance(int enemyId)
    {
        using SqliteConnectionLease lease = _schema.OpenConnection();
        SqliteConnection connection = lease.Connection;
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM enemy_instances WHERE enemy_id = $enemyId;";
        command.Parameters.AddWithValue("$enemyId", enemyId);
        command.ExecuteNonQuery();
    }

    // One UPDATE of the primary key: atomic on its own, and under the write queue's savepoint
    // when it runs in a group. A taken newEnemyId fails the primary key and changes nothing.
    public void ReassignEnemyId(int oldEnemyId, int newEnemyId)
    {
        using SqliteConnectionLease lease = _schema.OpenConnection();
        SqliteConnection connection = lease.Connection;
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE enemy_instances SET enemy_id = $newEnemyId WHERE enemy_id = $oldEnemyId;";
        command.Parameters.AddWithValue("$oldEnemyId", oldEnemyId);
        command.Parameters.AddWithValue("$newEnemyId", newEnemyId);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<PersistedItemInstance> LoadItemInstances()
    {
        using SqliteConnectionLease lease = _schema.OpenConnection();
        SqliteConnection connection = lease.Connection;
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
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
            ORDER BY item_instance_id;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        List<PersistedItemInstance> items = [];
        while (reader.Read())
        {
            Vector3? worldPosition = reader.IsDBNull(5)
                ? null
                : new Vector3(reader.GetFloat(5), reader.GetFloat(6), reader.GetFloat(7));
            items.Add(new PersistedItemInstance(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                worldPosition,
                reader.GetString(8),
                reader.GetInt32(9),
                SqliteSchemaBootstrapper.ParseUtc(reader.GetString(10)),
                SqliteSchemaBootstrapper.ParseUtc(reader.GetString(11))));
        }

        return items;
    }

    public IReadOnlyList<PersistedInventorySlot> LoadInventorySlots()
    {
        using SqliteConnectionLease lease = _schema.OpenConnection();
        SqliteConnection connection = lease.Connection;
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                player_name,
                slot_index,
                item_instance_id,
                updated_utc
            FROM player_inventory_slots
            ORDER BY player_name, slot_index;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        List<PersistedInventorySlot> slots = [];
        while (reader.Read())
        {
            slots.Add(new PersistedInventorySlot(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetInt64(2),
                SqliteSchemaBootstrapper.ParseUtc(reader.GetString(3))));
        }

        return slots;
    }

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

    public PersistedPlayerInventory LoadPlayerInventory(string playerName)
    {
        using SqliteConnectionLease lease = _schema.OpenConnection();
        SqliteConnection connection = lease.Connection;
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = LoadPlayerInventorySql;
        command.Parameters.AddWithValue("$playerName", playerName.Trim());
        using SqliteDataReader reader = command.ExecuteReader();
        List<PersistedInventorySlot> slots = [];
        List<PersistedItemInstance> items = [];
        HashSet<long> itemIds = [];
        while (reader.Read())
        {
            slots.Add(new PersistedInventorySlot(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetInt64(2),
                SqliteSchemaBootstrapper.ParseUtc(reader.GetString(3))));
            if (reader.IsDBNull(4) || !itemIds.Add(reader.GetInt64(4)))
            {
                continue;
            }

            Vector3? worldPosition = reader.IsDBNull(9)
                ? null
                : new Vector3(reader.GetFloat(9), reader.GetFloat(10), reader.GetFloat(11));
            items.Add(new PersistedItemInstance(
                reader.GetInt64(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                worldPosition,
                reader.GetString(12),
                reader.GetInt32(13),
                SqliteSchemaBootstrapper.ParseUtc(reader.GetString(14)),
                SqliteSchemaBootstrapper.ParseUtc(reader.GetString(15))));
        }

        return new PersistedPlayerInventory(slots, items);
    }

    // Both maxima read an index (the item table's primary key, the slot table's item id index).
    // Slots count too: foreign keys are not enforced on the runtime's connections.
    public long GetMaxItemInstanceId()
    {
        using SqliteConnectionLease lease = _schema.OpenConnection();
        SqliteConnection connection = lease.Connection;
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT MAX(
                COALESCE((SELECT MAX(item_instance_id) FROM item_instances), 0),
                COALESCE((SELECT MAX(item_instance_id) FROM player_inventory_slots), 0));
            """;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    public void SaveItemInstance(PersistedItemInstance item)
    {
        using SqliteConnectionLease lease = _schema.OpenConnection();
        SqliteConnection connection = lease.Connection;
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO item_instances (
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
            ON CONFLICT(item_instance_id) DO UPDATE SET
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
        command.Parameters.AddWithValue("$itemInstanceId", item.ItemInstanceId);
        command.Parameters.AddWithValue("$itemKind", item.ItemKind);
        command.Parameters.AddWithValue("$displayName", item.DisplayName);
        command.Parameters.AddWithValue("$visualId", item.VisualId);
        command.Parameters.AddWithValue("$ownerPlayerName", (object?)item.OwnerPlayerName ?? DBNull.Value);
        command.Parameters.AddWithValue("$worldPositionX", item.WorldPosition?.X ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$worldPositionY", item.WorldPosition?.Y ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$worldPositionZ", item.WorldPosition?.Z ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$state", item.State);
        command.Parameters.AddWithValue("$stackCount", item.StackCount);
        command.Parameters.AddWithValue("$createdUtc", SqliteSchemaBootstrapper.FormatUtc(item.CreatedUtc == default ? DateTime.UtcNow : item.CreatedUtc));
        command.Parameters.AddWithValue("$updatedUtc", SqliteSchemaBootstrapper.FormatUtc(DateTime.UtcNow));
        command.ExecuteNonQuery();
    }

    public void DeleteItemInstance(long itemInstanceId)
    {
        using SqliteConnectionLease lease = _schema.OpenConnection();
        SqliteConnection connection = lease.Connection;
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM item_instances WHERE item_instance_id = $itemInstanceId;";
        command.Parameters.AddWithValue("$itemInstanceId", itemInstanceId);
        command.ExecuteNonQuery();
    }

    public void SaveInventorySlot(PersistedInventorySlot slot)
    {
        using SqliteConnectionLease lease = _schema.OpenConnection();
        SqliteConnection connection = lease.Connection;
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
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
        command.Parameters.AddWithValue("$playerName", slot.PlayerName.Trim());
        command.Parameters.AddWithValue("$slotIndex", slot.SlotIndex);
        command.Parameters.AddWithValue("$itemInstanceId", slot.ItemInstanceId);
        command.Parameters.AddWithValue("$updatedUtc", SqliteSchemaBootstrapper.FormatUtc(DateTime.UtcNow));
        command.ExecuteNonQuery();
    }

    public void DeleteInventorySlots(string playerName)
    {
        using SqliteConnectionLease lease = _schema.OpenConnection();
        SqliteConnection connection = lease.Connection;
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM player_inventory_slots WHERE player_name = $playerName;";
        command.Parameters.AddWithValue("$playerName", playerName.Trim());
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
    }
}
