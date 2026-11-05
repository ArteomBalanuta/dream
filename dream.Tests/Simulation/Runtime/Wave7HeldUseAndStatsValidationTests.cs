using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Tests.Simulation.Runtime;

// Wave 7 fix loop 1.
// S1: a held use the server synthesizes from an already-accepted command frame does not go through
// the client action-sequence gate, so a seat that triggers twice from one repeated command frame
// (short attack interval, stalled client) resolves both uses instead of losing the second as stale.
// S5: a client's PlayerStatsUpdateIntent is held to the stat editor's rules.
public class Wave7HeldUseAndStatsValidationTests
{
    private const string PrimarySession = "session-a";
    private const string SecondarySession = "session-b";
    private const ulong StatsActionSequence = 1;
    private const ulong HeldCommandSequence = 10;

    private static readonly PlayerSimulationTuning Tuning = PlayerSimulationTuning.AuthoritativeDefault;

    // Fastest attack speed: 0.2 s, about 13 ticks, shorter than the 16 repeat ticks of a starved seat.
    [Theory]
    [InlineData(PrimarySession)]
    [InlineData(SecondarySession)]
    public void StalledSeat_WithShortAttackInterval_ResolvesEveryUseFromOneCommandFrame(string heldSession)
    {
        using RuntimeAuthoritativeSessionHost host = CreateHost();
        string otherSession = heldSession == PrimarySession ? SecondarySession : PrimarySession;
        SetMaxAttackSpeed(host, heldSession);
        float interval = 1f / PlayerCombatStats.MaxAttackSpeed;
        Assert.True(
            interval * SimulationClock.AuthoritativeTicksPerSecond < WorldSessionRuntime.MaxCommandRepeatTicks,
            "The attack interval must be shorter than the starvation repeat window for this scenario.");

        // The held seat sends one command frame, then stalls; the other seat keeps sending idle frames.
        List<AuthoritativeActionResult> heldResults = [];
        int totalTicks = WorldSessionRuntime.MaxCommandRepeatTicks + 8;
        for (int tick = 1; tick <= totalTicks; tick++)
        {
            List<PlayerCommandFrame> commands =
            [
                CreateCommand(otherSession, HeldCommandSequence + (ulong)tick, use: false)
            ];
            if (tick == 1)
            {
                commands.Add(CreateCommand(heldSession, HeldCommandSequence, use: true));
            }

            IReadOnlyList<AuthoritativeActionResult> results = host.AdvanceTick(
                (ulong)tick,
                SimulationClock.FixedDeltaSeconds,
                commands,
                Tuning,
                allowGameplaySimulation: false);
            heldResults.AddRange(results.Where(result => result.SessionId == heldSession));
        }

        Assert.True(heldResults.Count >= 2, $"Expected at least two uses from one held command, got {heldResults.Count}.");
        Assert.All(heldResults, result =>
        {
            Assert.True(result.Accepted, $"Use at tick {result.ServerTick} was rejected: {result.Rejection?.Reason}");
            Assert.Null(result.Rejection);
            Assert.Equal(HeldCommandSequence, result.ActionSequence);
        });
        Assert.Equal(heldResults.Count, heldResults.Select(result => result.ServerTick).Distinct().Count());
    }

    // The client's ClientActionSequenceTracker hands out the next action sequence just past the last
    // command it sent, so the next command frame can carry the same number as an action the server
    // just accepted. Its held use must still resolve.
    [Theory]
    [InlineData(PrimarySession)]
    [InlineData(SecondarySession)]
    public void HeldUse_OnACommandSequenceEqualToAnAcceptedClientAction_IsNotRejectedAsStale(string heldSession)
    {
        using RuntimeAuthoritativeSessionHost host = CreateHost();
        string otherSession = heldSession == PrimarySession ? SecondarySession : PrimarySession;
        const ulong sharedSequence = 5;
        AuthoritativeActionResult stats = host.ProcessAction(new PlayerActionMessage(
            new PlayerSessionId(heldSession),
            sharedSequence,
            new PlayerStatsUpdateIntent(new PlayerCombatStatsSnapshot(12, 2.5f, 11))));
        Assert.True(stats.Accepted);

        IReadOnlyList<AuthoritativeActionResult> results = host.AdvanceTick(
            1,
            SimulationClock.FixedDeltaSeconds,
            [
                CreateCommand(heldSession, sharedSequence, use: true),
                CreateCommand(otherSession, 1, use: false)
            ],
            Tuning,
            allowGameplaySimulation: false);

        AuthoritativeActionResult use = Assert.Single(results, result => result.SessionId == heldSession);
        Assert.True(use.Accepted, use.Rejection?.Reason);
    }

    // Client actions keep their own gate: a synthesized use still advances the accepted action
    // sequence, so a client action at or below that command sequence stays stale.
    [Fact]
    public void ClientAction_AtOrBelowAUsedCommandSequence_IsStillRejectedAsStale()
    {
        using RuntimeAuthoritativeSessionHost host = CreateHost();
        IReadOnlyList<AuthoritativeActionResult> results = host.AdvanceTick(
            1,
            SimulationClock.FixedDeltaSeconds,
            [
                CreateCommand(PrimarySession, 1, use: false),
                CreateCommand(SecondarySession, HeldCommandSequence, use: true)
            ],
            Tuning,
            allowGameplaySimulation: false);
        Assert.True(Assert.Single(results).Accepted);

        AuthoritativeActionResult stale = host.ProcessAction(new PlayerActionMessage(
            new PlayerSessionId(SecondarySession),
            HeldCommandSequence,
            new PlayerStatsUpdateIntent(new PlayerCombatStatsSnapshot(12, 2.5f, 11))));

        Assert.False(stale.Accepted);
        Assert.Contains("stale", stale.Rejection?.Reason ?? string.Empty, StringComparison.Ordinal);
    }

    public static TheoryData<int, float, int, string> InvalidStats => new()
    {
        { 10, float.NaN, 10, "finite" },
        { 10, float.PositiveInfinity, 10, "finite" },
        { 10, float.NegativeInfinity, 10, "finite" },
        { 10, -1f, 10, "Attack speed" },
        { 10, PlayerCombatStats.MinAttackSpeed - 0.01f, 10, "Attack speed" },
        { 10, PlayerCombatStats.MaxAttackSpeed + 0.01f, 10, "Attack speed" },
        { 10, 1000f, 10, "Attack speed" },
        { PlayerCombatStats.MinStrength - 1, 2f, 10, "Strength" },
        { -50, 2f, 10, "Strength" },
        { PlayerCombatStats.MaxStrength + 1, 2f, 10, "Strength" },
        { int.MaxValue, 2f, 10, "Strength" },
        { 10, 2f, PlayerCombatStats.MinVitality - 1, "Vitality" },
        { 10, 2f, PlayerCombatStats.MaxVitality + 1, "Vitality" },
        { 10, 2f, int.MinValue, "Vitality" }
    };

    [Theory]
    [MemberData(nameof(InvalidStats))]
    public void StatsUpdate_OutsideTheEditorRules_IsRejected_AndLeavesStatsUnchanged(
        int strength,
        float attackSpeed,
        int vitality,
        string expectedReason)
    {
        using RuntimeAuthoritativeSessionHost host = CreateHost();
        PlayerCombatant combatant = host.Runtime.Player.Combatant;
        PlayerCombatStatsSnapshot before = PlayerCombatStatsSnapshot.From(combatant.BaseStats);
        float intervalBefore = combatant.DerivedStats.AttackIntervalSeconds;

        AuthoritativeActionResult result = host.ProcessAction(new PlayerActionMessage(
            new PlayerSessionId(PrimarySession),
            StatsActionSequence,
            new PlayerStatsUpdateIntent(new PlayerCombatStatsSnapshot(strength, attackSpeed, vitality))));

        Assert.False(result.Accepted);
        Assert.Null(result.Payload);
        string reason = Assert.IsType<RejectedUseDelta>(result.Rejection).Reason;
        Assert.StartsWith("Stats update rejected:", reason, StringComparison.Ordinal);
        Assert.Contains(expectedReason, reason, StringComparison.Ordinal);
        Assert.Equal(before, PlayerCombatStatsSnapshot.From(combatant.BaseStats));
        Assert.Equal(intervalBefore, combatant.DerivedStats.AttackIntervalSeconds);
    }

    public static TheoryData<int, float, int> ValidStats => new()
    {
        { PlayerCombatStats.MinStrength, PlayerCombatStats.MinAttackSpeed, PlayerCombatStats.MinVitality },
        { PlayerCombatStats.MaxStrength, PlayerCombatStats.MaxAttackSpeed, PlayerCombatStats.MaxVitality },
        { 22, 4.5f, 16 }
    };

    [Theory]
    [MemberData(nameof(ValidStats))]
    public void StatsUpdate_InsideTheEditorRules_IsApplied(int strength, float attackSpeed, int vitality)
    {
        using RuntimeAuthoritativeSessionHost host = CreateHost();
        PlayerCombatStatsSnapshot requested = new(strength, attackSpeed, vitality);

        AuthoritativeActionResult result = host.ProcessAction(new PlayerActionMessage(
            new PlayerSessionId(SecondarySession),
            StatsActionSequence,
            new PlayerStatsUpdateIntent(requested)));

        Assert.True(result.Accepted, result.Rejection?.Reason);
        // The intent carries Strength, Attack Speed and Vitality only; its Agility and Energy fields
        // default to 0 and are deliberately ignored, so the character keeps the values it joined with -
        // the starting 10s here. That a stats update cannot edit Agility or Energy is the point.
        PlayerCombatStatsSnapshot expected = requested with
        {
            Agility = PlayerCombatStats.UnseededAttribute,
            Energy = PlayerCombatStats.UnseededAttribute
        };
        Assert.Equal(expected, Assert.IsType<PlayerStatsUpdateResult>(result.Payload).Stats);
    }

    [Fact]
    public void TryValidateBaseStats_MatchesTheEditorRanges()
    {
        Assert.True(PlayerCombatStats.TryValidateBaseStats(new PlayerCombatStatsSnapshot(10, 2f, 10), out string? reason));
        Assert.Null(reason);
        Assert.False(PlayerCombatStats.TryValidateBaseStats(new PlayerCombatStatsSnapshot(10, float.NaN, 10), out reason));
        Assert.NotNull(reason);
    }

    private static void SetMaxAttackSpeed(RuntimeAuthoritativeSessionHost host, string sessionId)
    {
        AuthoritativeActionResult result = host.ProcessAction(new PlayerActionMessage(
            new PlayerSessionId(sessionId),
            StatsActionSequence,
            new PlayerStatsUpdateIntent(new PlayerCombatStatsSnapshot(10, PlayerCombatStats.MaxAttackSpeed, 10))));
        Assert.True(result.Accepted, result.Rejection?.Reason);
    }

    private static RuntimeAuthoritativeSessionHost CreateHost()
    {
        WorldSessionRuntime runtime = new(16, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        RuntimeAuthoritativeSessionHost host = new(runtime);
        host.JoinSession(PrimarySession, PrimarySession);
        host.JoinSession(SecondarySession, SecondarySession);
        Assert.Equal(PrimarySession, runtime.PrimaryRemoteSessionId?.Value);
        runtime.Enemies.Clear();
        return host;
    }

    // Combat mode, facing straight up so no enemy or cube is in the way: every use is a plain miss
    // that the server accepts once the attack cooldown allows it.
    private static PlayerCommandFrame CreateCommand(string sessionId, ulong sequence, bool use)
    {
        return new PlayerCommandFrame(
            new PlayerSessionId(sessionId),
            sequence,
            new PlayerModeSwitchIntent(PlayerInteractionMode.Combat),
            new PlayerMoveIntent(0f, 0f),
            new PlayerLookIntent(0f, 0f, Vector3.UnitY),
            PrimaryUsePressed: use);
    }
}
