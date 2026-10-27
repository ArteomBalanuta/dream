using System.Text.Json;
using System.Text.Json.Serialization;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Players;

namespace Dream.Multiplayer.Protocol;

// Hand-written converters for the client command path (WIRE-10). The reflection serializer boxed
// PlayerCommandFrame and each nested record struct once per write and rented a WriteStack frame
// array, 520 B per command encode. These write the same bytes with nothing allocated, and the
// server decodes a command without the reflection serializer's constructor-argument state.
//
// Wire shape (protocol v2, properties in declaration order, nulls left out):
//   {"commandFrame":{"sessionId":{"value":s},"sequence":n,"requestedMode":{"mode":n},
//    "moveIntent":{"forwardAxis":f,"strafeAxis":f,"verticalAxis":f,"jumpPressed":b,
//                  "freezePressed":b,"toggleNoclipPressed":b},
//    "lookIntent":{"yawDelta":f,"pitchDelta":f,"facing":{"x":f,"y":f,"z":f}},
//    "primaryUsePressed":b}}
// Each type also has its own converter so PlayerSessionId keeps the same fast path inside
// PlayerActionMessage.
internal sealed class PlayerCommandMessageJsonConverter : JsonConverter<PlayerCommandMessage>
{
    private static readonly JsonEncodedText CommandFrame = JsonEncodedText.Encode("commandFrame");

    public override PlayerCommandMessage Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        HotPathJson.ExpectStartObject(ref reader, nameof(PlayerCommandMessage));
        PlayerCommandFrame frame = default;
        while (HotPathJson.NextProperty(ref reader, nameof(PlayerCommandMessage)))
        {
            if (reader.ValueTextEquals(CommandFrame.EncodedUtf8Bytes))
            {
                HotPathJson.ReadValue(ref reader);
                frame = PlayerCommandFrameJsonConverter.ReadFrame(ref reader, options);
            }
            else
            {
                HotPathJson.SkipValue(ref reader);
            }
        }

        return new PlayerCommandMessage(frame);
    }

    public override void Write(Utf8JsonWriter writer, PlayerCommandMessage value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WritePropertyName(CommandFrame);
        PlayerCommandFrameJsonConverter.WriteFrame(writer, value.CommandFrame);
        writer.WriteEndObject();
    }
}

internal sealed class PlayerCommandFrameJsonConverter : JsonConverter<PlayerCommandFrame>
{
    private static readonly JsonEncodedText SessionId = JsonEncodedText.Encode("sessionId");
    private static readonly JsonEncodedText Sequence = JsonEncodedText.Encode("sequence");
    private static readonly JsonEncodedText RequestedMode = JsonEncodedText.Encode("requestedMode");
    private static readonly JsonEncodedText MoveIntent = JsonEncodedText.Encode("moveIntent");
    private static readonly JsonEncodedText LookIntent = JsonEncodedText.Encode("lookIntent");
    private static readonly JsonEncodedText PrimaryUsePressed = JsonEncodedText.Encode("primaryUsePressed");

    public override PlayerCommandFrame Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return ReadFrame(ref reader, options);
    }

    public override void Write(Utf8JsonWriter writer, PlayerCommandFrame value, JsonSerializerOptions options)
    {
        WriteFrame(writer, value);
    }

    internal static PlayerCommandFrame ReadFrame(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        HotPathJson.ExpectStartObject(ref reader, nameof(PlayerCommandFrame));
        PlayerSessionId sessionId = default;
        ulong sequence = 0;
        PlayerModeSwitchIntent requestedMode = default;
        PlayerMoveIntent moveIntent = default;
        PlayerLookIntent lookIntent = default;
        bool primaryUsePressed = false;

        while (HotPathJson.NextProperty(ref reader, nameof(PlayerCommandFrame)))
        {
            if (reader.ValueTextEquals(SessionId.EncodedUtf8Bytes))
            {
                HotPathJson.ReadValue(ref reader);
                sessionId = PlayerSessionIdJsonConverter.ReadSessionId(ref reader);
            }
            else if (reader.ValueTextEquals(Sequence.EncodedUtf8Bytes))
            {
                sequence = HotPathJson.ReadUInt64(ref reader);
            }
            else if (reader.ValueTextEquals(RequestedMode.EncodedUtf8Bytes))
            {
                HotPathJson.ReadValue(ref reader);
                requestedMode = PlayerModeSwitchIntentJsonConverter.ReadIntent(ref reader);
            }
            else if (reader.ValueTextEquals(MoveIntent.EncodedUtf8Bytes))
            {
                HotPathJson.ReadValue(ref reader);
                moveIntent = PlayerMoveIntentJsonConverter.ReadIntent(ref reader);
            }
            else if (reader.ValueTextEquals(LookIntent.EncodedUtf8Bytes))
            {
                HotPathJson.ReadValue(ref reader);
                lookIntent = PlayerLookIntentJsonConverter.ReadIntent(ref reader, options);
            }
            else if (reader.ValueTextEquals(PrimaryUsePressed.EncodedUtf8Bytes))
            {
                primaryUsePressed = HotPathJson.ReadBoolean(ref reader);
            }
            else
            {
                HotPathJson.SkipValue(ref reader);
            }
        }

        return new PlayerCommandFrame(sessionId, sequence, requestedMode, moveIntent, lookIntent, primaryUsePressed);
    }

    internal static void WriteFrame(Utf8JsonWriter writer, PlayerCommandFrame value)
    {
        writer.WriteStartObject();
        writer.WritePropertyName(SessionId);
        PlayerSessionIdJsonConverter.WriteSessionId(writer, value.SessionId);
        writer.WriteNumber(Sequence, value.Sequence);
        writer.WritePropertyName(RequestedMode);
        PlayerModeSwitchIntentJsonConverter.WriteIntent(writer, value.RequestedMode);
        writer.WritePropertyName(MoveIntent);
        PlayerMoveIntentJsonConverter.WriteIntent(writer, value.MoveIntent);
        writer.WritePropertyName(LookIntent);
        PlayerLookIntentJsonConverter.WriteIntent(writer, value.LookIntent);
        writer.WriteBoolean(PrimaryUsePressed, value.PrimaryUsePressed);
        writer.WriteEndObject();
    }
}

internal sealed class PlayerSessionIdJsonConverter : JsonConverter<PlayerSessionId>
{
    private static readonly JsonEncodedText Value = JsonEncodedText.Encode("value");

    public override PlayerSessionId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return ReadSessionId(ref reader);
    }

    public override void Write(Utf8JsonWriter writer, PlayerSessionId value, JsonSerializerOptions options)
    {
        WriteSessionId(writer, value);
    }

    internal static PlayerSessionId ReadSessionId(ref Utf8JsonReader reader)
    {
        HotPathJson.ExpectStartObject(ref reader, nameof(PlayerSessionId));
        string? value = null;
        while (HotPathJson.NextProperty(ref reader, nameof(PlayerSessionId)))
        {
            if (reader.ValueTextEquals(Value.EncodedUtf8Bytes))
            {
                value = HotPathJson.ReadNullableString(ref reader);
            }
            else
            {
                HotPathJson.SkipValue(ref reader);
            }
        }

        return new PlayerSessionId(value!);
    }

    internal static void WriteSessionId(Utf8JsonWriter writer, PlayerSessionId value)
    {
        writer.WriteStartObject();
        if (value.Value is not null)
        {
            writer.WriteString(Value, value.Value);
        }

        writer.WriteEndObject();
    }
}

internal sealed class PlayerModeSwitchIntentJsonConverter : JsonConverter<PlayerModeSwitchIntent>
{
    private static readonly JsonEncodedText Mode = JsonEncodedText.Encode("mode");

    public override PlayerModeSwitchIntent Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return ReadIntent(ref reader);
    }

    public override void Write(Utf8JsonWriter writer, PlayerModeSwitchIntent value, JsonSerializerOptions options)
    {
        WriteIntent(writer, value);
    }

    internal static PlayerModeSwitchIntent ReadIntent(ref Utf8JsonReader reader)
    {
        HotPathJson.ExpectStartObject(ref reader, nameof(PlayerModeSwitchIntent));
        PlayerInteractionMode? mode = null;
        while (HotPathJson.NextProperty(ref reader, nameof(PlayerModeSwitchIntent)))
        {
            if (reader.ValueTextEquals(Mode.EncodedUtf8Bytes))
            {
                mode = (PlayerInteractionMode?)HotPathJson.ReadNullableInt32(ref reader);
            }
            else
            {
                HotPathJson.SkipValue(ref reader);
            }
        }

        return new PlayerModeSwitchIntent(mode);
    }

    internal static void WriteIntent(Utf8JsonWriter writer, PlayerModeSwitchIntent value)
    {
        writer.WriteStartObject();
        if (value.Mode is PlayerInteractionMode mode)
        {
            writer.WriteNumber(Mode, (int)mode);
        }

        writer.WriteEndObject();
    }
}

internal sealed class PlayerMoveIntentJsonConverter : JsonConverter<PlayerMoveIntent>
{
    private static readonly JsonEncodedText ForwardAxis = JsonEncodedText.Encode("forwardAxis");
    private static readonly JsonEncodedText StrafeAxis = JsonEncodedText.Encode("strafeAxis");
    private static readonly JsonEncodedText VerticalAxis = JsonEncodedText.Encode("verticalAxis");
    private static readonly JsonEncodedText JumpPressed = JsonEncodedText.Encode("jumpPressed");
    private static readonly JsonEncodedText FreezePressed = JsonEncodedText.Encode("freezePressed");
    private static readonly JsonEncodedText ToggleNoclipPressed = JsonEncodedText.Encode("toggleNoclipPressed");

    public override PlayerMoveIntent Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return ReadIntent(ref reader);
    }

    public override void Write(Utf8JsonWriter writer, PlayerMoveIntent value, JsonSerializerOptions options)
    {
        WriteIntent(writer, value);
    }

    internal static PlayerMoveIntent ReadIntent(ref Utf8JsonReader reader)
    {
        HotPathJson.ExpectStartObject(ref reader, nameof(PlayerMoveIntent));
        float forwardAxis = 0f;
        float strafeAxis = 0f;
        float verticalAxis = 0f;
        bool jumpPressed = false;
        bool freezePressed = false;
        bool toggleNoclipPressed = false;

        while (HotPathJson.NextProperty(ref reader, nameof(PlayerMoveIntent)))
        {
            if (reader.ValueTextEquals(ForwardAxis.EncodedUtf8Bytes))
            {
                forwardAxis = HotPathJson.ReadSingle(ref reader);
            }
            else if (reader.ValueTextEquals(StrafeAxis.EncodedUtf8Bytes))
            {
                strafeAxis = HotPathJson.ReadSingle(ref reader);
            }
            else if (reader.ValueTextEquals(VerticalAxis.EncodedUtf8Bytes))
            {
                verticalAxis = HotPathJson.ReadSingle(ref reader);
            }
            else if (reader.ValueTextEquals(JumpPressed.EncodedUtf8Bytes))
            {
                jumpPressed = HotPathJson.ReadBoolean(ref reader);
            }
            else if (reader.ValueTextEquals(FreezePressed.EncodedUtf8Bytes))
            {
                freezePressed = HotPathJson.ReadBoolean(ref reader);
            }
            else if (reader.ValueTextEquals(ToggleNoclipPressed.EncodedUtf8Bytes))
            {
                toggleNoclipPressed = HotPathJson.ReadBoolean(ref reader);
            }
            else
            {
                HotPathJson.SkipValue(ref reader);
            }
        }

        return new PlayerMoveIntent(forwardAxis, strafeAxis, verticalAxis, jumpPressed, freezePressed, toggleNoclipPressed);
    }

    internal static void WriteIntent(Utf8JsonWriter writer, PlayerMoveIntent value)
    {
        writer.WriteStartObject();
        writer.WriteNumber(ForwardAxis, value.ForwardAxis);
        writer.WriteNumber(StrafeAxis, value.StrafeAxis);
        writer.WriteNumber(VerticalAxis, value.VerticalAxis);
        writer.WriteBoolean(JumpPressed, value.JumpPressed);
        writer.WriteBoolean(FreezePressed, value.FreezePressed);
        writer.WriteBoolean(ToggleNoclipPressed, value.ToggleNoclipPressed);
        writer.WriteEndObject();
    }
}

internal sealed class PlayerLookIntentJsonConverter : JsonConverter<PlayerLookIntent>
{
    private static readonly JsonEncodedText YawDelta = JsonEncodedText.Encode("yawDelta");
    private static readonly JsonEncodedText PitchDelta = JsonEncodedText.Encode("pitchDelta");
    private static readonly JsonEncodedText Facing = JsonEncodedText.Encode("facing");

    public override PlayerLookIntent Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return ReadIntent(ref reader, options);
    }

    public override void Write(Utf8JsonWriter writer, PlayerLookIntent value, JsonSerializerOptions options)
    {
        WriteIntent(writer, value);
    }

    internal static PlayerLookIntent ReadIntent(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        HotPathJson.ExpectStartObject(ref reader, nameof(PlayerLookIntent));
        float yawDelta = 0f;
        float pitchDelta = 0f;
        OpenTK.Mathematics.Vector3? facing = null;

        while (HotPathJson.NextProperty(ref reader, nameof(PlayerLookIntent)))
        {
            if (reader.ValueTextEquals(YawDelta.EncodedUtf8Bytes))
            {
                yawDelta = HotPathJson.ReadSingle(ref reader);
            }
            else if (reader.ValueTextEquals(PitchDelta.EncodedUtf8Bytes))
            {
                pitchDelta = HotPathJson.ReadSingle(ref reader);
            }
            else if (reader.ValueTextEquals(Facing.EncodedUtf8Bytes))
            {
                facing = HotPathJson.ReadNullableVector3(ref reader, options);
            }
            else
            {
                HotPathJson.SkipValue(ref reader);
            }
        }

        return new PlayerLookIntent(yawDelta, pitchDelta, facing);
    }

    internal static void WriteIntent(Utf8JsonWriter writer, PlayerLookIntent value)
    {
        writer.WriteStartObject();
        writer.WriteNumber(YawDelta, value.YawDelta);
        writer.WriteNumber(PitchDelta, value.PitchDelta);
        if (value.Facing is OpenTK.Mathematics.Vector3 facing)
        {
            HotPathJson.WriteVector3(writer, Facing, facing);
        }

        writer.WriteEndObject();
    }
}
