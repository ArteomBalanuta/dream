using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenTK.Mathematics;
using Dream.Simulation.Combat;
using Dream.Simulation.Enemies;
using Dream.Simulation.Players;
using Dream.Simulation.World;

namespace Dream.Multiplayer.Protocol;

// Hand-written converter for ActorSnapshot, which every snapshot carries once per actor (WIRE-10).
// The reflection serializer boxed each actor on write and built constructor-argument state per
// actor on read. This writes the same bytes and reads the same values without either.
//
// Wire shape (protocol v2, declaration order, nulls left out):
//   {"actorId":n,"kind":n,"sessionId":s?,"position":v,"facing":v,"currentHealth":n,
//    "maxHealth":n,"isAlive":b,"interactionMode":n?,
//    "combatStats":{"strength":n,"attackSpeed":f,"vitality":n}?,"displayName":s?,
//    "progression":{"level":n,"experience":n,"experienceToNextLevel":n}?,"enemyBehaviorState":n?,
//    "starvedTicks":n?}
// starvedTicks (W9-2) is left out when zero, so a snapshot without starved ticks keeps the bytes it
// had before the field existed; a reader that predates it skips it as an unknown property.
internal sealed class ActorSnapshotJsonConverter : JsonConverter<ActorSnapshot>
{
    private static readonly JsonEncodedText ActorId = JsonEncodedText.Encode("actorId");
    private static readonly JsonEncodedText Kind = JsonEncodedText.Encode("kind");
    private static readonly JsonEncodedText SessionId = JsonEncodedText.Encode("sessionId");
    private static readonly JsonEncodedText Position = JsonEncodedText.Encode("position");
    private static readonly JsonEncodedText Facing = JsonEncodedText.Encode("facing");
    private static readonly JsonEncodedText CurrentHealth = JsonEncodedText.Encode("currentHealth");
    private static readonly JsonEncodedText MaxHealth = JsonEncodedText.Encode("maxHealth");
    private static readonly JsonEncodedText IsAlive = JsonEncodedText.Encode("isAlive");
    private static readonly JsonEncodedText InteractionMode = JsonEncodedText.Encode("interactionMode");
    private static readonly JsonEncodedText CombatStats = JsonEncodedText.Encode("combatStats");
    private static readonly JsonEncodedText DisplayName = JsonEncodedText.Encode("displayName");
    private static readonly JsonEncodedText Progression = JsonEncodedText.Encode("progression");
    private static readonly JsonEncodedText EnemyBehaviorStateName = JsonEncodedText.Encode("enemyBehaviorState");
    private static readonly JsonEncodedText StarvedTicks = JsonEncodedText.Encode("starvedTicks");

    public override ActorSnapshot Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return ReadActor(ref reader, options);
    }

    public override void Write(Utf8JsonWriter writer, ActorSnapshot value, JsonSerializerOptions options)
    {
        WriteActor(writer, value);
    }

    internal static ActorSnapshot ReadActor(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        HotPathJson.ExpectStartObject(ref reader, nameof(ActorSnapshot));
        int actorId = 0;
        CreatureKind kind = default;
        string? sessionId = null;
        Vector3 position = default;
        Vector3 facing = default;
        int currentHealth = 0;
        int maxHealth = 0;
        bool isAlive = false;
        PlayerInteractionMode? interactionMode = null;
        PlayerCombatStatsSnapshot? combatStats = null;
        string? displayName = null;
        PlayerProgressionSnapshot? progression = null;
        EnemyBehaviorState? enemyBehaviorState = null;
        int starvedTicks = 0;

        while (HotPathJson.NextProperty(ref reader, nameof(ActorSnapshot)))
        {
            if (reader.ValueTextEquals(ActorId.EncodedUtf8Bytes))
            {
                actorId = HotPathJson.ReadInt32(ref reader);
            }
            else if (reader.ValueTextEquals(Kind.EncodedUtf8Bytes))
            {
                kind = (CreatureKind)HotPathJson.ReadInt32(ref reader);
            }
            else if (reader.ValueTextEquals(SessionId.EncodedUtf8Bytes))
            {
                sessionId = HotPathJson.ReadNullableString(ref reader);
            }
            else if (reader.ValueTextEquals(Position.EncodedUtf8Bytes))
            {
                position = HotPathJson.ReadVector3(ref reader, options);
            }
            else if (reader.ValueTextEquals(Facing.EncodedUtf8Bytes))
            {
                facing = HotPathJson.ReadVector3(ref reader, options);
            }
            else if (reader.ValueTextEquals(CurrentHealth.EncodedUtf8Bytes))
            {
                currentHealth = HotPathJson.ReadInt32(ref reader);
            }
            else if (reader.ValueTextEquals(MaxHealth.EncodedUtf8Bytes))
            {
                maxHealth = HotPathJson.ReadInt32(ref reader);
            }
            else if (reader.ValueTextEquals(IsAlive.EncodedUtf8Bytes))
            {
                isAlive = HotPathJson.ReadBoolean(ref reader);
            }
            else if (reader.ValueTextEquals(InteractionMode.EncodedUtf8Bytes))
            {
                interactionMode = (PlayerInteractionMode?)HotPathJson.ReadNullableInt32(ref reader);
            }
            else if (reader.ValueTextEquals(CombatStats.EncodedUtf8Bytes))
            {
                HotPathJson.ReadValue(ref reader);
                combatStats = reader.TokenType == JsonTokenType.Null
                    ? null
                    : PlayerCombatStatsSnapshotJsonConverter.ReadStats(ref reader);
            }
            else if (reader.ValueTextEquals(DisplayName.EncodedUtf8Bytes))
            {
                displayName = HotPathJson.ReadNullableString(ref reader);
            }
            else if (reader.ValueTextEquals(Progression.EncodedUtf8Bytes))
            {
                HotPathJson.ReadValue(ref reader);
                progression = reader.TokenType == JsonTokenType.Null
                    ? null
                    : PlayerProgressionSnapshotJsonConverter.ReadProgression(ref reader);
            }
            else if (reader.ValueTextEquals(EnemyBehaviorStateName.EncodedUtf8Bytes))
            {
                enemyBehaviorState = (EnemyBehaviorState?)HotPathJson.ReadNullableInt32(ref reader);
            }
            else if (reader.ValueTextEquals(StarvedTicks.EncodedUtf8Bytes))
            {
                starvedTicks = HotPathJson.ReadInt32(ref reader);
            }
            else
            {
                HotPathJson.SkipValue(ref reader);
            }
        }

        return new ActorSnapshot(
            actorId,
            kind,
            sessionId,
            position,
            facing,
            currentHealth,
            maxHealth,
            isAlive,
            interactionMode,
            combatStats,
            displayName,
            progression,
            enemyBehaviorState,
            starvedTicks);
    }

    internal static void WriteActor(Utf8JsonWriter writer, ActorSnapshot value)
    {
        writer.WriteStartObject();
        writer.WriteNumber(ActorId, value.ActorId);
        writer.WriteNumber(Kind, (int)value.Kind);
        if (value.SessionId is not null)
        {
            writer.WriteString(SessionId, value.SessionId);
        }

        HotPathJson.WriteVector3(writer, Position, value.Position);
        HotPathJson.WriteVector3(writer, Facing, value.Facing);
        writer.WriteNumber(CurrentHealth, value.CurrentHealth);
        writer.WriteNumber(MaxHealth, value.MaxHealth);
        writer.WriteBoolean(IsAlive, value.IsAlive);
        if (value.InteractionMode is PlayerInteractionMode interactionMode)
        {
            writer.WriteNumber(InteractionMode, (int)interactionMode);
        }

        if (value.CombatStats is PlayerCombatStatsSnapshot combatStats)
        {
            writer.WritePropertyName(CombatStats);
            PlayerCombatStatsSnapshotJsonConverter.WriteStats(writer, combatStats);
        }

        if (value.DisplayName is not null)
        {
            writer.WriteString(DisplayName, value.DisplayName);
        }

        if (value.Progression is PlayerProgressionSnapshot progression)
        {
            writer.WritePropertyName(Progression);
            PlayerProgressionSnapshotJsonConverter.WriteProgression(writer, progression);
        }

        if (value.EnemyBehaviorState is EnemyBehaviorState enemyBehaviorState)
        {
            writer.WriteNumber(EnemyBehaviorStateName, (int)enemyBehaviorState);
        }

        if (value.StarvedTicks != 0)
        {
            writer.WriteNumber(StarvedTicks, value.StarvedTicks);
        }

        writer.WriteEndObject();
    }
}

// The actor list of a snapshot (and of JoinAcceptedMessage.InitialActors). The reflection
// serializer read it into a List<ActorSnapshot> grown by doubling, so a 24-actor snapshot paid for
// arrays of 4, 8, 16 and 32 actors of about 120 B each. This reads into a pooled scratch array and
// returns an exactly sized ActorSnapshot[]; writing indexes the list instead of boxing an
// enumerator. A null list never reaches a converter: it is left out on write and decoded as null.
internal sealed class ActorSnapshotListJsonConverter : JsonConverter<IReadOnlyList<ActorSnapshot>>
{
    private const int InitialScratchLength = 32;

    public override IReadOnlyList<ActorSnapshot> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException($"Expected a JSON array of {nameof(ActorSnapshot)}, found {reader.TokenType}.");
        }

        ActorSnapshot[] scratch = ArrayPool<ActorSnapshot>.Shared.Rent(InitialScratchLength);
        int count = 0;
        try
        {
            while (true)
            {
                if (!reader.Read())
                {
                    throw new JsonException($"Unexpected end of JSON while reading a {nameof(ActorSnapshot)} array.");
                }

                if (reader.TokenType == JsonTokenType.EndArray)
                {
                    break;
                }

                if (count == scratch.Length)
                {
                    ActorSnapshot[] grown = ArrayPool<ActorSnapshot>.Shared.Rent(scratch.Length * 2);
                    scratch.AsSpan(0, count).CopyTo(grown);
                    ArrayPool<ActorSnapshot>.Shared.Return(scratch, clearArray: true);
                    scratch = grown;
                }

                scratch[count++] = ActorSnapshotJsonConverter.ReadActor(ref reader, options);
            }

            return count == 0 ? [] : scratch.AsSpan(0, count).ToArray();
        }
        finally
        {
            // Actors hold strings; clearing keeps the pool from rooting them.
            ArrayPool<ActorSnapshot>.Shared.Return(scratch, clearArray: true);
        }
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<ActorSnapshot> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        for (int i = 0; i < value.Count; i++)
        {
            ActorSnapshotJsonConverter.WriteActor(writer, value[i]);
        }

        writer.WriteEndArray();
    }
}

// Also reached through PlayerStatsUpdateIntent and PlayerStatsUpdateResult.
internal sealed class PlayerCombatStatsSnapshotJsonConverter : JsonConverter<PlayerCombatStatsSnapshot>
{
    private static readonly JsonEncodedText Strength = JsonEncodedText.Encode("strength");
    private static readonly JsonEncodedText AttackSpeed = JsonEncodedText.Encode("attackSpeed");
    private static readonly JsonEncodedText Vitality = JsonEncodedText.Encode("vitality");

    public override PlayerCombatStatsSnapshot Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return ReadStats(ref reader);
    }

    public override void Write(Utf8JsonWriter writer, PlayerCombatStatsSnapshot value, JsonSerializerOptions options)
    {
        WriteStats(writer, value);
    }

    internal static PlayerCombatStatsSnapshot ReadStats(ref Utf8JsonReader reader)
    {
        HotPathJson.ExpectStartObject(ref reader, nameof(PlayerCombatStatsSnapshot));
        int strength = 0;
        float attackSpeed = 0f;
        int vitality = 0;
        while (HotPathJson.NextProperty(ref reader, nameof(PlayerCombatStatsSnapshot)))
        {
            if (reader.ValueTextEquals(Strength.EncodedUtf8Bytes))
            {
                strength = HotPathJson.ReadInt32(ref reader);
            }
            else if (reader.ValueTextEquals(AttackSpeed.EncodedUtf8Bytes))
            {
                attackSpeed = HotPathJson.ReadSingle(ref reader);
            }
            else if (reader.ValueTextEquals(Vitality.EncodedUtf8Bytes))
            {
                vitality = HotPathJson.ReadInt32(ref reader);
            }
            else
            {
                HotPathJson.SkipValue(ref reader);
            }
        }

        return new PlayerCombatStatsSnapshot(strength, attackSpeed, vitality);
    }

    internal static void WriteStats(Utf8JsonWriter writer, PlayerCombatStatsSnapshot value)
    {
        writer.WriteStartObject();
        writer.WriteNumber(Strength, value.Strength);
        writer.WriteNumber(AttackSpeed, value.AttackSpeed);
        writer.WriteNumber(Vitality, value.Vitality);
        writer.WriteEndObject();
    }
}

internal sealed class PlayerProgressionSnapshotJsonConverter : JsonConverter<PlayerProgressionSnapshot>
{
    private static readonly JsonEncodedText Level = JsonEncodedText.Encode("level");
    private static readonly JsonEncodedText Experience = JsonEncodedText.Encode("experience");
    private static readonly JsonEncodedText ExperienceToNextLevel = JsonEncodedText.Encode("experienceToNextLevel");

    public override PlayerProgressionSnapshot Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return ReadProgression(ref reader);
    }

    public override void Write(Utf8JsonWriter writer, PlayerProgressionSnapshot value, JsonSerializerOptions options)
    {
        WriteProgression(writer, value);
    }

    internal static PlayerProgressionSnapshot ReadProgression(ref Utf8JsonReader reader)
    {
        HotPathJson.ExpectStartObject(ref reader, nameof(PlayerProgressionSnapshot));
        int level = 0;
        int experience = 0;
        int experienceToNextLevel = 0;
        while (HotPathJson.NextProperty(ref reader, nameof(PlayerProgressionSnapshot)))
        {
            if (reader.ValueTextEquals(Level.EncodedUtf8Bytes))
            {
                level = HotPathJson.ReadInt32(ref reader);
            }
            else if (reader.ValueTextEquals(Experience.EncodedUtf8Bytes))
            {
                experience = HotPathJson.ReadInt32(ref reader);
            }
            else if (reader.ValueTextEquals(ExperienceToNextLevel.EncodedUtf8Bytes))
            {
                experienceToNextLevel = HotPathJson.ReadInt32(ref reader);
            }
            else
            {
                HotPathJson.SkipValue(ref reader);
            }
        }

        return new PlayerProgressionSnapshot(level, experience, experienceToNextLevel);
    }

    internal static void WriteProgression(Utf8JsonWriter writer, PlayerProgressionSnapshot value)
    {
        writer.WriteStartObject();
        writer.WriteNumber(Level, value.Level);
        writer.WriteNumber(Experience, value.Experience);
        writer.WriteNumber(ExperienceToNextLevel, value.ExperienceToNextLevel);
        writer.WriteEndObject();
    }
}
