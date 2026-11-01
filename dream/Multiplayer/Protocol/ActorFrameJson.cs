using System.Text.Json;
using System.Text.Json.Serialization;
using Dream.Simulation.Combat;
using Dream.Simulation.Enemies;
using Dream.Simulation.Inventory;
using Dream.Simulation.Players;
using Dream.Simulation.World;

namespace Dream.Multiplayer.Protocol;

// SCALE-2 wire shape of a snapshot's actors when they travel as a delta-coded frame, the
// "actorFrame" property of the snapshot in place of "actors":
//
//   {"seq":n,"base":n?,"actors":[record,...]}
//
// "base" is left out of a keyframe. Every actor of the snapshot appears once, in snapshot order;
// an actor missing from the list has left. A record is either
//   - a bare actor id: the actor is unchanged since the base frame, or
//   - [id, groups, ...fields]: groups is the ActorFieldGroups mask, followed by the fields of each
//     group present, in bit order:
//       Full        kind, sessionId|null, displayName|null (and then every other group)
//       Position    x, y, z     quantized (1/4096 m); absolute when Full, else the change
//       Facing      x, y, z     quantized (1/4096); absolute when Full, else the change
//       Health      currentHealth, maxHealth, alive (0 or 1)
//       Mode        interactionMode|null, enemyBehaviorState|null
//       Combat      null | [strength, attackSpeed, vitality]
//       Progression null | [level, experience, experienceToNextLevel]
//       Starved     starvedTicks
//       Equipment   heldLight (HeldLightKind), protocol v6
//       EnemyPresentation  enemyLevel, visualKey|null, protocol v11 (absolute, also in a delta)
// A Full record carries every group through Starved (groups is 255), and Equipment (511) only
// when the held light is not None; a Full record without Equipment means None. A delta record
// carries Equipment when the held light changed. EnemyPresentation (512) is carried by a Full
// record only when the level is not 0 or the visual key is not null (a Full Dire Wolf is 767), and
// by a delta record when either changed; without it an enemy is level 1 with the default visual. An idle actor costs its id and a comma; a
// walking player about 20 bytes (`[18,2,410,0,-205],` is 18, pinned by DeltaActorSnapshotTests),
// about 30 while it also turns (the Facing group adds three more changes).
// The frame as a standalone converter. The codec reaches the frame through the snapshot envelope's
// converter; this one is the reference that tests plug into the reflection serializer, as they do
// with WorldSnapshotJsonConverter for the compact world.
public sealed class ActorWireFrameJsonConverter : JsonConverter<ActorWireFrame>
{
    public override ActorWireFrame Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return ActorFrameJson.Read(ref reader);
    }

    public override void Write(Utf8JsonWriter writer, ActorWireFrame value, JsonSerializerOptions options)
    {
        ActorFrameJson.Write(writer, value);
    }
}

internal static class ActorFrameJson
{
    private static readonly JsonEncodedText Seq = JsonEncodedText.Encode("seq");
    private static readonly JsonEncodedText Base = JsonEncodedText.Encode("base");
    private static readonly JsonEncodedText Actors = JsonEncodedText.Encode("actors");

    private const int InitialScratchLength = 32;

    // A wire enemy level outside 0..this is hostile; the client clamps what it displays further.
    internal const int MaxEnemyLevel = 1_000_000;

    // A frame decoded earlier, written back exactly as it came.
    public static void Write(Utf8JsonWriter writer, ActorWireFrame frame)
    {
        WriteHeader(writer, frame.Sequence, frame.BaseSequence);
        ActorWireRecord[] records = frame.Records;
        for (int i = 0; i < records.Length; i++)
        {
            WriteRecord(writer, records[i]);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    // The server's frame: each record is built from the context while it is written, so the encode
    // allocates nothing.
    public static void Write(Utf8JsonWriter writer, in ActorDeltaContext context, IReadOnlyList<ActorSnapshot> actors)
    {
        WriteHeader(writer, context.Sequence, context.BaseSequence);
        int cursor = 0;
        for (int i = 0; i < actors.Count; i++)
        {
            WriteRecord(writer, context.BuildRecord(actors, i, ref cursor));
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteHeader(Utf8JsonWriter writer, ulong sequence, ulong baseSequence)
    {
        writer.WriteStartObject();
        writer.WriteNumber(Seq, sequence);
        if (baseSequence != 0)
        {
            writer.WriteNumber(Base, baseSequence);
        }

        writer.WritePropertyName(Actors);
        writer.WriteStartArray();
    }

    internal static void WriteRecord(Utf8JsonWriter writer, in ActorWireRecord record)
    {
        ActorFieldGroups groups = record.Groups;
        if (groups == ActorFieldGroups.None)
        {
            writer.WriteNumberValue(record.ActorId);
            return;
        }

        writer.WriteStartArray();
        writer.WriteNumberValue(record.ActorId);
        writer.WriteNumberValue((int)groups);
        if ((groups & ActorFieldGroups.Full) != 0)
        {
            writer.WriteNumberValue((int)record.Kind);
            WriteNullableString(writer, record.SessionId);
            WriteNullableString(writer, record.DisplayName);
        }

        if ((groups & ActorFieldGroups.Position) != 0)
        {
            writer.WriteNumberValue(record.PositionX);
            writer.WriteNumberValue(record.PositionY);
            writer.WriteNumberValue(record.PositionZ);
        }

        if ((groups & ActorFieldGroups.Facing) != 0)
        {
            writer.WriteNumberValue(record.FacingX);
            writer.WriteNumberValue(record.FacingY);
            writer.WriteNumberValue(record.FacingZ);
        }

        if ((groups & ActorFieldGroups.Health) != 0)
        {
            writer.WriteNumberValue(record.CurrentHealth);
            writer.WriteNumberValue(record.MaxHealth);
            writer.WriteNumberValue(record.IsAlive ? 1 : 0);
        }

        if ((groups & ActorFieldGroups.Mode) != 0)
        {
            WriteNullableInt32(writer, (int?)record.InteractionMode);
            WriteNullableInt32(writer, (int?)record.EnemyBehaviorState);
        }

        if ((groups & ActorFieldGroups.Combat) != 0)
        {
            if (record.CombatStats is PlayerCombatStatsSnapshot stats)
            {
                writer.WriteStartArray();
                writer.WriteNumberValue(stats.Strength);
                writer.WriteNumberValue(stats.AttackSpeed);
                writer.WriteNumberValue(stats.Vitality);
                writer.WriteEndArray();
            }
            else
            {
                writer.WriteNullValue();
            }
        }

        if ((groups & ActorFieldGroups.Progression) != 0)
        {
            if (record.Progression is PlayerProgressionSnapshot progression)
            {
                writer.WriteStartArray();
                writer.WriteNumberValue(progression.Level);
                writer.WriteNumberValue(progression.Experience);
                writer.WriteNumberValue(progression.ExperienceToNextLevel);
                writer.WriteEndArray();
            }
            else
            {
                writer.WriteNullValue();
            }
        }

        if ((groups & ActorFieldGroups.Starved) != 0)
        {
            writer.WriteNumberValue(record.StarvedTicks);
        }

        if ((groups & ActorFieldGroups.Equipment) != 0)
        {
            writer.WriteNumberValue((int)record.HeldLight);
        }

        if ((groups & ActorFieldGroups.EnemyPresentation) != 0)
        {
            writer.WriteNumberValue(record.EnemyLevel);
            WriteNullableString(writer, record.VisualKey);
        }

        writer.WriteEndArray();
    }

    // The reader is on the frame's StartObject.
    public static ActorWireFrame Read(ref Utf8JsonReader reader)
    {
        HotPathJson.ExpectStartObject(ref reader, nameof(ActorWireFrame));
        ulong sequence = 0;
        ulong baseSequence = 0;
        ActorWireRecord[]? records = null;
        while (HotPathJson.NextProperty(ref reader, nameof(ActorWireFrame)))
        {
            if (reader.ValueTextEquals(Seq.EncodedUtf8Bytes))
            {
                sequence = HotPathJson.ReadUInt64(ref reader);
            }
            else if (reader.ValueTextEquals(Base.EncodedUtf8Bytes))
            {
                baseSequence = HotPathJson.ReadUInt64(ref reader);
            }
            else if (reader.ValueTextEquals(Actors.EncodedUtf8Bytes))
            {
                HotPathJson.ReadValue(ref reader);
                records = ReadRecords(ref reader);
            }
            else
            {
                HotPathJson.SkipValue(ref reader);
            }
        }

        if (sequence == 0)
        {
            throw new JsonException("An actor frame needs a non-zero seq.");
        }

        ActorWireFrame frame = new(sequence, baseSequence, records ?? []);
        if (frame.IsKeyframe)
        {
            for (int i = 0; i < frame.Records.Length; i++)
            {
                if (!frame.Records[i].IsFull)
                {
                    throw new JsonException($"Keyframe {sequence} has a delta record for actor {frame.Records[i].ActorId}.");
                }
            }
        }

        return frame;
    }

    // A keyframe's actors, read straight from its records (every one Full).
    public static ActorSnapshot[] Materialize(ActorWireFrame keyframe)
    {
        ActorWireRecord[] records = keyframe.Records;
        if (records.Length == 0)
        {
            return [];
        }

        ActorSnapshot[] actors = new ActorSnapshot[records.Length];
        for (int i = 0; i < records.Length; i++)
        {
            actors[i] = records[i].ToActorSnapshot();
        }

        return actors;
    }

    private static ActorWireRecord[] ReadRecords(ref Utf8JsonReader reader)
    {
        ServerSnapshotEnvelopeJsonConverter.ExpectStartArray(ref reader, nameof(ActorWireRecord));
        PooledScratch<ActorWireRecord> records = new(InitialScratchLength);
        try
        {
            while (ServerSnapshotEnvelopeJsonConverter.NextElement(ref reader, nameof(ActorWireRecord)))
            {
                records.Add(ReadRecord(ref reader));
            }

            return records.ToArray();
        }
        finally
        {
            records.Dispose();
        }
    }

    // The reader is on the record's first token: a number (unchanged actor) or StartArray.
    private static ActorWireRecord ReadRecord(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            return new ActorWireRecord(GetInt32(ref reader), ActorFieldGroups.None);
        }

        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException($"Expected an actor id or an actor record array, found {reader.TokenType}.");
        }

        int actorId = NextInt32(ref reader);
        int rawGroups = NextInt32(ref reader);
        if (rawGroups <= 0 || (rawGroups & ~(int)ActorFieldGroups.Valid) != 0)
        {
            throw new JsonException($"Actor {actorId}'s record has an invalid group mask {rawGroups}.");
        }

        ActorFieldGroups groups = (ActorFieldGroups)rawGroups;
        if ((groups & ActorFieldGroups.Full) != 0 && (groups & ActorFieldGroups.All) != ActorFieldGroups.All)
        {
            throw new JsonException($"Actor {actorId}'s full record must carry every group, found {rawGroups}.");
        }

        CreatureKind kind = default;
        string? sessionId = null;
        string? displayName = null;
        int px = 0, py = 0, pz = 0, fx = 0, fy = 0, fz = 0;
        int currentHealth = 0, maxHealth = 0;
        bool isAlive = false;
        PlayerInteractionMode? interactionMode = null;
        EnemyBehaviorState? behaviorState = null;
        PlayerCombatStatsSnapshot? combatStats = null;
        PlayerProgressionSnapshot? progression = null;
        int starvedTicks = 0;

        if ((groups & ActorFieldGroups.Full) != 0)
        {
            kind = (CreatureKind)NextInt32(ref reader);
            sessionId = NextNullableString(ref reader);
            displayName = NextNullableString(ref reader);
        }

        if ((groups & ActorFieldGroups.Position) != 0)
        {
            px = NextInt32(ref reader);
            py = NextInt32(ref reader);
            pz = NextInt32(ref reader);
        }

        if ((groups & ActorFieldGroups.Facing) != 0)
        {
            fx = NextInt32(ref reader);
            fy = NextInt32(ref reader);
            fz = NextInt32(ref reader);
        }

        if ((groups & ActorFieldGroups.Health) != 0)
        {
            currentHealth = NextInt32(ref reader);
            maxHealth = NextInt32(ref reader);
            isAlive = NextInt32(ref reader) switch
            {
                0 => false,
                1 => true,
                int other => throw new JsonException($"Actor {actorId}'s alive flag must be 0 or 1, found {other}.")
            };
        }

        if ((groups & ActorFieldGroups.Mode) != 0)
        {
            interactionMode = (PlayerInteractionMode?)NextNullableInt32(ref reader);
            behaviorState = (EnemyBehaviorState?)NextNullableInt32(ref reader);
        }

        if ((groups & ActorFieldGroups.Combat) != 0)
        {
            Next(ref reader);
            if (reader.TokenType != JsonTokenType.Null)
            {
                ExpectArray(ref reader, "combat stats");
                int strength = NextInt32(ref reader);
                Next(ref reader);
                if (reader.TokenType != JsonTokenType.Number || !reader.TryGetSingle(out float attackSpeed))
                {
                    throw new JsonException($"Expected a single-precision number, found {reader.TokenType}.");
                }

                int vitality = NextInt32(ref reader);
                ExpectEndArray(ref reader, "combat stats");
                combatStats = new PlayerCombatStatsSnapshot(strength, attackSpeed, vitality);
            }
        }

        if ((groups & ActorFieldGroups.Progression) != 0)
        {
            Next(ref reader);
            if (reader.TokenType != JsonTokenType.Null)
            {
                ExpectArray(ref reader, "progression");
                int level = NextInt32(ref reader);
                int experience = NextInt32(ref reader);
                int toNext = NextInt32(ref reader);
                ExpectEndArray(ref reader, "progression");
                progression = new PlayerProgressionSnapshot(level, experience, toNext);
            }
        }

        if ((groups & ActorFieldGroups.Starved) != 0)
        {
            starvedTicks = NextInt32(ref reader);
        }

        HeldLightKind heldLight = HeldLightKind.None;
        if ((groups & ActorFieldGroups.Equipment) != 0)
        {
            int rawHeldLight = NextInt32(ref reader);
            if (rawHeldLight is < byte.MinValue or > byte.MaxValue)
            {
                throw new JsonException($"Actor {actorId}'s held light {rawHeldLight} is out of range.");
            }

            heldLight = (HeldLightKind)rawHeldLight;
        }

        int enemyLevel = 0;
        string? visualKey = null;
        if ((groups & ActorFieldGroups.EnemyPresentation) != 0)
        {
            enemyLevel = NextInt32(ref reader);
            if (enemyLevel is < 0 or > MaxEnemyLevel)
            {
                throw new JsonException($"Actor {actorId}'s enemy level {enemyLevel} is out of range.");
            }

            visualKey = NextNullableString(ref reader);
        }

        ExpectEndArray(ref reader, "actor record");
        return new ActorWireRecord(
            actorId,
            groups,
            kind,
            sessionId,
            displayName,
            px,
            py,
            pz,
            fx,
            fy,
            fz,
            currentHealth,
            maxHealth,
            isAlive,
            interactionMode,
            behaviorState,
            combatStats,
            progression,
            starvedTicks,
            heldLight,
            enemyLevel,
            visualKey);
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string? value)
    {
        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStringValue(value);
        }
    }

    private static void WriteNullableInt32(Utf8JsonWriter writer, int? value)
    {
        if (value is int number)
        {
            writer.WriteNumberValue(number);
        }
        else
        {
            writer.WriteNullValue();
        }
    }

    private static void Next(ref Utf8JsonReader reader)
    {
        if (!reader.Read())
        {
            throw new JsonException("Unexpected end of JSON while reading an actor record.");
        }
    }

    private static int GetInt32(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out int value))
        {
            throw new JsonException($"Expected a 32-bit integer, found {reader.TokenType}.");
        }

        return value;
    }

    private static int NextInt32(ref Utf8JsonReader reader)
    {
        Next(ref reader);
        return GetInt32(ref reader);
    }

    private static int? NextNullableInt32(ref Utf8JsonReader reader)
    {
        Next(ref reader);
        return reader.TokenType == JsonTokenType.Null ? null : GetInt32(ref reader);
    }

    private static string? NextNullableString(ref Utf8JsonReader reader)
    {
        Next(ref reader);
        return reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Null => null,
            _ => throw new JsonException($"Expected a string or null, found {reader.TokenType}.")
        };
    }

    private static void ExpectArray(ref Utf8JsonReader reader, string what)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException($"Expected an array or null for {what}, found {reader.TokenType}.");
        }
    }

    private static void ExpectEndArray(ref Utf8JsonReader reader, string what)
    {
        Next(ref reader);
        if (reader.TokenType != JsonTokenType.EndArray)
        {
            throw new JsonException($"Unexpected {reader.TokenType} after the last field of an {what}.");
        }
    }
}
