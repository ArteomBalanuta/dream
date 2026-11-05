using System.Text;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.Stats;
using Dream.Simulation.World;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Simulation.Runtime;

// M2-A: stat points are derived from the level and the base stats, spent through
// PlayerStatSpendIntent, and checked by the server on every server (not only with dev commands).
// Since the stats sheet merge the four attributes (Strength, Agility, Vitality, Energy) take points
// and Attack Speed does not (it comes from Agility); caps are PlayerCombatStats' (10000, AttributeCeiling).
// Points are measured from the creation value (PlayerStatRules.NewCharacterAttribute, 10), so every
// character here is built from that origin; a combatant a host has not loaded a profile into holds the
// unseeded 10, which is the same value, so it too counts as nothing spent.
public class PlayerStatPointsTests
{
    private const string SessionA = "session-a";
    private const string SessionB = "session-b";

    private const int Origin = PlayerStatRules.NewCharacterAttribute;

    // A new character as it is created: every attribute at 10 (PersistedPlayerProfile.CreateDefault), 0 spent.
    private static PlayerCombatStats Stats(int strength = Origin, float attackSpeed = 2f, int vitality = Origin, int agility = Origin, int energy = Origin) =>
        new() { Strength = strength, AttackSpeed = attackSpeed, Vitality = vitality, Agility = agility, Energy = energy };

    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 0)]
    [InlineData(-5, 0)]
    [InlineData(2, 5)]
    [InlineData(10, 45)]
    public void PointsAvailable_IsFivePerLevelAfterTheFirst_ForAFreshCharacter(int level, int expected)
    {
        Assert.Equal(expected, PlayerStatRules.PointsAvailable(level, Stats()));
    }

    [Fact]
    public void PointsAvailable_CountsStrengthVitalityAndEveryFiveHundredthsOfAttackSpeed()
    {
        // 3 + 2 + 4 steps (2.0 -> 2.2) = 9 spent of 10 earned at level 3 (5 * (3 - 1)), from the origin of 10.
        Assert.Equal(1, PlayerStatRules.PointsAvailable(3, Stats(Origin + 3, 2.2f, Origin + 2)));
    }

    [Fact]
    public void PointsAvailable_IsZeroNotNegative_ForAnOverBudgetCharacter()
    {
        // Spent from the origin of 10: 50 + 50 + 60 steps (2.0 -> 5.0 at 0.05) = 160, over the 20 that level 5 earns
        // (5 * (5 - 1)).
        PlayerCombatStats freeSliders = Stats(Origin + 50, 5f, Origin + 50);
        Assert.Equal(0, PlayerStatRules.PointsAvailable(1, freeSliders));
        Assert.Equal(0, PlayerStatRules.PointsAvailable(5, freeSliders));
        Assert.Equal(160, PlayerStatRules.PointsSpent(freeSliders));
        Assert.True(PlayerStatRules.PointsSpent(freeSliders) > PlayerStatRules.PointsEarned(5));
    }

    [Fact]
    public void Spend_AppliesToTheSendersCharacter_AndIsAcceptedWithoutDevCommands()
    {
        using RuntimeAuthoritativeSessionHost host = CreateHost();
        Assert.False(host.DevCommandsAllowed);
        PlayerCombatant a = Combatant(host, SessionA);
        a.ApplyBaseStats(Stats());
        a.ApplyProgression(3, 0); // 10 points: 4 + 1 + 2 + 3

        Assert.True(Spend(host, SessionA, 1, StatId.Strength, 4).Accepted);
        Assert.True(Spend(host, SessionA, 2, StatId.Vitality, 1).Accepted);
        Assert.True(Spend(host, SessionA, 3, StatId.Agility, 2).Accepted);
        AuthoritativeActionResult energy = Spend(host, SessionA, 4, StatId.Energy, 3);

        Assert.True(energy.Accepted, energy.Rejection?.Reason);
        Assert.Equal(Origin + 4, a.BaseStats.Strength);
        Assert.Equal(Origin + 1, a.BaseStats.Vitality);
        Assert.Equal(Origin + 2, a.BaseStats.Agility);
        Assert.Equal(Origin + 3, a.BaseStats.Energy);
        Assert.Equal(0, PlayerStatRules.PointsAvailable(a.Level, a.BaseStats));
        Assert.Equal(
            PlayerCombatStatsSnapshot.From(a.BaseStats),
            Assert.IsType<PlayerStatsUpdateResult>(energy.Payload).Stats);
    }

    [Fact]
    public void Overspend_IsRejected_AndChangesNothing()
    {
        using RuntimeAuthoritativeSessionHost host = CreateHost();
        PlayerCombatant a = Combatant(host, SessionA);
        a.ApplyBaseStats(Stats());
        a.ApplyProgression(2, 0); // 5 points
        PlayerCombatStatsSnapshot before = PlayerCombatStatsSnapshot.From(a.BaseStats);

        AuthoritativeActionResult result = Spend(host, SessionA, 1, StatId.Strength, 6);

        Assert.False(result.Accepted);
        Assert.StartsWith("Stat spend rejected:", result.Rejection!.Reason, StringComparison.Ordinal);
        Assert.Equal(before, PlayerCombatStatsSnapshot.From(a.BaseStats));

        // Two spends that together pass the budget: the second is the one refused.
        Assert.True(Spend(host, SessionA, 2, StatId.Strength, 3).Accepted);
        Assert.False(Spend(host, SessionA, 3, StatId.Vitality, 3).Accepted);
        Assert.Equal(Origin + 3, a.BaseStats.Strength);
        Assert.Equal(Origin, a.BaseStats.Vitality);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void NonPositivePoints_AreRejected_AndNeverRefund(int points)
    {
        using RuntimeAuthoritativeSessionHost host = CreateHost();
        PlayerCombatant a = Combatant(host, SessionA);
        a.ApplyBaseStats(Stats());
        a.ApplyProgression(5, 0);
        Assert.True(Spend(host, SessionA, 1, StatId.Strength, 5).Accepted);
        PlayerCombatStatsSnapshot before = PlayerCombatStatsSnapshot.From(a.BaseStats);

        AuthoritativeActionResult result = Spend(host, SessionA, 2, StatId.Strength, points);

        Assert.False(result.Accepted);
        Assert.Equal(before, PlayerCombatStatsSnapshot.From(a.BaseStats));
    }

    [Fact]
    public void HugePoints_AreRejected_WithoutOverflow()
    {
        using RuntimeAuthoritativeSessionHost host = CreateHost();
        PlayerCombatant a = Combatant(host, SessionA);
        a.ApplyBaseStats(Stats());
        a.ApplyProgression(int.MaxValue, 0);

        Assert.False(Spend(host, SessionA, 1, StatId.Strength, int.MaxValue).Accepted);
        Assert.Equal(Origin, a.BaseStats.Strength);
    }

    [Theory]
    [InlineData(StatId.AttackSpeed)]
    [InlineData(StatId.MovementSpeed)]
    [InlineData(StatId.Armor)]
    [InlineData(StatId.Count)]
    [InlineData((StatId)200)]
    public void AStatThatTakesNoPoints_IsRejected_AndChangesNothing(StatId stat)
    {
        using RuntimeAuthoritativeSessionHost host = CreateHost();
        PlayerCombatant a = Combatant(host, SessionA);
        a.ApplyBaseStats(Stats());
        a.ApplyProgression(10, 0);
        PlayerCombatStatsSnapshot before = PlayerCombatStatsSnapshot.From(a.BaseStats);

        AuthoritativeActionResult result = Spend(host, SessionA, 1, stat, 1);

        Assert.False(result.Accepted);
        Assert.Equal(before, PlayerCombatStatsSnapshot.From(a.BaseStats));
    }

    [Fact]
    public void ACharacterAtItsStatCap_CannotPassTheCap_OrTheBudget()
    {
        using RuntimeAuthoritativeSessionHost host = CreateHost();
        PlayerCombatant a = Combatant(host, SessionA);
        // Enough levels that the budget is not what stops the spends below. The most any one case needs is
        // an attribute at the 10000 ceiling, 9990 above the origin of 10, plus a point or two: level 2000 earns
        // 5 * 1999 = 9995.
        a.ApplyProgression(2000, 0);

        // One under the cap: the over-cap spend is refused, not clamped.
        a.ApplyBaseStats(Stats(strength: PlayerCombatStats.MaxStrength - 1));
        Assert.False(Spend(host, SessionA, 1, StatId.Strength, 2).Accepted);
        Assert.Equal(PlayerCombatStats.MaxStrength - 1, a.BaseStats.Strength);
        Assert.True(Spend(host, SessionA, 2, StatId.Strength, 1).Accepted);
        Assert.Equal(PlayerCombatStats.MaxStrength, a.BaseStats.Strength);
        Assert.False(Spend(host, SessionA, 3, StatId.Strength, 1).Accepted);

        a.ApplyBaseStats(Stats(vitality: PlayerCombatStats.MaxVitality));
        Assert.False(Spend(host, SessionA, 4, StatId.Vitality, 1).Accepted);

        a.ApplyBaseStats(Stats(agility: PlayerCombatStats.MaxAgility));
        Assert.False(Spend(host, SessionA, 5, StatId.Agility, 1).Accepted);

        a.ApplyBaseStats(Stats(energy: PlayerCombatStats.MaxEnergy - 1));
        Assert.False(Spend(host, SessionA, 6, StatId.Energy, 2).Accepted);
        Assert.True(Spend(host, SessionA, 7, StatId.Energy, 1).Accepted);
        Assert.Equal(PlayerCombatStats.MaxEnergy, a.BaseStats.Energy);

        // Attack Speed takes no points at all.
        a.ApplyBaseStats(Stats(attackSpeed: 4.95f));
        Assert.False(Spend(host, SessionA, 8, StatId.AttackSpeed, 1).Accepted);
    }

    [Fact]
    public void AnOverBudgetCharacter_KeepsItsStats_EarnsNothing_AndCannotSpend()
    {
        using RuntimeAuthoritativeSessionHost host = CreateHost();
        PlayerCombatant a = Combatant(host, SessionA);
        a.ApplyProgression(2, 0);
        // Spent from the origin of 10: 30 + 20 + 20 steps (2.0 -> 3.0) = 70, against 5 earned at level 2 and 15 at level 4.
        a.ApplyBaseStats(Stats(Origin + 30, 3f, Origin + 20));
        PlayerCombatStatsSnapshot kept = PlayerCombatStatsSnapshot.From(a.BaseStats);

        Assert.Equal(0, PlayerStatRules.PointsAvailable(a.Level, a.BaseStats));
        Assert.False(Spend(host, SessionA, 1, StatId.Vitality, 1).Accepted);
        Assert.Equal(kept, PlayerCombatStatsSnapshot.From(a.BaseStats));

        // Levelling does not reset the character; it just earns toward the overspend.
        a.ApplyProgression(4, 0);
        Assert.Equal(kept, PlayerCombatStatsSnapshot.From(a.BaseStats));
        Assert.Equal(0, PlayerStatRules.PointsAvailable(a.Level, a.BaseStats));
    }

    [Fact]
    public void AClient_CannotSpendForAnotherPlayer_OrWithAnotherPlayersPoints()
    {
        using RuntimeAuthoritativeSessionHost host = CreateHost();
        PlayerCombatant a = Combatant(host, SessionA);
        PlayerCombatStatsSnapshot bBefore = StatsOf(host, SessionB);
        a.ApplyBaseStats(Stats());
        a.ApplyProgression(10, 0);

        // B is level 1 with no points; A's 45 points are not B's.
        Assert.False(Spend(host, SessionB, 1, StatId.Strength, 1).Accepted);
        Assert.Equal(bBefore, StatsOf(host, SessionB));

        // A's spend lands on A only.
        Assert.True(Spend(host, SessionA, 1, StatId.Strength, 5).Accepted);
        Assert.Equal(Origin + 5, a.BaseStats.Strength);
        Assert.Equal(bBefore, StatsOf(host, SessionB));
    }

    [Fact]
    public void Handler_DropsASpendForAnotherSession_AndQueuesOwnSpend_WithoutDevCommands()
    {
        PlayerActionQueue queue = new();
        ServerRoutingContext context = new("connection-1", new FakeAuthority(), actionQueue: queue)
        {
            LastJoinRequest = new ClientJoinRequestMessage(SessionA, "player-a")
        };
        PlayerActionHandler handler = new();

        handler.Handle(context, new PlayerActionMessage(
            new PlayerSessionId(SessionB), 1, new PlayerStatSpendIntent(StatId.Strength, 1)));
        Assert.False(Assert.IsType<ServerActionMessage>(Assert.Single(context.OutboundMessages)).Result.Accepted);
        Assert.Empty(queue.Drain([new PlayerSessionId(SessionB), new PlayerSessionId(SessionA)]));

        context.OutboundMessages.Clear();
        handler.Handle(context, new PlayerActionMessage(
            new PlayerSessionId(SessionA), 2, new PlayerStatSpendIntent(StatId.Strength, 1)));
        Assert.Empty(context.OutboundMessages);
        Assert.IsType<PlayerStatSpendIntent>(Assert.Single(queue.Drain([new PlayerSessionId(SessionA)])).Message.Payload);
    }

    [Fact]
    public void SpendIntent_RoundTripsOnTheWire()
    {
        TransportJsonCodec codec = new();
        PlayerActionMessage message = new(new PlayerSessionId("s"), 1, new PlayerStatSpendIntent(StatId.AttackSpeed, 3));
        byte[] bytes = codec.Encode(
            new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, message.MessageKind, 1), message);

        Assert.Contains("\"$type\":\"player.stats.spend\"", Encoding.UTF8.GetString(bytes));
        PlayerStatSpendIntent decoded = Assert.IsType<PlayerStatSpendIntent>(
            Assert.IsType<PlayerActionMessage>(codec.Decode(bytes).Message).Payload);
        Assert.Equal(new PlayerStatSpendIntent(StatId.AttackSpeed, 3), decoded);
    }

    private static AuthoritativeActionResult Spend(
        RuntimeAuthoritativeSessionHost host, string session, ulong sequence, StatId stat, int points)
    {
        return host.ProcessAction(new PlayerActionMessage(
            new PlayerSessionId(session), sequence, new PlayerStatSpendIntent(stat, points)));
    }

    private static PlayerCombatant Combatant(RuntimeAuthoritativeSessionHost host, string session)
    {
        // The first joined session holds the primary seat, whose combatant the runtime exposes.
        Assert.Equal(SessionA, session);
        Assert.Equal(SessionA, host.Runtime.PrimaryRemoteSessionId?.Value);
        return host.Runtime.Player.Combatant;
    }

    // Another seat's stats as the server reports them (no equipment, so effective equals base).
    private static PlayerCombatStatsSnapshot StatsOf(RuntimeAuthoritativeSessionHost host, string session)
    {
        return host.Runtime.CaptureActorSnapshots().Single(actor => actor.SessionId == session).CombatStats!.Value;
    }

    private static RuntimeAuthoritativeSessionHost CreateHost()
    {
        WorldSessionRuntime runtime = new(16, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        RuntimeAuthoritativeSessionHost host = new(runtime);
        host.JoinSession(SessionA, SessionA);
        host.JoinSession(SessionB, SessionB);
        runtime.Enemies.Clear();
        return host;
    }

    private sealed class FakeAuthority : IAuthoritativeSessionHost
    {
        public bool DevCommandsAllowed => false;

        public JoinAcceptedMessage JoinSession(string requestedSessionId, string displayName) => throw new NotSupportedException();

        public AuthoritativeActionResult ProcessAction(PlayerActionMessage message) => throw new NotSupportedException();

        public ServerSnapshotEnvelope ExportSnapshot(bool includeWorldSnapshot = false) => throw new NotSupportedException();
    }
}
