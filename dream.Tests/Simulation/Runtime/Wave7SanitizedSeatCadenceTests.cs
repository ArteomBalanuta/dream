using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Tests.Simulation.Runtime;

// Wave 7 tester: NETR-6 sanitizes command frames at the server boundary and NETR-3 repeats a seat's
// last command every tick (decaying to idle after MaxCommandRepeatTicks). These run hostile frames
// through PlayerCommandHandler.SanitizeCommandFrame, as the wire path does, into the tick loop.
public class Wave7SanitizedSeatCadenceTests
{
    private const string PrimarySession = "session-a";
    private const string SecondarySession = "session-b";
    private const int TicksPerSecond = 64;

    private static readonly PlayerSimulationTuning Tuning = PlayerSimulationTuning.AuthoritativeDefault;

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(3e38f)]
    [InlineData(float.PositiveInfinity)]
    public void HostileAxesAndFacing_WhileHoldingUse_KeepTheGatedCadenceOnBothSeats(float hostileAxis)
    {
        using RuntimeAuthoritativeSessionHost host = CreateHost();
        float interval = host.Runtime.Player.Combatant.DerivedStats.AttackIntervalSeconds;
        int ticks = 2 * TicksPerSecond;
        List<int> primaryUses = [];
        List<int> secondaryUses = [];
        for (int tick = 1; tick <= ticks; tick++)
        {
            // Every other frame also carries a NaN facing, which the sanitizer drops to null.
            Vector3 facing = tick % 2 == 0 ? new Vector3(float.NaN, 0f, 1f) : Vector3.UnitZ;
            IReadOnlyList<AuthoritativeActionResult> results = host.AdvanceTick(
                (ulong)tick,
                SimulationClock.FixedDeltaSeconds,
                [
                    Sanitized(CreateCommand(PrimarySession, (ulong)tick, PlayerInteractionMode.Combat, facing, use: true, forward: hostileAxis)),
                    Sanitized(CreateCommand(SecondarySession, (ulong)tick, PlayerInteractionMode.Combat, facing, use: true, forward: hostileAxis))
                ],
                Tuning,
                allowGameplaySimulation: false);
            foreach (AuthoritativeActionResult result in results)
            {
                (result.SessionId == PrimarySession ? primaryUses : secondaryUses).Add(tick);
            }

            foreach (ActorSnapshot actor in host.Runtime.CaptureActorSnapshots().Where(actor => actor.SessionId != null))
            {
                Assert.True(
                    float.IsFinite(actor.Position.X) && float.IsFinite(actor.Position.Y) && float.IsFinite(actor.Position.Z),
                    $"{actor.SessionId} position {actor.Position} at tick {tick}");
                Assert.True(
                    float.IsFinite(actor.Facing.X) && float.IsFinite(actor.Facing.Y) && float.IsFinite(actor.Facing.Z),
                    $"{actor.SessionId} facing {actor.Facing} at tick {tick}");
            }
        }

        Assert.Equal(ExpectedGatedUses(interval, ticks), primaryUses.Count);
        Assert.Equal(primaryUses, secondaryUses);
    }

    [Fact]
    public void HugeAxis_IsClampedToTheSameWalkAsAFullAxis()
    {
        Vector3 clean = WalkSecondary(forward: 1f);
        Vector3 hostile = WalkSecondary(forward: 3e38f);

        Assert.True((clean - hostile).Length < 1e-4f, $"clean {clean} vs clamped {hostile}");
        Assert.True(clean.Z > 5.5f, $"the seat should have walked forward: {clean}");
    }

    // The secondary holds use, starves past MaxCommandRepeatTicks, then resumes still holding use.
    // Decaying to idle released the button, so the first fresh command uses at once, and walking
    // resumes on that same tick (the repeat counter restarted).
    //
    // Character-gaits wave: PlayerMotor ramps, so a seat whose command decays to idle brakes rather
    // than halting on the next tick, and "standing still at the end of the starvation" was no
    // longer true 20 ticks in (it measured 0.0526 m of coast). The starve is lengthened to 32 ticks
    // so the premise holds again, rather than relaxing what it asserts. Derivation at 64 Hz,
    // MoveSpeed 4.3 m/s, DefaultGroundAcceleration 18 m/s^2, DefaultGroundDeceleration 24 m/s^2:
    //   the repeated command is the same walk, so the seat is at cruise after 4.3 / 18 = 15.3
    //   ticks and still at cruise when the repeats run out at tick firstTicks + 16;
    //   braking then takes 4.3 / 24 = 0.1792 s = 11.5 ticks -> at rest within 12 ticks.
    // So the starve needs at least 16 + 12 = 28 ticks for the seat to be at rest before it resumes;
    // 32 leaves a margin. The resumed walk is measured 8 ticks out instead of 4, because a ramping
    // body covers 0.5 * 18 * (8/64)^2 = 0.141 m in 8 ticks against 0.05 m asked for, where 4 ticks
    // would have been 0.035 m and the old instant motor's 4 ticks were 0.375 m.
    [Theory]
    [InlineData(SecondarySession, PrimarySession)]
    [InlineData(PrimarySession, SecondarySession)]
    public void StarvedSeat_ResumingWithUseHeld_UsesImmediately_AndWalksAgain(string seat, string other)
    {
        using RuntimeAuthoritativeSessionHost host = CreateHost();
        host.Runtime.Enemies.Clear();
        const int firstTicks = 4;
        const int starvedTicks = 32;
        int resumeTick = firstTicks + starvedTicks + 1;
        List<int> useTicks = [];
        Dictionary<int, Vector3> positions = [];
        for (int tick = 1; tick <= resumeTick + 8; tick++)
        {
            List<PlayerCommandFrame> commands =
            [
                Sanitized(CreateCommand(other, (ulong)tick, PlayerInteractionMode.Combat, Vector3.UnitZ, use: false))
            ];
            if (tick <= firstTicks || tick >= resumeTick)
            {
                commands.Add(Sanitized(CreateCommand(seat, (ulong)tick, PlayerInteractionMode.Combat, Vector3.UnitZ, use: true, forward: 1f)));
            }

            IReadOnlyList<AuthoritativeActionResult> results = host.AdvanceTick(
                (ulong)tick,
                SimulationClock.FixedDeltaSeconds,
                commands,
                Tuning,
                allowGameplaySimulation: false);
            if (results.Any(result => result.SessionId == seat))
            {
                useTicks.Add(tick);
            }

            positions[tick] = Assert.Single(host.Runtime.CaptureActorSnapshots(), actor => actor.SessionId == seat).Position;
        }

        Assert.Equal(1, useTicks[0]);
        Assert.Contains(resumeTick, useTicks);
        Assert.DoesNotContain(useTicks, tick => tick > firstTicks + WorldSessionRuntime.MaxCommandRepeatTicks && tick < resumeTick);

        // Exactly still at the end of the starvation (it has had its full braking window), walking
        // again from the resume tick on.
        Assert.Equal(positions[resumeTick - 3].X, positions[resumeTick - 1].X);
        Assert.Equal(positions[resumeTick - 3].Z, positions[resumeTick - 1].Z);
        Assert.True(HorizontalDistance(positions[resumeTick - 1], positions[resumeTick + 8]) > 0.05f);
    }

    // Holding use while starved within the repeat window resolves the use from the repeated frame,
    // so the result carries the last command's sequence again; nothing else is keyed by it.
    [Fact]
    public void RepeatedFrameUse_ReusesTheLastCommandSequence_AsItsActionSequence()
    {
        using RuntimeAuthoritativeSessionHost host = CreateHost();
        List<AuthoritativeActionResult> secondaryResults = [];
        for (int tick = 1; tick <= 3 * TicksPerSecond; tick++)
        {
            List<PlayerCommandFrame> commands =
            [
                CreateCommand(PrimarySession, (ulong)tick, PlayerInteractionMode.Combat, Vector3.UnitZ, use: false)
            ];
            // Only the first command is ever sent for the secondary.
            if (tick == 1)
            {
                commands.Add(CreateCommand(SecondarySession, 1, PlayerInteractionMode.Combat, Vector3.UnitZ, use: true));
            }

            secondaryResults.AddRange(host.AdvanceTick(
                (ulong)tick,
                SimulationClock.FixedDeltaSeconds,
                commands,
                Tuning,
                allowGameplaySimulation: false).Where(result => result.SessionId == SecondarySession));
        }

        Assert.NotEmpty(secondaryResults);
        Assert.All(secondaryResults, result => Assert.Equal(1UL, result.ActionSequence));
        float interval = host.Runtime.Player.Combatant.DerivedStats.AttackIntervalSeconds;
        Assert.Equal(ExpectedGatedUses(interval, 1 + WorldSessionRuntime.MaxCommandRepeatTicks), secondaryResults.Count);
    }

    private static Vector3 WalkSecondary(float forward)
    {
        using RuntimeAuthoritativeSessionHost host = CreateHost();
        host.Runtime.Enemies.Clear();
        for (int tick = 1; tick <= 20; tick++)
        {
            host.AdvanceTick(
                (ulong)tick,
                SimulationClock.FixedDeltaSeconds,
                [
                    Sanitized(CreateCommand(PrimarySession, (ulong)tick, PlayerInteractionMode.Building, Vector3.UnitZ, use: false)),
                    Sanitized(CreateCommand(SecondarySession, (ulong)tick, PlayerInteractionMode.Building, Vector3.UnitZ, use: false, forward: forward))
                ],
                Tuning,
                allowGameplaySimulation: false);
        }

        return Assert.Single(host.Runtime.CaptureActorSnapshots(), actor => actor.SessionId == SecondarySession).Position;
    }

    private static PlayerCommandFrame Sanitized(PlayerCommandFrame frame)
    {
        return PlayerCommandHandler.SanitizeCommandFrame(frame);
    }

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
