using Dream.Simulation.World;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;
using Dream.Multiplayer.Protocol;
using Dream.Server;
using Dream.Server.Persistence;
using Dream.Simulation.Persistence;
using Dream.Simulation.Players;
using Dream.Tests.Load;
using Dream.Tests.Integration;

namespace Dream.Tests.Server.Persistence;

// SCALE-3 measurements, gated like the load harness (DREAM_LOAD_TEST=1, Release): the write
// queue's throughput on a burst of immediate writes, and how long the tick that applies a batch
// of deferred joins takes while the write queue has a backlog (the fracture-heavy case, where the
// old join path flushed the queue and read the database on the tick).
public sealed class PersistenceThroughputMeasurementTests(ITestOutputHelper output)
{
    private const int BurstWrites = 1000;
    private const int JoinRounds = 6;
    private const int JoinsPerRound = 4;
    private const int BacklogWritesPerRound = 150;

    [LoadFact]
    public void WriteBurst_Throughput()
    {
        using MeasurementDatabase database = new();
        SqliteSchemaBootstrapper schema = new(database.DatabasePath, database.SchemaPath);
        schema.EnsureSchema();
        SqlitePlayerProfileRepository profiles = new(schema);
        profiles.GetOrCreate(WorldSeeder.OwnerPlayerName);
        SqliteWorldPersistenceRepository world = new(schema);
        using PersistenceWriteQueue writes = PersistenceWriteQueue.Background();

        Stopwatch stopwatch = Stopwatch.StartNew();
        EnqueueMixedWrites(writes, profiles, world, BurstWrites, prefix: "burst");
        Assert.True(writes.Flush(TimeSpan.FromMinutes(2)));
        stopwatch.Stop();

        Assert.Equal(BurstWrites, writes.CompletedWrites);
        double perSecond = BurstWrites / stopwatch.Elapsed.TotalSeconds;
        string line = $"write burst: {BurstWrites} writes in {stopwatch.Elapsed.TotalMilliseconds:0.0} ms = {perSecond:0} writes/s";
        output.WriteLine(line);
        WriteArtifact("persistence-write-burst.txt", line);
    }

    [LoadFact]
    public void JoinTick_WithAWriteBacklog()
    {
        using MeasurementDatabase database = new();
        WorldSeeder.Seed(database.DatabasePath, database.SchemaPath, LoadScenario.Smoke8);
        SqliteConnection.ClearAllPools();
        using ServerPersistenceBundle bundle = ServerBootstrap.CreatePersistenceBundle(database.DatabasePath, database.SchemaPath);
        using DedicatedServerHost host = ServerBootstrap.CreateAuthoritativeHost(bundle);
        PersistenceWriteQueue writes = host.Runtime!.PersistenceWrites!;
        SqlitePlayerProfileRepository profiles = new(bundle.Schema);
        SqliteWorldPersistenceRepository world = new(bundle.Schema);

        for (int i = 0; i < 64; i++)
        {
            host.AdvanceTime(1d / 64d);
        }

        List<double> joinTicks = [];
        List<double> quietTicks = [];
        int depthAtJoin = 0;
        for (int round = 0; round < JoinRounds; round++)
        {
            EnqueueMixedWrites(writes, profiles, world, BacklogWritesPerRound, prefix: $"backlog-{round}");
            depthAtJoin = Math.Max(depthAtJoin, writes.PendingCount);
            Task[] joins = Enumerable.Range(0, JoinsPerRound)
                .Select(index => Task.Run(() =>
                {
                    string session = $"join-{round}-{index}";
                    host.HandleClientMessage($"connection-{session}", new ClientJoinRequestMessage(session, $"Name {session}"));
                }))
                .ToArray();
            Assert.True(Task.WaitAll(joins, TimeSpan.FromSeconds(30)));

            joinTicks.Add(TimeTick(host));
            List<PendingConnectionReply> replies = [];
            for (int i = 0; i < 8 && replies.Count < JoinsPerRound; i++)
            {
                host.DrainPendingJoinRepliesInto(replies);
                if (replies.Count < JoinsPerRound)
                {
                    quietTicks.Add(TimeTick(host));
                }
            }

            quietTicks.Add(TimeTick(host));
            _ = host.CreateSnapshotMessage();
        }

        Assert.True(writes.Flush(TimeSpan.FromMinutes(1)));
        string line =
            $"join tick ({JoinsPerRound} joins, backlog {BacklogWritesPerRound} writes, max depth {depthAtJoin}): " +
            $"mean {joinTicks.Average():0.0} ms, max {joinTicks.Max():0.0} ms [{string.Join(", ", joinTicks.Select(value => value.ToString("0.0")))}]; " +
            $"other ticks mean {quietTicks.Average():0.0} ms, max {quietTicks.Max():0.0} ms";
        output.WriteLine(line);
        WriteArtifact("persistence-join-tick.txt", line);
    }

    private static double TimeTick(DedicatedServerHost host)
    {
        long started = Stopwatch.GetTimestamp();
        host.AdvanceTime(1d / 64d);
        return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    // The shapes a fracture and a profile change queue: a profile upsert, a placed-cube upsert and
    // a placed-cube delete, in rotation.
    private static void EnqueueMixedWrites(
        PersistenceWriteQueue writes,
        SqlitePlayerProfileRepository profiles,
        SqliteWorldPersistenceRepository world,
        int count,
        string prefix)
    {
        DateTime now = DateTime.UtcNow;
        for (int i = 0; i < count; i++)
        {
            int id = 500_000 + (Math.Abs(prefix.GetHashCode()) % 1000) * 1000 + i;
            switch (i % 3)
            {
                case 0:
                {
                    PersistedPlayerProfile profile = new(
                        $"{prefix}-{i}",
                        $"{prefix}-{i}",
                        PlayerInteractionMode.Building,
                        new PersistedCreatureStats(100, 100, 10, 10, 10, 10, 6f, 2f, 1, i),
                        now,
                        now);
                    writes.Enqueue(() => profiles.Save(profile));
                    break;
                }
                case 1:
                {
                    PersistedPlacedCube cube = new(
                        new ObjectId(id),
                        WorldSeeder.OwnerPlayerName,
                        new OpenTK.Mathematics.Vector3(40 + i % 10, 30, 40),
                        Dream.Simulation.World.Geometry.GeometryShapeKind.SolidCube,
                        Dream.Simulation.World.Geometry.GeometryMaterialId.Stone,
                        5,
                        5,
                        now,
                        now,
                        null);
                    writes.Enqueue(() => world.SavePlacedCube(cube));
                    break;
                }
                default:
                    writes.Enqueue(() => world.DeletePlacedCube(new ObjectId(id - 1)));
                    break;
            }
        }
    }

    private static void WriteArtifact(string name, string line)
    {
        string directory = Path.Combine(RepoRoot.Find(), "artifacts", "load");
        Directory.CreateDirectory(directory);
        File.AppendAllText(Path.Combine(directory, name), $"{DateTime.UtcNow:O} {line}{Environment.NewLine}");
    }

    private sealed class MeasurementDatabase : IDisposable
    {
        private readonly string _rootDirectory;

        public MeasurementDatabase()
        {
            _rootDirectory = Path.Combine(Path.GetTempPath(), $"dream-persistence-measure-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_rootDirectory);
            SchemaPath = Path.Combine(RepoRoot.Find(), "dream", "config", "player-profiles.init.sql");
            DatabasePath = Path.Combine(_rootDirectory, "persistence.sqlite");
        }

        public string DatabasePath { get; }

        public string SchemaPath { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(_rootDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
