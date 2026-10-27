using System.Buffers;
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

    // Matches the writer JsonSerializer.SerializeToUtf8Bytes builds from SerializerOptions, so the
    // single-pass output is byte-identical to the old WireEnvelope encode.
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = SerializerOptions.Encoder,
        Indented = SerializerOptions.WriteIndented
    };

    public byte[] Encode<TMessage>(TransportEnvelope envelope, TMessage message)
        where TMessage : IProtocolMessage
    {
        ArrayBufferWriter<byte> buffer = new();
        Encode(envelope, message, buffer);
        return buffer.WrittenSpan.ToArray();
    }

    // Property order and names match the old WireEnvelope serialized with the camelCase policy:
    // protocolVersion, messageKind, sequence, payload.
    public void Encode<TMessage>(TransportEnvelope envelope, TMessage message, IBufferWriter<byte> destination)
        where TMessage : IProtocolMessage
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(destination);
        ValidateProtocolVersion(envelope);
        if (!string.Equals(envelope.MessageKind, message.MessageKind, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Envelope message kind '{envelope.MessageKind}' does not match payload kind '{message.MessageKind}'.");
        }

        Utf8JsonWriter writer = RentWriter(destination);
        try
        {
            WriteEnvelopeHeader(writer, envelope);
            JsonSerializer.Serialize(writer, message, SerializerOptions);
            writer.WriteEndObject();
            writer.Flush();
        }
        finally
        {
            ReturnWriter(writer, destination);
        }
    }

    public void EncodePayload<TMessage>(TMessage message, IBufferWriter<byte> destination)
        where TMessage : IProtocolMessage
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(destination);

        Utf8JsonWriter writer = RentWriter(destination);
        try
        {
            JsonSerializer.Serialize(writer, message, SerializerOptions);
            writer.Flush();
        }
        finally
        {
            ReturnWriter(writer, destination);
        }
    }

    public void WriteEnvelope(TransportEnvelope envelope, ReadOnlySpan<byte> payloadJson, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ValidateProtocolVersion(envelope);
        if (payloadJson.IsEmpty)
        {
            throw new ArgumentException("Payload JSON must not be empty.", nameof(payloadJson));
        }

        Utf8JsonWriter writer = RentWriter(destination);
        try
        {
            WriteEnvelopeHeader(writer, envelope);
            writer.WriteRawValue(payloadJson, skipInputValidation: true);
            writer.WriteEndObject();
            writer.Flush();
        }
        finally
        {
            ReturnWriter(writer, destination);
        }
    }

    private static void ValidateProtocolVersion(TransportEnvelope envelope)
    {
        if (envelope.ProtocolVersion != TransportEnvelope.CurrentProtocolVersion)
        {
            throw new InvalidOperationException(
                $"Cannot encode protocol version {envelope.ProtocolVersion}. " +
                $"Supported version: {TransportEnvelope.CurrentProtocolVersion}.");
        }
    }

    // Leaves the writer positioned after the "payload" property name.
    private static void WriteEnvelopeHeader(Utf8JsonWriter writer, TransportEnvelope envelope)
    {
        writer.WriteStartObject();
        writer.WriteNumber("protocolVersion"u8, envelope.ProtocolVersion);
        writer.WriteString("messageKind"u8, envelope.MessageKind);
        writer.WriteNumber("sequence"u8, envelope.Sequence);
        writer.WritePropertyName("payload"u8);
    }

    // A PooledFrameWriter carries a cached Utf8JsonWriter, so steady-state encodes into it allocate
    // nothing; any other destination gets a writer of its own.
    private static Utf8JsonWriter RentWriter(IBufferWriter<byte> destination)
    {
        return destination is PooledFrameWriter pooled
            ? pooled.GetJsonWriter(WriterOptions)
            : new Utf8JsonWriter(destination, WriterOptions);
    }

    private static void ReturnWriter(Utf8JsonWriter writer, IBufferWriter<byte> destination)
    {
        if (destination is not PooledFrameWriter)
        {
            writer.Dispose();
        }
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
}
