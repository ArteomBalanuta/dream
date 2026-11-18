using Dream.Multiplayer.Protocol;
using Dream.Server;
using Dream.Server.Persistence;
using Dream.Simulation.Persistence;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Tests.Utilities;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Server.Persistence;

// Wave T1a: the world clock is saved as worlds.world_time_ticks with every non-null
// checkpoint and resumed by ServerBootstrap, so world time survives a restart.
[Collection(TimingSensitiveCollection.Name)]
public sealed class WorldClockPersistenceTests
{
    private const int FloorSize = 16;

    [Fact]
    public void CleanRestart_ResumesTheWorldClock_AndADelayBeforeReopeningChangesNothing()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"dream-t1a-worldclock-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string databasePath = Path.Combine(directory, "world.sqlite");
        string schemaPath = Path.Combine(Dream.Tests.Load.LoadPaths.FindRepoRoot(), "dream", "config", "player-profiles.init.sql");
        try
        {
            ServerPersistenceBundle fresh = ServerBootstrap.CreatePersistenceBundle(databasePath, schemaPath);
            Assert.Equal(0L, fresh.WorldTimeTicks);
            long ticksRun = 0;
            using (DedicatedServerHost host = ServerBootstrap.CreateAuthoritativeHost(fresh))
            {
                WorldSessionRuntime runtime = host.Runtime!;
                Assert.Equal(0L, runtime.WorldClock.WorldTicks);
                for (int i = 0; i < 50; i++)
                {
                    ticksRun += host.AdvanceTime(SimulationClock.FixedDeltaSeconds * 8);
                }

                Assert.True(ticksRun >= 390, $"ran {ticksRun} ticks");
                Assert.Equal(ticksRun, runtime.WorldClock.WorldTicks);
            }

            // The world clock never reads the wall clock: time spent stopped is not world time.
            Thread.Sleep(250);

            ServerPersistenceBundle reopened = ServerBootstrap.CreatePersistenceBundle(databasePath, schemaPath);
            Assert.Equal(ticksRun, reopened.WorldTimeTicks);
            using (DedicatedServerHost host = ServerBootstrap.CreateAuthoritativeHost(reopened))
            {
                WorldSessionRuntime runtime = host.Runtime!;
                Assert.Equal(ticksRun, runtime.WorldClock.WorldTicks);
                long offset = runtime.WorldClock.WorldTicks - (long)runtime.CurrentServerTick;
                int more = host.AdvanceTime(SimulationClock.FixedDeltaSeconds * 8);
                Assert.Equal(ticksRun + more, runtime.WorldClock.WorldTicks);
                Assert.Equal(offset, runtime.WorldClock.WorldTicks - (long)runtime.CurrentServerTick);
            }
        }
        finally
        {
            SqlitePools.Release(databasePath);
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void Captures_CarryTheWorldClock_AndAnIdleIncrementalCaptureIsStillNull()
    {
        using IncrementalCheckpointTests.TempDatabase database = new();
        using WorldSessionRuntime runtime = CreatePersistentRuntime(database);
        runtime.Enemies.Clear();
        ulong tick = 0;
        Advance(runtime, ref tick, 64);

        // The first capture is full and carries the clock.
        PersistenceCheckpoint? full = runtime.CapturePersistenceCheckpoint();
        Assert.NotNull(full);
        Assert.False(full.IsIncremental);
        Assert.Equal(runtime.WorldClock.WorldTicks, full.WorldTimeTicks);

        // Only the clock moved: an incremental capture still finds nothing to write.
        Advance(runtime, ref tick, 64);
        Assert.Null(runtime.CapturePersistenceCheckpoint());
        AllocationAssert.AtMost(0, () => runtime.CapturePersistenceCheckpoint());

        // A real change (a newly registered owned cube) makes a non-null incremental capture,
        // which carries the current clock.
        Cube cube = Assert.IsType<Cube>(runtime.Map.TryAddCube(new Vector3(3f, 1f, 3f), materialId: GeometryMaterialId.Dirt));
        WorldObjectRegistry registry = (WorldObjectRegistry)typeof(WorldSessionRuntime)
            .GetField("_worldObjects", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(runtime)!;
        WorldObjectRecord cubeRecord = registry.RegisterCube(cube, runtime.Map, ownerPlayerName: "player-one");
        runtime.ObjectIdentities.Bind(runtime.ObjectIds.Take(), cubeRecord.Actor.Id);
        Advance(runtime, ref tick, 8);
        PersistenceCheckpoint? incremental = runtime.CapturePersistenceCheckpoint();
        Assert.NotNull(incremental);
        Assert.True(incremental.IsIncremental);
        Assert.Single(incremental.PlacedCubes);
        Assert.Equal(runtime.WorldClock.WorldTicks, incremental.WorldTimeTicks);

        // The writer stores it in the checkpoint's transaction.
        new SqlitePersistenceCheckpointWriter(database.Schema).Write(full);
        Assert.True(new SqliteWorldMetadataRepository(database.Schema).TryGetWorldTimeTicks(out long stored));
        Assert.Equal(full.WorldTimeTicks, stored);
    }

    [Fact]
    public void SqliteWriter_UpsertsWorldTime_AndLeavesItWhenTheCheckpointHasNone()
    {
        using IncrementalCheckpointTests.TempDatabase database = new();
        SqliteWorldMetadataRepository metadata = new(database.Schema);
        SqlitePersistenceCheckpointWriter writer = new(database.Schema);
        Assert.False(metadata.TryGetWorldTimeTicks(out _));

        writer.Write(PersistenceCheckpoint.Empty with { WorldTimeTicks = 100 });
        Assert.True(metadata.TryGetWorldTimeTicks(out long first));
        Assert.Equal(100L, first);

        writer.Write(PersistenceCheckpoint.Empty with { WorldTimeTicks = 4_000_000_000L });
        Assert.True(metadata.TryGetWorldTimeTicks(out long second));
        Assert.Equal(4_000_000_000L, second);

        writer.Write(PersistenceCheckpoint.Empty);
        Assert.True(metadata.TryGetWorldTimeTicks(out long kept));
        Assert.Equal(4_000_000_000L, kept);
    }

    [Fact]
    public void Merge_KeepsTheNewerWorldTime_OrTheOlderWhenTheNewerHasNone()
    {
        PersistenceCheckpoint withRows = new([], [], [], [], [])
        {
            IsIncremental = true,
            RemovedCubeObjectIds = [new ObjectId(5)],
            WorldTimeTicks = 10
        };
        PersistenceCheckpoint newer = PersistenceCheckpoint.Empty with { IsIncremental = true, WorldTimeTicks = 20 };
        PersistenceCheckpoint none = PersistenceCheckpoint.Empty with { IsIncremental = true };

        Assert.Equal(20L, PersistenceCheckpoint.Merge(withRows, newer).WorldTimeTicks);
        Assert.Equal(10L, PersistenceCheckpoint.Merge(withRows, none).WorldTimeTicks);
        Assert.Null(PersistenceCheckpoint.Merge(none, none).WorldTimeTicks);

        // The empty-older shortcut keeps the same rule, and still returns newer itself when it can.
        PersistenceCheckpoint emptyOlder = PersistenceCheckpoint.Empty with { WorldTimeTicks = 30 };
        Assert.Same(newer, PersistenceCheckpoint.Merge(emptyOlder, newer));
        PersistenceCheckpoint fromOlder = PersistenceCheckpoint.Merge(emptyOlder, none);
        Assert.Equal(30L, fromOlder.WorldTimeTicks);
        Assert.True(fromOlder.IsIncremental);
        Assert.Same(none, PersistenceCheckpoint.Merge(PersistenceCheckpoint.Empty, none));
    }

    [Theory]
    [InlineData(null, 30d, false)]
    [InlineData("", 30d, false)]
    [InlineData("  ", 30d, false)]
    [InlineData("45", 45d, false)]
    [InlineData(" 2.5 ", 2.5d, false)]
    [InlineData("1440", 1440d, false)]
    [InlineData("0", 1d, true)]
    [InlineData("-3", 1d, true)]
    [InlineData("0.25", 1d, true)]
    [InlineData("100000", 1440d, true)]
    [InlineData("abc", 30d, true)]
    [InlineData("NaN", 30d, true)]
    [InlineData("Infinity", 30d, true)]
    [InlineData("12,5", 30d, true)]
    public void ResolveDayLengthMinutes_DefaultsClampsAndLogs(string? value, double expected, bool logs)
    {
        List<string> lines = [];
        double minutes = ServerBootstrap.ResolveDayLengthMinutes(value, lines.Add);

        Assert.Equal(expected, minutes);
        Assert.Equal(logs, lines.Count > 0);
        Assert.All(lines, line => Assert.Contains(ServerBootstrap.DayLengthEnvironmentVariable, line));
    }

    [Fact]
    public void MetadataRepository_RejectsACorruptWorldTime()
    {
        using IncrementalCheckpointTests.TempDatabase database = new();
        using (Microsoft.Data.Sqlite.SqliteConnection connection = database.Schema.CreateOpenConnection())
        using (Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE worlds SET world_time_ticks = -5 WHERE world_id = 1;";
            command.ExecuteNonQuery();
        }

        Assert.Throws<InvalidDataException>(() => new SqliteWorldMetadataRepository(database.Schema).TryGetWorldTimeTicks(out _));
    }

    private static WorldSessionRuntime CreatePersistentRuntime(IncrementalCheckpointTests.TempDatabase database)
    {
        return new WorldSessionRuntime(
            FloorSize,
            new Vector3(5f, 1.5f, 5f),
            new SceneEventHub(),
            playerProfiles: new SqlitePlayerProfileRepository(database.Schema),
            worldPersistence: new SqliteWorldPersistenceRepository(database.Schema),
            checkpointWriter: new SqlitePersistenceCheckpointWriter(database.Schema))
        {
            FullCheckpointInterval = int.MaxValue,
            WriteCheckpointOnDispose = false
        };
    }

    private static void Advance(WorldSessionRuntime runtime, ref ulong tick, int ticks)
    {
        for (int i = 0; i < ticks; i++)
        {
            runtime.AdvanceAuthoritativeTick(
                ++tick,
                SimulationClock.FixedDeltaSeconds,
                (PlayerCommandFrame?)null,
                PlayerSimulationTuning.AuthoritativeDefault);
        }
    }

    // CreateRuntimeHost's DREAM_DAY_LENGTH_MINUTES warning goes to the injected log, like
    // CreatePersistenceBundle's seed lines, and the clock gets the fallback day length.
    [Collection(Dream.Tests.Integration.ProcessEnvironmentCollection.Name)]
    public sealed class DayLengthLog
    {
        [Fact]
        public void CreateRuntimeHost_LogsAnInvalidDayLength_ThroughTheInjectedLog()
        {
            string? previous = Environment.GetEnvironmentVariable(ServerBootstrap.DayLengthEnvironmentVariable);
            try
            {
                Environment.SetEnvironmentVariable(ServerBootstrap.DayLengthEnvironmentVariable, "abc");
                List<string> lines = [];
                using RuntimeAuthoritativeSessionHost host = ServerBootstrap.CreateRuntimeHost(persistence: null, lines.Add);

                string line = Assert.Single(lines);
                Assert.Contains(ServerBootstrap.DayLengthEnvironmentVariable, line);
                Assert.Equal(WorldClockSettings.FromDayLengthMinutes(WorldClockSettings.DefaultDayLengthMinutes), host.Runtime.WorldClock.Settings);

                Environment.SetEnvironmentVariable(ServerBootstrap.DayLengthEnvironmentVariable, "2");
                lines.Clear();
                using RuntimeAuthoritativeSessionHost valid = ServerBootstrap.CreateRuntimeHost(persistence: null, lines.Add);
                Assert.Empty(lines);
                Assert.Equal(WorldClockSettings.FromDayLengthMinutes(2), valid.Runtime.WorldClock.Settings);
            }
            finally
            {
                Environment.SetEnvironmentVariable(ServerBootstrap.DayLengthEnvironmentVariable, previous);
            }
        }
    }
}
