using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;
using static Dream.Tests.Integration.ObjectIdTestSupport;
using static Dream.Tests.Integration.PersistentWorldSeedDeterminismIntegrationTests;

namespace Dream.Tests.Integration;

// Persistence wave D: two lockstep servers on two copies of one database, each restarting twice,
// while the scripted session fractures generated terrain. Checksums must be equal on every tick of
// every phase, the rubble cells must be equal, and after every phase the database dumps (and, once
// migration 0006 is in, the terrain override rows) must be equal. The scripted secondaries fracture
// terrain in each phase; from the second phase on the restored overrides remove those cells again
// before the script replays.
public sealed class TerrainLockstepRestartTests(ITestOutputHelper output)
{
    private static readonly int[] PhaseTicks = [600, 200, 200];

    [Fact]
    public void TwoServers_WithTerrainFractures_RestartingTwice_KeepEqualChecksumsEveryTick_AndEqualDatabases()
    {
        using TempDatabase left = new();
        using TempDatabase right = new();
        using (left.OpenBundle())
        {
        }

        SqliteConnection.ClearAllPools();
        foreach (string suffix in new[] { "", "-wal", "-shm" })
        {
            if (File.Exists(left.DatabasePath + suffix))
            {
                File.Copy(left.DatabasePath + suffix, right.DatabasePath + suffix);
            }
        }

        long previousOverrides = 0;
        for (int phase = 0; phase < PhaseTicks.Length; phase++)
        {
            int divergence = RunPhase(left, right, PhaseTicks[phase], out int compared, out int terrainRubble);
            Assert.True(divergence < 0, $"phase {phase + 1}: the servers diverged at tick {divergence}");
            SqliteConnection.ClearAllPools();
            Assert.Equal(DumpWorld(left.DatabasePath), DumpWorld(right.DatabasePath));
            string leftTerrain = DumpTerrain(left.DatabasePath);
            Assert.Equal(leftTerrain, DumpTerrain(right.DatabasePath));
            PersistedIds ids = ReadIds(left.DatabasePath);
            Assert.Equal(ids.AllObjects.Count, ids.AllObjects.Distinct().Count());
            Assert.Empty(ids.Origins.Intersect(ids.AllObjects));

            long overrides = TerrainTableExists(left.DatabasePath, "terrain_cell_overrides")
                ? Scalar(left.DatabasePath, "SELECT COUNT(*) FROM terrain_cell_overrides WHERE world_id = 1;")
                : -1;
            if (overrides >= 0)
            {
                // Migration 0006 and the bridge are in: the scripted terrain fractures are stored,
                // and the override ids are unique across every kind.
                Assert.True(overrides > 0, $"phase {phase + 1}: no terrain override was stored");
                Assert.True(overrides >= previousOverrides);
                previousOverrides = overrides;
                Assert.Equal(
                    overrides,
                    Scalar(left.DatabasePath, "SELECT COUNT(DISTINCT object_id) FROM terrain_cell_overrides WHERE world_id = 1;"));
                Assert.Equal(
                    0L,
                    Scalar(left.DatabasePath, "SELECT COUNT(*) FROM terrain_cell_overrides o JOIN placed_cube_instances p ON p.world_id = o.world_id AND p.object_id = o.object_id;"));
            }

            output.WriteLine($"phase {phase + 1}: {compared} ticks equal; rubble cells {terrainRubble}; terrain overrides {overrides}; dumps equal ({leftTerrain.Length} terrain chars)");
        }
    }

    private static int RunPhase(TempDatabase left, TempDatabase right, int ticks, out int compared, out int rubbleCells)
    {
        int firstDivergence = -1;
        compared = 0;
        using PersistentScriptedServer first = new(left.OpenBundle());
        using PersistentScriptedServer second = new(right.OpenBundle());
        Assert.Equal(first.Seed, second.Seed);
        Assert.Equal(first.Hash(), second.Hash());
        Assert.Equal(first.Runtime.Map.WorldChecksum, second.Runtime.Map.WorldChecksum);
        Assert.Equal(first.Runtime.ObjectIds.Next, second.Runtime.ObjectIds.Next);
        for (int tick = 1; tick <= ticks; tick++)
        {
            ScriptStep step = Script(tick, first);
            first.Advance(tick, step);
            second.Advance(tick, step);
            compared++;
            if (firstDivergence < 0 &&
                (first.Hash() != second.Hash() || first.Runtime.Map.WorldChecksum != second.Runtime.Map.WorldChecksum))
            {
                firstDivergence = tick;
            }
        }

        Assert.Equal(first.Runtime.ObjectIds.Next, second.Runtime.ObjectIds.Next);
        Assert.Equal(
            first.Runtime.Map.RubbleCells.Keys.OrderBy(key => (key.X, key.Y, key.Z)).ToArray(),
            second.Runtime.Map.RubbleCells.Keys.OrderBy(key => (key.X, key.Y, key.Z)).ToArray());
        rubbleCells = first.Runtime.Map.RubbleCells.Count;
        return firstDivergence;
    }

    private static bool TerrainTableExists(string databasePath, string table)
    {
        return Scalar(databasePath, $"SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '{table}';") > 0;
    }

    // The wave D tables (migration 0006), ordered, when they exist.
    private static string DumpTerrain(string databasePath)
    {
        StringBuilder dump = new();
        using SqliteConnection connection = Open(databasePath);
        foreach ((string table, string columns, string order) in new[]
        {
            ("terrain_cell_overrides", "object_id, cell_x, cell_y, cell_z, kind, length(cell_data)", "object_id"),
            ("terrain_chunks", "chunk_x, chunk_z, format_version, cell_count, length(data)", "chunk_z, chunk_x")
        })
        {
            if (!TerrainTableExists(databasePath, table))
            {
                continue;
            }

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"SELECT {columns} FROM {table} WHERE world_id = 1 ORDER BY {order};";
            using SqliteDataReader reader = command.ExecuteReader();
            dump.AppendLine(table);
            while (reader.Read())
            {
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    dump.Append(i == 0 ? "  " : " | ");
                    dump.Append(reader.IsDBNull(i) ? "null" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture));
                }

                dump.AppendLine();
            }
        }

        return dump.ToString();
    }
}
