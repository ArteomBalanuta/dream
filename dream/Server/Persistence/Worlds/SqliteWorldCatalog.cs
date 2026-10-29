using System.Globalization;
using Dream.Server.Persistence.Store;
using Dream.Server.Worlds;
using Dream.Simulation.World;
using Microsoft.Data.Sqlite;

namespace Dream.Server.Persistence.Worlds;

// The worlds table as an IWorldCatalog. Every write (Create, Copy, Delete) runs in one owned
// IMMEDIATE transaction, so the name check and the insert cannot race another process, and a failed
// copy leaves nothing behind. Copy and Delete name their columns and tables explicitly, so a column
// added to a world-scoped table later is a visible decision here, not a silent omission.
public sealed class SqliteWorldCatalog : IWorldCatalog
{
    private const string WorldColumns =
        "world_id, name, width, height, depth, map_source, generator_key, generator_version, " +
        "seed, world_time_ticks, created_schema_version, created_utc, updated_utc";

    // Children first: inventory slots reference item_instances.
    private static readonly string[] DeleteOrder =
    [
        "player_inventory_slots",
        "player_starter_grants",
        "item_instances",
        "enemy_instances",
        "cube_fracture_pieces",
        "placed_cube_instances",
        "player_world_state"
    ];

    // Parents first: items before the slots that reference them.
    private static readonly (string Table, string Columns)[] CopyTables =
    [
        ("player_world_state",
            "player_name, position_x, position_y, position_z, facing_x, facing_y, facing_z, current_hp, " +
            "current_energy, updated_utc"),
        ("placed_cube_instances",
            "cube_id, owner_player_name, cell_x, cell_y, cell_z, center_x, center_y, center_z, shape_kind, " +
            "material_id, current_health, max_health, created_utc, updated_utc"),
        ("cube_fracture_pieces",
            "source_cube_id, piece_index, owner_player_name, parent_cell_x, parent_cell_y, parent_cell_z, " +
            "center_x, center_y, center_z, side_length, material_id, created_utc, updated_utc"),
        ("enemy_instances",
            "enemy_id, enemy_kind, display_name, position_x, position_y, position_z, facing_x, facing_y, " +
            "facing_z, current_hp, max_hp, strength, agility, vitality, energy, movement_speed, attack_speed, " +
            "level, created_utc, updated_utc"),
        ("item_instances",
            "item_instance_id, item_kind, display_name, visual_id, owner_player_name, world_position_x, " +
            "world_position_y, world_position_z, state, stack_count, created_utc, updated_utc"),
        ("player_inventory_slots", "player_name, slot_index, item_instance_id, updated_utc"),
        ("player_starter_grants", "player_name, kit_version, updated_utc")
    ];

    private readonly IWorldStore _store;

    public SqliteWorldCatalog(SqliteSchemaBootstrapper schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        Schema = schema;
        _store = new SqliteWorldStore(schema);
    }

    internal SqliteSchemaBootstrapper Schema { get; }

    public IReadOnlyList<WorldRecord> List()
    {
        using SqliteSession session = _store.OpenSession();
        using SqliteCommand command = session.CreateCommand($"SELECT {WorldColumns} FROM worlds ORDER BY world_id;");
        using SqliteDataReader reader = command.ExecuteReader();
        List<WorldRecord> records = [];
        while (reader.Read())
        {
            records.Add(ReadRecord(reader));
        }

        return records;
    }

    public WorldRecord? TryGet(WorldId id)
    {
        using SqliteSession session = _store.OpenSession();
        return TryGet(session, id);
    }

    public WorldRecord? TryGetByName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        using SqliteSession session = _store.OpenSession();
        return TryGetByName(session, name.Trim());
    }

    public WorldRecord Create(WorldCreateOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        string name = (options.Name ?? string.Empty).Trim();
        RequireValidName(name);
        WorldDescriptor.ForFloorSize(options.Size, name: name).Validate();
        ulong seed = options.Seed ?? SqliteWorldMetadataRepository.GenerateSimulationSeed();

        using SqliteSession session = _store.BeginOwnedTransaction();
        RequireNameFree(session, name);
        long id = InsertWorld(
            session,
            name,
            options.Size,
            WorldDescriptor.DefaultHeight,
            options.Size,
            WorldMapSourceText.ToStorage(WorldMapSource.Generated),
            WorldGeneratorCatalog.DefaultKey,
            WorldGeneratorCatalog.DefaultVersion,
            seed.ToString(CultureInfo.InvariantCulture),
            0L);
        WorldRecord created = TryGet(session, new WorldId(id))!;
        session.Commit();
        return created;
    }

    public WorldRecord Copy(WorldId source, string newName)
    {
        ArgumentNullException.ThrowIfNull(newName);
        string name = newName.Trim();
        RequireValidName(name);

        using SqliteSession session = _store.BeginOwnedTransaction();
        WorldRecord sourceRecord = TryGet(session, source) ?? throw new WorldNotFoundException(source.ToString());
        RequireNameFree(session, name);

        // A source without a stored seed gets one now, in this transaction, so the copy keeps the
        // same seed instead of each of them generating its own later.
        string? seedText = sourceRecord.Seed?.ToString(CultureInfo.InvariantCulture);
        if (seedText == null)
        {
            seedText = SqliteWorldMetadataRepository.GenerateSimulationSeed().ToString(CultureInfo.InvariantCulture);
            using SqliteCommand store = session.CreateCommand(
                "UPDATE worlds SET seed = $seed WHERE world_id = $id AND seed IS NULL;");
            store.Parameters.AddWithValue("$seed", seedText);
            store.Parameters.AddWithValue("$id", source.Value);
            store.ExecuteNonQuery();
        }

        WorldDescriptor d = sourceRecord.Descriptor;
        long id = InsertWorld(
            session,
            name,
            d.Width,
            d.Height,
            d.Depth,
            WorldMapSourceText.ToStorage(d.MapSource),
            d.GeneratorKey,
            d.GeneratorVersion,
            seedText,
            sourceRecord.WorldTimeTicks);

        foreach ((string table, string columns) in CopyTables)
        {
            using SqliteCommand copy = session.CreateCommand(
                $"INSERT INTO {table} (world_id, {columns}) SELECT $newId, {columns} FROM {table} WHERE world_id = $sourceId;");
            copy.Parameters.AddWithValue("$newId", id);
            copy.Parameters.AddWithValue("$sourceId", source.Value);
            copy.ExecuteNonQuery();
        }

        WorldRecord created = TryGet(session, new WorldId(id))!;
        session.Commit();
        return created;
    }

    public bool Delete(WorldId id)
    {
        using SqliteSession session = _store.BeginOwnedTransaction();
        foreach (string table in DeleteOrder)
        {
            using SqliteCommand delete = session.CreateCommand($"DELETE FROM {table} WHERE world_id = $id;");
            delete.Parameters.AddWithValue("$id", id.Value);
            delete.ExecuteNonQuery();
        }

        int removed;
        using (SqliteCommand world = session.CreateCommand("DELETE FROM worlds WHERE world_id = $id;"))
        {
            world.Parameters.AddWithValue("$id", id.Value);
            removed = world.ExecuteNonQuery();
        }

        session.Commit();
        return removed > 0;
    }

    public WorldRecord Resolve(WorldSelector selector, int newWorldSize, out bool created)
    {
        created = false;
        if (selector.Id is WorldId id)
        {
            return TryGet(id) ?? throw new WorldNotFoundException(id.ToString());
        }

        if (selector.Name is string name)
        {
            WorldRecord? existing = TryGetByName(name);
            if (existing != null)
            {
                return existing;
            }

            WorldRecord made = Create(new WorldCreateOptions(name, newWorldSize));
            created = true;
            return made;
        }

        WorldRecord? world = TryGet(WorldId.Default) ?? List().FirstOrDefault();
        if (world != null)
        {
            return world;
        }

        WorldRecord first = Create(new WorldCreateOptions(WorldDescriptor.DefaultName, newWorldSize));
        created = true;
        return first;
    }

    private static void RequireValidName(string trimmed)
    {
        if (trimmed.Length < 1 || trimmed.Length > WorldDescriptor.MaxNameLength)
        {
            throw new ArgumentException(
                $"World name must be 1-{WorldDescriptor.MaxNameLength} characters after trimming.", "name");
        }

        // An all-digit name would be read as a world id by WorldSelector.Parse and never found by name.
        if (trimmed.All(char.IsAsciiDigit))
        {
            throw new ArgumentException(
                $"World name '{trimmed}' cannot be only digits: it would be read as a world id.", "name");
        }

        if (trimmed.Any(char.IsControl))
        {
            throw new ArgumentException("World name cannot contain control characters.", "name");
        }
    }

    private static void RequireNameFree(SqliteSession session, string trimmedName)
    {
        if (TryGetByName(session, trimmedName) != null)
        {
            throw new WorldNameTakenException(trimmedName);
        }
    }

    private static long InsertWorld(
        SqliteSession session,
        string name,
        int width,
        int height,
        int depth,
        string mapSource,
        string generatorKey,
        int generatorVersion,
        string? seed,
        long worldTimeTicks)
    {
        using (SqliteCommand insert = session.CreateCommand(
            "INSERT INTO worlds (name, width, height, depth, map_source, generator_key, generator_version, " +
            "seed, world_time_ticks, created_schema_version) " +
            "VALUES ($name, $width, $height, $depth, $mapSource, $generatorKey, $generatorVersion, " +
            "$seed, $ticks, (SELECT COALESCE(MAX(version), 1) FROM schema_version));"))
        {
            insert.Parameters.AddWithValue("$name", name);
            insert.Parameters.AddWithValue("$width", width);
            insert.Parameters.AddWithValue("$height", height);
            insert.Parameters.AddWithValue("$depth", depth);
            insert.Parameters.AddWithValue("$mapSource", mapSource);
            insert.Parameters.AddWithValue("$generatorKey", generatorKey);
            insert.Parameters.AddWithValue("$generatorVersion", generatorVersion);
            insert.Parameters.AddWithValue("$seed", (object?)seed ?? DBNull.Value);
            insert.Parameters.AddWithValue("$ticks", worldTimeTicks);
            try
            {
                insert.ExecuteNonQuery();
            }
            catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == 2067) // SQLITE_CONSTRAINT_UNIQUE
            {
                throw new WorldNameTakenException(name);
            }
        }

        using SqliteCommand rowId = session.CreateCommand("SELECT last_insert_rowid();");
        return Convert.ToInt64(rowId.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static WorldRecord? TryGet(SqliteSession session, WorldId id)
    {
        using SqliteCommand command = session.CreateCommand(
            $"SELECT {WorldColumns} FROM worlds WHERE world_id = $id;");
        command.Parameters.AddWithValue("$id", id.Value);
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? ReadRecord(reader) : null;
    }

    private static WorldRecord? TryGetByName(SqliteSession session, string trimmedName)
    {
        using SqliteCommand command = session.CreateCommand(
            $"SELECT {WorldColumns} FROM worlds WHERE name = $name COLLATE NOCASE;");
        command.Parameters.AddWithValue("$name", trimmedName);
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? ReadRecord(reader) : null;
    }

    private static WorldRecord ReadRecord(SqliteDataReader reader)
    {
        long id = reader.GetInt64(0);
        WorldDescriptor descriptor = new(
            new WorldId(id),
            reader.GetString(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            WorldMapSourceText.Parse(reader.GetString(5)),
            reader.GetString(6),
            reader.GetInt32(7));

        ulong? seed = null;
        if (!reader.IsDBNull(8))
        {
            string text = reader.GetString(8);
            if (!ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out ulong parsed))
            {
                throw new InvalidDataException($"World {id} has an invalid seed.");
            }

            seed = parsed;
        }

        return new WorldRecord(
            descriptor,
            seed,
            ReadWorldTimeTicks(reader, id),
            reader.GetInt32(10),
            ParseUtc(reader.GetString(11)),
            ParseUtc(reader.GetString(12)));
    }

    // GetInt64 would turn a TEXT value into 0 and silently restart the world clock.
    private static long ReadWorldTimeTicks(SqliteDataReader reader, long id)
    {
        if (reader.IsDBNull(9) || reader.GetFieldType(9) != typeof(long) || reader.GetInt64(9) < 0)
        {
            throw new InvalidDataException($"World {id} has an invalid world_time_ticks value.");
        }

        return reader.GetInt64(9);
    }

    private static DateTime ParseUtc(string text) =>
        DateTime.Parse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
}
