using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Tests.Simulation.Runtime;

// NETR-3: every seat resolves primary use at the same server-side cadence (the attack-interval
// repeat gate, evaluated every tick from the seat's last command), and a starved seat repeats its
// last command for at most WorldSessionRuntime.MaxCommandRepeatTicks before it decays to idle.
public class SeatUseCadenceAndStarvationTests
{
    private const string PrimarySession = "session-a";
    private const string SecondarySession = "session-b";
    private const int TicksPerSecond = 64;

    private static readonly PlayerSimulationTuning Tuning = PlayerSimulationTuning.AuthoritativeDefault;

    // Both seats hold build for one second while sweeping their facing, so every triggered use
    // aims at a fresh cell. Before NETR-3 the secondary resolved a use on every received command
    // (64 per second, one cube per free cell); the primary seat was already gated.
    [Fact]
    public void HeldBuild_ForOneSecond_ResolvesAtTheAttackIntervalCadence_OnBothSeats()
    {
        using RuntimeAuthoritativeSessionHost host = CreateHost();
        WorldSessionRuntime runtime = host.Runtime;
        float interval = runtime.Player.Combatant.DerivedStats.AttackIntervalSeconds;
        int expectedUses = ExpectedGatedUses(interval, TicksPerSecond);
        Assert.Equal((int)Math.Ceiling(1.0 / interval), expectedUses);

        Dictionary<string, int> uses = new() { [PrimarySession] = 0, [SecondarySession] = 0 };
        Dictionary<string, int> placed = new() { [PrimarySession] = 0, [SecondarySession] = 0 };
        for (int tick = 1; tick <= TicksPerSecond; tick++)
        {
            float yaw = tick * MathF.Tau / TicksPerSecond;
            Vector3 facing = new Vector3(MathF.Sin(yaw), -0.35f, MathF.Cos(yaw)).Normalized();
            IReadOnlyList<AuthoritativeActionResult> results = host.AdvanceTick(
                (ulong)tick,
                SimulationClock.FixedDeltaSeconds,
                [
                    CreateCommand(PrimarySession, (ulong)tick, PlayerInteractionMode.Building, facing, use: true),
                    CreateCommand(SecondarySession, (ulong)tick, PlayerInteractionMode.Building, -facing with { Y = facing.Y }, use: true)
                ],
                Tuning,
                allowGameplaySimulation: false);

            foreach (AuthoritativeActionResult result in results)
            {
                uses[result.SessionId]++;
                if (result.Accepted && result.Payload is WorldMutationResult { Kind: WorldMutationKind.CubePlaced })
                {
                    placed[result.SessionId]++;
                }
            }
        }

        Assert.Equal(expectedUses, uses[PrimarySession]);
        Assert.Equal(expectedUses, uses[SecondarySession]);
        Assert.Equal(placed[PrimarySession], placed[SecondarySession]);
        Assert.Equal(expectedUses, placed[SecondarySession]);
    }

    // Held use, one command per tick: the secondary resolves on exactly the primary seat's ticks.
    [Fact]
    public void HeldUse_ForTwoSeconds_ResolvesOnTheSameTicksOnBothSeats()
    {
        using RuntimeAuthoritativeSessionHost host = CreateHost();
        float interval = host.Runtime.Player.Combatant.DerivedStats.AttackIntervalSeconds;
        int ticks = 2 * TicksPerSecond;
        List<int> primaryTicks = [];
        List<int> secondaryTicks = [];
        for (int tick = 1; tick <= ticks; tick++)
        {
            IReadOnlyList<AuthoritativeActionResult> results = host.AdvanceTick(
                (ulong)tick,
                SimulationClock.FixedDeltaSeconds,
                [
                    CreateCommand(PrimarySession, (ulong)tick, PlayerInteractionMode.Combat, Vector3.UnitZ, use: true),
                    CreateCommand(SecondarySession, (ulong)tick, PlayerInteractionMode.Combat, Vector3.UnitZ, use: true)
                ],
                Tuning,
                allowGameplaySimulation: false);
            foreach (AuthoritativeActionResult result in results)
            {
                (result.SessionId == PrimarySession ? primaryTicks : secondaryTicks).Add(tick);
            }
        }

        Assert.Equal(ExpectedGatedUses(interval, ticks), primaryTicks.Count);
        Assert.Equal(primaryTicks, secondaryTicks);
    }

    [Fact]
    public void SecondaryWalkingForward_StopsWithinMaxCommandRepeatTicks_AfterItsCommandsStop()
    {
        AssertWalkingSeatStopsAfterStarvation(walkingSession: SecondarySession, steadySession: PrimarySession);
    }

    [Fact]
    public void PrimarySeatWalkingForward_StopsWithinMaxCommandRepeatTicks_AfterItsCommandsStop()
    {
        AssertWalkingSeatStopsAfterStarvation(walkingSession: PrimarySession, steadySession: SecondarySession);
    }

    // A starved seat that was holding use stops using once it decays to idle.
    [Fact]
    public void StarvedSecondary_HoldingUse_StopsUsingAfterMaxCommandRepeatTicks()
    {
        using RuntimeAuthoritativeSessionHost host = CreateHost();
        int lastUseTick = 0;
        const int commandTicks = 3;
        for (int tick = 1; tick <= 6 * TicksPerSecond; tick++)
        {
            List<PlayerCommandFrame> commands = [CreateCommand(PrimarySession, (ulong)tick, PlayerInteractionMode.Combat, Vector3.UnitZ, use: false)];
            if (tick <= commandTicks)
            {
                commands.Add(CreateCommand(SecondarySession, (ulong)tick, PlayerInteractionMode.Combat, Vector3.UnitZ, use: true));
            }

            IReadOnlyList<AuthoritativeActionResult> results = host.AdvanceTick(
                (ulong)tick,
                SimulationClock.FixedDeltaSeconds,
                commands,
                Tuning,
                allowGameplaySimulation: false);
            if (results.Any(result => result.SessionId == SecondarySession))
            {
                lastUseTick = tick;
            }
        }

        Assert.InRange(lastUseTick, 1, commandTicks + WorldSessionRuntime.MaxCommandRepeatTicks);
    }

    // Walks a seat forward for 10 ticks, then stops sending its commands while the other seat keeps
    // sending idle ones. The walker keeps repeating its last command for MaxCommandRepeatTicks ticks
    // and then stands still; before NETR-3 it walked on until it disconnected.
    private static void AssertWalkingSeatStopsAfterStarvation(string walkingSession, string steadySession)
    {
        using RuntimeAuthoritativeSessionHost host = CreateHost();
        WorldSessionRuntime runtime = host.Runtime;
        runtime.Enemies.Clear();
        const int walkTicks = 10;
        const int totalTicks = 80;
        Dictionary<int, Vector3> positions = [];
        for (int tick = 1; tick <= totalTicks; tick++)
        {
            List<PlayerCommandFrame> commands =
            [
                CreateCommand(steadySession, (ulong)tick, PlayerInteractionMode.Building, Vector3.UnitZ, use: false)
            ];
            if (tick <= walkTicks)
            {
                commands.Add(CreateCommand(walkingSession, (ulong)tick, PlayerInteractionMode.Building, Vector3.UnitZ, use: false, forward: 1f));
            }

            host.AdvanceTick((ulong)tick, SimulationClock.FixedDeltaSeconds, commands, Tuning, allowGameplaySimulation: false);
            positions[tick] = Assert.Single(runtime.CaptureActorSnapshots(), actor => actor.SessionId == walkingSession).Position;
        }

        int lastRepeatTick = walkTicks + WorldSessionRuntime.MaxCommandRepeatTicks;
        Assert.True(
            HorizontalDistance(positions[lastRepeatTick - 1], positions[lastRepeatTick]) > 0.01f,
            "The starved seat should keep repeating its last command for MaxCommandRepeatTicks ticks.");
        for (int tick = lastRepeatTick + 2; tick <= totalTicks; tick++)
        {
            float moved = HorizontalDistance(positions[lastRepeatTick + 1], positions[tick]);
            Assert.True(
                moved < 1e-3f,
                $"The starved seat moved {moved} m by tick {tick}; it should stand still after {WorldSessionRuntime.MaxCommandRepeatTicks} repeated ticks.");
        }
    }

    // The attack-interval repeat gate, run on its own: uses triggered by a held button over the ticks.
    private static int ExpectedGatedUses(float interval, int ticks)
    {
        PlayerInteraction gate = new();
        int uses = 0;
        PlayerInteractionCommand held = new(PrimaryUsePressed: true, SelectBuildingModePressed: false, SelectCombatModePressed: false);
        for (int tick = 0; tick < ticks; tick++)
        {
            uses += gate.ShouldTriggerPrimaryUse(held, interval, SimulationClock.FixedDeltaSeconds) ? 1 : 0;
        }

        return uses;
    }

    private static float HorizontalDistance(Vector3 from, Vector3 to)
    {
        return new Vector2(to.X - from.X, to.Z - from.Z).Length;
    }

    private static RuntimeAuthoritativeSessionHost CreateHost()
    {
        WorldSessionRuntime runtime = new(16, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        RuntimeAuthoritativeSessionHost host = new(runtime);
        host.JoinSession(PrimarySession, PrimarySession);
        host.JoinSession(SecondarySession, SecondarySession);
        Assert.Equal(PrimarySession, runtime.PrimaryRemoteSessionId?.Value);
        return host;
    }

    private static PlayerCommandFrame CreateCommand(
        string sessionId,
        ulong sequence,
        PlayerInteractionMode mode,
        Vector3 facing,
        bool use,
        float forward = 0f)
    {
        return new PlayerCommandFrame(
            new PlayerSessionId(sessionId),
            sequence,
            new PlayerModeSwitchIntent(mode),
            new PlayerMoveIntent(forward, 0f),
            new PlayerLookIntent(0f, 0f, facing),
            PrimaryUsePressed: use);
    }
}
