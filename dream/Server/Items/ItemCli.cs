using System.Globalization;
using System.Text;
using Dream.Server.Characters;
using Dream.Server.Hosting;
using Dream.Server.Persistence;
using Dream.Server.Persistence.Migrations;
using Dream.Server.Persistence.Repositories;
using Dream.Server.Persistence.Store;
using Dream.Server.Persistence.Worlds;
using Dream.Server.Worlds;
using Dream.Simulation.Content;
using Dream.Simulation.Determinism;
using Dream.Simulation.Inventory;
using Dream.Simulation.Persistence;
using Dream.Simulation.Stats;
using Dream.Simulation.World;
using Microsoft.Data.Sqlite;

namespace Dream.Server.Items;

// The `--items` command line:
//   grant <world> <character> <item kind> [--rarity common|uncommon|rare]
//   list <world> <character>
// Output is tab-separated. `grant` is offline: it takes the world's lease (as `--worlds` does) and
// runs as one owned transaction. It needs a starter-grant row in that world; without one the next join
// would treat the inventory as a pre-kit legacy one. `list` takes no lease. A character matches by
// display name (ignoring ASCII case), then by exact key.
public static class ItemCli
{
    public const int ExitOk = 0;
    public const int ExitUsage = 1;
    public const int ExitNotFound = 2;
    public const int ExitSchemaTooNew = 3;
    public const int ExitWorldHeld = 7;
    public const int ExitContentInvalid = 8;

    public const string Usage =
        "Usage: --items grant <world> <character> <item kind> [--rarity common|uncommon|rare] | list <world> <character>";

    private const string FindByDisplayNameSql = "SELECT player_name FROM player_profiles WHERE display_name = $name COLLATE NOCASE;";

    private const string FindByKeySql = "SELECT player_name FROM player_profiles WHERE player_name = $name;";

    private const string GrantRowSql = "SELECT COUNT(*) FROM player_starter_grants WHERE world_id = $world AND player_name = $key;";

    private const string BackpackSlotsSql = """
        SELECT slot_index FROM player_inventory_slots
        WHERE world_id = $world AND player_name = $key AND slot_index >= 0 AND slot_index < $count;
        """;

    private const string WorldSql = "SELECT seed, world_time_ticks FROM worlds WHERE world_id = $world;";

    // The trigger on item_instances raises worlds.next_object_id past the new id.
    private const string InsertItemSql = """
        INSERT INTO item_instances (
            world_id, item_instance_id, item_kind, display_name, visual_id, owner_player_name,
            world_position_x, world_position_y, world_position_z, state, stack_count,
            created_utc, updated_utc, rolled_modifiers)
        VALUES (
            $world, $id, $kind, $displayName, $visualId, $key,
            NULL, NULL, NULL, 'Inventory', 1,
            $now, $now, $roll);
        """;

    private const string InsertSlotSql = """
        INSERT INTO player_inventory_slots (world_id, player_name, slot_index, item_instance_id, updated_utc)
        VALUES ($world, $key, $slot, $id, $now);
        """;

    // Runs against the default database (migrating it first). args are the arguments after --items.
    public static int Run(string[] args)
    {
        DatabaseMaintenanceLock? maintenance = null;
        try
        {
            return Run(
                args,
                () =>
                {
                    SqliteSchemaBootstrapper schema = new(ServerBootstrap.DefaultDatabasePath, ServerBootstrap.DefaultSchemaScriptPath);
                    maintenance = DatabaseMaintenanceLock.EnsureSchemaGuarded(schema, ServerBootstrap.DefaultDatabasePath, log: null);
                    return schema;
                },
                Console.Out,
                Console.Error,
                worldId => WorldHostLease.TryAcquire(ServerBootstrap.DefaultDatabasePath, worldId));
        }
        finally
        {
            maintenance?.Dispose();
        }
    }

    // acquireLease returns the world's lease, or null when another holder has it; null (the default)
    // takes none (a test seam). readContent defaults to the shipped content.
    public static int Run(
        string[] args,
        Func<SqliteSchemaBootstrapper> openSchema,
        TextWriter output,
        TextWriter error,
        Func<WorldId, IDisposable?>? acquireLease = null,
        Func<ContentCatalog>? readContent = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(openSchema);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        string? usageError = Parse(args, out string verb, out string[] positional, out ItemRarity rarity);
        if (usageError != null)
        {
            error.WriteLine(usageError);
            error.WriteLine(Usage);
            return ExitUsage;
        }

        try
        {
            SqliteSchemaBootstrapper schema = openSchema();
            SqliteWorldCatalog catalog = new(schema);
            WorldSelector selector;
            try
            {
                selector = WorldSelector.Parse(positional[0]);
            }
            catch (ArgumentException ex)
            {
                error.WriteLine(ex.Message);
                error.WriteLine(Usage);
                return ExitUsage;
            }

            WorldRecord? world = selector.Id is WorldId id ? catalog.TryGet(id) : catalog.TryGetByName(selector.Name!);
            if (world == null)
            {
                error.WriteLine($"World '{CharacterCli.Sanitize(positional[0])}' does not exist.");
                return ExitNotFound;
            }

            string? key = FindCharacterKey(schema, positional[1].Trim());
            if (key == null)
            {
                error.WriteLine($"No character matches '{CharacterCli.Sanitize(positional[1])}'.");
                return ExitNotFound;
            }

            if (verb == "list")
            {
                return List(schema, world.Descriptor.Id, key, output);
            }

            ContentCatalog content = (readContent ?? (static () => ContentCatalog.Shipped))();
            using IDisposable? lease = acquireLease?.Invoke(world.Descriptor.Id);
            if (acquireLease != null && lease == null)
            {
                error.WriteLine(
                    $"World {world.Descriptor.Id} is in use by a running server or another command; " +
                    "stop it first. Nothing was changed.");
                return ExitWorldHeld;
            }

            return Grant(schema, world, key, positional[2], rarity, content, output, error);
        }
        catch (ContentValidationException ex)
        {
            error.WriteLine($"The game content is invalid: {ex.Message}");
            return ExitContentInvalid;
        }
        catch (SchemaTooNewException ex)
        {
            error.WriteLine(
                $"The database schema is at version {ex.StoredVersion}, newer than the version " +
                $"{ex.LatestVersion} this build knows. Run a newer build.");
            return ExitSchemaTooNew;
        }
        catch (WorldHeldException ex)
        {
            error.WriteLine($"{ex.Message} Stop the server hosting it first.");
            return ExitWorldHeld;
        }
        catch (DatabaseBusyException ex)
        {
            error.WriteLine($"{ex.Message} Try again when it has finished.");
            return ExitWorldHeld;
        }
        catch (LockFileUnavailableException ex)
        {
            error.WriteLine($"Cannot create the lock file {ex.LockPath}: {ex.InnerException?.Message ?? ex.Message}");
            return ExitNotFound;
        }
        catch (SqliteException ex)
        {
            string advice = ex.SqliteErrorCode is 11 or 26 ? " If the file is damaged, restore a backup (--db restore <path> --yes)." : string.Empty;
            error.WriteLine($"The database could not be used: {ex.Message}.{advice}");
            return ExitNotFound;
        }
    }

    private static string? Parse(string[] args, out string verb, out string[] positional, out ItemRarity rarity)
    {
        verb = string.Empty;
        positional = [];
        rarity = ItemRarity.Common;
        if (args.Length == 0)
        {
            return "Missing --items command.";
        }

        verb = args[0].ToLowerInvariant();
        List<string> words = [];
        bool rarityGiven = false;
        for (int i = 1; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--rarity", StringComparison.OrdinalIgnoreCase))
            {
                if (verb != "grant" || rarityGiven || i + 1 >= args.Length)
                {
                    return "'--rarity' belongs to 'grant', is given once and needs a value.";
                }

                string text = args[++i];
                if (text.Equals("common", StringComparison.OrdinalIgnoreCase))
                {
                    rarity = ItemRarity.Common;
                }
                else if (text.Equals("uncommon", StringComparison.OrdinalIgnoreCase))
                {
                    rarity = ItemRarity.Uncommon;
                }
                else if (text.Equals("rare", StringComparison.OrdinalIgnoreCase))
                {
                    rarity = ItemRarity.Rare;
                }
                else
                {
                    return $"Unknown rarity '{CharacterCli.Sanitize(text)}'.";
                }

                rarityGiven = true;
                continue;
            }

            words.Add(args[i]);
        }

        positional = [.. words];
        switch (verb)
        {
            case "grant":
                return positional.Length == 3 && positional.All(static w => !string.IsNullOrWhiteSpace(w))
                    ? null
                    : "'grant' needs a world, a character and an item kind.";
            case "list":
                return positional.Length == 2 && positional.All(static w => !string.IsNullOrWhiteSpace(w))
                    ? null
                    : "'list' needs a world and a character.";
            default:
                return $"Unknown --items command '{CharacterCli.Sanitize(args[0])}'.";
        }
    }

    private static string? FindCharacterKey(SqliteSchemaBootstrapper schema, string name)
    {
        using SqliteConnection connection = schema.CreateOpenConnection();
        foreach (string sql in new[] { FindByDisplayNameSql, FindByKeySql })
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$name", name);
            if (command.ExecuteScalar() is string key)
            {
                return key;
            }
        }

        return null;
    }

    private static int Grant(
        SqliteSchemaBootstrapper schema,
        WorldRecord world,
        string key,
        string kind,
        ItemRarity rarity,
        ContentCatalog content,
        TextWriter output,
        TextWriter error)
    {
        long worldId = world.Descriptor.Id.Value;
        string safeKind = CharacterCli.Sanitize(kind);
        if (!content.TryGetItem(kind, out ItemDefinition? definition) || definition.Rolls == null)
        {
            error.WriteLine($"Item kind '{safeKind}' does not exist or cannot be rolled.");
            return ExitNotFound;
        }

        // The counter is healed against the rows in use first, so the new id is above every id taken.
        SqliteWorldStore store = new(schema);
        long itemId = new SqliteObjectIdCounterRepository(store, world.Descriptor.Id).LoadNextObjectId();

        using SqliteConnection connection = schema.CreateOpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        long grants;
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = GrantRowSql;
            command.Parameters.AddWithValue("$world", worldId);
            command.Parameters.AddWithValue("$key", key);
            grants = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        if (grants == 0)
        {
            error.WriteLine(
                $"'{CharacterCli.Sanitize(key)}' has no starter-grant row in world {worldId}; " +
                "the character must join that world once first. Nothing was changed.");
            return ExitNotFound;
        }

        bool[] used = new bool[InventoryConstants.BackpackSlotCount];
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = BackpackSlotsSql;
            command.Parameters.AddWithValue("$world", worldId);
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$count", InventoryConstants.BackpackSlotCount);
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                used[reader.GetInt32(0)] = true;
            }
        }

        int slot = Array.IndexOf(used, false);
        if (slot < 0)
        {
            error.WriteLine($"The backpack of '{CharacterCli.Sanitize(key)}' is full. Nothing was changed.");
            return ExitNotFound;
        }

        ulong seed = 0;
        ulong ticks = 0;
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = WorldSql;
            command.Parameters.AddWithValue("$world", worldId);
            using SqliteDataReader reader = command.ExecuteReader();
            if (reader.Read())
            {
                if (!reader.IsDBNull(0) && ulong.TryParse(reader.GetString(0), NumberStyles.None, CultureInfo.InvariantCulture, out ulong parsed))
                {
                    seed = parsed;
                }

                long storedTicks = reader.GetInt64(1);
                ticks = storedTicks < 0 ? 0UL : (ulong)storedTicks;
            }
        }

        // The item id as DropIndex is fine here: this command is not lockstep.
        ItemRoll roll = ItemRoller.Roll(definition, rarity, new ItemRollSeed(seed, ticks, StableHash.Of(key), 0, (int)itemId));
        string now = SqliteSchemaBootstrapper.FormatUtc(DateTime.UtcNow);

        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = InsertItemSql;
            command.Parameters.AddWithValue("$world", worldId);
            command.Parameters.AddWithValue("$id", itemId);
            command.Parameters.AddWithValue("$kind", definition.Key);
            command.Parameters.AddWithValue("$displayName", definition.DisplayName);
            command.Parameters.AddWithValue("$visualId", definition.VisualId);
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$now", now);
            command.Parameters.AddWithValue("$roll", ItemRollCodec.Serialize(roll));
            command.ExecuteNonQuery();
        }

        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = InsertSlotSql;
            command.Parameters.AddWithValue("$world", worldId);
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$slot", slot);
            command.Parameters.AddWithValue("$id", itemId);
            command.Parameters.AddWithValue("$now", now);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
        output.WriteLine(string.Join(
            '\t',
            itemId.ToString(CultureInfo.InvariantCulture),
            slot.ToString(CultureInfo.InvariantCulture),
            RarityText(roll.Rarity),
            FormatModifiers(roll.Modifiers)));
        return ExitOk;
    }

    private static int List(SqliteSchemaBootstrapper schema, WorldId worldId, string key, TextWriter output)
    {
        SqliteWorldStore store = new(schema);
        PersistedPlayerInventory inventory = new SqliteInventoryRepository(store, worldId).LoadPlayerInventory(key);
        Dictionary<long, PersistedItemInstance> items = [];
        foreach (PersistedItemInstance item in inventory.Items)
        {
            items[item.ItemInstanceId] = item;
        }

        foreach (PersistedInventorySlot slot in inventory.Slots)
        {
            if (!items.TryGetValue(slot.ItemInstanceId, out PersistedItemInstance item))
            {
                output.WriteLine(string.Join('\t', slot.SlotIndex.ToString(CultureInfo.InvariantCulture), slot.ItemInstanceId.ToString(CultureInfo.InvariantCulture), "-", "-", "-"));
                continue;
            }

            string rarity = "-";
            string modifiers = "-";
            if (item.Roll is { } roll)
            {
                rarity = roll.IsDamaged ? "damaged" : RarityText(roll.Rarity);
                modifiers = FormatModifiers(roll.Modifiers);
            }

            output.WriteLine(string.Join(
                '\t',
                slot.SlotIndex.ToString(CultureInfo.InvariantCulture),
                item.ItemInstanceId.ToString(CultureInfo.InvariantCulture),
                CharacterCli.Sanitize(item.ItemKind),
                rarity,
                modifiers));
        }

        return ExitOk;
    }

    private static string RarityText(ItemRarity rarity) => rarity.ToString().ToLowerInvariant();

    // Strength=5,AttackSpeed=1.25 in stored order; "-" when empty. Floats print round-trip.
    private static string FormatModifiers(IReadOnlyList<StatModifier> modifiers)
    {
        if (modifiers.Count == 0)
        {
            return "-";
        }

        StringBuilder text = new();
        for (int i = 0; i < modifiers.Count; i++)
        {
            if (i > 0)
            {
                text.Append(',');
            }

            text.Append(modifiers[i].Stat).Append('=').Append(modifiers[i].Value.ToString("R", CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }
}
