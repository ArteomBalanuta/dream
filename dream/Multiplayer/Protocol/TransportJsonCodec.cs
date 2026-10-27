using System.Text.Json;

namespace Dream.Multiplayer.Protocol;


public sealed class TransportJsonCodec : IProtocolCodec
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    static TransportJsonCodec()
    {
        SerializerOptions.Converters.Add(new Vector3JsonConverter());
    }

    public byte[] Encode<TMessage>(TransportEnvelope envelope, TMessage message)
        where TMessage : IProtocolMessage
    {
        ArgumentNullException.ThrowIfNull(message);
        if (envelope.ProtocolVersion != TransportEnvelope.CurrentProtocolVersion)
        {
            throw new InvalidOperationException(
                $"Cannot encode protocol version {envelope.ProtocolVersion}. " +
                $"Supported version: {TransportEnvelope.CurrentProtocolVersion}.");
        }

        if (!string.Equals(envelope.MessageKind, message.MessageKind, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Envelope message kind '{envelope.MessageKind}' does not match payload kind '{message.MessageKind}'.");
        }

        WireEnvelope wireEnvelope = new()
        {
            ProtocolVersion = envelope.ProtocolVersion,
            MessageKind = envelope.MessageKind,
            Sequence = envelope.Sequence,
            Payload = JsonSerializer.SerializeToElement(message, SerializerOptions)
        };

        return JsonSerializer.SerializeToUtf8Bytes(wireEnvelope, SerializerOptions);
    }

    public DecodedProtocolMessage Decode(ReadOnlySpan<byte> payload)
    {
        WireEnvelope wireEnvelope = JsonSerializer.Deserialize<WireEnvelope>(payload, SerializerOptions)
            ?? throw new InvalidOperationException("Failed to deserialize transport envelope.");

        TransportEnvelope envelope = new(
            wireEnvelope.ProtocolVersion,
            wireEnvelope.MessageKind ?? throw new InvalidOperationException("Envelope message kind is required."),
            wireEnvelope.Sequence);

        if (envelope.ProtocolVersion != TransportEnvelope.CurrentProtocolVersion)
        {
            throw new InvalidOperationException(
                $"Unsupported protocol version {envelope.ProtocolVersion}. " +
                $"Supported version: {TransportEnvelope.CurrentProtocolVersion}.");
        }

        IProtocolMessage message = wireEnvelope.MessageKind switch
        {
            "client.hello" => DeserializeMessage<ClientHelloMessage>(wireEnvelope.Payload),
            "client.join-request" => DeserializeMessage<ClientJoinRequestMessage>(wireEnvelope.Payload),
            "player.command" => DeserializeMessage<PlayerCommandMessage>(wireEnvelope.Payload),
            "player.action" => DeserializeMessage<PlayerActionMessage>(wireEnvelope.Payload),
            "client.world-resync" => DeserializeMessage<ClientWorldResyncRequestMessage>(wireEnvelope.Payload),
            "server.hello" => DeserializeMessage<ServerHelloMessage>(wireEnvelope.Payload),
            "server.join-accepted" => DeserializeMessage<JoinAcceptedMessage>(wireEnvelope.Payload),
            "server.join-rejected" => DeserializeMessage<JoinRejectedMessage>(wireEnvelope.Payload),
            "server.snapshot" => DeserializeMessage<ServerSnapshotMessage>(wireEnvelope.Payload),
            "server.action" => DeserializeMessage<ServerActionMessage>(wireEnvelope.Payload),
            _ => throw new InvalidOperationException($"Unknown protocol message kind '{wireEnvelope.MessageKind}'.")
        };

        return new DecodedProtocolMessage(envelope, message);
    }

    private static TMessage DeserializeMessage<TMessage>(JsonElement payload)
    {
        TMessage? message = payload.Deserialize<TMessage>(SerializerOptions);
        return message ?? throw new InvalidOperationException($"Failed to deserialize message '{typeof(TMessage).Name}'.");
    }

    private sealed class WireEnvelope
    {
        public int ProtocolVersion { get; init; }

        public string? MessageKind { get; init; }

        public ulong Sequence { get; init; }

        public JsonElement Payload { get; init; }
    }
}
