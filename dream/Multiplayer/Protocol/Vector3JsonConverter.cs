using System.Text.Json;
using System.Text.Json.Serialization;
using OpenTK.Mathematics;

namespace Dream.Multiplayer.Protocol;


public sealed class Vector3JsonConverter : JsonConverter<Vector3>
{
    public override Vector3 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Expected a JSON object for Vector3.");
        }

        float x = 0f;
        float y = 0f;
        float z = 0f;
        bool hasX = false;
        bool hasY = false;
        bool hasZ = false;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                if (!hasX || !hasY || !hasZ)
                {
                    throw new JsonException("Vector3 requires x, y and z properties.");
                }

                return new Vector3(x, y, z);
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Unexpected token in Vector3 object.");
            }

            if (reader.ValueTextEquals("x"u8))
            {
                x = ReadComponent(ref reader);
                hasX = true;
            }
            else if (reader.ValueTextEquals("y"u8))
            {
                y = ReadComponent(ref reader);
                hasY = true;
            }
            else if (reader.ValueTextEquals("z"u8))
            {
                z = ReadComponent(ref reader);
                hasZ = true;
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }

        throw new JsonException("Unexpected end of JSON while reading Vector3.");
    }

    private static float ReadComponent(ref Utf8JsonReader reader)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.Number)
        {
            throw new JsonException("Vector3 components must be numbers.");
        }

        return reader.GetSingle();
    }

    public override void Write(Utf8JsonWriter writer, Vector3 value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("x", value.X);
        writer.WriteNumber("y", value.Y);
        writer.WriteNumber("z", value.Z);
        writer.WriteEndObject();
    }
}
