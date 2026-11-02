using System.Buffers;
using System.Text;
using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Replication;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Combat;
using Dream.Simulation.Players;
using Dream.Simulation.Stats;
using Dream.Simulation.World;

namespace Dream.Tests.Multiplayer.Protocol;

// Protocol v14: the Combat group is [str, aspd, vit, agi, energy, statPoints, mana, maxMana] and a
// player's VisualKey ("<main>|<off>") travels like an enemy's.
public sealed class CharacterStatsWireTests
{
    private static readonly PlayerCombatStatsSnapshot Stats = new(14, 2.3f, 12, 13, 11, 5, 44, 75);

    [Fact]
    public void Keyframe_CarriesTheWholeCombatGroupAndThePlayerVisualKey()
    {
        ActorSnapshot[] actors = [Player(1, Stats, "held.weapon.sword|held.shield.buckler")];
        TransportJsonCodec codec = new();
        ServerSnapshotMessage sent = new(Envelope(2, actors) with { ActorDelta = ActorDeltaContext.CreateKeyframe(1) });

        string json = Encoding.UTF8.GetString(EncodePayload(codec, sent));
        ServerSnapshotMessage shown = new ActorStateCache().Apply(DecodePayload(codec, EncodePayload(codec, sent)));

        Assert.Contains("[14,2.3,12,13,11,5,44,75]", json, StringComparison.Ordinal);
        Assert.Contains("\"held.weapon.sword|held.shield.buckler\"", json, StringComparison.Ordinal);
        ActorSnapshot actor = Assert.Single(shown.Snapshot.Actors);
        Assert.Equal(Stats, actor.CombatStats);
        Assert.Equal("held.weapon.sword|held.shield.buckler", actor.VisualKey);
    }

    [Fact]
    public void Delta_WhenOnlyTheManaChanges_CarriesTheCombatGroupAgain()
    {
        ActorSnapshot[] first = [Player(1, Stats, null)];
        ActorSnapshot[] second = [Player(1, Stats with { Mana = 45 }, null)];
        ActorDeltaChain chain = new();
        TransportJsonCodec codec = new();
        ActorStateCache cache = new();
        ulong tick = 2;
        foreach (ActorSnapshot[] actors in new[] { first, second })
        {
            ServerSnapshotMessage sent = new(Envelope(tick, actors) with { ActorDelta = chain.Next(tick, actors) });
            ServerSnapshotMessage shown = cache.Apply(DecodePayload(codec, EncodePayload(codec, sent)));
            Assert.Equal(actors[0].CombatStats, Assert.Single(shown.Snapshot.Actors).CombatStats);
            tick += 2;
        }

        Assert.Equal(0, cache.ChainBreaks);
    }

    [Fact]
    public void PlayerVisualKey_ChangesSurviveDeltas_IncludingClearing()
    {
        ActorSnapshot[][] steps =
        [
            [Player(1, Stats, null)],
            [Player(1, Stats, "held.weapon.mace|")],
            [Player(1, Stats, "held.weapon.mace|held.shield.tower")],
            [Player(1, Stats, null)]
        ];
        ActorDeltaChain chain = new();
        TransportJsonCodec codec = new();
        ActorStateCache cache = new();
        ulong tick = 2;
        foreach (ActorSnapshot[] actors in steps)
        {
            ServerSnapshotMessage sent = new(Envelope(tick, actors) with { ActorDelta = chain.Next(tick, actors) });
            ServerSnapshotMessage shown = cache.Apply(DecodePayload(codec, EncodePayload(codec, sent)));
            Assert.Equal(actors[0].VisualKey, Assert.Single(shown.Snapshot.Actors).VisualKey);
            tick += 2;
        }

        Assert.Equal(1, cache.KeyframesApplied);
        Assert.Equal(0, cache.ChainBreaks);
    }

    [Fact]
    public void LegacyActorList_RoundTripsTheStatsObject()
    {
        ActorSnapshot[] actors = [Player(1, Stats, "held.weapon.axe|")];
        TransportJsonCodec codec = new();
        ServerSnapshotMessage sent = new(Envelope(2, actors));

        ServerSnapshotMessage decoded = DecodePayload(codec, EncodePayload(codec, sent));

        ActorSnapshot actor = Assert.Single(decoded.Snapshot.Actors);
        Assert.Equal(Stats, actor.CombatStats);
        Assert.Equal("held.weapon.axe|", actor.VisualKey);
    }

    [Fact]
    public void SpendIntent_RoundTrips()
    {
        TransportJsonCodec codec = new();
        PlayerActionMessage message = new(new PlayerSessionId("session-a"), 7, new PlayerAttributeSpendIntent(StatId.Energy, 1));

        byte[] bytes = codec.Encode(new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, message.MessageKind, 3), message);
        PlayerActionMessage decoded = Assert.IsType<PlayerActionMessage>(codec.Decode(bytes).Message);

        PlayerAttributeSpendIntent intent = Assert.IsType<PlayerAttributeSpendIntent>(decoded.Payload);
        Assert.Equal(StatId.Energy, intent.Attribute);
        Assert.Equal(1, intent.Points);
    }

    private static ActorSnapshot Player(int id, PlayerCombatStatsSnapshot stats, string? visualKey)
    {
        return new ActorSnapshot(
            id,
            CreatureKind.Player,
            $"session-{id}",
            new Vector3(1f, 0f, 1f),
            -Vector3.UnitZ,
            100,
            100,
            true,
            PlayerInteractionMode.Combat,
            stats,
            $"name-{id}",
            new PlayerProgressionSnapshot(2, 10, 1000),
            VisualKey: visualKey);
    }

    private static ServerSnapshotEnvelope Envelope(ulong tick, ActorSnapshot[] actors)
    {
        return new ServerSnapshotEnvelope(
            tick,
            new Dictionary<string, ulong>(),
            actors,
            PlayerInteractionMode.Combat,
            null,
            Array.Empty<WorldMutationDelta>(),
            Array.Empty<DamageEventDelta>());
    }

    private static byte[] EncodePayload(TransportJsonCodec codec, ServerSnapshotMessage message)
    {
        ArrayBufferWriter<byte> writer = new();
        codec.EncodePayload(message, writer);
        return writer.WrittenSpan.ToArray();
    }

    private static ServerSnapshotMessage DecodePayload(TransportJsonCodec codec, byte[] payload)
    {
        byte[] frame = Encoding.UTF8.GetBytes(
            "{\"protocolVersion\":" + TransportEnvelope.CurrentProtocolVersion + ",\"messageKind\":\"server.snapshot\",\"sequence\":1,\"payload\":" + Encoding.UTF8.GetString(payload) + "}");
        return Assert.IsType<ServerSnapshotMessage>(codec.Decode(frame).Message);
    }
}
