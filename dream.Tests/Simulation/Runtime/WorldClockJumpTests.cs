using Dream.Server;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Determinism;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Simulation.Runtime;

// WorldSessionRuntime.JumpWorldClockForward: the one thing that moves WorldTicks - CurrentServerTick after the
// first tick. After it the offset is constant again, and the night population step follows the new time.
public sealed class WorldClockJumpTests
{
    [Fact]
    public void AfterAJumpToNight_TheOffsetIsConstantAgain_AndTheNightWolfApplies()
    {
        using WorldSessionRuntime runtime = CreateRuntime();
        for (ulong tick = 1; tick <= 20; tick++)
        {
            Tick(runtime, tick);
        }

        Assert.Equal(WorldDayPhase.Day, runtime.WorldClock.Phase);
        Assert.Single(runtime.Enemies.ActiveActors);
        long beforeOffset = runtime.WorldClock.WorldTicks - (long)runtime.CurrentServerTick;

        long forward = WorldTimeOfDay.ForwardTicks(runtime.WorldClock.Settings, runtime.WorldClock.WorldTicks, WorldTimeOfDay.Night);
        WorldClockAnchor anchor = runtime.JumpWorldClockForward(forward);

        Assert.Equal(runtime.WorldClock.CaptureAnchor(runtime.CurrentServerTick), anchor);
        Assert.Equal(22d / 24d, runtime.WorldClock.TimeOfDay01, precision: 9);
        long afterOffset = runtime.WorldClock.WorldTicks - (long)runtime.CurrentServerTick;
        Assert.Equal(beforeOffset + forward, afterOffset);

        for (ulong tick = 21; tick <= 60; tick++)
        {
            Tick(runtime, tick);
            Assert.Equal(afterOffset, runtime.WorldClock.WorldTicks - (long)runtime.CurrentServerTick);
        }

        Assert.Equal(WorldDayPhase.Night, runtime.WorldClock.Phase);
        Assert.Equal(2, runtime.Enemies.ActiveActors.Count);
    }

    // The negative: without a jump the same ticks leave the day wolf alone, so the test above is measuring the jump.
    [Fact]
    public void WithoutAJump_TheSameTicksStayDay_AndOneWolf()
    {
        using WorldSessionRuntime runtime = CreateRuntime();
        for (ulong tick = 1; tick <= 60; tick++)
        {
            Tick(runtime, tick);
        }

        Assert.Equal(WorldDayPhase.Day, runtime.WorldClock.Phase);
        Assert.Single(runtime.Enemies.ActiveActors);
    }

    private static WorldSessionRuntime CreateRuntime()
    {
        WorldSessionRuntime runtime = new(WorldDefaults.StartupFloorSize, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        runtime.AttachRemoteSession(new PlayerSessionId("session-a"), "session-a");
        return runtime;
    }

    private static void Tick(WorldSessionRuntime runtime, ulong tick)
    {
        runtime.AdvanceAuthoritativeTick(
            tick,
            SimulationClock.FixedDeltaSeconds,
            default(PlayerCommandFrame),
            PlayerSimulationTuning.AuthoritativeDefault);
    }
}
