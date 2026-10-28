using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;
using Dream.Client.Presentation;
using Dream.Multiplayer.Protocol;
using Dream.Server;
using Dream.Server.Persistence;
using Dream.Simulation.Persistence;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using static Dream.Tests.Integration.PersistentWorldSeedDeterminismIntegrationTests;

namespace Dream.Tests.Integration;

// Wave T1 tester: the world clock end to end after the five branches merged. The production
// persistent host (ServerBootstrap.CreateAuthoritativeHost over a temp SQLite world) serves real
// loopback clients that apply join-accepted like PlayScene. The client's time must match the
// server's at every snapshot tick, survive a shutdown and restart from the same database with no
// gap and no jump, and not move while the server is off.
public sealed class WaveT1ClockEndToEndTests(ITestOutputHelper output)
{
    private const double ToleranceTicks = 1.1d;

    [Fact]
    public async Task PersistentServer_ClientMatches_AndARestartResumesWithNoGapNoJump()
    {
        using TempDatabase database = new();
        long worldTicksAtShutdown;
        ulong lastSnapshotTickBeforeShutdown;
        double clientTicksBeforeShutdown;

        using (ServerPersistenceBundle fresh = database.OpenBundle())
        {
            Assert.Equal(0L, fresh.WorldTimeTicks);
        }

        SqliteConnection.ClearAllPools();
        await using (ClockServer server = await ClockServer.StartAsync(database.OpenBundle()))
        {
            Assert.Equal(0L, server.Runtime.WorldClock.WorldTicks);
            await server.TickAsync(30);
            await using WireLoopbackClient client = await server.Wire.ConnectAndJoinAsync("t1-clock");
            await server.TickAsync(60);
            ClientWorldClock clock = await server.AssertClientMatchesAsync(client, minimumComparedTicks: 50);

            lastSnapshotTickBeforeShutdown = server.Runtime.CurrentServerTick;
            clientTicksBeforeShutdown = ClientWorldTicks(clock, lastSnapshotTickBeforeShutdown);
            worldTicksAtShutdown = server.Runtime.WorldClock.WorldTicks;
            Assert.Equal(worldTicksAtShutdown, clientTicksBeforeShutdown, precision: 6);
            await server.Wire.DisconnectAsync(client);
        }

        // Time spent stopped is not world time.
        await Task.Delay(400);
        SqliteConnection.ClearAllPools();

        await using (ClockServer server = await ClockServer.StartAsync(database.OpenBundle()))
        {
            WorldSessionRuntime runtime = server.Runtime;
            Assert.Equal(worldTicksAtShutdown, runtime.WorldClock.WorldTicks);
            ulong restartServerTick = runtime.CurrentServerTick;
            output.WriteLine($"shutdown at world tick {worldTicksAtShutdown} (server tick {lastSnapshotTickBeforeShutdown}); restart at server tick {restartServerTick}");

            await using WireLoopbackClient client = await server.Wire.ConnectAndJoinAsync("t1-clock");
            await server.TickAsync(40);
            ClientWorldClock clock = await server.AssertClientMatchesAsync(client, minimumComparedTicks: 35);
            JoinAcceptedMessage join = Assert.Single(
                WorldTransferReassembler.Reassemble(client.Frames.Select(frame => frame.Decoded.Message)).OfType<JoinAcceptedMessage>());
            WorldClockAnchor anchor = Assert.NotNull(join.WorldClock);

            // No gap, no jump: every world tick after the restart is one server tick of this run.
            long ticksSinceRestart = (long)(anchor.ServerTick - restartServerTick);
            Assert.Equal(worldTicksAtShutdown + ticksSinceRestart, anchor.WorldTicks);
            Assert.Equal(runtime.WorldClock.Settings.TicksPerDay, anchor.TicksPerDay);
            Assert.Equal(runtime.WorldClock.Settings.StartOffsetTicks, anchor.StartOffsetTicks);


            // The client's world time continued from where the old client left it, by exactly the
            // ticks this run has executed.
            double clientNow = ClientWorldTicks(clock, runtime.CurrentServerTick);
            Assert.Equal(clientTicksBeforeShutdown + (runtime.CurrentServerTick - restartServerTick), clientNow, precision: 6);
        }
    }

    // DREAM_DAY_LENGTH_MINUTES=1 through ServerBootstrap: a 3,840-tick day. The world is resumed
    // shortly before dusk and run past the next morning, so the server crosses every phase bound
    // and midnight, and the client follows each one.
    [Collection(ProcessEnvironmentCollection.Name)]
    public sealed class ShortDay(ITestOutputHelper output)
    {
        [Fact]
        public async Task OneMinuteDay_TheServerCyclesThroughEveryPhase_AndTheClientFollows()
        {
            string? previous = Environment.GetEnvironmentVariable(ServerBootstrap.DayLengthEnvironmentVariable);
            using TempDatabase database = new();
            WorldClockSettings oneMinute = WorldClockSettings.FromDayLengthMinutes(1);
            Assert.Equal(3_840L, oneMinute.TicksPerDay);

            // Resume 150 ticks before dusk (17:00).
            long duskTicks = (long)Math.Round(WorldDayPhases.DuskStart01 * oneMinute.TicksPerDay) - oneMinute.StartOffsetTicks;
            long persisted = duskTicks - 150;
            using (ServerPersistenceBundle bundle = database.OpenBundle())
            {
                new SqlitePersistenceCheckpointWriter(bundle.Schema).Write(PersistenceCheckpoint.Empty with { WorldTimeTicks = persisted });
            }

            SqliteConnection.ClearAllPools();
            try
            {
                Environment.SetEnvironmentVariable(ServerBootstrap.DayLengthEnvironmentVariable, "1");
                await using ClockServer server = await ClockServer.StartAsync(database.OpenBundle());
                WorldSessionRuntime runtime = server.Runtime;
                Assert.Equal(oneMinute, runtime.WorldClock.Settings);
                Assert.Equal(persisted, runtime.WorldClock.WorldTicks);
                Assert.Equal(WorldDayPhase.Day, runtime.WorldClock.Phase);
                long startDay = runtime.WorldClock.DayNumber;

                List<(ulong Tick, WorldDayPhase From, WorldDayPhase To)> changes = [];
                runtime.WorldClock.PhaseChanged += (from, to) => changes.Add((runtime.CurrentServerTick, from, to));

                await using WireLoopbackClient client = await server.Wire.ConnectAndJoinAsync("t1-short-day");
                // 17:00 -> 07:00 is 14 h = 2,240 ticks; run 2,450 to land in the next day phase.
                await server.TickAsync(2_450);

                Assert.Equal(
                    [(WorldDayPhase.Day, WorldDayPhase.Dusk), (WorldDayPhase.Dusk, WorldDayPhase.Night), (WorldDayPhase.Night, WorldDayPhase.Dawn), (WorldDayPhase.Dawn, WorldDayPhase.Day)],
                    changes.Select(change => (change.From, change.To)).ToArray());
                Assert.Equal(startDay + 1, runtime.WorldClock.DayNumber);
                output.WriteLine(string.Join(", ", changes.Select(change => $"{change.From}->{change.To}@{change.Tick}")));

                ClientWorldClock clock = await server.AssertClientMatchesAsync(client, minimumComparedTicks: 2_400);

                // The client's phase (from its replicated time) follows the server's at every
                // snapshot tick, and it sees every phase and the day roll over.
                List<WorldDayPhase> clientPhases = [];
                int mismatches = 0;
                int floatBoundMismatches = 0;
                foreach (ulong tick in server.ComparedTicks(client))
                {
                    WorldDayPhase clientPhase = WorldDayPhases.At(clock.TimeOfDay01(tick));
                    WorldDayPhase anchorPhase = WorldDayPhases.At(clock.Anchor.TimeOfDay01At(tick));
                    WorldDayPhase serverPhase = server.ServerPhaseAt(tick);
                    if (anchorPhase != serverPhase)
                    {
                        mismatches++;
                        output.WriteLine($"tick {tick}: anchor (double) {anchorPhase}, server {serverPhase}");
                    }

                    if (clientPhase != serverPhase)
                    {
                        // ClientWorldClock.TimeOfDay01 returns float: on the exact bound tick the
                        // rounding may land just below it. Allowed only on that one tick.
                        floatBoundMismatches++;
                        Assert.Equal(serverPhase, WorldDayPhases.At(clock.TimeOfDay01(tick + 1)));
                        output.WriteLine($"tick {tick}: client (float) {clientPhase}, server {serverPhase}");
                    }

                    if (clientPhases.Count == 0 || clientPhases[^1] != clientPhase)
                    {
                        clientPhases.Add(clientPhase);
                    }
                }

                Assert.Equal(0, mismatches);
                Assert.True(floatBoundMismatches <= changes.Count, $"{floatBoundMismatches} float mismatches");
                Assert.Equal(
                    [WorldDayPhase.Day, WorldDayPhase.Dusk, WorldDayPhase.Night, WorldDayPhase.Dawn, WorldDayPhase.Day],
                    clientPhases);
                Assert.Equal(startDay + 1, clock.DayNumber(runtime.CurrentServerTick));
            }
            finally
            {
                Environment.SetEnvironmentVariable(ServerBootstrap.DayLengthEnvironmentVariable, previous);
            }
        }
    }

    private static double ClientWorldTicks(ClientWorldClock clock, double serverTick) =>
        clock.Anchor.WorldTicksAt(serverTick);

    internal sealed class ClockServer : IAsyncDisposable
    {
        private readonly Dictionary<ulong, long> _serverWorldTicks = [];

        private ClockServer(WireLoopbackServer wire)
        {
            Wire = wire;
            Record();
        }

        public WireLoopbackServer Wire { get; }

        public WorldSessionRuntime Runtime => Wire.Runtime;

        public static async Task<ClockServer> StartAsync(ServerPersistenceBundle bundle)
        {
            DedicatedServerHost host = ServerBootstrap.CreateAuthoritativeHost(bundle);
            return new ClockServer(await WireLoopbackServer.StartAsync(host));
        }

        public async Task TickAsync(int ticks)
        {
            for (int i = 0; i < ticks; i++)
            {
                await Wire.TickAsync();
                Record();
                Assert.True(await Wire.FlushAllAsync());
            }
        }

        public WorldDayPhase ServerPhaseAt(ulong tick)
        {
            long ticksPerDay = Runtime.WorldClock.Settings.TicksPerDay;
            long offset = Runtime.WorldClock.Settings.StartOffsetTicks;
            return WorldDayPhases.At((_serverWorldTicks[tick] + offset) % ticksPerDay / (double)ticksPerDay);
        }

        public IEnumerable<ulong> ComparedTicks(WireLoopbackClient client) =>
            client.Frames
                .Select(frame => frame.Decoded.Message)
                .OfType<ServerSnapshotMessage>()
                .Select(snapshot => snapshot.Snapshot.ServerTick)
                .Where(_serverWorldTicks.ContainsKey)
                .Distinct()
                .ToArray();

        // Replays the client's frames like PlayScene (join applies or clears the anchor) and
        // compares at each snapshot tick. Returns the client's clock after the replay.
        public async Task<ClientWorldClock> AssertClientMatchesAsync(WireLoopbackClient client, int minimumComparedTicks)
        {
            ulong lastTick = Runtime.CurrentServerTick;
            Assert.True(await Wire.FlushAllAsync());
            await client.WaitForSnapshotTickAsync(lastTick);
            Assert.Null(client.ReadFault);

            ClientWorldClock clock = new();
            long ticksPerDay = Runtime.WorldClock.Settings.TicksPerDay;
            long startOffset = Runtime.WorldClock.Settings.StartOffsetTicks;
            int compared = 0;
            foreach (IServerToClientMessage message in WorldTransferReassembler.Reassemble(client.Frames.Select(frame => frame.Decoded.Message)))
            {
                switch (message)
                {
                    case JoinAcceptedMessage join:
                        if (join.WorldClock is { } anchor)
                        {
                            clock.Apply(anchor);
                        }
                        else
                        {
                            clock.Clear();
                        }

                        Assert.True(clock.HasAnchor, "the join carried no world clock");
                        break;
                    case ServerSnapshotMessage snapshot when _serverWorldTicks.TryGetValue(snapshot.Snapshot.ServerTick, out long serverWorldTicks):
                        double serverTime = (double)((serverWorldTicks + startOffset) % ticksPerDay) / ticksPerDay;
                        double clientTime = clock.TimeOfDay01(snapshot.Snapshot.ServerTick);
                        double difference = Math.Abs(clientTime - serverTime);
                        double differenceTicks = Math.Min(difference, 1d - difference) * ticksPerDay;
                        Assert.True(
                            differenceTicks <= ToleranceTicks,
                            $"{client.SessionId} at tick {snapshot.Snapshot.ServerTick}: client {clientTime:F8}, server {serverTime:F8} ({differenceTicks:F3} ticks apart)");
                        Assert.Equal((serverWorldTicks + startOffset) / ticksPerDay, clock.DayNumber(snapshot.Snapshot.ServerTick));
                        compared++;
                        break;
                }
            }

            Assert.True(compared >= minimumComparedTicks, $"{client.SessionId}: only {compared} snapshot ticks compared");
            return clock;
        }

        public ValueTask DisposeAsync() => Wire.DisposeAsync();

        private void Record() => _serverWorldTicks[Runtime.CurrentServerTick] = Runtime.WorldClock.WorldTicks;
    }
}

// Tests that set a process-wide environment variable read by production bootstrap code. They run
// alone (after the parallel phase) so no other test's host sees the value.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection
{
    public const string Name = "ProcessEnvironment";
}
