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

    public bool QuickCheck { get; init; } = true;
}

public sealed record WorldIntegrityReport(
    int ForeignKeyViolations,
    string? CorruptionMessage,
    int ObjectsOutOfBounds,
    int SlotsMissingItem,
    int EquippedWithoutBackpackItem,
    int UnknownItemKinds,
    int UnknownEnemyKinds,
    IReadOnlyList<string> UnknownKindNames,
    TimeSpan Elapsed)
{
    // Foreign-key violations or corruption: the start must stop.
    public bool IsFatal => ForeignKeyViolations > 0 || CorruptionMessage != null;

    public bool HasSoftFindings =>
        ObjectsOutOfBounds > 0 || SlotsMissingItem > 0 || EquippedWithoutBackpackItem > 0
        || UnknownItemKinds > 0 || UnknownEnemyKinds > 0;

    public string FatalMessage =>
        CorruptionMessage != null
            ? $"the database failed its integrity check: {CorruptionMessage}"
            : $"the database has {ForeignKeyViolations} foreign-key violation(s) (PRAGMA foreign_key_check)";

    // One summary line, then one line per finding. Counts only; never row contents.
    public IEnumerable<string> ToLogLines(WorldDescriptor descriptor)
    {
        string head = $"World '{descriptor.Name}' (id {descriptor.Id}) integrity check: " +
            $"{Elapsed.TotalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture)} ms";
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
        int unknownItems = 0;
        int unknownEnemies = 0;
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
            unknownItems = CountUnknown(connection, "item_instances", "item_kind", worldId, options.IsKnownItemKind, unknownNames);
            unknownEnemies = CountUnknown(connection, "enemy_instances", "enemy_kind", worldId, options.IsKnownEnemyKind, unknownNames);
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
            unknownItems,
            unknownEnemies,
            unknownNames,
            Stopwatch.GetElapsedTime(started));
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

    private static int Count(SqliteConnection connection, string sql, long worldId, WorldDescriptor? descriptor)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$world", worldId);
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

    private const string SlotsMissingItemSql = """
        SELECT COUNT(*) FROM player_inventory_slots AS s
        LEFT JOIN item_instances AS i
          ON i.world_id = s.world_id AND i.item_instance_id = s.item_instance_id
        WHERE s.world_id = $world AND i.item_instance_id IS NULL;
        """;

    private static readonly string EquippedWithoutBackpackSql = $"""
        SELECT COUNT(*) FROM player_inventory_slots AS e
        WHERE e.world_id = $world AND e.slot_index = {InventoryConstants.EquippedSlotIndex}
          AND NOT EXISTS (
            SELECT 1 FROM player_inventory_slots AS b
            WHERE b.world_id = e.world_id AND b.player_name = e.player_name
              AND b.slot_index >= 0 AND b.slot_index < {InventoryConstants.BackpackSlotCount}
              AND b.item_instance_id = e.item_instance_id);
        """;
}
