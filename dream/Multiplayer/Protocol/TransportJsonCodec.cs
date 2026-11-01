using System.Buffers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Dream.Multiplayer.Protocol;


public sealed class TransportJsonCodec : IProtocolCodec
{
    // Protocol v2: null properties are left out (a missing property decodes to the same default
    // as an explicit null), and properties derived from others are never written. Protocol v3
    // keeps that and adds the compact world and chunked world transfers (SCALE-1).
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers = { RemoveDerivedProperties }
        }
    };

    // The hand-written converters (WIRE-10, WIRE-11) cover the hot types: the client command and
    // the server snapshot with its actors, world mutations and damage events. They are registered here rather than with [JsonConverter] on the
    // types, so the reflection serializer stays available as an independent byte-identity
    // reference for them, and they write exactly what these options' reflection contracts wrote.
    static TransportJsonCodec()
    {
        SerializerOptions.Converters.Add(new Vector3JsonConverter());
        SerializerOptions.Converters.Add(new PlayerCommandMessageJsonConverter());
        SerializerOptions.Converters.Add(new PlayerCommandFrameJsonConverter());
        SerializerOptions.Converters.Add(new PlayerSessionIdJsonConverter());
        SerializerOptions.Converters.Add(new PlayerModeSwitchIntentJsonConverter());
        SerializerOptions.Converters.Add(new PlayerMoveIntentJsonConverter());
        SerializerOptions.Converters.Add(new PlayerLookIntentJsonConverter());
        SerializerOptions.Converters.Add(new ActorSnapshotJsonConverter());
        SerializerOptions.Converters.Add(new ActorSnapshotListJsonConverter());
        SerializerOptions.Converters.Add(new PlayerCombatStatsSnapshotJsonConverter());
        SerializerOptions.Converters.Add(new PlayerProgressionSnapshotJsonConverter());

        // WIRE-11: the rest of the server snapshot, so a snapshot encode no longer reaches the
        // reflection serializer (except for the full world, sent only on join and resync).
        SerializerOptions.Converters.Add(new ServerSnapshotMessageJsonConverter());
        SerializerOptions.Converters.Add(new ServerSnapshotEnvelopeJsonConverter());
        SerializerOptions.Converters.Add(new WorldMutationDeltaJsonConverter());
        SerializerOptions.Converters.Add(new DamageEventDeltaJsonConverter());

        // Protocol v3 (SCALE-1): the compact world, and the chunks a join or resync world is sent in.
        SerializerOptions.Converters.Add(new WorldSnapshotJsonConverter());
        SerializerOptions.Converters.Add(new WorldChunkMessageJsonConverter());
        SerializerOptions.Converters.Add(new TerrainChunkMessageJsonConverter());
    }

    // MessageKind is a per-type constant that the envelope already carries; the [JsonIgnore] on
    // IProtocolMessage does not reach the concrete records' own MessageKind properties.
    // EffectiveRoster and EffectiveInitialActors only replace a null Roster or InitialActors
    // with an empty list. None has a constructor parameter, so decoding never read them.
    private static void RemoveDerivedProperties(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object || !typeof(IProtocolMessage).IsAssignableFrom(typeInfo.Type))
        {
            return;
        }

        bool isJoinAccepted = typeInfo.Type == typeof(JoinAcceptedMessage);
        for (int i = typeInfo.Properties.Count - 1; i >= 0; i--)
        {
            string? memberName = (typeInfo.Properties[i].AttributeProvider as MemberInfo)?.Name;
            if (memberName == nameof(IProtocolMessage.MessageKind)
                || (isJoinAccepted
                    && memberName is nameof(JoinAcceptedMessage.EffectiveRoster) or nameof(JoinAcceptedMessage.EffectiveInitialActors)))
            {
                typeInfo.Properties.RemoveAt(i);
            }
        }
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
        if (envelope.Ack is TransportAck ack)
        {
            // SCALE-2: {"<session>":n}, or {} when the session has nothing accepted yet.
            writer.WritePropertyName("ack"u8);
            writer.WriteStartObject();
            if (ack.SessionId is string sessionId)
            {
                writer.WriteNumber(sessionId, ack.Sequence);
            }

            writer.WriteEndObject();
        }

        writer.WritePropertyName("payload"u8);
    }

    // The header ack, read like the snapshot's own lastAcceptedCommandSequences map: at most one
    // entry is expected, and a repeated key keeps its last value.
    private static TransportAck ReadAck(ref Utf8JsonReader reader)
    {
        reader.Read();
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Envelope ack must be a JSON object.");
        }

        string? sessionId = null;
        ulong sequence = 0;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            sessionId = reader.GetString();
            reader.Read();
            if (reader.TokenType != JsonTokenType.Number || !reader.TryGetUInt64(out sequence))
            {
                throw new JsonException("Envelope ack sequence must be an unsigned 64-bit integer.");
            }
        }

        if (reader.TokenType != JsonTokenType.EndObject)
        {
            throw new JsonException("Envelope ack must be a JSON object of sequences.");
        }

        return new TransportAck(sessionId, sequence);
    }

    // A shared snapshot body carries no sequence map of its own; the receiving connection's ack
    // from the header becomes it, so every consumer still reads LastAcceptedCommandSequences.
    private static IProtocolMessage ApplyAck(IProtocolMessage message, TransportAck? ack)
    {
        if (ack is not TransportAck value || message is not ServerSnapshotMessage snapshot)
        {
            return message;
        }

        Dictionary<string, ulong> sequences = new(value.SessionId is null ? 0 : 1, StringComparer.Ordinal);
        if (value.SessionId is string sessionId)
        {
            sequences[sessionId] = value.Sequence;
        }

        return snapshot with
        {
            SharedBody = true,
            Snapshot = snapshot.Snapshot with { LastAcceptedCommandSequences = sequences }
        };
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
        return Decode(payload, fromClient: false);
    }

    public DecodedProtocolMessage DecodeFromClient(ReadOnlySpan<byte> payload)
    {
        return Decode(payload, fromClient: true);
    }

    // The kinds only a server sends. A client frame of one of these is refused from its envelope,
    // before the payload (which may be a large world chunk or snapshot) is decoded.
    public static bool IsServerOnlyKind(string messageKind)
    {
        return messageKind is "server.hello" or "server.join-accepted" or "server.join-rejected"
            or "server.snapshot" or "server.action" or WorldChunkMessage.Kind or TerrainChunkMessage.Kind
            or InventoryPushMessage.Kind;
    }

    private DecodedProtocolMessage Decode(ReadOnlySpan<byte> payload, bool fromClient)
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
        TransportAck? ack = null;
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
                    JsonTokenType.String => ReadMessageKind(ref reader),
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
            else if (reader.ValueTextEquals("ack"u8))
            {
                ack = ReadAck(ref reader);
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
            sequence)
        {
            Ack = ack
        };

        if (envelope.ProtocolVersion != TransportEnvelope.CurrentProtocolVersion)
        {
            throw new InvalidOperationException(
                $"Unsupported protocol version {envelope.ProtocolVersion}. " +
                $"Supported version: {TransportEnvelope.CurrentProtocolVersion}.");
        }

        if (fromClient && IsServerOnlyKind(envelope.MessageKind))
        {
            throw new InvalidOperationException(
                $"Received server-only message kind '{messageKind}' from a client; its payload was not decoded.");
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
            WorldChunkMessage.Kind => DeserializeMessage<WorldChunkMessage>(ref payloadReader, hasPayload),
            TerrainChunkMessage.Kind => DeserializeMessage<TerrainChunkMessage>(ref payloadReader, hasPayload),
            InventoryPushMessage.Kind => DeserializeMessage<InventoryPushMessage>(ref payloadReader, hasPayload),
            _ => throw new InvalidOperationException($"Unknown protocol message kind '{messageKind}'.")
        };

        return new DecodedProtocolMessage(envelope, ApplyAck(message, ack));
    }

    // The known kinds decode to their shared literals instead of a new string per message
    // (WIRE-11); ValueTextEquals compares the unescaped value, so an escaped spelling still matches.
    private static readonly string[] KnownMessageKinds =
    [
        "server.snapshot", "player.command", "server.action", "player.action", "client.hello",
        "client.join-request", "client.world-resync", "server.hello", "server.join-accepted", "server.join-rejected",
        WorldChunkMessage.Kind, TerrainChunkMessage.Kind, InventoryPushMessage.Kind
    ];

    private static readonly byte[][] KnownMessageKindsUtf8 = Array.ConvertAll(KnownMessageKinds, System.Text.Encoding.UTF8.GetBytes);

    private static string? ReadMessageKind(ref Utf8JsonReader reader)
    {
        for (int i = 0; i < KnownMessageKindsUtf8.Length; i++)
        {
            if (reader.ValueTextEquals(KnownMessageKindsUtf8[i]))
            {
                return KnownMessageKinds[i];
            }
        }

        return reader.GetString();
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
