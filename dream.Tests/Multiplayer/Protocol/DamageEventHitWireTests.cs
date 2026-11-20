using System.Text;
using Xunit;
using Dream.Client.Replication;
using Dream.Multiplayer.Protocol;
using Dream.Simulation.Combat;
using Dream.Simulation.Players;
using Dream.Simulation.World;

namespace Dream.Tests.Multiplayer.Protocol;

// Protocol v12: DamageEventDelta.HitKind is the trailing "hit" field, written only for a critical or
// incoming hit.
public class DamageEventHitWireTests
{
    private const string RegularEvent = "{\"actorId\":2003,\"kind\":1,\"amount\":7,\"actorDied\":false,\"currentHealth\":33,\"maxHealth\":40}";

    private static ServerSnapshotEnvelope Snapshot(params DamageEventDelta[] damage) => new(
        500,
        new Dictionary<string, ulong>(),
        Array.Empty<ActorSnapshot>(),
        PlayerInteractionMode.Combat,
        null,
        Array.Empty<WorldMutationDelta>(),
        damage);

    private static string Encode(params DamageEventDelta[] damage) => Encoding.UTF8.GetString(
        new TransportJsonCodec().Encode(
            new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, "server.snapshot", 1),
            new ServerSnapshotMessage(Snapshot(damage))));

    private static ServerSnapshotEnvelope Decode(string json) =>
        ((ServerSnapshotMessage)new TransportJsonCodec().Decode(Encoding.UTF8.GetBytes(json)).Message).Snapshot;

    private static string RawSnapshot(string damageEventJson) =>
        "{\"protocolVersion\":23,\"messageKind\":\"server.snapshot\",\"sequence\":1,\"payload\":{\"snapshot\":{\"serverTick\":500," +
        "\"lastAcceptedCommandSequences\":{},\"actors\":[],\"interactionMode\":0,\"worldMutations\":[],\"damageEvents\":[" +
        damageEventJson + "],\"worldChecksumSequence\":0}}}";

    // The tick goldens hash this text and their script lands critical hits: HitKind must stay out of it.
    [Theory]
    [InlineData(FloatingCombatTextKind.Regular)]
    [InlineData(FloatingCombatTextKind.Critical)]
    [InlineData(FloatingCombatTextKind.Incoming)]
    public void ToString_IsPinnedAndNeverPrintsTheHitKind(FloatingCombatTextKind hit)
    {
        DamageEventDelta evt = new(7, CreatureKind.Enemy, 12, true, 0, 100, hit);

        Assert.Equal(
            "DamageEventDelta { ActorId = 7, Kind = Enemy, Amount = 12, ActorDied = True, CurrentHealth = 0, MaxHealth = 100 }",
            evt.ToString());
    }

    [Fact]
    public void RegularHit_IsWrittenWithoutTheHitField()
    {
        string json = Encode(new DamageEventDelta(2003, CreatureKind.Enemy, 7, false, 33, 40));

        Assert.StartsWith("{\"protocolVersion\":23,", json, StringComparison.Ordinal);
        Assert.Contains(RegularEvent, json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"hit\"", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(FloatingCombatTextKind.Critical, 1)]
    [InlineData(FloatingCombatTextKind.Incoming, 2)]
    public void NonRegularHit_IsAppendedAsTheLastField_AndRoundTrips(FloatingCombatTextKind kind, int wire)
    {
        DamageEventDelta sent = new(2003, CreatureKind.Enemy, 7, false, 33, 40, kind);

        string json = Encode(sent);

        Assert.Contains(RegularEvent[..^1] + ",\"hit\":" + wire + "}", json, StringComparison.Ordinal);
        Assert.Equal(sent, Assert.Single(Decode(json).DamageEvents));
    }

    [Fact]
    public void AnEventWithoutHit_DecodesAsRegular()
    {
        DamageEventDelta decoded = Assert.Single(Decode(RawSnapshot(RegularEvent)).DamageEvents);

        Assert.Equal(FloatingCombatTextKind.Regular, decoded.HitKind);
        Assert.Equal(new DamageEventDelta(2003, CreatureKind.Enemy, 7, false, 33, 40), decoded);
    }

    [Theory]
    [InlineData("99")]
    [InlineData("-5")]
    [InlineData("2147483647")]
    public void AnUndefinedHit_DecodesAndTheClientTreatsItAsRegular(string hit)
    {
        string json = RawSnapshot(RegularEvent[..^1] + ",\"hit\":" + hit + "}");

        DamageEventDelta decoded = Assert.Single(Decode(json).DamageEvents);

        Assert.False(Enum.IsDefined(decoded.HitKind));
        Assert.Equal(FloatingCombatTextKind.Regular, ClientSnapshotDamageApplier.ResolveHitKind(decoded.HitKind));
    }

    [Fact]
    public void ADefinedHit_IsKeptByTheClient()
    {
        Assert.Equal(FloatingCombatTextKind.Critical, ClientSnapshotDamageApplier.ResolveHitKind(FloatingCombatTextKind.Critical));
        Assert.Equal(FloatingCombatTextKind.Incoming, ClientSnapshotDamageApplier.ResolveHitKind(FloatingCombatTextKind.Incoming));
        Assert.Equal(FloatingCombatTextKind.Regular, ClientSnapshotDamageApplier.ResolveHitKind(FloatingCombatTextKind.Regular));
    }

    [Fact]
    public void Coalescing_KeepsEachEventsHitInOrder()
    {
        DamageEventDelta first = new(1, CreatureKind.Enemy, 3, false, 10, 20, FloatingCombatTextKind.Critical);
        DamageEventDelta second = new(1, CreatureKind.Enemy, 4, false, 6, 20);
        DamageEventDelta third = new(2, CreatureKind.Enemy, 5, true, 0, 20, FloatingCombatTextKind.Critical);
        ServerSnapshotCoalesceBuffer buffer = new();

        buffer.Begin(Snapshot(first));
        buffer.Append(Snapshot(second, third));

        Assert.Equal([first, second, third], buffer.ToEnvelope().DamageEvents);
    }
}
