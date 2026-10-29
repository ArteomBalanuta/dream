using Dream.Multiplayer.Protocol;
using Dream.Server;
using Dream.Simulation.World;
using Microsoft.Data.Sqlite;
using Xunit;
using static Dream.Tests.Integration.PersistentWorldSeedDeterminismIntegrationTests;

namespace Dream.Tests.Integration;

// DREAM_TIME_OF_DAY through ServerBootstrap: the clock the server starts with, the clock anchor in
// the join-accepted message, and the persisted world time a later run resumes from.
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class WorldTimeOfDayBootstrapTests
{
    [Fact]
    public async Task Midnight_StartsTheServerAtNight_AndTheJoinAnchorCarriesIt()
    {
        string? previous = Environment.GetEnvironmentVariable(ServerBootstrap.TimeOfDayEnvironmentVariable);
        using TempDatabase database = new();
        try
        {
            Environment.SetEnvironmentVariable(ServerBootstrap.TimeOfDayEnvironmentVariable, "midnight");
            await using WaveT1ClockEndToEndTests.ClockServer server = await WaveT1ClockEndToEndTests.ClockServer.StartAsync(database.OpenBundle());
            WorldClock clock = server.Runtime.WorldClock;
            long midnightTicks = clock.Settings.TicksPerDay - clock.Settings.StartOffsetTicks;

            Assert.Equal(0d, clock.TimeOfDay01);
            Assert.Equal(WorldDayPhase.Night, clock.Phase);
            Assert.Equal(midnightTicks, clock.WorldTicks);

            await using WireLoopbackClient client = await server.Wire.ConnectAndJoinAsync("time-of-day");
            await server.TickAsync(5);
            JoinAcceptedMessage join = Assert.Single(
                WorldTransferReassembler.Reassemble(client.Frames.Select(frame => frame.Decoded.Message)).OfType<JoinAcceptedMessage>());
            WorldClockAnchor anchor = Assert.NotNull(join.WorldClock);

            Assert.Equal(clock.Settings.TicksPerDay, anchor.TicksPerDay);
            Assert.Equal(clock.Settings.StartOffsetTicks, anchor.StartOffsetTicks);
            // The anchor is the server's clock at the join tick: world ticks minus server tick is constant.
            Assert.Equal(clock.WorldTicks - (long)server.Runtime.CurrentServerTick, anchor.WorldTicks - (long)anchor.ServerTick);
            long ticksSinceStart = anchor.WorldTicks - midnightTicks;
            Assert.InRange(ticksSinceStart, 0L, 5L);
            double anchorTime = (anchor.WorldTicks + anchor.StartOffsetTicks) % anchor.TicksPerDay / (double)anchor.TicksPerDay;
            Assert.Equal(ticksSinceStart / (double)anchor.TicksPerDay, anchorTime, precision: 12);
            Assert.Equal(WorldDayPhase.Night, WorldDayPhases.At(anchorTime));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ServerBootstrap.TimeOfDayEnvironmentVariable, previous);
        }
    }

    [Fact]
    public async Task TheChosenTimeIsPersisted_AndARestartWithoutTheVariableContinuesFromIt()
    {
        string? previous = Environment.GetEnvironmentVariable(ServerBootstrap.TimeOfDayEnvironmentVariable);
        using TempDatabase database = new();
        long worldTicksAtShutdown;
        try
        {
            Environment.SetEnvironmentVariable(ServerBootstrap.TimeOfDayEnvironmentVariable, "22:30");
            await using (WaveT1ClockEndToEndTests.ClockServer server = await WaveT1ClockEndToEndTests.ClockServer.StartAsync(database.OpenBundle()))
            {
                await server.TickAsync(20);
                worldTicksAtShutdown = server.Runtime.WorldClock.WorldTicks;
                Assert.Equal(
                    22.5 / 24d + 20d / server.Runtime.WorldClock.Settings.TicksPerDay,
                    server.Runtime.WorldClock.TimeOfDay01,
                    precision: 9);
            }

            SqliteConnection.ClearAllPools();
            Environment.SetEnvironmentVariable(ServerBootstrap.TimeOfDayEnvironmentVariable, null);
            await using WaveT1ClockEndToEndTests.ClockServer restarted = await WaveT1ClockEndToEndTests.ClockServer.StartAsync(database.OpenBundle());
            Assert.Equal(worldTicksAtShutdown, restarted.Runtime.WorldClock.WorldTicks);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ServerBootstrap.TimeOfDayEnvironmentVariable, previous);
        }
    }
}
