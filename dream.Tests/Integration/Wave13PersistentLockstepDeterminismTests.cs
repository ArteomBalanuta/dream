using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;
using Dream.Server.Persistence;
using static Dream.Tests.Integration.PersistentWorldSeedDeterminismIntegrationTests;

namespace Dream.Tests.Integration;

// Wave 13 tester, determinism through the extracted classes on the production persistent host: two
// servers on two databases holding the same stored seed run PersistentWorldSeedDeterminismIntegrationTests'
// script (walk, build, three players fighting the scripted enemy and then the runtime's wolves,
// the primary breaking its cube into rubble) for 600 ticks in lockstep. That crosses the 5 s
// periodic checkpoint (tick 320), so the combat resolver, the inventory service and the
// persistence bridge (immediate fracture writes, incremental checkpoint, final checkpoint) all run.
// Every tick's SimulationStateChecksum must match, and after both hosts are disposed the two
// databases must hold the same world, ignoring timestamps.
public sealed class Wave13PersistentLockstepDeterminismTests(ITestOutputHelper output)
{
    private const int LongTicks = 600;

    [Fact]
    public void SameStoredSeed_TwoPersistentServers_StayIdenticalFor600Ticks_AndPersistTheSameWorld()
    {
        using TempDatabase left = new();
        using TempDatabase right = new();
        ulong seed;
        using (ServerPersistenceBundle bundle = left.OpenBundle())
        {
            seed = bundle.SimulationSeed!.Value;
        }

        SqliteConnection.ClearAllPools();
        foreach (string suffix in new[] { "", "-wal", "-shm" })
        {
            if (File.Exists(left.DatabasePath + suffix))
            {
                File.Copy(left.DatabasePath + suffix, right.DatabasePath + suffix);
            }
        }

        int firstDivergence = -1;
        RunRecord record;
        using (PersistentScriptedServer first = new(left.OpenBundle()))
        using (PersistentScriptedServer second = new(right.OpenBundle()))
        {
            Assert.Equal(seed, first.Seed);
            Assert.Equal(seed, second.Seed);
            Assert.Equal(first.Hash(), second.Hash());
            for (int tick = 1; tick <= LongTicks; tick++)
            {
                ScriptStep step = Script(tick, first);
                first.Advance(tick, step);
                second.Advance(tick, step);
                if (firstDivergence < 0 && first.Hash() != second.Hash())
                {
                    firstDivergence = tick;
                    output.WriteLine($"tick {tick}: {first.CurrentParts} vs {second.CurrentParts}");
                }
            }

            record = first.Record;
            Assert.Equal(record.HitTicks, second.Record.HitTicks);
            Assert.Equal(record.FractureTick, second.Record.FractureTick);
            output.WriteLine(
                $"{LongTicks} ticks: first hit {record.FirstHitTick}, enemy died {record.EnemyDeathTick}, first fracture {record.FractureTick}, " +
                $"roll ticks {record.HitTicks.Count}, rubble pieces {record.RubblePieces}, enemies alive {first.Runtime.Enemies.ActiveActors.Count}");
        }

        Assert.True(firstDivergence < 0, $"The servers diverged at tick {firstDivergence}.");
        AssertScriptExercisedEverything(record);
        Assert.True(record.HitTicks[^1] > CheckpointTicks, $"no hit after the first periodic checkpoint (last roll tick {record.HitTicks[^1]})");

        SqliteConnection.ClearAllPools();
        string leftWorld = DumpWorld(left.DatabasePath);
        string rightWorld = DumpWorld(right.DatabasePath);
        output.WriteLine(leftWorld);
        Assert.Contains("cube_fracture_pieces", leftWorld);
        Assert.Equal(leftWorld, rightWorld);
    }

    // Every persisted world and player table, minus timestamps, in a stable order.
    private static string DumpWorld(string databasePath)
    {
        (string Table, string Columns, string Order)[] tables =
        [
            ("player_profiles", "player_name, interaction_mode, strength, vitality, attack_speed, level, experience, current_hp, max_hp", "player_name"),
            ("player_world_state", "player_name, position_x, position_y, position_z, facing_x, facing_y, facing_z, current_hp", "player_name"),
            ("placed_cube_instances", "object_id, owner_player_name, cell_x, cell_y, cell_z, center_x, center_y, center_z, material_id, current_health, max_health", "object_id"),
            ("cube_fracture_pieces", "object_id, origin_object_id, piece_index, owner_player_name, parent_cell_x, parent_cell_y, parent_cell_z, center_x, center_y, center_z, side_length, material_id", "origin_object_id, piece_index"),
            ("enemy_instances", "object_id, enemy_kind, display_name, position_x, position_y, position_z, facing_x, facing_y, facing_z, current_hp, max_hp", "object_id"),
            ("item_instances", "item_instance_id, item_kind, owner_player_name, state, stack_count", "item_instance_id"),
            ("player_inventory_slots", "player_name, slot_index, item_instance_id", "player_name, slot_index"),
        ];
        StringBuilder dump = new();
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
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
}
