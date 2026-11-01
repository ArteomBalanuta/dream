using Microsoft.Data.Sqlite;
using Xunit;

namespace Dream.Tests.Server.Persistence.Migrations;

// One player_inventory_slots row.
internal readonly record struct SlotRow(long WorldId, string Player, int Slot, long ItemId);

// Migration 0010 (the footprint bag) remaps slot_index, so a test that compared "every row, before and
// after" must compare through this mapping. The mapping is written here in C#, apart from the SQL, from
// the rules in the migration's header: rows in 16..99 are parked at +20000; a bag row (0..15) whose
// player wears the same item (a row 100..111) is deleted; the other bag rows 0..15 go to the 2x2 block
// anchors (i / 4) * 16 + (i % 4) * 2; everything else stays.
internal static class BagGridMigrationAsserts
{
    public static List<SlotRow> ReadSlots(MigrationTestDatabase database)
    {
        using SqliteConnection connection = database.Open();
        return ReadSlots(connection);
    }

    public static List<SlotRow> ReadSlots(SqliteConnection connection)
    {
        // A database from before worlds (schema 2 and older) has no world_id column: everything is world 1.
        bool hasWorlds;
        using (SqliteCommand probe = connection.CreateCommand())
        {
            probe.CommandText = "SELECT COUNT(*) FROM pragma_table_info('player_inventory_slots') WHERE name = 'world_id';";
            hasWorlds = (long)probe.ExecuteScalar()! > 0;
        }

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = hasWorlds
            ? "SELECT world_id, player_name, slot_index, item_instance_id FROM player_inventory_slots ORDER BY world_id, player_name, slot_index;"
            : "SELECT 1, player_name, slot_index, item_instance_id FROM player_inventory_slots ORDER BY player_name, slot_index;";
        using SqliteDataReader reader = command.ExecuteReader();
        List<SlotRow> rows = [];
        while (reader.Read())
        {
            rows.Add(new SlotRow(reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt64(3)));
        }

        return rows;
    }

    public static int AnchorOfOldSlot(int oldSlot) => (oldSlot / 4) * 16 + (oldSlot % 4) * 2;

    // The rows a v9 database must hold after the migration.
    public static List<SlotRow> Expected(IReadOnlyList<SlotRow> v9)
    {
        List<SlotRow> expected = [];
        foreach (SlotRow row in v9)
        {
            if (row.Slot is >= 16 and <= 99)
            {
                expected.Add(row with { Slot = row.Slot + 20000 });
            }
            else if (row.Slot is >= 0 and <= 15)
            {
                bool worn = v9.Any(other =>
                    other.WorldId == row.WorldId && other.Player == row.Player && other.ItemId == row.ItemId && other.Slot is >= 100 and <= 111);
                if (!worn)
                {
                    expected.Add(row with { Slot = AnchorOfOldSlot(row.Slot) });
                }
            }
            else
            {
                expected.Add(row);
            }
        }

        return [.. expected.OrderBy(row => row.WorldId).ThenBy(row => row.Player, StringComparer.Ordinal).ThenBy(row => row.Slot)];
    }

    // The migrated rows are the expected ones, and the guarantees hold on their own: every item of the
    // old rows is still referenced, every worn row is untouched, no worn item keeps a bag row, and no
    // bag anchor is outside 0..63.
    public static void AssertMigrated(IReadOnlyList<SlotRow> v9, IReadOnlyList<SlotRow> v10)
    {
        Assert.Equal(Expected(v9), v10.OrderBy(row => row.WorldId).ThenBy(row => row.Player, StringComparer.Ordinal).ThenBy(row => row.Slot).ToList());

        Assert.Equal(
            v9.Select(row => (row.WorldId, row.ItemId)).Distinct().Order(),
            v10.Select(row => (row.WorldId, row.ItemId)).Distinct().Order());

        foreach (SlotRow worn in v9.Where(row => row.Slot is >= 100 and <= 111))
        {
            Assert.Contains(worn, v10);
        }

        foreach (SlotRow wornRow in v10.Where(row => row.Slot is >= 100 and <= 111))
        {
            Assert.DoesNotContain(
                v10,
                row => row.WorldId == wornRow.WorldId && row.Player == wornRow.Player && row.ItemId == wornRow.ItemId
                    && row.Slot is >= 0 and <= 63);
        }

        // Rows in 0..99 are bag anchors now: none past 63 (16..99 were parked, and 0..15 remapped to at most 54).
        Assert.DoesNotContain(v10, row => row.Slot is >= 64 and <= 99);
        Assert.All(v10.Where(row => row.Slot is >= 0 and <= 99), row => Assert.InRange(row.Slot, 0, 63));
    }
}
