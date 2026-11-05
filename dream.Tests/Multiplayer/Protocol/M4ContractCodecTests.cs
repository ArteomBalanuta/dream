using System.Text;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server;
using Dream.Simulation.Inventory;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Multiplayer.Protocol;

// M4 contract (protocol v17): the three ability intents are client sendable and round trip, the
// ability.used result and the server-only vitals message round trip, and the inventory snapshot carries
// an optional quickbar that leaves a snapshot without one byte-identical to v16.
public sealed class M4ContractCodecTests
{
    private static readonly TransportJsonCodec Codec = new();

    private static byte[] Encode<TMessage>(TMessage message, ulong sequence = 1) where TMessage : IProtocolMessage =>
        Codec.Encode(new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, message.MessageKind, sequence), message);

    public static TheoryData<IPlayerActionPayload, string> Intents => new()
    {
        { new SkillUseIntent("skill.cleave"), "skill.use" },
        { new SkillUseIntent("skill.bolt", new Vector3(0f, 0.5f, -1f)), "skill.use" },
        { new ItemUseIntent(7, 4001), "item.use" },
        { new QuickbarBindIntent(3, QuickbarEntryKind.Skill, "skill.mend"), "quickbar.bind" },
        { new QuickbarBindIntent(5, QuickbarEntryKind.None, string.Empty), "quickbar.bind" }
    };

    [Theory]
    [MemberData(nameof(Intents))]
    public void TheAbilityIntents_RideThePlayerActionFrame_FromAClient(IPlayerActionPayload payload, string discriminator)
    {
        PlayerActionMessage original = new(new PlayerSessionId("session-a"), 5, payload);

        byte[] bytes = Encode(original);

        Assert.Contains($"\"$type\":\"{discriminator}\"", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        PlayerActionMessage message = Assert.IsType<PlayerActionMessage>(Codec.DecodeFromClient(bytes).Message);
        Assert.Equal(payload, message.Payload);
    }

    [Theory]
    [MemberData(nameof(Intents))]
    public void TheAbilityIntents_AreClientSendable_AndGetQueued(IPlayerActionPayload payload, string _)
    {
        PlayerActionQueue queue = new();
        ServerRoutingContext context = new("connection-1", new NullAuthority(), actionQueue: queue)
        {
            LastJoinRequest = new ClientJoinRequestMessage("session-a", "player-a")
        };

        new PlayerActionHandler().Handle(context, new PlayerActionMessage(new PlayerSessionId("session-a"), 1, payload));

        Assert.Empty(context.OutboundMessages);
        Assert.Equal(payload, Assert.Single(queue.Drain([new PlayerSessionId("session-a")])).Message.Payload);
    }

    [Fact]
    public void AbilityUsedResult_RoundTripsInAnActionResult()
    {
        AbilityUsedResult payload = new("skill.cleave", 3, 120, 0);
        ServerActionMessage original = new(new AuthoritativeActionResult("s", 9, 100, Accepted: true, Payload: payload));

        byte[] bytes = Encode(original);

        Assert.Contains("\"$type\":\"ability.used\"", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        ServerActionMessage decoded = Assert.IsType<ServerActionMessage>(Codec.Decode(bytes).Message);
        Assert.Equal(payload, decoded.Result.Payload);
    }

    [Fact]
    public void ServerPlayerVitals_RoundTrips_AndIsRefusedFromAClient()
    {
        ServerPlayerVitals original = new(37, 55, [new AbilityCooldown("skill.cleave", 4000UL), new AbilityCooldown("potion.health", 4320UL)]);

        byte[] bytes = Encode(original);

        DecodedProtocolMessage decodedMessage = Codec.Decode(bytes);
        Assert.Equal("server.vitals", decodedMessage.Envelope.MessageKind);
        ServerPlayerVitals decoded = Assert.IsType<ServerPlayerVitals>(decodedMessage.Message);
        Assert.Equal(37, decoded.CurrentMana);
        Assert.Equal(55, decoded.MaxMana);
        Assert.Equal(original.Cooldowns, decoded.Cooldowns);
        Assert.True(TransportJsonCodec.IsServerOnlyKind(ServerPlayerVitals.Kind));
        Assert.Throws<InvalidOperationException>(() => Codec.DecodeFromClient(bytes));
    }

    [Fact]
    public void AnEmptyServerPlayerVitals_RoundTrips()
    {
        ServerPlayerVitals decoded = Assert.IsType<ServerPlayerVitals>(Codec.Decode(Encode(new ServerPlayerVitals(0, 50, []))).Message);

        Assert.Equal(0, decoded.CurrentMana);
        Assert.Equal(50, decoded.MaxMana);
        Assert.Empty(decoded.Cooldowns);
    }

    private static string InventoryJson(PlayerInventorySnapshot snapshot) =>
        Encoding.UTF8.GetString(Encode(new ServerActionMessage(
            new AuthoritativeActionResult("s", 1, 1, Accepted: true, Payload: new InventoryChangedResult(snapshot)))));

    private static PlayerInventorySnapshot Decode(string json) =>
        Assert.IsType<InventoryChangedResult>(
            Assert.IsType<ServerActionMessage>(Codec.Decode(Encoding.UTF8.GetBytes(json)).Message).Result.Payload).Inventory;

    [Fact]
    public void AnInventoryWithoutAQuickbar_HasTheBytesItHadInV16()
    {
        string json = InventoryJson(PlayerInventorySnapshot.Empty("p"));

        Assert.DoesNotContain("quick", json, StringComparison.Ordinal);
        // Apart from the version digit, this is exactly the v16 frame.
        Assert.Equal(
            "{\"protocolVersion\":21,\"messageKind\":\"server.action\",\"sequence\":1,\"payload\":{\"result\":{\"sessionId\":\"s\",\"actionSequence\":1,\"serverTick\":1,\"accepted\":true,\"payload\":{\"$type\":\"inventory.changed\",\"inventory\":{\"playerName\":\"p\",\"columns\":8,\"rows\":8,\"bag\":[],\"worn\":[]}}}}}",
            json);
        Assert.Null(Decode(json).Quickbar);
    }

    [Fact]
    public void AQuickbar_IsWrittenAsQuick_AndRoundTrips()
    {
        QuickbarSlotSnapshot[] bar =
        [
            new(0, QuickbarEntryKind.Skill, "skill.cleave"),
            new(1, QuickbarEntryKind.None, string.Empty),
            new(4, QuickbarEntryKind.Item, "potion.health")
        ];
        string json = InventoryJson(PlayerInventorySnapshot.Empty("p") with { Quickbar = bar });

        Assert.Contains("\"quick\":[[0,1,\"skill.cleave\"],[1,0,\"\"],[4,2,\"potion.health\"]]", json, StringComparison.Ordinal);
        Assert.Equal(bar, Decode(json).Quickbar);
    }

    [Fact]
    public void AQuickbarOnTheWire_IsCappedAndScreened()
    {
        // Slot 9 and kind 7 are out of range, the repeated slot 0 is dropped, and entries past the sixth
        // on the wire are ignored whatever they hold ("g" and later).
        string quick = "\"quick\":[[0,1,\"a\"],[9,1,\"b\"],[1,7,\"c\"],[0,2,\"d\"],[2,1,\"e\"],[3,1,\"f\"],[4,1,\"g\"],[5,1,\"h\"],[5,1,\"i\"]]";
        string json = InventoryJson(PlayerInventorySnapshot.Empty("p") with { Quickbar = [] })
            .Replace("\"quick\":[]", quick, StringComparison.Ordinal);

        IReadOnlyList<QuickbarSlotSnapshot> bar = Decode(json).Quickbar!;

        Assert.Equal(
            [new QuickbarSlotSnapshot(0, QuickbarEntryKind.Skill, "a"), new QuickbarSlotSnapshot(2, QuickbarEntryKind.Skill, "e"),
             new QuickbarSlotSnapshot(3, QuickbarEntryKind.Skill, "f")],
            bar);
    }

    private sealed class NullAuthority : IAuthoritativeSessionHost
    {
        public bool DevCommandsAllowed => false;

        public JoinAcceptedMessage JoinSession(string requestedSessionId, string displayName) => throw new NotSupportedException();

        public AuthoritativeActionResult ProcessAction(PlayerActionMessage message) => throw new NotSupportedException();

        public ServerSnapshotEnvelope ExportSnapshot(bool includeWorldSnapshot = false) => throw new NotSupportedException();
    }
}
