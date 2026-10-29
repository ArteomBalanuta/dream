using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Dream.Tests.Integration;

// Shared reads for the wave C ObjectId integration tests (wave C schema: migration 0005). Opens the
// database after the servers are disposed, so call SqliteConnection.ClearAllPools() first.
internal static class ObjectIdTestSupport
{
    // Every persisted id, by object kind, and the world's stored counter (world 1).
    internal sealed record PersistedIds(
        IReadOnlyList<long> Cubes,
        IReadOnlyList<long> Pieces,
        IReadOnlyList<long> Origins,
        IReadOnlyList<long> Enemies,
        IReadOnlyList<long> Items,
        long NextObjectId)
    {
        // Cube, piece, enemy and item ids. Origins are retired cube ids, so they are not an extra kind.
        public IReadOnlyList<long> AllObjects => [.. Cubes, .. Pieces, .. Enemies, .. Items];

        public long MaxId => Math.Max(AllObjects.DefaultIfEmpty(0L).Max(), Origins.DefaultIfEmpty(0L).Max());
    }

    internal static PersistedIds ReadIds(string databasePath)
    {
        using SqliteConnection connection = Open(databasePath);
        return new PersistedIds(
            Longs(connection, "SELECT object_id FROM placed_cube_instances WHERE world_id = 1 ORDER BY object_id;"),
            Longs(connection, "SELECT object_id FROM cube_fracture_pieces WHERE world_id = 1 ORDER BY object_id;"),
            Longs(connection, "SELECT DISTINCT origin_object_id FROM cube_fracture_pieces WHERE world_id = 1 ORDER BY origin_object_id;"),
            Longs(connection, "SELECT object_id FROM enemy_instances WHERE world_id = 1 ORDER BY object_id;"),
            Longs(connection, "SELECT item_instance_id FROM item_instances WHERE world_id = 1 ORDER BY item_instance_id;"),
            Longs(connection, "SELECT next_object_id FROM worlds WHERE world_id = 1;").Single());
    }

    internal static long Scalar(string databasePath, string sql)
    {
        using SqliteConnection connection = Open(databasePath);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    internal static SqliteConnection Open(string databasePath, bool readOnly = true)
    {
        SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    // Every persisted world and player table, minus timestamps, in a stable order, including the
    // wave C identity columns and the worlds counter.
    internal static string DumpWorld(string databasePath)
    {
        (string Table, string Columns, string Order)[] tables =
        [
            ("worlds", "world_id, next_object_id", "world_id"),
            ("player_profiles", "player_name, interaction_mode, strength, vitality, attack_speed, level, experience, current_hp, max_hp", "player_name"),
            ("player_world_state", "player_name, position_x, position_y, position_z, facing_x, facing_y, facing_z, current_hp", "player_name"),
            ("placed_cube_instances", "object_id, owner_player_name, cell_x, cell_y, cell_z, center_x, center_y, center_z, material_id, current_health, max_health", "object_id"),
            ("cube_fracture_pieces", "object_id, origin_object_id, piece_index, owner_player_name, parent_cell_x, parent_cell_y, parent_cell_z, center_x, center_y, center_z, side_length, material_id", "object_id"),
            ("enemy_instances", "object_id, enemy_kind, display_name, position_x, position_y, position_z, facing_x, facing_y, facing_z, current_hp, max_hp", "object_id"),
            ("item_instances", "item_instance_id, item_kind, owner_player_name, state, stack_count", "item_instance_id"),
            ("player_inventory_slots", "player_name, slot_index, item_instance_id", "player_name, slot_index"),
            ("terrain_cell_overrides", "object_id, cell_x, cell_y, cell_z, kind, length(cell_data)", "object_id"),
            ("terrain_chunks", "chunk_x, chunk_z, format_version, cell_count, length(data)", "chunk_z, chunk_x"),
        ];
        StringBuilder dump = new();
        using SqliteConnection connection = Open(databasePath);
        foreach ((string table, string columns, string order) in tables)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"SELECT {columns} FROM {table} ORDER BY {order};";
            using SqliteDataReader reader = command.ExecuteReader();
            int rows = 0;
            StringBuilder body = new();
            while (reader.Read())
            {
                rows++;
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    body.Append(i == 0 ? "  " : " | ");
                    body.Append(reader.IsDBNull(i) ? "null" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture));
                }

                body.AppendLine();
            }

            dump.AppendLine($"{table}: {rows} rows");
            dump.Append(body);
        }

        return dump.ToString();
    }

    private static List<long> Longs(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        using SqliteDataReader reader = command.ExecuteReader();
        List<long> values = [];
        while (reader.Read())
        {
            values.Add(reader.GetInt64(0));
        }

        return values;
    }
}
