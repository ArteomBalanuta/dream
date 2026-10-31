using System.Diagnostics;
using System.Globalization;
using Dream.Simulation.Inventory;
using Dream.Simulation.World;
using Microsoft.Data.Sqlite;

namespace Dream.Server.Persistence;

// Thrown when a world's data fails the checks that must stop a start: a foreign-key violation or
// a damaged database file. The soft findings never throw.
public sealed class DatabaseIntegrityException : InvalidOperationException
{
    public DatabaseIntegrityException(string message)
        : base(message)
    {
    }
}

// Which kinds a build knows. Both default to accept-all; the glue connects them to the content
// catalog. QuickCheck (on by default) adds SQLite's PRAGMA quick_check, which reads the whole file;
// about 5 ms per 2.6 MB on the World10k database (measured), so it scales with the file.
public sealed record WorldIntegrityOptions
{
    public static WorldIntegrityOptions Default { get; } = new();

    public Func<string, bool> IsKnownItemKind { get; init; } = static _ => true;

    public Func<string, bool> IsKnownEnemyKind { get; init; } = static _ => true;

    // Whether an item kind may sit in the given equipment slot index (100..103). The default accepts all.
    public Func<string, int, bool> CanOccupy { get; init; } = static (_, _) => true;

    public bool QuickCheck { get; init; } = true;
}

public sealed record WorldIntegrityReport(
    int ForeignKeyViolations,
    string? CorruptionMessage,
    int ObjectsOutOfBounds,
    int SlotsMissingItem,
    int EquippedWithoutBackpackItem,
    int SharedItemReferences,
    int UnknownItemKinds,
    int UnknownEnemyKinds,
    IReadOnlyList<string> UnknownKindNames,
    TimeSpan Elapsed,
    int AccountsOverLimit = 0,
    int DuplicateDisplayNames = 0,
    int UnownedCharacters = 0,
    int ItemsInTwoEquipmentSlots = 0,
    int UnknownSlotIndexes = 0,
    int EquipmentSlotMismatches = 0,
    int DamagedItemRolls = 0)
{
    // UnownedCharacters is information only (every legacy character is unowned until a token claims it):
    // it is in the head line and never in HasSoftFindings.
    // Foreign-key violations or corruption: the start must stop.
    public bool IsFatal => ForeignKeyViolations > 0 || CorruptionMessage != null;

    public bool HasSoftFindings =>
        ObjectsOutOfBounds > 0 || SlotsMissingItem > 0 || EquippedWithoutBackpackItem > 0
        || SharedItemReferences > 0 || UnknownItemKinds > 0 || UnknownEnemyKinds > 0
        || AccountsOverLimit > 0 || DuplicateDisplayNames > 0
        || ItemsInTwoEquipmentSlots > 0 || UnknownSlotIndexes > 0 || EquipmentSlotMismatches > 0 || DamagedItemRolls > 0;

    public string FatalMessage =>
        CorruptionMessage != null
            ? $"the database failed its integrity check: {CorruptionMessage}"
            : $"the database has {ForeignKeyViolations} foreign-key violation(s) (PRAGMA foreign_key_check)";

    // One summary line, then one line per finding. Counts only; never row contents.
    public IEnumerable<string> ToLogLines(WorldDescriptor descriptor)
    {
        string head = $"World '{descriptor.Name}' (id {descriptor.Id}) integrity check: " +
            $"{Elapsed.TotalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture)} ms";
        if (UnownedCharacters > 0)
        {
            head += $", {UnownedCharacters} unowned character(s)";
        }

        if (!IsFatal && !HasSoftFindings)
        {
            yield return head + ", no findings.";
            yield break;
        }

        yield return head + ".";
        if (ForeignKeyViolations > 0)
        {
            yield return $"  foreign_key_check: {ForeignKeyViolations} violation(s).";
        }

        if (CorruptionMessage != null)
        {
            yield return $"  corruption: {CorruptionMessage}";
        }

        if (ObjectsOutOfBounds > 0)
        {
            yield return $"  {ObjectsOutOfBounds} stored object(s) outside the world's bounds (kept).";
        }

        if (SlotsMissingItem > 0)
        {
            yield return $"  {SlotsMissingItem} inventory slot(s) point at a missing item.";
        }

        if (EquippedWithoutBackpackItem > 0)
        {
            yield return $"  {EquippedWithoutBackpackItem} equipped slot(s) match no backpack item.";
        }

        if (SharedItemReferences > 0)
        {
            yield return $"  {SharedItemReferences} extra inventory slot reference(s) to an item already held in another slot or by another player.";
        }

        if (ItemsInTwoEquipmentSlots > 0)
        {
            yield return $"  {ItemsInTwoEquipmentSlots} extra equipment slot row(s) hold an item already in another equipment slot (kept).";
        }

        if (UnknownSlotIndexes > 0)
        {
            yield return $"  {UnknownSlotIndexes} inventory slot row(s) outside the backpack and equipment ranges (kept).";
        }

        if (EquipmentSlotMismatches > 0)
        {
            yield return $"  {EquipmentSlotMismatches} equipped item(s) sit in a slot their kind does not allow (kept, inert).";
        }

        if (DamagedItemRolls > 0)
        {
            yield return $"  {DamagedItemRolls} item(s) have a damaged stored roll (kept, no stats).";
        }

        if (AccountsOverLimit > 0)
        {
            yield return $"  {AccountsOverLimit} account(s) own more than {Dream.Simulation.Characters.CharacterNames.MaxCharactersPerAccount} characters (kept).";
        }

        if (DuplicateDisplayNames > 0)
        {
            yield return $"  {DuplicateDisplayNames} character display name(s) shared ignoring case (kept).";
        }

        if (UnknownItemKinds > 0 || UnknownEnemyKinds > 0)
        {
            yield return $"  unknown kinds: {UnknownItemKinds} item row(s), {UnknownEnemyKinds} enemy row(s) " +
                $"({string.Join(", ", UnknownKindNames)}); kept as stored.";
        }
    }
}

// The checks a world runs when it is loaded. Only foreign-key violations and a damaged file
// are fatal; a real legacy database is never refused for the soft findings, which are counted
// and logged so an operator can see them. Read-only.
public static class WorldIntegrityCheck
{
    private const int MaxKindNames = 5;

    public static WorldIntegrityReport Run(
        SqliteConnection connection,
        WorldDescriptor descriptor,
        WorldIntegrityOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(descriptor);
        options ??= WorldIntegrityOptions.Default;
        long started = Stopwatch.GetTimestamp();
        string? corruption = null;
        int foreignKeys = 0;
        int outOfBounds = 0;
        int missingItem = 0;
        int equipped = 0;
        int shared = 0;
        int unknownItems = 0;
        int unknownEnemies = 0;
        int overLimit = 0;
        int duplicateNames = 0;
        int unowned = 0;
        int twoSlots = 0;
        int badIndexes = 0;
        int mismatches = 0;
        int damaged = 0;
        List<string> unknownNames = [];
        try
        {
            if (options.QuickCheck)
            {
                corruption = QuickCheck(connection);
            }

            foreignKeys = CountRows(connection, "PRAGMA foreign_key_check;");
            long worldId = descriptor.Id.Value;
            outOfBounds = Count(connection, OutOfBoundsSql, worldId, descriptor);
            missingItem = Count(connection, SlotsMissingItemSql, worldId, descriptor: null);
            equipped = Count(connection, EquippedWithoutBackpackSql, worldId, descriptor: null);
            shared = Count(connection, SharedItemReferencesSql, worldId, descriptor: null);
            unknownItems = CountUnknown(connection, "item_instances", "item_kind", worldId, options.IsKnownItemKind, unknownNames);
            unknownEnemies = CountUnknown(connection, "enemy_instances", "enemy_kind", worldId, options.IsKnownEnemyKind, unknownNames);
            overLimit = Count(connection, AccountsOverLimitSql, worldId: null, descriptor: null);
            duplicateNames = Count(connection, DuplicateDisplayNamesSql, worldId: null, descriptor: null);
            unowned = Count(connection, UnownedCharactersSql, worldId: null, descriptor: null);
            twoSlots = Count(connection, ItemsInTwoEquipmentSlotsSql, worldId, descriptor: null);
            badIndexes = Count(connection, UnknownSlotIndexesSql, worldId, descriptor: null);
            mismatches = CountSlotMismatches(connection, worldId, options.CanOccupy);
            damaged = CountDamagedRolls(connection, worldId);
        }
        catch (SqliteException ex) when (IsCorruption(ex))
        {
            corruption = $"SQLite error {ex.SqliteErrorCode}: {ex.Message}";
        }

        return new WorldIntegrityReport(
            foreignKeys,
            corruption,
            outOfBounds,
            missingItem,
            equipped,
            shared,
            unknownItems,
            unknownEnemies,
            unknownNames,
            Stopwatch.GetElapsedTime(started),
            overLimit,
            duplicateNames,
            unowned,
            twoSlots,
            badIndexes,
            mismatches,
            damaged);
    }

    // SQLITE_CORRUPT (11) and SQLITE_NOTADB (26).
    private static bool IsCorruption(SqliteException ex)
    {
        return ex.SqliteErrorCode is 11 or 26;
    }

    private static string? QuickCheck(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check(1);";
        string? text = command.ExecuteScalar() as string;
        return string.Equals(text, "ok", StringComparison.OrdinalIgnoreCase) ? null : text ?? "quick_check returned nothing";
    }

    private static int CountRows(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        using SqliteDataReader reader = command.ExecuteReader();
        int rows = 0;
        while (reader.Read())
        {
            rows++;
        }

        return rows;
    }

    private static int Count(SqliteConnection connection, string sql, long? worldId, WorldDescriptor? descriptor)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        if (worldId is { } world)
        {
            command.Parameters.AddWithValue("$world", world);
        }

        if (descriptor != null)
        {
            command.Parameters.AddWithValue("$w", descriptor.Width);
            command.Parameters.AddWithValue("$h", descriptor.Height);
            command.Parameters.AddWithValue("$d", descriptor.Depth);
        }

        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static int CountUnknown(
        SqliteConnection connection,
        string table,
        string column,
        long worldId,
        Func<string, bool> isKnown,
        List<string> names)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT {column}, COUNT(*) FROM {table} WHERE world_id = $world GROUP BY {column};";
        command.Parameters.AddWithValue("$world", worldId);
        using SqliteDataReader reader = command.ExecuteReader();
        int unknown = 0;
        while (reader.Read())
        {
            string kind = reader.GetString(0);
            if (isKnown(kind))
            {
                continue;
            }

            unknown += reader.GetInt32(1);
            if (names.Count < MaxKindNames && !names.Contains(kind))
            {
                names.Add(kind);
            }
        }

        return unknown;
    }

    // Equipped rows whose kind does not allow their slot. CanOccupy decides; the SQL only lists the rows.
    private static int CountSlotMismatches(SqliteConnection connection, long worldId, Func<string, int, bool> canOccupy)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = EquippedKindsSql;
        command.Parameters.AddWithValue("$world", worldId);
        using SqliteDataReader reader = command.ExecuteReader();
        int mismatches = 0;
        while (reader.Read())
        {
            if (!canOccupy(reader.GetString(0), reader.GetInt32(1)))
            {
                mismatches++;
            }
        }

        return mismatches;
    }

    // Only rows with a stored roll are read; each is parsed, which never throws.
    private static int CountDamagedRolls(SqliteConnection connection, long worldId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT rolled_modifiers FROM item_instances WHERE world_id = $world AND rolled_modifiers IS NOT NULL;";
        command.Parameters.AddWithValue("$world", worldId);
        using SqliteDataReader reader = command.ExecuteReader();
        int damaged = 0;
        while (reader.Read())
        {
            if (ItemRollCodec.Parse(reader.GetString(0))?.IsDamaged == true)
            {
                damaged++;
            }
        }

        return damaged;
    }

    // Accounts holding more characters than the limit (two processes can breach it; see the wave G plan).
    private static readonly string AccountsOverLimitSql = $"""
        SELECT COUNT(*) FROM (SELECT account_id FROM player_profiles WHERE account_id IS NOT NULL
          GROUP BY account_id HAVING COUNT(*) > {Dream.Simulation.Characters.CharacterNames.MaxCharactersPerAccount});
        """;

    // Display names shared ignoring ASCII case; only possible when the unique index is missing.
    private const string DuplicateDisplayNamesSql = """
        SELECT COUNT(*) FROM (SELECT 1 FROM player_profiles GROUP BY display_name COLLATE NOCASE HAVING COUNT(*) > 1);
        """;

    private const string UnownedCharactersSql = "SELECT COUNT(*) FROM player_profiles WHERE account_id IS NULL;";

    // Cube cells, rubble parent cells and terrain override cells outside [0,width) x [0,height) x [0,depth).
    private const string OutOfBoundsSql = """
        SELECT
          (SELECT COUNT(*) FROM placed_cube_instances WHERE world_id = $world AND
             (cell_x < 0 OR cell_x >= $w OR cell_y < 0 OR cell_y >= $h OR cell_z < 0 OR cell_z >= $d))
        + (SELECT COUNT(*) FROM cube_fracture_pieces WHERE world_id = $world AND
             (parent_cell_x < 0 OR parent_cell_x >= $w OR parent_cell_y < 0 OR parent_cell_y >= $h
              OR parent_cell_z < 0 OR parent_cell_z >= $d))
        + (SELECT COUNT(*) FROM terrain_cell_overrides WHERE world_id = $world AND
             (cell_x >= $w OR cell_y >= $h OR cell_z >= $d));
        """;

    // Slots whose item does not exist. The slot table has a foreign key to item_instances, so a
    // non-empty result is also a foreign_key_check violation, which is fatal and reported first: this
    // soft count can only show up when foreign keys are not enforced by the file's schema (a legacy or
    // hand-built database). It is kept as a cheap second view, not because it can normally fire.
    private const string SlotsMissingItemSql = """
        SELECT COUNT(*) FROM player_inventory_slots AS s
        LEFT JOIN item_instances AS i
          ON i.world_id = s.world_id AND i.item_instance_id = s.item_instance_id
        WHERE s.world_id = $world AND i.item_instance_id IS NULL;
        """;

    private const int FirstEquip = EquipmentSlots.FirstSlotIndex;
    private const int LastEquip = EquipmentSlots.LastSlotIndex;

    private static readonly string EquippedWithoutBackpackSql = $"""
        SELECT COUNT(*) FROM player_inventory_slots AS e
        WHERE e.world_id = $world AND e.slot_index BETWEEN {FirstEquip} AND {LastEquip}
          AND NOT EXISTS (
            SELECT 1 FROM player_inventory_slots AS b
            WHERE b.world_id = e.world_id AND b.player_name = e.player_name
              AND b.slot_index >= 0 AND b.slot_index < {InventoryConstants.BackpackSlotCount}
              AND b.item_instance_id = e.item_instance_id);
        """;

    // Extra references beyond the first to the same item, over backpack slots and players. The equipped
    // slots (100..103) legitimately alias a backpack slot of the same player, so that pair is not counted; an
    // equipped slot with no such alias is counted as a reference of its own.
    private static readonly string SharedItemReferencesSql = $"""
        SELECT COALESCE(SUM(n - 1), 0) FROM (
          SELECT COUNT(*) AS n FROM player_inventory_slots AS s
          WHERE s.world_id = $world
            AND NOT (s.slot_index BETWEEN {FirstEquip} AND {LastEquip} AND EXISTS (
              SELECT 1 FROM player_inventory_slots AS b
              WHERE b.world_id = s.world_id AND b.player_name = s.player_name
                AND b.slot_index >= 0 AND b.slot_index < {InventoryConstants.BackpackSlotCount}
                AND b.item_instance_id = s.item_instance_id))
          GROUP BY s.item_instance_id
          HAVING COUNT(*) > 1);
        """;

    // Extra equipment rows beyond the first for one item of one player.
    private static readonly string ItemsInTwoEquipmentSlotsSql = $"""
        SELECT COALESCE(SUM(n - 1), 0) FROM (
          SELECT COUNT(*) AS n FROM player_inventory_slots
          WHERE world_id = $world AND slot_index BETWEEN {FirstEquip} AND {LastEquip}
          GROUP BY player_name, item_instance_id
          HAVING COUNT(*) > 1);
        """;

    private static readonly string UnknownSlotIndexesSql = $"""
        SELECT COUNT(*) FROM player_inventory_slots
        WHERE world_id = $world
          AND NOT (slot_index >= 0 AND slot_index < {InventoryConstants.BackpackSlotCount})
          AND NOT (slot_index BETWEEN {FirstEquip} AND {LastEquip});
        """;

    private static readonly string EquippedKindsSql = $"""
        SELECT i.item_kind, s.slot_index FROM player_inventory_slots AS s
        JOIN item_instances AS i ON i.world_id = s.world_id AND i.item_instance_id = s.item_instance_id
        WHERE s.world_id = $world AND s.slot_index BETWEEN {FirstEquip} AND {LastEquip};
        """;
}
