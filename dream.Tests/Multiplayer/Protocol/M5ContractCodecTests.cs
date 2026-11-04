using System.Text;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server;
using Dream.Simulation.Inventory;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Xunit;
using static Dream.Tests.Server.ServerConnectionTestFixtures;

namespace Dream.Tests.Multiplayer.Protocol;

// M5, M6 and M8 contract (protocol v18): the twelve party, trade and quest intents are client sendable and
// round trip, the npc.dialogue result round trips, the four server pushes pass the connection's encoder,
// round trip and are refused from a client, and the Party and Command chat channels are valid.
public sealed class M5ContractCodecTests
{
    private static readonly TransportJsonCodec Codec = new();

    private static byte[] Encode<TMessage>(TMessage message, ulong sequence = 1) where TMessage : IProtocolMessage =>
        Codec.Encode(new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, message.MessageKind, sequence), message);

    public static TheoryData<IPlayerActionPayload, string> Intents => new()
    {
        { new PartyInviteIntent("Bob"), "party.invite" },
        { new PartyRespondIntent(true), "party.respond" },
        { new PartyRespondIntent(false), "party.respond" },
        { new PartyLeaveIntent(), "party.leave" },
        { new TradeRequestIntent("Bob"), "trade.request" },
        { new TradeRespondIntent(true), "trade.respond" },
        { new TradeOfferIntent([11, 12, 13], 250), "trade.offer" },
        { new TradeOfferIntent([], 0), "trade.offer" },
        { new TradeLockIntent(), "trade.lock" },
        { new TradeConfirmIntent(), "trade.confirm" },
        { new TradeCancelIntent(), "trade.cancel" },
        { new QuestAcceptIntent(5001, "wolf-cull"), "quest.accept" },
        { new QuestTurnInIntent(5001, "wolf-cull"), "quest.turn-in" },
        { new QuestAbandonIntent("wolf-cull"), "quest.abandon" }
    };

    [Fact]
    public void TheProtocolVersion_IsEighteen()
    {
        Assert.Equal(20, TransportEnvelope.CurrentProtocolVersion);
    }

    [Theory]
    [MemberData(nameof(Intents))]
    public void TheIntents_RideThePlayerActionFrame_FromAClient(IPlayerActionPayload payload, string discriminator)
    {
        PlayerActionMessage original = new(new PlayerSessionId("session-a"), 5, payload);

        byte[] bytes = Encode(original);

        Assert.Contains($"\"$type\":\"{discriminator}\"", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        PlayerActionMessage message = Assert.IsType<PlayerActionMessage>(Codec.DecodeFromClient(bytes).Message);
        Assert.Equal(payload.GetType(), message.Payload.GetType());
        if (payload is TradeOfferIntent offer)
        {
            TradeOfferIntent decoded = Assert.IsType<TradeOfferIntent>(message.Payload);
            Assert.Equal(offer.ItemInstanceIds, decoded.ItemInstanceIds);
            Assert.Equal(offer.Gold, decoded.Gold);
        }
        else
        {
            Assert.Equal(payload, message.Payload);
        }
    }

    [Theory]
    [MemberData(nameof(Intents))]
    public void TheIntents_AreClientSendable_AndGetQueued(IPlayerActionPayload payload, string _)
    {
        PlayerActionQueue queue = new();
        ServerRoutingContext context = new("connection-1", new NullAuthority(), actionQueue: queue)
        {
            LastJoinRequest = new ClientJoinRequestMessage("session-a", "player-a")
        };

        new PlayerActionHandler().Handle(context, new PlayerActionMessage(new PlayerSessionId("session-a"), 1, payload));

        Assert.Empty(context.OutboundMessages);
        Assert.Equal(payload.GetType(), Assert.Single(queue.Drain([new PlayerSessionId("session-a")])).Message.Payload.GetType());
    }

    [Fact]
    public void NpcDialogueResult_RoundTripsInAnActionResult()
    {
        NpcDialogueResult payload = new(
            5001,
            "Gate Warden",
            "Keep the gate.",
            [
                new QuestOfferLine("wolf-cull", "Thin the Pack", "Wolves howl.", "100 XP, 40 gold", QuestOfferState.Available),
                new QuestOfferLine("torch-supply", "Light for the Watch", "Bring torches.", "60 XP", QuestOfferState.ReadyToTurnIn)
            ]);
        ServerActionMessage original = new(new AuthoritativeActionResult("s", 9, 100, Accepted: true, Payload: payload));

        byte[] bytes = Encode(original);

        Assert.Contains("\"$type\":\"npc.dialogue\"", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        NpcDialogueResult decoded = Assert.IsType<NpcDialogueResult>(
            Assert.IsType<ServerActionMessage>(Codec.Decode(bytes).Message).Result.Payload);
        Assert.Equal(payload.NpcActorId, decoded.NpcActorId);
        Assert.Equal(payload.NpcName, decoded.NpcName);
        Assert.Equal(payload.Greeting, decoded.Greeting);
        Assert.Equal(payload.Quests, decoded.Quests);
    }

    public static TheoryData<IServerToClientMessage> ServerMessages => new()
    {
        new ServerPartyState(
            [new PartyMemberSnapshot("Alice", 3, 80, 100, true), new PartyMemberSnapshot("Bob", 2, 55, 90, false)],
            "Carol"),
        new ServerPartyState([], null),
        new ServerTradeState(
            TradeStage.Open,
            "Bob",
            [new TradeOfferLine(11, "gear.iron-blade", ItemRarity.Uncommon, "+3 attack")],
            25,
            true,
            false,
            [],
            0,
            false,
            false,
            null),
        new ServerTradeState(TradeStage.Cancelled, "Bob", [], 0, false, false, [], 0, false, false, "range"),
        new ServerQuestLog(
        [
            new QuestLogEntry("wolf-cull", "Thin the Pack", QuestState.Active, [new QuestObjectiveLine("Kill 2 Wolf", 1, 2)]),
            new QuestLogEntry("torch-supply", "Light for the Watch", QuestState.Completed, [])
        ]),
        new ServerPlayerProgress(4, 120, 400, new PlayerCombatStatsSnapshot(5, 1.5f, 7))
    };

    [Theory]
    [MemberData(nameof(ServerMessages))]
    public void TheServerPushes_RoundTrip_AndAreRefusedFromAClient(IServerToClientMessage original)
    {
        byte[] bytes = original switch
        {
            ServerPartyState m => Encode(m),
            ServerTradeState m => Encode(m),
            ServerQuestLog m => Encode(m),
            ServerPlayerProgress m => Encode(m),
            _ => throw new InvalidOperationException("unexpected message")
        };

        DecodedProtocolMessage decoded = Codec.Decode(bytes);

        Assert.Equal(original.MessageKind, decoded.Envelope.MessageKind);
        Assert.IsType(original.GetType(), decoded.Message);
        Assert.True(TransportJsonCodec.IsServerOnlyKind(original.MessageKind));
        Assert.Throws<InvalidOperationException>(() => Codec.DecodeFromClient(bytes));
    }

    [Fact]
    public void ThePushContents_SurviveTheWire()
    {
        ServerPartyState party = new([new PartyMemberSnapshot("Alice", 3, 80, 100, true)], "Carol");
        ServerPartyState partyBack = Assert.IsType<ServerPartyState>(Codec.Decode(Encode(party)).Message);
        Assert.Equal(party.Members, partyBack.Members);
        Assert.Equal("Carol", partyBack.PendingInviteFrom);

        ServerTradeState trade = new(
            TradeStage.Open, "Bob",
            [new TradeOfferLine(11, "gear.iron-blade", ItemRarity.Rare, "+3 attack")], 25, true, false,
            [new TradeOfferLine(12, "potion.health", ItemRarity.Common, "heals")], 5, false, true, null);
        ServerTradeState tradeBack = Assert.IsType<ServerTradeState>(Codec.Decode(Encode(trade)).Message);
        Assert.Equal(trade.Stage, tradeBack.Stage);
        Assert.Equal(trade.MyItems, tradeBack.MyItems);
        Assert.Equal(trade.TheirItems, tradeBack.TheirItems);
        Assert.Equal((trade.MyGold, trade.MyLocked, trade.MyConfirmed), (tradeBack.MyGold, tradeBack.MyLocked, tradeBack.MyConfirmed));
        Assert.Equal((trade.TheirGold, trade.TheirLocked, trade.TheirConfirmed), (tradeBack.TheirGold, tradeBack.TheirLocked, tradeBack.TheirConfirmed));

        ServerPlayerProgress progress = new(4, 120, 400, new PlayerCombatStatsSnapshot(5, 1.5f, 7));
        ServerPlayerProgress progressBack = Assert.IsType<ServerPlayerProgress>(Codec.Decode(Encode(progress)).Message);
        Assert.Equal((4, 120, 400), (progressBack.Level, progressBack.Experience, progressBack.ExperienceToNextLevel));
        Assert.Equal(progress.BaseStats, progressBack.BaseStats);

        ServerQuestLog log = new([new QuestLogEntry("wolf-cull", "Thin the Pack", QuestState.Active, [new QuestObjectiveLine("Kill 2 Wolf", 1, 2)])]);
        ServerQuestLog logBack = Assert.IsType<ServerQuestLog>(Codec.Decode(Encode(log)).Message);
        QuestLogEntry entry = Assert.Single(logBack.Entries);
        Assert.Equal(("wolf-cull", QuestState.Active), (entry.Key, entry.State));
        Assert.Equal(log.Entries[0].Objectives, entry.Objectives);
    }

    [Theory]
    [MemberData(nameof(ServerMessages))]
    public async Task TheServerPushes_PassTheConnectionEncoder(IServerToClientMessage message)
    {
        GatedTransport transport = new("push");
        DedicatedServerHost host = CreateHost(new MutatingAuthority());
        await using ServerConnection connection = new(
            transport, host, new TransportJsonCodec(), new LengthPrefixedMessageFramer(), WithoutLiveness);

        connection.Enqueue(message);
        Assert.True(await connection.FlushOutboundAsync(TimeSpan.FromSeconds(10)));

        byte[] frame = Assert.Single(transport.Frames);
        Assert.True(new LengthPrefixedMessageFramer(1 << 26).TryDeframe(frame, out byte[]? payload, out _));
        Assert.Equal(message.MessageKind, Codec.Decode(payload!).Envelope.MessageKind);
        Assert.False(connection.IsFaulted);
    }

    private sealed class NullAuthority : IAuthoritativeSessionHost
    {
        public bool DevCommandsAllowed => false;

        public JoinAcceptedMessage JoinSession(string requestedSessionId, string displayName) => throw new NotSupportedException();

        public AuthoritativeActionResult ProcessAction(PlayerActionMessage message) => throw new NotSupportedException();

        public ServerSnapshotEnvelope ExportSnapshot(bool includeWorldSnapshot = false) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData(ChatChannel.Party, 4)]
    [InlineData(ChatChannel.Command, 5)]
    public void ThePartyAndCommandChannels_HaveTheirWireValues_AndPassTheChatHandler(ChatChannel channel, byte value)
    {
        Assert.Equal(value, (byte)channel);
        Assert.True(Enum.IsDefined(channel));
        ServerRoutingContext context = new("connection-1")
        {
            LastJoinRequest = new ClientJoinRequestMessage("session-a", "player-a")
        };

        new ChatMessageHandler().Handle(context, new ClientChatMessage(channel, "hello"));

        ClientChatMessage queued = Assert.Single(context.PendingChats);
        Assert.Equal(channel, queued.Channel);
        Assert.Null(queued.Target);
    }
}
