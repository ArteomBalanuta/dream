using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Combat;
using Dream.Simulation.Inventory;
using Dream.Simulation.Players;
using Dream.Simulation.World;

namespace Dream.Tests.Multiplayer.Protocol;


public class ProtocolFoundationTests
{
    [Fact]
    public void TransportJsonCodec_RoundTrips_PlayerCommandMessage()
    {
        TransportJsonCodec codec = new();
        PlayerCommandMessage message = new(
            new PlayerCommandFrame(
                new PlayerSessionId("session-a"),
                42,
                new PlayerModeSwitchIntent(PlayerInteractionMode.Combat),
                new PlayerMoveIntent(1f, -1f, 0f, JumpPressed: true, FreezePressed: false, ToggleNoclipPressed: false),
                new PlayerLookIntent(3f, -2f),
                PrimaryUsePressed: true));
        TransportEnvelope envelope = new(TransportEnvelope.CurrentProtocolVersion, message.MessageKind, 99);

        byte[] payload = codec.Encode(envelope, message);
        DecodedProtocolMessage decoded = codec.Decode(payload);

        Assert.Equal(TransportEnvelope.CurrentProtocolVersion, decoded.Envelope.ProtocolVersion);
        Assert.Equal("player.command", decoded.Envelope.MessageKind);
        Assert.Equal((ulong)99, decoded.Envelope.Sequence);

        PlayerCommandMessage roundTripped = Assert.IsType<PlayerCommandMessage>(decoded.Message);
        Assert.Equal("session-a", roundTripped.CommandFrame.SessionId.Value);
        Assert.Equal((ulong)42, roundTripped.CommandFrame.Sequence);
        Assert.Equal(1f, roundTripped.CommandFrame.MoveIntent.ForwardAxis);
        Assert.Equal(-1f, roundTripped.CommandFrame.MoveIntent.StrafeAxis);
        Assert.True(roundTripped.CommandFrame.PrimaryUsePressed);
        Assert.Equal(PlayerInteractionMode.Combat, roundTripped.CommandFrame.RequestedMode.Mode);
    }

    [Fact]
    public void TransportJsonCodec_RoundTrips_PlayerCommandMessage_WithClientFacing()
    {
        TransportJsonCodec codec = new();
        PlayerCommandMessage message = new(
            new PlayerCommandFrame(
                new PlayerSessionId("session-a"),
                43,
                PlayerModeSwitchIntent.None,
                new PlayerMoveIntent(1f, 0f),
                new PlayerLookIntent(0f, 0f, Facing: Vector3.UnitX),
                PrimaryUsePressed: false));
        TransportEnvelope envelope = new(TransportEnvelope.CurrentProtocolVersion, message.MessageKind, 100);

        byte[] payload = codec.Encode(envelope, message);
        DecodedProtocolMessage decoded = codec.Decode(payload);

        PlayerCommandMessage roundTripped = Assert.IsType<PlayerCommandMessage>(decoded.Message);
        Assert.Equal(0f, roundTripped.CommandFrame.LookIntent.YawDelta);
        Assert.Equal(0f, roundTripped.CommandFrame.LookIntent.PitchDelta);
        Assert.NotNull(roundTripped.CommandFrame.LookIntent.Facing);
        Assert.Equal(1f, roundTripped.CommandFrame.LookIntent.Facing.Value.X, 3);
        Assert.Equal(0f, roundTripped.CommandFrame.LookIntent.Facing.Value.Z, 3);
    }

    [Fact]
    public void TransportJsonCodec_RoundTrips_PlayerActionMessage_WithTypedPayload()
    {
        TransportJsonCodec codec = new();
        PlayerActionMessage message = new(
            new PlayerSessionId("session-a"),
            77,
            new BuildUseIntent(new Vector3(4f, 2f, 8f)));
        TransportEnvelope envelope = new(TransportEnvelope.CurrentProtocolVersion, message.MessageKind, 100);

        byte[] payload = codec.Encode(envelope, message);
        DecodedProtocolMessage decoded = codec.Decode(payload);

        PlayerActionMessage roundTripped = Assert.IsType<PlayerActionMessage>(decoded.Message);
        BuildUseIntent build = Assert.IsType<BuildUseIntent>(roundTripped.Payload);
        Assert.Equal("session-a", roundTripped.SessionId.Value);
        Assert.Equal((ulong)77, roundTripped.ActionSequence);
        Assert.Equal(4f, build.PlacementTarget.X);
        Assert.Equal(2f, build.PlacementTarget.Y);
        Assert.Equal(8f, build.PlacementTarget.Z);
    }

    [Fact]
    public void TransportJsonCodec_RoundTrips_PlayerStatsUpdateActionAndResultPayloads()
    {
        TransportJsonCodec codec = new();
        PlayerActionMessage actionMessage = new(
            new PlayerSessionId("session-a"),
            78,
            new PlayerStatsUpdateIntent(new PlayerCombatStatsSnapshot(19, 3.75f, 15)));
        ServerActionMessage resultMessage = new(
            new AuthoritativeActionResult(
                "session-a",
                78,
                22,
                Accepted: true,
                Payload: new PlayerStatsUpdateResult(new PlayerCombatStatsSnapshot(19, 3.75f, 15))));

        DecodedProtocolMessage decodedAction = codec.Decode(
            codec.Encode(new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, actionMessage.MessageKind, 102), actionMessage));
        DecodedProtocolMessage decodedResult = codec.Decode(
            codec.Encode(new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, resultMessage.MessageKind, 103), resultMessage));

        PlayerActionMessage roundTrippedAction = Assert.IsType<PlayerActionMessage>(decodedAction.Message);
        PlayerStatsUpdateIntent actionPayload = Assert.IsType<PlayerStatsUpdateIntent>(roundTrippedAction.Payload);
        Assert.Equal(19, actionPayload.Stats.Strength);
        ServerActionMessage roundTrippedResult = Assert.IsType<ServerActionMessage>(decodedResult.Message);
        PlayerStatsUpdateResult resultPayload = Assert.IsType<PlayerStatsUpdateResult>(roundTrippedResult.Result.Payload);
        Assert.Equal(3.75f, resultPayload.Stats.AttackSpeed, 3);
    }

    [Fact]
    public void TransportJsonCodec_RoundTrips_InventoryEquipAction_And_InventoryChangedResult()
    {
        TransportJsonCodec codec = new();
        PlayerActionMessage actionMessage = new(
            new PlayerSessionId("session-a"),
            81,
            new InventoryEquipIntent(3));
        ServerActionMessage resultMessage = new(
            new AuthoritativeActionResult(
                "session-a",
                81,
                30,
                Accepted: true,
                Payload: new InventoryChangedResult(new PlayerInventorySnapshot(
                    "Player A",
                    4,
                    4,
                    [new InventorySlotSnapshot(3, new InventoryItemSnapshot(101, "tool.builder-hammer", "Builder Hammer", "held.tool.builder", 1, "Builder", ["Role: Building"]), true)],
                    new InventorySlotSnapshot(InventoryConstants.EquippedSlotIndex, new InventoryItemSnapshot(101, "tool.builder-hammer", "Builder Hammer", "held.tool.builder", 1, "Builder", ["Role: Building"]), true)))));

        DecodedProtocolMessage decodedAction = codec.Decode(
            codec.Encode(new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, actionMessage.MessageKind, 105), actionMessage));
        DecodedProtocolMessage decodedResult = codec.Decode(
            codec.Encode(new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, resultMessage.MessageKind, 106), resultMessage));

        PlayerActionMessage roundTrippedAction = Assert.IsType<PlayerActionMessage>(decodedAction.Message);
        InventoryEquipIntent actionPayload = Assert.IsType<InventoryEquipIntent>(roundTrippedAction.Payload);
        Assert.Equal(3, actionPayload.SlotIndex);
        ServerActionMessage roundTrippedResult = Assert.IsType<ServerActionMessage>(decodedResult.Message);
        InventoryChangedResult resultPayload = Assert.IsType<InventoryChangedResult>(roundTrippedResult.Result.Payload);
        Assert.Equal("Player A", resultPayload.Inventory.PlayerName);
        Assert.Equal("held.tool.builder", resultPayload.Inventory.EquippedSlot?.Item?.VisualId);
    }

    [Fact]
    public void TransportJsonCodec_RoundTrips_JoinAcceptedMessage_WithRoster()
    {
        TransportJsonCodec codec = new();
        JoinAcceptedMessage message = new(
            "session-a",
            101,
            22,
            InitialWorld: null,
            Roster:
            [
                new PlayerRosterEntry("session-a", 101, "Player A"),
                new PlayerRosterEntry("session-b", 102, "Player B")
            ]);

        DecodedProtocolMessage decoded = codec.Decode(
            codec.Encode(new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, message.MessageKind, 104), message));

        JoinAcceptedMessage roundTripped = Assert.IsType<JoinAcceptedMessage>(decoded.Message);
        Assert.Equal("session-a", roundTripped.SessionId);
        Assert.Collection(
            roundTripped.EffectiveRoster,
            first =>
            {
                Assert.Equal("session-a", first.SessionId);
                Assert.Equal("Player A", first.DisplayName);
            },
            second =>
            {
                Assert.Equal("session-b", second.SessionId);
                Assert.Equal("Player B", second.DisplayName);
            });
    }

    [Fact]
    public void TransportJsonCodec_RoundTrips_JoinAcceptedMessage_WithInventory()
    {
        TransportJsonCodec codec = new();
        JoinAcceptedMessage message = new(
            "session-a",
            101,
            22,
            InitialWorld: null,
            Roster: null,
            Inventory: new PlayerInventorySnapshot(
                "Player A",
                4,
                4,
                [new InventorySlotSnapshot(0, new InventoryItemSnapshot(101, "tool.builder-hammer", "Builder Hammer", "held.tool.builder", 1, "Builder", ["Role: Building"]))],
                new InventorySlotSnapshot(InventoryConstants.EquippedSlotIndex, new InventoryItemSnapshot(101, "tool.builder-hammer", "Builder Hammer", "held.tool.builder", 1, "Builder", ["Role: Building"]), true)));

        DecodedProtocolMessage decoded = codec.Decode(
            codec.Encode(new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, message.MessageKind, 107), message));

        JoinAcceptedMessage roundTripped = Assert.IsType<JoinAcceptedMessage>(decoded.Message);
        Assert.Equal("Player A", roundTripped.Inventory?.PlayerName);
        Assert.Equal("Builder Hammer", roundTripped.Inventory?.EquippedSlot?.Item?.DisplayName);
    }

    [Fact]
    public void LengthPrefixedMessageFramer_DeframesSingleBufferedPayload()
    {
        LengthPrefixedMessageFramer framer = new();
        byte[] framed = framer.Frame([10, 20, 30, 40]);

        bool complete = framer.TryDeframe(framed, out byte[]? payload, out int bytesConsumed);

        Assert.True(complete);
        Assert.NotNull(payload);
        Assert.Equal(new byte[] { 10, 20, 30, 40 }, payload);
        Assert.Equal(framed.Length, bytesConsumed);
    }

    [Fact]
    public void ProtocolRouter_RoutesServerAndClientMessagesThroughTypedHandlers()
    {
        RecordingServerHandler serverJoinHandler = new("client.join-request");
        RecordingClientHandler clientSnapshotHandler = new("server.snapshot");
        ProtocolRouter router = new([serverJoinHandler], [clientSnapshotHandler]);

        ServerRoutingContext serverContext = new("connection-1");
        ClientRoutingContext clientContext = new();

        router.RouteServerInbound(serverContext, new ClientJoinRequestMessage("session-a", "player-a"));
        router.RouteClientInbound(clientContext, new ServerSnapshotMessage(
            new ServerSnapshotEnvelope(
                12,
                new Dictionary<string, ulong>(),
                Array.Empty<ActorSnapshot>(),
                PlayerInteractionMode.Building,
                null,
                Array.Empty<WorldMutationDelta>(),
                Array.Empty<DamageEventDelta>())));

        Assert.Equal("client.join-request", serverJoinHandler.LastHandledKind);
        Assert.Equal("connection-1", serverContext.ConnectionId);
        Assert.Equal("server.snapshot", clientSnapshotHandler.LastHandledKind);
        Assert.Equal((ulong)12, clientContext.LastSnapshot?.ServerTick);
    }

    [Fact]
    public void TransportJsonCodec_RoundTrips_ServerSnapshotMessage_WithInteractionMode()
    {
        TransportJsonCodec codec = new();
        ServerSnapshotMessage message = new(
            new ServerSnapshotEnvelope(
                21,
                new Dictionary<string, ulong>(),
                [
                    new ActorSnapshot(
                        7,
                        CreatureKind.Player,
                        "session-b",
                        new Vector3(4f, 1.5f, 4f),
                        -Vector3.UnitZ,
                        90,
                        100,
                        true,
                        PlayerInteractionMode.Combat,
                        new PlayerCombatStatsSnapshot(16, 3f, 11),
                        "player-b",
                        new PlayerProgressionSnapshot(2, 14, 1023))
                ],
                PlayerInteractionMode.Combat,
                null,
                Array.Empty<WorldMutationDelta>(),
                Array.Empty<DamageEventDelta>()));
        TransportEnvelope envelope = new(TransportEnvelope.CurrentProtocolVersion, message.MessageKind, 101);

        byte[] payload = codec.Encode(envelope, message);
        DecodedProtocolMessage decoded = codec.Decode(payload);

        ServerSnapshotMessage roundTripped = Assert.IsType<ServerSnapshotMessage>(decoded.Message);
        Assert.Equal(PlayerInteractionMode.Combat, roundTripped.Snapshot.InteractionMode);
        ActorSnapshot actor = Assert.Single(roundTripped.Snapshot.Actors);
        Assert.Equal(PlayerInteractionMode.Combat, actor.InteractionMode);
        Assert.Equal(16, actor.CombatStats?.Strength);
        Assert.Equal("player-b", actor.DisplayName);
        Assert.Equal(2, actor.Progression?.Level);
    }

    [Fact]
    public void TransportJsonCodec_Decode_RejectsUnsupportedProtocolVersion()
    {
        TransportJsonCodec codec = new();
        ClientHelloMessage message = new("client-a");
        byte[] payload = codec.Encode(new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, message.MessageKind, 1), message);
        string mutatedJson = System.Text.Encoding.UTF8.GetString(payload).Replace("\"protocolVersion\":1", "\"protocolVersion\":999", StringComparison.Ordinal);
        payload = System.Text.Encoding.UTF8.GetBytes(mutatedJson);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => codec.Decode(payload));
        Assert.Contains("protocol version", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TransportJsonCodec_Encode_RejectsEnvelopeAndPayloadKindMismatch()
    {
        TransportJsonCodec codec = new();
        PlayerCommandMessage message = new(
            new PlayerCommandFrame(
                new PlayerSessionId("session-a"),
                5,
                new PlayerModeSwitchIntent(null),
                new PlayerMoveIntent(0f, 0f, 0f),
                new PlayerLookIntent(0f, 0f),
                PrimaryUsePressed: false));

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            codec.Encode(new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, "server.snapshot", 1), message));
        Assert.Contains("message kind", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LengthPrefixedMessageFramer_RejectsFramesLargerThanConfiguredMaximum()
    {
        LengthPrefixedMessageFramer framer = new(maxPayloadBytes: 8);
        byte[] framed = new byte[4];
        BitConverter.GetBytes(12).CopyTo(framed, 0);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            framer.TryDeframe(framed, out _, out _));
        Assert.Contains("maximum", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class RecordingServerHandler(string messageKind) : IServerMessageHandler
    {
        public string MessageKind => messageKind;

        public string? LastHandledKind { get; private set; }

        public void Handle(ServerRoutingContext context, IClientToServerMessage message)
        {
            LastHandledKind = message.MessageKind;
        }
    }

    private sealed class RecordingClientHandler(string messageKind) : IClientMessageHandler
    {
        public string MessageKind => messageKind;

        public string? LastHandledKind { get; private set; }

        public void Handle(ClientRoutingContext context, IServerToClientMessage message)
        {
            LastHandledKind = message.MessageKind;
            if (message is ServerSnapshotMessage snapshotMessage)
            {
                context.LastSnapshot = snapshotMessage.Snapshot;
            }
        }
    }
}
