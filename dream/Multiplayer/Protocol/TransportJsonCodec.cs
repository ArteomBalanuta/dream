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
        Utf8JsonReader reader = new(payload);
        if (!reader.Read())
        {
            throw new JsonException("Transport envelope is empty.");
        }

        if (reader.TokenType == JsonTokenType.Null)
        {
            throw new InvalidOperationException("Failed to deserialize transport envelope.");
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"Transport envelope must be a JSON object, found {reader.TokenType}.");
        }

        int protocolVersion = 0;
        string? messageKind = null;
        ulong sequence = 0;
        bool hasPayload = false;
        Utf8JsonReader payloadReader = default;

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("protocolVersion"u8))
            {
                reader.Read();
                if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out protocolVersion))
                {
                    throw new JsonException("Envelope protocolVersion must be a 32-bit integer.");
                }
            }
            else if (reader.ValueTextEquals("messageKind"u8))
            {
                reader.Read();
                messageKind = reader.TokenType switch
                {
                    JsonTokenType.String => reader.GetString(),
                    JsonTokenType.Null => null,
                    _ => throw new JsonException("Envelope messageKind must be a string.")
                };
            }
            else if (reader.ValueTextEquals("sequence"u8))
            {
                reader.Read();
                if (reader.TokenType != JsonTokenType.Number || !reader.TryGetUInt64(out sequence))
                {
                    throw new JsonException("Envelope sequence must be an unsigned 64-bit integer.");
                }
            }
            else if (reader.ValueTextEquals("payload"u8))
            {
                payloadReader = reader;
                hasPayload = true;
                reader.Skip();
            }
            else
            {
                reader.Skip();
            }
        }

        if (reader.TokenType != JsonTokenType.EndObject || reader.Read())
        {
            throw new JsonException("Transport envelope must be a single JSON object.");
        }

        TransportEnvelope envelope = new(
            protocolVersion,
            messageKind ?? throw new InvalidOperationException("Envelope message kind is required."),
            sequence);

        if (envelope.ProtocolVersion != TransportEnvelope.CurrentProtocolVersion)
        {
            throw new InvalidOperationException(
                $"Unsupported protocol version {envelope.ProtocolVersion}. " +
                $"Supported version: {TransportEnvelope.CurrentProtocolVersion}.");
        }

        IProtocolMessage message = messageKind switch
        {
            "client.hello" => DeserializeMessage<ClientHelloMessage>(ref payloadReader, hasPayload),
            "client.join-request" => DeserializeMessage<ClientJoinRequestMessage>(ref payloadReader, hasPayload),
            "player.command" => DeserializeMessage<PlayerCommandMessage>(ref payloadReader, hasPayload),
            "player.action" => DeserializeMessage<PlayerActionMessage>(ref payloadReader, hasPayload),
            "client.world-resync" => DeserializeMessage<ClientWorldResyncRequestMessage>(ref payloadReader, hasPayload),
            "server.hello" => DeserializeMessage<ServerHelloMessage>(ref payloadReader, hasPayload),
            "server.join-accepted" => DeserializeMessage<JoinAcceptedMessage>(ref payloadReader, hasPayload),
            "server.join-rejected" => DeserializeMessage<JoinRejectedMessage>(ref payloadReader, hasPayload),
            "server.snapshot" => DeserializeMessage<ServerSnapshotMessage>(ref payloadReader, hasPayload),
            "server.action" => DeserializeMessage<ServerActionMessage>(ref payloadReader, hasPayload),
            _ => throw new InvalidOperationException($"Unknown protocol message kind '{messageKind}'.")
        };

        return new DecodedProtocolMessage(envelope, message);
    }

    // payloadReader is a checkpoint positioned on the "payload" property name; a missing payload
    // throws the same parameterless InvalidOperationException an undefined JsonElement used to.
    private static TMessage DeserializeMessage<TMessage>(ref Utf8JsonReader payloadReader, bool hasPayload)
    {
        if (!hasPayload)
        {
            throw new InvalidOperationException();
        }

        TMessage? message = JsonSerializer.Deserialize<TMessage>(ref payloadReader, SerializerOptions);
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
