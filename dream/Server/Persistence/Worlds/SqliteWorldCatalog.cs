using System.Globalization;
using Dream.Server.Persistence.Repositories;
using Dream.Server.Persistence.Store;
using Dream.Server.Worlds;
using Dream.Simulation.Persistence;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Simulation.World.MapSources;
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
        "terrain_chunks",
        "terrain_cell_overrides",
        "player_inventory_slots",
        "player_starter_grants",
        "player_wallets",
        "player_quickbar_slots",
        "player_quests",
        "character_presence",
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
            "object_id, owner_player_name, cell_x, cell_y, cell_z, center_x, center_y, center_z, shape_kind, " +
            "material_id, current_health, max_health, created_utc, updated_utc"),
        ("cube_fracture_pieces",
            "object_id, origin_object_id, piece_index, owner_player_name, parent_cell_x, parent_cell_y, parent_cell_z, " +
            "center_x, center_y, center_z, side_length, material_id, created_utc, updated_utc"),
        ("enemy_instances",
            "object_id, enemy_kind, display_name, position_x, position_y, position_z, facing_x, facing_y, " +
            "facing_z, current_hp, max_hp, strength, agility, vitality, energy, movement_speed, attack_speed, " +
            "level, created_utc, updated_utc, camp_key"),
        ("item_instances",
            "item_instance_id, item_kind, display_name, visual_id, owner_player_name, world_position_x, " +
            "world_position_y, world_position_z, state, stack_count, created_utc, updated_utc, rolled_modifiers"),
        ("terrain_cell_overrides",
            "object_id, cell_x, cell_y, cell_z, kind, cell_data, created_utc, updated_utc"),
        ("terrain_chunks", "chunk_x, chunk_z, format_version, cell_count, data, updated_utc"),
        ("player_inventory_slots", "player_name, slot_index, item_instance_id, updated_utc"),
        ("player_starter_grants", "player_name, kit_version, updated_utc"),
        ("player_wallets", "player_name, gold, updated_utc"),
        ("player_quickbar_slots", "player_name, slot, kind, ref_key, updated_utc"),
        ("player_quests", "player_name, quest_key, state, progress, updated_utc")
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
        // Validate rejects an unregistered generator key before a row is written.
        WorldDescriptor descriptor = WorldDescriptor.ForFloorSize(
            options.Size,
            name: name,
            generatorKey: options.GeneratorKey);
        descriptor.Validate();
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
            descriptor.GeneratorKey,
            descriptor.GeneratorVersion,
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
            CopyTable(session, table, columns, source.Value, id);
        }

        RaiseCounter(session, source.Value, id);

        WorldRecord created = TryGet(session, new WorldId(id))!;
        session.Commit();
        return created;
    }

    public WorldTerrainSource ReadTerrainSource(WorldId id)
    {
        WorldRecord record;
        using (SqliteSession session = _store.BeginOwnedTransaction())
        {
            record = TryGet(session, id) ?? throw new WorldNotFoundException(id.ToString());
            if (record.Seed == null)
            {
                string generated = SqliteWorldMetadataRepository.GenerateSimulationSeed().ToString(CultureInfo.InvariantCulture);
                using SqliteCommand store = session.CreateCommand(
                    "UPDATE worlds SET seed = $seed WHERE world_id = $id AND seed IS NULL;");
                store.Parameters.AddWithValue("$seed", generated);
                store.Parameters.AddWithValue("$id", id.Value);
                store.ExecuteNonQuery();
                record = TryGet(session, id)!;
            }

            session.Commit();
        }

        IReadOnlyList<PersistedTerrainChunk>? chunks = null;
        if (record.Descriptor.MapSource == WorldMapSource.Stored)
        {
            chunks = new SqliteTerrainChunkRepository(_store, id).LoadTerrainChunks();
        }

        IReadOnlyList<PersistedTerrainCellOverride> overrides =
            new SqliteTerrainOverrideRepository(_store, id).LoadTerrainCellOverrides();
        return new WorldTerrainSource(record, record.Seed!.Value, chunks, overrides);
    }

    public WorldRecord CreateStored(
        WorldId source,
        string newName,
        IReadOnlyList<PersistedTerrainChunk> chunks,
        bool terrainOnly)
    {
        ArgumentNullException.ThrowIfNull(newName);
        ArgumentNullException.ThrowIfNull(chunks);
        string name = newName.Trim();
        RequireValidName(name);

        using SqliteSession session = _store.BeginOwnedTransaction();
        WorldRecord sourceRecord = TryGet(session, source) ?? throw new WorldNotFoundException(source.ToString());
        RequireNameFree(session, name);

        string seedText = (terrainOnly ? null : sourceRecord.Seed?.ToString(CultureInfo.InvariantCulture))
            ?? SqliteWorldMetadataRepository.GenerateSimulationSeed().ToString(CultureInfo.InvariantCulture);
        WorldDescriptor d = sourceRecord.Descriptor;
        long id = InsertWorld(
            session,
            name,
            d.Width,
            d.Height,
            d.Depth,
            WorldMapSourceText.ToStorage(WorldMapSource.Stored),
            d.GeneratorKey,
            d.GeneratorVersion,
            seedText,
            terrainOnly ? 0L : sourceRecord.WorldTimeTicks);

        if (!terrainOnly)
        {
            foreach ((string table, string columns) in CopyTables)
            {
                // The removals are baked into the chunks; the chunks are the new ones.
                if (table is "terrain_cell_overrides" or "terrain_chunks")
                {
                    continue;
                }

                CopyTable(session, table, columns, source.Value, id);
            }

            RaiseCounter(session, source.Value, id);
        }

        using (SqliteCommand insert = session.CreateCommand(
            "INSERT INTO terrain_chunks (world_id, chunk_x, chunk_z, format_version, cell_count, data, updated_utc) " +
            "VALUES ($worldId, $chunkX, $chunkZ, $formatVersion, $cellCount, $data, $updatedUtc);"))
        {
            insert.Parameters.AddWithValue("$worldId", id);
            insert.Parameters.AddWithValue("$updatedUtc", SqliteSchemaBootstrapper.FormatUtc(DateTime.UtcNow));
            SqliteParameter chunkX = insert.Parameters.Add("$chunkX", SqliteType.Integer);
            SqliteParameter chunkZ = insert.Parameters.Add("$chunkZ", SqliteType.Integer);
            SqliteParameter version = insert.Parameters.Add("$formatVersion", SqliteType.Integer);
            SqliteParameter cells = insert.Parameters.Add("$cellCount", SqliteType.Integer);
            SqliteParameter data = insert.Parameters.Add("$data", SqliteType.Blob);
            foreach (PersistedTerrainChunk chunk in chunks)
            {
                chunkX.Value = chunk.ChunkX;
                chunkZ.Value = chunk.ChunkZ;
                version.Value = chunk.FormatVersion;
                cells.Value = chunk.CellCount;
                data.Value = chunk.Data;
                insert.ExecuteNonQuery();
            }
        }

        WorldRecord created = TryGet(session, new WorldId(id))!;
        session.Commit();
        return created;
    }

    public TerrainResetResult ResetTerrain(WorldId id)
    {
        // The cells (Y > 0) the world's base terrain, generated or stored, creates: read before the write
        // transaction, from the same map source the server hosts.
        WorldTerrainSource terrain = ReadTerrainSource(id);
        HashSet<(int, int, int)> sourceCells = [];
        foreach (GeometryCellRecord record in MapSources.ForWorld(terrain.Record.Descriptor, terrain.Seed, terrain.StoredChunks).CreateCells())
        {
            if (record.Key.Y > 0)
            {
                sourceCells.Add((record.Key.X, record.Key.Y, record.Key.Z));
            }
        }

        using SqliteSession session = _store.BeginOwnedTransaction();
        _ = TryGet(session, id) ?? throw new WorldNotFoundException(id.ToString());

        // The cells about to be restored: override cells with no placed cube. Read first, for the report of
        // players and enemies saved inside them (they are not moved).
        HashSet<(int, int, int)> restoredCells = [];
        using (SqliteCommand command = session.CreateCommand(
            "SELECT o.cell_x, o.cell_y, o.cell_z FROM terrain_cell_overrides AS o WHERE o.world_id = $id AND NOT EXISTS (" +
            "SELECT 1 FROM placed_cube_instances AS p WHERE p.world_id = $id " +
            "AND p.cell_x = o.cell_x AND p.cell_y = o.cell_y AND p.cell_z = o.cell_z);"))
        {
            command.Parameters.AddWithValue("$id", id.Value);
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                restoredCells.Add((reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2)));
            }
        }

        List<string> playersInside = [];
        int enemiesInside = 0;
        if (restoredCells.Count > 0)
        {
            using (SqliteCommand command = session.CreateCommand(
                "SELECT player_name, position_x, position_y, position_z FROM player_world_state " +
                "WHERE world_id = $id ORDER BY player_name;"))
            {
                command.Parameters.AddWithValue("$id", id.Value);
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    if (restoredCells.Contains(CellOf(reader.GetDouble(1), reader.GetDouble(2), reader.GetDouble(3))))
                    {
                        playersInside.Add(reader.GetString(0));
                    }
                }
            }

            using (SqliteCommand command = session.CreateCommand(
                "SELECT position_x, position_y, position_z FROM enemy_instances WHERE world_id = $id;"))
            {
                command.Parameters.AddWithValue("$id", id.Value);
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    if (restoredCells.Contains(CellOf(reader.GetDouble(0), reader.GetDouble(1), reader.GetDouble(2))))
                    {
                        enemiesInside++;
                    }
                }
            }
        }

        // Rubble first (it is matched through the override rows), then the overrides.
        int rubble;
        using (SqliteCommand command = session.CreateCommand(
            "DELETE FROM cube_fracture_pieces WHERE world_id = $id AND EXISTS (" +
            "SELECT 1 FROM terrain_cell_overrides AS o WHERE o.world_id = $id " +
            "AND o.cell_x = cube_fracture_pieces.parent_cell_x AND o.cell_y = cube_fracture_pieces.parent_cell_y " +
            "AND o.cell_z = cube_fracture_pieces.parent_cell_z AND NOT EXISTS (" +
            "SELECT 1 FROM placed_cube_instances AS p WHERE p.world_id = $id " +
            "AND p.cell_x = o.cell_x AND p.cell_y = o.cell_y AND p.cell_z = o.cell_z));"))
        {
            command.Parameters.AddWithValue("$id", id.Value);
            rubble = command.ExecuteNonQuery();
        }

        // Lost-override rubble: rubble on a SOURCE cell of the world's base terrain whose override row is
        // gone (so it is not matched above) and whose origin is neither an override nor a live placed cube.
        // After the reset the cell stands, and the load-time heal would remove it again to match that
        // rubble. Rubble in cells that are not source cells (debris of a destroyed player cube in the air,
        // a baked export's copied rubble in baked-out cells) is ordinary debris and stays.
        const string LostOverrideRubble =
            "world_id = $id " +
            "AND NOT EXISTS (SELECT 1 FROM terrain_cell_overrides AS o WHERE o.world_id = $id " +
            "AND o.object_id = cube_fracture_pieces.origin_object_id) " +
            "AND NOT EXISTS (SELECT 1 FROM placed_cube_instances AS p WHERE p.world_id = $id " +
            "AND p.object_id = cube_fracture_pieces.origin_object_id) " +
            "AND NOT EXISTS (SELECT 1 FROM placed_cube_instances AS p WHERE p.world_id = $id " +
            "AND p.cell_x = cube_fracture_pieces.parent_cell_x AND p.cell_y = cube_fracture_pieces.parent_cell_y " +
            "AND p.cell_z = cube_fracture_pieces.parent_cell_z)";
        List<(int, int, int)> lostRubbleCells = [];
        using (SqliteCommand command = session.CreateCommand(
            "SELECT DISTINCT parent_cell_x, parent_cell_y, parent_cell_z FROM cube_fracture_pieces WHERE " + LostOverrideRubble + ";"))
        {
            command.Parameters.AddWithValue("$id", id.Value);
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                (int, int, int) cell = (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
                if (sourceCells.Contains(cell))
                {
                    lostRubbleCells.Add(cell);
                }
            }
        }

        foreach ((int x, int y, int z) in lostRubbleCells)
        {
            using SqliteCommand command = session.CreateCommand(
                "DELETE FROM cube_fracture_pieces WHERE " + LostOverrideRubble +
                " AND parent_cell_x = $x AND parent_cell_y = $y AND parent_cell_z = $z;");
            command.Parameters.AddWithValue("$id", id.Value);
            command.Parameters.AddWithValue("$x", x);
            command.Parameters.AddWithValue("$y", y);
            command.Parameters.AddWithValue("$z", z);
            rubble += command.ExecuteNonQuery();
        }

        int cleared;
        using (SqliteCommand command = session.CreateCommand(
            "DELETE FROM terrain_cell_overrides WHERE world_id = $id AND NOT EXISTS (" +
            "SELECT 1 FROM placed_cube_instances AS p WHERE p.world_id = $id " +
            "AND p.cell_x = terrain_cell_overrides.cell_x AND p.cell_y = terrain_cell_overrides.cell_y " +
            "AND p.cell_z = terrain_cell_overrides.cell_z);"))
        {
            command.Parameters.AddWithValue("$id", id.Value);
            cleared = command.ExecuteNonQuery();
        }

        int kept;
        using (SqliteCommand command = session.CreateCommand(
            "SELECT COUNT(*) FROM terrain_cell_overrides WHERE world_id = $id;"))
        {
            command.Parameters.AddWithValue("$id", id.Value);
            kept = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        session.Commit();
        return new TerrainResetResult(
            cleared,
            rubble,
            kept,
            playersInside.Count,
            enemiesInside,
            playersInside.Count > 0 ? playersInside : null);
    }

    private static (int, int, int) CellOf(double x, double y, double z) =>
        ((int)Math.Floor(x), (int)Math.Floor(y), (int)Math.Floor(z));

    private static void CopyTable(SqliteSession session, string table, string columns, long sourceId, long newId)
    {
        using SqliteCommand copy = session.CreateCommand(
            $"INSERT INTO {table} (world_id, {columns}) SELECT $newId, {columns} FROM {table} WHERE world_id = $sourceId;");
        copy.Parameters.AddWithValue("$newId", newId);
        copy.Parameters.AddWithValue("$sourceId", sourceId);
        copy.ExecuteNonQuery();
    }

    // The copy keeps the counter, so an id the source retired is not reissued in the copy. The
    // insert triggers already raised it past every copied row; this only ever raises it further.
    private static void RaiseCounter(SqliteSession session, long sourceId, long newId)
    {
        using SqliteCommand counter = session.CreateCommand(
            "UPDATE worlds SET next_object_id = MAX(next_object_id, " +
            "(SELECT next_object_id FROM worlds WHERE world_id = $sourceId)) WHERE world_id = $newId;");
        counter.Parameters.AddWithValue("$newId", newId);
        counter.Parameters.AddWithValue("$sourceId", sourceId);
        counter.ExecuteNonQuery();
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
            return ResizeIfPristineDefault(TryGet(id) ?? throw new WorldNotFoundException(id.ToString()), newWorldSize);
        }

        if (selector.Name is string name)
        {
            WorldRecord? existing = TryGetByName(name);
            if (existing != null)
            {
                return ResizeIfPristineDefault(existing, newWorldSize);
            }

            WorldRecord made = Create(new WorldCreateOptions(name, newWorldSize));
            created = true;
            return made;
        }

        WorldRecord? world = TryGet(WorldId.Default) ?? List().FirstOrDefault();
        if (world != null)
        {
            return ResizeIfPristineDefault(world, newWorldSize);
        }

        WorldRecord first = Create(new WorldCreateOptions(WorldDescriptor.DefaultName, newWorldSize));
        created = true;
        return first;
    }

    // Per-world tables: a world with a row in any of them has been played in (or authored) and keeps its size.
    private static readonly string[] WorldDataTables =
    [
        "player_world_state", "placed_cube_instances", "cube_fracture_pieces", "enemy_instances", "item_instances",
        "player_inventory_slots", "player_starter_grants", "terrain_cell_overrides", "terrain_chunks",
        "player_wallets", "player_quickbar_slots", "player_quests", "character_presence",
    ];

    // Migration 0003 always seeds world 1 at 56 x 8 x 56, so a fresh database's first server start would
    // never get the size it asked for. A pristine world 1 (generated, no seed, no world time, no data in any
    // per-world table) is resized to that size; a world with anything in it is never touched.
    private WorldRecord ResizeIfPristineDefault(WorldRecord record, int newWorldSize)
    {
        WorldDescriptor d = record.Descriptor;
        if (d.Id != WorldId.Default || d.MapSource != WorldMapSource.Generated || record.Seed != null ||
            record.WorldTimeTicks != 0 || (d.Width == newWorldSize && d.Depth == newWorldSize))
        {
            return record;
        }

        WorldDescriptor.ForFloorSize(newWorldSize, name: d.Name).Validate();
        using SqliteSession session = _store.BeginOwnedTransaction();
        foreach (string table in WorldDataTables)
        {
            using SqliteCommand count = session.CreateCommand($"SELECT EXISTS (SELECT 1 FROM {table} WHERE world_id = $id);");
            count.Parameters.AddWithValue("$id", WorldId.Default.Value);
            if (Convert.ToInt64(count.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
            {
                return record;
            }
        }

        using (SqliteCommand update = session.CreateCommand(
            "UPDATE worlds SET width = $size, depth = $size, updated_utc = $now " +
            "WHERE world_id = $id AND map_source = 'generated' AND seed IS NULL AND world_time_ticks = 0;"))
        {
            update.Parameters.AddWithValue("$size", newWorldSize);
            update.Parameters.AddWithValue("$id", WorldId.Default.Value);
            update.Parameters.AddWithValue("$now", SqliteSchemaBootstrapper.FormatUtc(DateTime.UtcNow));
            update.ExecuteNonQuery();
        }

        WorldRecord resized = TryGet(session, WorldId.Default) ?? record;
        session.Commit();
        return resized;
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
