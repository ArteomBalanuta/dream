using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using Xunit;
using Xunit.Abstractions;
using Dream.Server;
using Dream.Server.Persistence;
using Dream.Simulation.Persistence;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;

namespace Dream.Tests.Server.Persistence;

// Wave 10 tester, ARCH-2: PersistenceWriteQueue (Simulation) no longer references the SQLite batch;
// SqliteWriteBatch installs itself as WriteBatchFactory from a [ModuleInitializer]. If that did not
// run in a real process the factory would stay the pass-through and the wave-8 group commit would
// silently vanish (one autocommit per write). Checked two ways:
//  - in a fresh AssemblyLoadContext, where dream.dll is loaded for the first time exactly as a
//    process start loads it, and nothing but ServerBootstrap is called;
//  - through ServerBootstrap.CreateRuntimeHost, with a write burst on the runtime's own queue.
public sealed class Wave10GroupCommitRegistrationTests(ITestOutputHelper output)
{
    [Fact]
    public void FreshLoadOfTheDreamAssembly_InstallsTheSqliteBatch_BeforeAnyQueueRuns()
    {
        string dreamPath = typeof(ServerBootstrap).Assembly.Location;
        AssemblyLoadContext context = new($"wave10-module-init-{Guid.NewGuid():N}", isCollectible: true);
        try
        {
            Assembly fresh = context.LoadFromAssemblyPath(dreamPath);
            Assert.NotSame(typeof(ServerBootstrap).Assembly, fresh);

            // The first thing a dedicated server does with the assembly: ServerBootstrap.
            Type bootstrap = fresh.GetType("Dream.Server.ServerBootstrap", throwOnError: true)!;
            Assert.NotNull(bootstrap.GetMethod("CreateRuntimePersistencePorts"));

            Type queue = fresh.GetType("Dream.Simulation.Persistence.PersistenceWriteQueue", throwOnError: true)!;
            PropertyInfo factoryProperty = queue.GetProperty("WriteBatchFactory", BindingFlags.Static | BindingFlags.NonPublic)!;
            Delegate factory = (Delegate)factoryProperty.GetValue(null)!;
            object batch = factory.DynamicInvoke()!;
            try
            {
                Assert.Equal("Dream.Server.Persistence.SqliteWriteBatch", batch.GetType().FullName);
            }
            finally
            {
                ((IDisposable)batch).Dispose();
            }
        }
        finally
        {
            context.Unload();
        }
    }

    [Fact]
    public void WriteBurst_ThroughServerBootstrapCreateRuntimeHost_IsGroupCommitted()
    {
        Assert.Equal("SqliteWriteBatch", PersistenceWriteQueue.WriteBatchFactory().GetType().Name);

        string directory = Path.Combine(Path.GetTempPath(), $"dream-w10-groupcommit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string schemaPath = Path.Combine(Dream.Tests.Load.LoadPaths.FindRepoRoot(), "dream", "config", "player-profiles.init.sql");
        try
        {
            ServerPersistenceBundle bundle = ServerBootstrap.CreatePersistenceBundle(Path.Combine(directory, "world.sqlite"), schemaPath);
            using RuntimeAuthoritativeSessionHost host = ServerBootstrap.CreateRuntimeHost(bundle);
            PersistenceWriteQueue writes = host.Runtime.PersistenceWrites ?? throw new InvalidOperationException("no write queue");
            Assert.True(writes.Flush(TimeSpan.FromSeconds(30)));
            long groupsBefore = writes.CommittedGroups;
            long completedBefore = writes.CompletedWrites;

            const int burst = 2000;
            using ManualResetEventSlim gate = new(initialState: false);
            writes.Enqueue(() => gate.Wait(TimeSpan.FromSeconds(30)));
            for (int i = 0; i < burst; i++)
            {
                PersistedPlayerProfile profile = PersistenceGroupCommitTests.Profile($"burst-{i:D4}", experience: i);
                writes.Enqueue(() => bundle.PlayerProfiles.Save(profile));
            }

            Stopwatch stopwatch = Stopwatch.StartNew();
            gate.Set();
            Assert.True(writes.Flush(TimeSpan.FromSeconds(120)));
            stopwatch.Stop();

            long groups = writes.CommittedGroups - groupsBefore;
            Assert.Equal(burst + 1, writes.CompletedWrites - completedBefore);
            Assert.Equal(0, writes.FailedWrites);
            output.WriteLine($"{burst} profile saves through CreateRuntimeHost's queue: {stopwatch.ElapsedMilliseconds} ms, {groups} committed groups ({burst / Math.Max(1e-3, stopwatch.Elapsed.TotalSeconds):N0} writes/s)");
            // Pass-through commits no groups at all; group commit takes a handful.
            int expectedGroups = (burst + PersistenceWriteQueue.MaxWritesPerGroup - 1) / PersistenceWriteQueue.MaxWritesPerGroup;
            Assert.InRange(groups, expectedGroups, expectedGroups + 2);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
