using System.Text.Json;
using OpenTK.Mathematics;

namespace Dream.Multiplayer.Protocol;

// Shared reading and writing helpers for the hand-written hot-path converters
// (PlayerCommandJsonConverters, ActorSnapshotJsonConverter). They reproduce what the reflection
// serializer does with the codec's protocol v2 options:
// - writing: camelCase names in declaration order, numbers through the same Utf8JsonWriter
//   overloads, enums as their underlying number, and null properties left out;
// - reading: any property order, case-sensitive names, unknown properties skipped, a missing
//   property left at its default, and every type mismatch reported as a JsonException.
internal static class HotPathJson
{
    // The Vector3 wire shape lives in one place; reading it here keeps its strict x/y/z rule.
    private static readonly Vector3JsonConverter Vector3Converter = new();

    public static void ExpectStartObject(ref Utf8JsonReader reader, string typeName)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"Expected a JSON object for {typeName}, found {reader.TokenType}.");
        }
    }

    // Advances to the next property name; false once the object's EndObject is reached. The
    // reader is left on the property name, so the caller reads its value with one of the Read*
    // helpers below or calls SkipValue.
    public static bool NextProperty(ref Utf8JsonReader reader, string typeName)
    {
        if (!reader.Read())
        {
            throw new JsonException($"Unexpected end of JSON while reading {typeName}.");
        }

        if (reader.TokenType == JsonTokenType.EndObject)
        {
            return false;
        }

        if (reader.TokenType != JsonTokenType.PropertyName)
        {
            throw new JsonException($"Unexpected {reader.TokenType} token in {typeName}.");
        }

        return true;
    }

    public static void SkipValue(ref Utf8JsonReader reader)
    {
        reader.Skip();
    }

    // Moves from a property name to its value.
    public static void ReadValue(ref Utf8JsonReader reader)
    {
        if (!reader.Read())
        {
            throw new JsonException("Unexpected end of JSON while reading a property value.");
        }
    }

    public static int ReadInt32(ref Utf8JsonReader reader)
    {
        ReadValue(ref reader);
        if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out int value))
        {
            throw new JsonException($"Expected a 32-bit integer, found {reader.TokenType}.");
        }

        return value;
    }

    public static int? ReadNullableInt32(ref Utf8JsonReader reader)
    {
        ReadValue(ref reader);
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out int value))
        {
            throw new JsonException($"Expected a 32-bit integer or null, found {reader.TokenType}.");
        }

        return value;
    }

    public static ulong ReadUInt64(ref Utf8JsonReader reader)
    {
        ReadValue(ref reader);
        if (reader.TokenType != JsonTokenType.Number || !reader.TryGetUInt64(out ulong value))
        {
            throw new JsonException($"Expected an unsigned 64-bit integer, found {reader.TokenType}.");
        }

        return value;
    }

    public static float ReadSingle(ref Utf8JsonReader reader)
    {
        ReadValue(ref reader);
        if (reader.TokenType != JsonTokenType.Number || !reader.TryGetSingle(out float value))
        {
            throw new JsonException($"Expected a single-precision number, found {reader.TokenType}.");
        }

        return value;
    }

    public static bool ReadBoolean(ref Utf8JsonReader reader)
    {
        ReadValue(ref reader);
        return reader.TokenType switch
        {
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            _ => throw new JsonException($"Expected a boolean, found {reader.TokenType}.")
        };
    }

    public static string? ReadNullableString(ref Utf8JsonReader reader)
    {
        ReadValue(ref reader);
        return reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Null => null,
            _ => throw new JsonException($"Expected a string or null, found {reader.TokenType}.")
        };
    }

    // The reader is on the property name; a null value is rejected like the reflection serializer
    // rejects null for a non-nullable struct.
    public static Vector3 ReadVector3(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        ReadValue(ref reader);
        return Vector3Converter.Read(ref reader, typeof(Vector3), options);
    }

    public static Vector3? ReadNullableVector3(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        ReadValue(ref reader);
        return reader.TokenType == JsonTokenType.Null
            ? null
            : Vector3Converter.Read(ref reader, typeof(Vector3), options);
    }

    // Same output as Vector3JsonConverter.Write.
    public static void WriteVector3(Utf8JsonWriter writer, JsonEncodedText propertyName, Vector3 value)
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartObject();
        writer.WriteNumber(Names.X, value.X);
        writer.WriteNumber(Names.Y, value.Y);
        writer.WriteNumber(Names.Z, value.Z);
        writer.WriteEndObject();
    }

    private static class Names
    {
        public static readonly JsonEncodedText X = JsonEncodedText.Encode("x");
        public static readonly JsonEncodedText Y = JsonEncodedText.Encode("y");
        public static readonly JsonEncodedText Z = JsonEncodedText.Encode("z");
    }
}
