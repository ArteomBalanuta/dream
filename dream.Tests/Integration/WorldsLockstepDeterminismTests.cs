using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;
using Dream.Server;
using Dream.Server.Persistence;
using Dream.Server.Persistence.Worlds;
using Dream.Server.Worlds;
using Dream.Simulation.Determinism;
using Dream.Simulation.Persistence;
using Dream.Simulation.World;
using Dream.Tests.Server.Persistence.Migrations;
using static Dream.Tests.Integration.PersistentWorldSeedDeterminismIntegrationTests;

namespace Dream.Tests.Integration;

// Persistence wave B: the world id never enters simulation state. Two production persistent hosts run
// the wave 13 script in lockstep and every tick's SimulationStateChecksum agrees (past the periodic
// checkpoint), when they are (1) world 1 and a catalog copy of it, and (2) a world migrated from a
// schema v2 database (seed and clock from world_metadata) and a world created with that same seed
// and clock.
public sealed class WorldsLockstepDeterminismTests(ITestOutputHelper output)
{
    // In the Day phase for the whole run (the night wolf is a gameplay input, see WaveT1ClockDeterminismTests).
    private const long DayClockTicks = 50_000;
    private const ulong MigratedSeed = 5150;

    [Fact(Skip = WorldBundles.SkipReason)]
    public void World1_AndACopyOfIt_RunTheSameScript_WithEqualChecksumsOnEveryTick()
    {
        using MigrationTestDatabase database = new();
        SqliteWorldCatalog catalog = WorldBundles.OpenCatalog(database);
        WorldRecord original = catalog.Resolve(WorldSelector.Default, WorldDefaults.StartupFloorSize, out _);
        Assert.Equal(WorldId.Default, original.Descriptor.Id);
        Assert.NotNull(original.Seed);
        WorldRecord copy = catalog.Copy(original.Descriptor.Id, "twin");
        Assert.NotEqual(WorldId.Default, copy.Descriptor.Id);
        Assert.Equal(original.Seed, copy.Seed);
        Assert.Equal(original.WorldTimeTicks, copy.WorldTimeTicks);

        AssertLockstep(database, original, database, copy);
    }

    [Fact(Skip = WorldBundles.SkipReason)]
    public void AMigratedWorld_KeepsTheChecksums_OfItsPreMigrationSeedAndTime()
    {
        // The reference: a fresh v3 database whose world has the migrated world's seed and clock.
        using MigrationTestDatabase reference = new();
        SqliteWorldCatalog referenceCatalog = WorldBundles.OpenCatalog(reference);
        WorldRecord created = referenceCatalog.Create(new WorldCreateOptions("reference", Seed: MigratedSeed));
        new SqlitePersistenceCheckpointWriter(reference.Schema, created.Descriptor.Id)
            .Write(PersistenceCheckpoint.Empty with { WorldTimeTicks = DayClockTicks });
        SqliteConnection.ClearAllPools();
        WorldRecord referenceWorld = referenceCatalog.TryGet(created.Descriptor.Id)!;
        Assert.Equal(DayClockTicks, referenceWorld.WorldTimeTicks);

        // The migrated world: a v2 database with the same seed and clock in world_metadata, opened by 0003.
        using MigrationTestDatabase migrated = new();
        migrated.Schema.EnsureSchema(MigrationTestDatabase.BaselineOnlyMigrator());
        migrated.ExecuteScript(
            $"INSERT INTO world_metadata (key, value) VALUES ('simulation_seed', '{MigratedSeed}'), ('world_time_ticks', '{DayClockTicks}');");
        MigrationTestDatabase.ReleasePooledConnections();
        SqliteWorldCatalog migratedCatalog = WorldBundles.OpenCatalog(migrated);
        WorldRecord migratedWorld = migratedCatalog.TryGet(WorldId.Default)!;
        Assert.Equal(MigratedSeed, migratedWorld.Seed);
        Assert.Equal(DayClockTicks, migratedWorld.WorldTimeTicks);

        AssertLockstep(migrated, migratedWorld, reference, referenceWorld);
    }

    private void AssertLockstep(MigrationTestDatabase leftDatabase, WorldRecord left, MigrationTestDatabase rightDatabase, WorldRecord right)
    {
        using PersistentScriptedServer first = new(WorldBundles.Bind(leftDatabase.Schema, left));
        using PersistentScriptedServer second = new(WorldBundles.Bind(rightDatabase.Schema, right));
        Assert.Equal(first.Seed, second.Seed);
        Assert.Equal(first.Hash(), second.Hash());

        int firstDivergence = -1;
        for (int tick = 1; tick <= Ticks; tick++)
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

        Assert.True(firstDivergence < 0, $"The worlds diverged at tick {firstDivergence}.");
        Assert.Equal(WorldStateChecksum.Compute(first.Runtime.Map), WorldStateChecksum.Compute(second.Runtime.Map));
        Assert.Equal(first.Record.HitTicks, second.Record.HitTicks);
        Assert.True(first.Record.HitTicks.Count > 0, "the script never rolled a hit");
        output.WriteLine($"{Ticks} ticks in lockstep, {first.Record.HitTicks.Count} roll ticks");
    }
}
