using Dream.Server;
using Dream.Server.Persistence;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Xunit;

namespace Dream.Tests.BugHunt;

// BH-LIFE-4: a start that aborts after RuntimeAuthoritativeSessionHost.CreateDefault has returned
// used to dispose the host the ordinary way, which writes a final full checkpoint -- world clock
// and tombstones included -- over a world that was never served. The codebase states the rule
// itself: WorldSessionRuntime.DisposeWithoutFinalCheckpoint, "the world was never served, so no
// final checkpoint is written over what the database holds".
//
// The abort is injected through the log ServerBootstrap.CreateRuntimeHost already accepts: with
// DREAM_TIME_OF_DAY set, ResolveStartWorldTicks writes one line, which here throws. That lands the
// failure between CreateDefault and ConfigureWorldClock -- the worst case, where the runtime's
// clock is still 0 while the database holds the real world time.
[Collection(Dream.Tests.Integration.ProcessEnvironmentCollection.Name)]
public sealed class LifeStartFailureCheckpointTests
{
    [Fact]
    public void AStartThatFailsAfterTheRuntimeExists_LeavesTheStoredWorldClockAlone()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"dream-bh-life4-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string databasePath = Path.Combine(directory, "world.sqlite");
        string schemaPath = Path.Combine(Dream.Tests.Load.LoadPaths.FindRepoRoot(), "dream", "config", "player-profiles.init.sql");
        string? previousTimeOfDay = Environment.GetEnvironmentVariable(ServerBootstrap.TimeOfDayEnvironmentVariable);
        try
        {
            // A world that really was served, so the database holds a non-zero clock.
            long ticksRun = 0;
            using (DedicatedServerHost served = ServerBootstrap.CreateAuthoritativeHost(
                ServerBootstrap.CreatePersistenceBundle(databasePath, schemaPath)))
            {
                for (int i = 0; i < 50; i++)
                {
                    ticksRun += served.AdvanceTime(SimulationClock.FixedDeltaSeconds * 8);
                }
            }

            long stored;
            using (ServerPersistenceBundle check = ServerBootstrap.CreatePersistenceBundle(databasePath, schemaPath))
            {
                stored = check.WorldTimeTicks;
            }

            Assert.True(stored > 0, $"the served world stored {stored} world ticks after {ticksRun} ticks");

            // The start that aborts after the runtime exists.
            Environment.SetEnvironmentVariable(ServerBootstrap.TimeOfDayEnvironmentVariable, "noon");
            ServerPersistenceBundle aborted = ServerBootstrap.CreatePersistenceBundle(databasePath, schemaPath);
            Assert.Throws<InvalidOperationException>(() => ServerBootstrap.CreateRuntimeHost(
                aborted,
                _ => throw new InvalidOperationException("start aborted")));
            Environment.SetEnvironmentVariable(ServerBootstrap.TimeOfDayEnvironmentVariable, null);

            // Reopening at all proves the failed start released the world lease and the database;
            // the clock proves it wrote no checkpoint over them.
            using (ServerPersistenceBundle after = ServerBootstrap.CreatePersistenceBundle(databasePath, schemaPath))
            {
                Assert.Equal(stored, after.WorldTimeTicks);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(ServerBootstrap.TimeOfDayEnvironmentVariable, previousTimeOfDay);
            TryDelete(directory);
        }
    }

    // The mechanism the fix leans on: skipping the final checkpoint still releases everything,
    // because WorldSessionRuntime.Dispose is idempotent, so the host dispose that follows it is
    // free to run and release the owned bundle.
    [Fact]
    public void SkippingTheFinalCheckpoint_StillLetsTheHostDisposeReleaseTheBundle()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"dream-bh-life4b-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string databasePath = Path.Combine(directory, "world.sqlite");
        string schemaPath = Path.Combine(Dream.Tests.Load.LoadPaths.FindRepoRoot(), "dream", "config", "player-profiles.init.sql");
        try
        {
            RuntimeAuthoritativeSessionHost host = ServerBootstrap.CreateRuntimeHost(
                ServerBootstrap.CreatePersistenceBundle(databasePath, schemaPath));

            // Exactly what the start-failure catch now does, in that order.
            host.Runtime.DisposeWithoutFinalCheckpoint();
            host.Dispose();

            // The lease and the database are free again.
            using ServerPersistenceBundle reopened = ServerBootstrap.CreatePersistenceBundle(databasePath, schemaPath);
            Assert.Equal(0L, reopened.WorldTimeTicks);
        }
        finally
        {
            TryDelete(directory);
        }
    }

    private static void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
