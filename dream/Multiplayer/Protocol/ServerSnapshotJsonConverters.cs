using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenTK.Mathematics;
using Dream.Simulation.Combat;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Multiplayer.Protocol;

// Hand-written converters for the server snapshot (WIRE-11). After WIRE-10 the actors no longer
// allocated, but the envelope still went through the reflection serializer: the boxed envelope,
// the WriteStack frame array, and boxed enumerators for the dictionary, mutation and damage-event
// lists (824 B per 24-actor encode). These write the same bytes with nothing allocated, and read
// into exactly sized arrays and a pre-sized dictionary instead of growing List<T>s.
//
// Wire shape (protocol v2, declaration order, nulls left out):
//   {"snapshot":{"serverTick":n,"lastAcceptedCommandSequences":{"<session>":n,...}?,
//    "actors":[actor,...]?,"interactionMode":n,"world":{...}?,"worldMutations":[mutation,...]?,
//    "damageEvents":[damage,...]?,"worldChecksum":n?,"worldChecksumSequence":n}}
//   mutation: {"kind":n,"cellPosition":v,"targetCellPosition":v?,"centerPosition":v?,
//              "materialId":n?,"fracturePieces":[{"center":v,"sideLength":f},...]?,"sequence":n,
//              "actorId":n?}
//   damage:   {"actorId":n,"kind":n,"amount":n,"actorDied":b,"currentHealth":n,"maxHealth":n}
// The world is only sent on join and resync; a nested JsonSerializer call writes it with the
// options' WorldSnapshotJsonConverter (the protocol v3 compact world). Protocol v3 also adds
// "worldTransferId":n? after "snapshot" on a resync carrier whose world follows in chunks.
// SCALE-2 (protocol v4): a broadcast export's actors go out as "actorFrame":{...} in the
// place of "actors" (ActorFrameJson: delta records against the previous broadcast, or a keyframe),
// and a shared broadcast body (ServerSnapshotMessage.SharedBody) leaves out
// "lastAcceptedCommandSequences", which each connection then gets in its envelope header.
internal sealed class ServerSnapshotMessageJsonConverter : JsonConverter<ServerSnapshotMessage>
{
    private static readonly JsonEncodedText Snapshot = JsonEncodedText.Encode("snapshot");
    private static readonly JsonEncodedText WorldTransferId = JsonEncodedText.Encode("worldTransferId");

    public override ServerSnapshotMessage Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        HotPathJson.ExpectStartObject(ref reader, nameof(ServerSnapshotMessage));
        ServerSnapshotEnvelope snapshot = default;
        int? worldTransferId = null;
        while (HotPathJson.NextProperty(ref reader, nameof(ServerSnapshotMessage)))
        {
            if (reader.ValueTextEquals(Snapshot.EncodedUtf8Bytes))
            {
                HotPathJson.ReadValue(ref reader);
                snapshot = ServerSnapshotEnvelopeJsonConverter.ReadEnvelope(ref reader, options);
            }
            else if (reader.ValueTextEquals(WorldTransferId.EncodedUtf8Bytes))
            {
                worldTransferId = HotPathJson.ReadNullableInt32(ref reader);
            }
            else
            {
                HotPathJson.SkipValue(ref reader);
            }
        }

        return new ServerSnapshotMessage(snapshot, worldTransferId);
    }

    public override void Write(Utf8JsonWriter writer, ServerSnapshotMessage value, JsonSerializerOptions options)
    {
        // A shared broadcast body leaves the sequence map to each connection's envelope header;
        // SharedEncodedPayload's encode marks its message so (and may ask for the keyframe twin).
        bool sharedBody = value.SharedBody;
        ServerSnapshotEnvelope snapshot = value.Snapshot;
        if (SharedSnapshotEncodeScope.Applies(value, out bool keyframe))
        {
            sharedBody = true;
            if (keyframe && snapshot.ActorDelta is ActorDeltaContext context)
            {
                snapshot = snapshot with { ActorDelta = context.Keyframe };
            }
        }

        writer.WriteStartObject();
        writer.WritePropertyName(Snapshot);
        ServerSnapshotEnvelopeJsonConverter.WriteEnvelope(writer, snapshot, options, omitSequences: sharedBody);
        if (value.WorldTransferId is int worldTransferId)
        {
            writer.WriteNumber(WorldTransferId, worldTransferId);
        }

        writer.WriteEndObject();
    }
}

internal sealed class ServerSnapshotEnvelopeJsonConverter : JsonConverter<ServerSnapshotEnvelope>
{
    private static readonly JsonEncodedText ServerTick = JsonEncodedText.Encode("serverTick");
    private static readonly JsonEncodedText LastAcceptedCommandSequences = JsonEncodedText.Encode("lastAcceptedCommandSequences");
    private static readonly JsonEncodedText Actors = JsonEncodedText.Encode("actors");
    private static readonly JsonEncodedText ActorFrame = JsonEncodedText.Encode("actorFrame");
    private static readonly JsonEncodedText InteractionMode = JsonEncodedText.Encode("interactionMode");
    private static readonly JsonEncodedText World = JsonEncodedText.Encode("world");
    private static readonly JsonEncodedText WorldMutations = JsonEncodedText.Encode("worldMutations");
    private static readonly JsonEncodedText DamageEvents = JsonEncodedText.Encode("damageEvents");
    private static readonly JsonEncodedText WorldChecksum = JsonEncodedText.Encode("worldChecksum");
    private static readonly JsonEncodedText WorldChecksumSequence = JsonEncodedText.Encode("worldChecksumSequence");

    private static readonly ActorSnapshotListJsonConverter ActorListConverter = new();

    public override ServerSnapshotEnvelope Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return ReadEnvelope(ref reader, options);
    }

    public override void Write(Utf8JsonWriter writer, ServerSnapshotEnvelope value, JsonSerializerOptions options)
    {
        WriteEnvelope(writer, value, options);
    }

    internal static ServerSnapshotEnvelope ReadEnvelope(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        HotPathJson.ExpectStartObject(ref reader, nameof(ServerSnapshotEnvelope));
        ulong serverTick = 0;
        IReadOnlyDictionary<string, ulong>? sequences = null;
        IReadOnlyList<ActorSnapshot>? actors = null;
        ActorWireFrame? actorFrame = null;
        PlayerInteractionMode interactionMode = default;
        WorldSnapshot? world = null;
        IReadOnlyList<WorldMutationDelta>? mutations = null;
        IReadOnlyList<DamageEventDelta>? damageEvents = null;
        ulong? worldChecksum = null;
        ulong worldChecksumSequence = 0;

        while (HotPathJson.NextProperty(ref reader, nameof(ServerSnapshotEnvelope)))
        {
            if (reader.ValueTextEquals(ServerTick.EncodedUtf8Bytes))
            {
                serverTick = HotPathJson.ReadUInt64(ref reader);
            }
            else if (reader.ValueTextEquals(LastAcceptedCommandSequences.EncodedUtf8Bytes))
            {
                HotPathJson.ReadValue(ref reader);
                sequences = reader.TokenType == JsonTokenType.Null ? null : ReadSequences(ref reader);
            }
            else if (reader.ValueTextEquals(Actors.EncodedUtf8Bytes))
            {
                HotPathJson.ReadValue(ref reader);
                actors = reader.TokenType == JsonTokenType.Null
                    ? null
                    : ActorListConverter.Read(ref reader, typeof(IReadOnlyList<ActorSnapshot>), options);
            }
            else if (reader.ValueTextEquals(ActorFrame.EncodedUtf8Bytes))
            {
                HotPathJson.ReadValue(ref reader);
                actorFrame = reader.TokenType == JsonTokenType.Null ? null : ActorFrameJson.Read(ref reader);
            }
            else if (reader.ValueTextEquals(InteractionMode.EncodedUtf8Bytes))
            {
                interactionMode = (PlayerInteractionMode)HotPathJson.ReadInt32(ref reader);
            }
            else if (reader.ValueTextEquals(World.EncodedUtf8Bytes))
            {
                HotPathJson.ReadValue(ref reader);
                world = reader.TokenType == JsonTokenType.Null
                    ? null
                    : JsonSerializer.Deserialize<WorldSnapshot>(ref reader, options);
            }
            else if (reader.ValueTextEquals(WorldMutations.EncodedUtf8Bytes))
            {
                HotPathJson.ReadValue(ref reader);
                mutations = reader.TokenType == JsonTokenType.Null ? null : ReadMutations(ref reader, options);
            }
            else if (reader.ValueTextEquals(DamageEvents.EncodedUtf8Bytes))
            {
                HotPathJson.ReadValue(ref reader);
                damageEvents = reader.TokenType == JsonTokenType.Null ? null : ReadDamageEvents(ref reader);
            }
            else if (reader.ValueTextEquals(WorldChecksum.EncodedUtf8Bytes))
            {
                HotPathJson.ReadValue(ref reader);
                worldChecksum = reader.TokenType == JsonTokenType.Null ? null : GetUInt64(ref reader);
            }
            else if (reader.ValueTextEquals(WorldChecksumSequence.EncodedUtf8Bytes))
            {
                worldChecksumSequence = HotPathJson.ReadUInt64(ref reader);
            }
            else
            {
                HotPathJson.SkipValue(ref reader);
            }
        }

        // A keyframe carries every actor whole, so its actors are known without any earlier
        // state; a delta frame's are left null for the client's ActorStateCache to fill in.
        if (actorFrame != null && actors == null && actorFrame.IsKeyframe)
        {
            actors = ActorFrameJson.Materialize(actorFrame);
        }

        return new ServerSnapshotEnvelope(
            serverTick,
            sequences!,
            actors!,
            interactionMode,
            world,
            mutations!,
            damageEvents!,
            worldChecksum,
            worldChecksumSequence)
        {
            ActorFrame = actorFrame
        };
    }

    internal static void WriteEnvelope(Utf8JsonWriter writer, in ServerSnapshotEnvelope value, JsonSerializerOptions options)
    {
        WriteEnvelope(writer, value, options, omitSequences: false);
    }

    // Actors are written as the first of: a decoded frame (written back unchanged), the server's
    // delta context (a frame built from the full actors), or the legacy full list.
    internal static void WriteEnvelope(Utf8JsonWriter writer, in ServerSnapshotEnvelope value, JsonSerializerOptions options, bool omitSequences)
    {
        writer.WriteStartObject();
        writer.WriteNumber(ServerTick, value.ServerTick);
        if (!omitSequences && value.LastAcceptedCommandSequences is IReadOnlyDictionary<string, ulong> sequences)
        {
            writer.WritePropertyName(LastAcceptedCommandSequences);
            WriteSequences(writer, sequences);
        }

        if (value.ActorFrame is ActorWireFrame frame)
        {
            writer.WritePropertyName(ActorFrame);
            ActorFrameJson.Write(writer, frame);
        }
        else if (value.ActorDelta is ActorDeltaContext context && value.Actors is IReadOnlyList<ActorSnapshot> deltaActors)
        {
            writer.WritePropertyName(ActorFrame);
            ActorFrameJson.Write(writer, context, deltaActors);
        }
        else if (value.Actors is IReadOnlyList<ActorSnapshot> actors)
        {
            writer.WritePropertyName(Actors);
            ActorListConverter.Write(writer, actors, options);
        }

        writer.WriteNumber(InteractionMode, (int)value.InteractionMode);
        if (value.World is WorldSnapshot world)
        {
            writer.WritePropertyName(World);
            JsonSerializer.Serialize(writer, world, options);
        }

        if (value.WorldMutations is IReadOnlyList<WorldMutationDelta> mutations)
        {
            writer.WritePropertyName(WorldMutations);
            writer.WriteStartArray();
            for (int i = 0; i < mutations.Count; i++)
            {
                WorldMutationDeltaJsonConverter.WriteMutation(writer, mutations[i]);
            }

            writer.WriteEndArray();
        }

        if (value.DamageEvents is IReadOnlyList<DamageEventDelta> damageEvents)
        {
            writer.WritePropertyName(DamageEvents);
            writer.WriteStartArray();
            for (int i = 0; i < damageEvents.Count; i++)
            {
                DamageEventDeltaJsonConverter.WriteDamage(writer, damageEvents[i]);
            }

            writer.WriteEndArray();
        }

        if (value.WorldChecksum is ulong worldChecksum)
        {
            writer.WriteNumber(WorldChecksum, worldChecksum);
        }

        writer.WriteNumber(WorldChecksumSequence, value.WorldChecksumSequence);
        writer.WriteEndObject();
    }

    // The reflection serializer writes an IReadOnlyDictionary in its enumeration order, keys as
    // given (no naming policy applies to dictionary keys) and escaped by the writer's encoder. A
    // Dictionary is enumerated with its struct enumerator, which yields the same order as the
    // boxed interface enumerator the reflection serializer used; any other implementation goes
    // through the interface, exactly as before.
    private static void WriteSequences(Utf8JsonWriter writer, IReadOnlyDictionary<string, ulong> sequences)
    {
        writer.WriteStartObject();
        if (sequences is Dictionary<string, ulong> dictionary)
        {
            foreach (KeyValuePair<string, ulong> entry in dictionary)
            {
                writer.WriteNumber(entry.Key, entry.Value);
            }
        }
        else
        {
            foreach (KeyValuePair<string, ulong> entry in sequences)
            {
                writer.WriteNumber(entry.Key, entry.Value);
            }
        }

        writer.WriteEndObject();
    }

    // Read like the reflection serializer reads IReadOnlyDictionary<string, ulong>: into a
    // Dictionary with the default comparer, entries in JSON order, a repeated key keeping its last
    // value. The entries are gathered first so the dictionary is created at its final size.
    private static Dictionary<string, ulong> ReadSequences(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"Expected a JSON object for lastAcceptedCommandSequences, found {reader.TokenType}.");
        }

        PooledScratch<KeyValuePair<string, ulong>> entries = new(16);
        try
        {
            while (HotPathJson.NextProperty(ref reader, "lastAcceptedCommandSequences"))
            {
                string key = reader.GetString()!;
                entries.Add(new KeyValuePair<string, ulong>(key, HotPathJson.ReadUInt64(ref reader)));
            }

            Dictionary<string, ulong> sequences = new(entries.Count);
            ReadOnlySpan<KeyValuePair<string, ulong>> span = entries.Span;
            for (int i = 0; i < span.Length; i++)
            {
                sequences[span[i].Key] = span[i].Value;
            }

            return sequences;
        }
        finally
        {
            entries.Dispose();
        }
    }

    private static WorldMutationDelta[] ReadMutations(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        ExpectStartArray(ref reader, nameof(WorldMutationDelta));
        PooledScratch<WorldMutationDelta> mutations = new(8);
        try
        {
            while (NextElement(ref reader, nameof(WorldMutationDelta)))
            {
                mutations.Add(WorldMutationDeltaJsonConverter.ReadMutation(ref reader, options));
            }

            return mutations.ToArray();
        }
        finally
        {
            mutations.Dispose();
        }
    }

    private static DamageEventDelta[] ReadDamageEvents(ref Utf8JsonReader reader)
    {
        ExpectStartArray(ref reader, nameof(DamageEventDelta));
        PooledScratch<DamageEventDelta> events = new(8);
        try
        {
            while (NextElement(ref reader, nameof(DamageEventDelta)))
            {
                events.Add(DamageEventDeltaJsonConverter.ReadDamage(ref reader));
            }

            return events.ToArray();
        }
        finally
        {
            events.Dispose();
        }
    }

    internal static void ExpectStartArray(ref Utf8JsonReader reader, string elementName)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException($"Expected a JSON array of {elementName}, found {reader.TokenType}.");
        }
    }

    // Advances to the next array element; false once the array's EndArray is reached.
    internal static bool NextElement(ref Utf8JsonReader reader, string elementName)
    {
        if (!reader.Read())
        {
            throw new JsonException($"Unexpected end of JSON while reading a {elementName} array.");
        }

        return reader.TokenType != JsonTokenType.EndArray;
    }

    // The reader is on the value.
    internal static ulong GetUInt64(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.Number || !reader.TryGetUInt64(out ulong value))
        {
            throw new JsonException($"Expected an unsigned 64-bit integer, found {reader.TokenType}.");
        }

        return value;
    }
}

internal sealed class WorldMutationDeltaJsonConverter : JsonConverter<WorldMutationDelta>
{
    private static readonly JsonEncodedText Kind = JsonEncodedText.Encode("kind");
    private static readonly JsonEncodedText CellPosition = JsonEncodedText.Encode("cellPosition");
    private static readonly JsonEncodedText TargetCellPosition = JsonEncodedText.Encode("targetCellPosition");
    private static readonly JsonEncodedText CenterPosition = JsonEncodedText.Encode("centerPosition");
    private static readonly JsonEncodedText MaterialId = JsonEncodedText.Encode("materialId");
    private static readonly JsonEncodedText FracturePieces = JsonEncodedText.Encode("fracturePieces");
    private static readonly JsonEncodedText Sequence = JsonEncodedText.Encode("sequence");
    private static readonly JsonEncodedText ActorId = JsonEncodedText.Encode("actorId");
    private static readonly JsonEncodedText Center = JsonEncodedText.Encode("center");
    private static readonly JsonEncodedText SideLength = JsonEncodedText.Encode("sideLength");

    public override WorldMutationDelta Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return ReadMutation(ref reader, options);
    }

    public override void Write(Utf8JsonWriter writer, WorldMutationDelta value, JsonSerializerOptions options)
    {
        WriteMutation(writer, value);
    }

    internal static WorldMutationDelta ReadMutation(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        HotPathJson.ExpectStartObject(ref reader, nameof(WorldMutationDelta));
        WorldMutationKind kind = default;
        Vector3 cellPosition = default;
        Vector3? targetCellPosition = null;
        Vector3? centerPosition = null;
        GeometryMaterialId? materialId = null;
        IReadOnlyList<GeometryFracturePiece>? fracturePieces = null;
        ulong sequence = 0;
        int? actorId = null;

        while (HotPathJson.NextProperty(ref reader, nameof(WorldMutationDelta)))
        {
            if (reader.ValueTextEquals(Kind.EncodedUtf8Bytes))
            {
                kind = (WorldMutationKind)HotPathJson.ReadInt32(ref reader);
            }
            else if (reader.ValueTextEquals(CellPosition.EncodedUtf8Bytes))
            {
                cellPosition = HotPathJson.ReadVector3(ref reader, options);
            }
            else if (reader.ValueTextEquals(TargetCellPosition.EncodedUtf8Bytes))
            {
                targetCellPosition = HotPathJson.ReadNullableVector3(ref reader, options);
            }
            else if (reader.ValueTextEquals(CenterPosition.EncodedUtf8Bytes))
            {
                centerPosition = HotPathJson.ReadNullableVector3(ref reader, options);
            }
            else if (reader.ValueTextEquals(MaterialId.EncodedUtf8Bytes))
            {
                materialId = (GeometryMaterialId?)HotPathJson.ReadNullableInt32(ref reader);
            }
            else if (reader.ValueTextEquals(FracturePieces.EncodedUtf8Bytes))
            {
                HotPathJson.ReadValue(ref reader);
                fracturePieces = reader.TokenType == JsonTokenType.Null ? null : ReadPieces(ref reader, options);
            }
            else if (reader.ValueTextEquals(Sequence.EncodedUtf8Bytes))
            {
                sequence = HotPathJson.ReadUInt64(ref reader);
            }
            else if (reader.ValueTextEquals(ActorId.EncodedUtf8Bytes))
            {
                actorId = HotPathJson.ReadNullableInt32(ref reader);
            }
            else
            {
                HotPathJson.SkipValue(ref reader);
            }
        }

        return new WorldMutationDelta(kind, cellPosition, targetCellPosition, centerPosition, materialId, fracturePieces, sequence, actorId);
    }

    internal static void WriteMutation(Utf8JsonWriter writer, in WorldMutationDelta value)
    {
        writer.WriteStartObject();
        writer.WriteNumber(Kind, (int)value.Kind);
        HotPathJson.WriteVector3(writer, CellPosition, value.CellPosition);
        if (value.TargetCellPosition is Vector3 targetCellPosition)
        {
            HotPathJson.WriteVector3(writer, TargetCellPosition, targetCellPosition);
        }

        if (value.CenterPosition is Vector3 centerPosition)
        {
            HotPathJson.WriteVector3(writer, CenterPosition, centerPosition);
        }

        if (value.MaterialId is GeometryMaterialId materialId)
        {
            writer.WriteNumber(MaterialId, (int)materialId);
        }

        if (value.FracturePieces is IReadOnlyList<GeometryFracturePiece> pieces)
        {
            writer.WritePropertyName(FracturePieces);
            writer.WriteStartArray();
            for (int i = 0; i < pieces.Count; i++)
            {
                GeometryFracturePiece piece = pieces[i];
                writer.WriteStartObject();
                HotPathJson.WriteVector3(writer, Center, piece.Center);
                writer.WriteNumber(SideLength, piece.SideLength);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        writer.WriteNumber(Sequence, value.Sequence);
        if (value.ActorId is int actorId)
        {
            writer.WriteNumber(ActorId, actorId);
        }

        writer.WriteEndObject();
    }

    private static GeometryFracturePiece[] ReadPieces(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        ServerSnapshotEnvelopeJsonConverter.ExpectStartArray(ref reader, nameof(GeometryFracturePiece));
        PooledScratch<GeometryFracturePiece> pieces = new(16);
        try
        {
            while (ServerSnapshotEnvelopeJsonConverter.NextElement(ref reader, nameof(GeometryFracturePiece)))
            {
                HotPathJson.ExpectStartObject(ref reader, nameof(GeometryFracturePiece));
                Vector3 center = default;
                float sideLength = 0f;
                while (HotPathJson.NextProperty(ref reader, nameof(GeometryFracturePiece)))
                {
                    if (reader.ValueTextEquals(Center.EncodedUtf8Bytes))
                    {
                        center = HotPathJson.ReadVector3(ref reader, options);
                    }
                    else if (reader.ValueTextEquals(SideLength.EncodedUtf8Bytes))
                    {
                        sideLength = HotPathJson.ReadSingle(ref reader);
                    }
                    else
                    {
                        HotPathJson.SkipValue(ref reader);
                    }
                }

                pieces.Add(new GeometryFracturePiece(center, sideLength));
            }

            return pieces.ToArray();
        }
        finally
        {
            pieces.Dispose();
        }
    }
}

internal sealed class DamageEventDeltaJsonConverter : JsonConverter<DamageEventDelta>
{
    private static readonly JsonEncodedText ActorId = JsonEncodedText.Encode("actorId");
    private static readonly JsonEncodedText Kind = JsonEncodedText.Encode("kind");
    private static readonly JsonEncodedText Amount = JsonEncodedText.Encode("amount");
    private static readonly JsonEncodedText ActorDied = JsonEncodedText.Encode("actorDied");
    private static readonly JsonEncodedText CurrentHealth = JsonEncodedText.Encode("currentHealth");
    private static readonly JsonEncodedText MaxHealth = JsonEncodedText.Encode("maxHealth");

    public override DamageEventDelta Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return ReadDamage(ref reader);
    }

    public override void Write(Utf8JsonWriter writer, DamageEventDelta value, JsonSerializerOptions options)
    {
        WriteDamage(writer, value);
    }

    internal static DamageEventDelta ReadDamage(ref Utf8JsonReader reader)
    {
        HotPathJson.ExpectStartObject(ref reader, nameof(DamageEventDelta));
        int actorId = 0;
        CreatureKind kind = default;
        int amount = 0;
        bool actorDied = false;
        int currentHealth = 0;
        int maxHealth = 0;
        while (HotPathJson.NextProperty(ref reader, nameof(DamageEventDelta)))
        {
            if (reader.ValueTextEquals(ActorId.EncodedUtf8Bytes))
            {
                actorId = HotPathJson.ReadInt32(ref reader);
            }
            else if (reader.ValueTextEquals(Kind.EncodedUtf8Bytes))
            {
                kind = (CreatureKind)HotPathJson.ReadInt32(ref reader);
            }
            else if (reader.ValueTextEquals(Amount.EncodedUtf8Bytes))
            {
                amount = HotPathJson.ReadInt32(ref reader);
            }
            else if (reader.ValueTextEquals(ActorDied.EncodedUtf8Bytes))
            {
                actorDied = HotPathJson.ReadBoolean(ref reader);
            }
            else if (reader.ValueTextEquals(CurrentHealth.EncodedUtf8Bytes))
            {
                currentHealth = HotPathJson.ReadInt32(ref reader);
            }
            else if (reader.ValueTextEquals(MaxHealth.EncodedUtf8Bytes))
            {
                maxHealth = HotPathJson.ReadInt32(ref reader);
            }
            else
            {
                HotPathJson.SkipValue(ref reader);
            }
        }

        return new DamageEventDelta(actorId, kind, amount, actorDied, currentHealth, maxHealth);
    }

    internal static void WriteDamage(Utf8JsonWriter writer, in DamageEventDelta value)
    {
        writer.WriteStartObject();
        writer.WriteNumber(ActorId, value.ActorId);
        writer.WriteNumber(Kind, (int)value.Kind);
        writer.WriteNumber(Amount, value.Amount);
        writer.WriteBoolean(ActorDied, value.ActorDied);
        writer.WriteNumber(CurrentHealth, value.CurrentHealth);
        writer.WriteNumber(MaxHealth, value.MaxHealth);
        writer.WriteEndObject();
    }
}

// A growable scratch list over pooled arrays: the snapshot readers gather elements here and
// return one exactly sized array, instead of a List<T> grown by doubling. Every rented array is
// cleared on return, so the pool never roots strings or fracture-piece lists.
internal struct PooledScratch<T>
{
    private T[] _items;
    private int _count;

    public PooledScratch(int initialLength)
    {
        _items = ArrayPool<T>.Shared.Rent(initialLength);
        _count = 0;
    }

    public readonly int Count => _count;

    public readonly ReadOnlySpan<T> Span => _items.AsSpan(0, _count);

    public void Add(T item)
    {
        if (_count == _items.Length)
        {
            T[] grown = ArrayPool<T>.Shared.Rent(_items.Length * 2);
            _items.AsSpan(0, _count).CopyTo(grown);
            ArrayPool<T>.Shared.Return(_items, clearArray: true);
            _items = grown;
        }

        _items[_count++] = item;
    }

    public readonly T[] ToArray()
    {
        return _count == 0 ? [] : _items.AsSpan(0, _count).ToArray();
    }

    public void Dispose()
    {
        T[] items = _items;
        _items = [];
        _count = 0;
        if (items.Length > 0)
        {
            ArrayPool<T>.Shared.Return(items, clearArray: true);
        }
    }
}
