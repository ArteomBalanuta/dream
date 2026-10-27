using System.Text;
using System.Text.Json;
using Dream.Multiplayer.Protocol;
using Dream.Tests.Utilities;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Multiplayer.Protocol;

public sealed class Vector3JsonConverterTests
{
    private static readonly Vector3JsonConverter Converter = new Vector3JsonConverter();
    private static readonly JsonSerializerOptions Options = new JsonSerializerOptions();

    private static Vector3 ReadJson(string json)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        Utf8JsonReader reader = new Utf8JsonReader(bytes);
        Assert.True(reader.Read());
        return Converter.Read(ref reader, typeof(Vector3), Options);
    }

    private static byte[] Write(Vector3 value)
    {
        using MemoryStream stream = new MemoryStream();
        using (Utf8JsonWriter writer = new Utf8JsonWriter(stream))
        {
            Converter.Write(writer, value, Options);
        }

        return stream.ToArray();
    }

    [Theory]
    [InlineData(float.MaxValue, float.MinValue, float.Epsilon)]
    [InlineData(0f, 1.5f, -2.25f)]
    [InlineData(float.Epsilon, -float.Epsilon, 123456.789f)]
    public void RoundTripsEdgeValues(float x, float y, float z)
    {
        Vector3 original = new Vector3(x, y, z);

        Vector3 result = ReadJson(Encoding.UTF8.GetString(Write(original)));

        Assert.Equal(BitConverter.SingleToInt32Bits(x), BitConverter.SingleToInt32Bits(result.X));
        Assert.Equal(BitConverter.SingleToInt32Bits(y), BitConverter.SingleToInt32Bits(result.Y));
        Assert.Equal(BitConverter.SingleToInt32Bits(z), BitConverter.SingleToInt32Bits(result.Z));
    }

    [Fact]
    public void RoundTripsNegativeZero()
    {
        Vector3 result = ReadJson(Encoding.UTF8.GetString(Write(new Vector3(-0f, 0f, -0f))));

        Assert.True(float.IsNegative(result.X));
        Assert.False(float.IsNegative(result.Y));
        Assert.True(float.IsNegative(result.Z));
    }

    [Fact]
    public void AcceptsReorderedAndExtraProperties()
    {
        Vector3 result = ReadJson("{\"extra\":{\"a\":[1,2,{\"b\":null}]},\"z\":3,\"w\":\"s\",\"x\":1,\"y\":2,\"tail\":[true]}");

        Assert.Equal(new Vector3(1f, 2f, 3f), result);
    }

    [Theory]
    [InlineData("{\"y\":2,\"z\":3}")]
    [InlineData("{\"x\":1,\"z\":3}")]
    [InlineData("{\"x\":1,\"y\":2}")]
    [InlineData("{}")]
    [InlineData("{\"X\":1,\"Y\":2,\"Z\":3}")]
    public void MissingPropertyThrowsJsonException(string json)
    {
        Assert.Throws<JsonException>(() => ReadJson(json));
    }

    [Theory]
    [InlineData("[1,2,3]")]
    [InlineData("1")]
    [InlineData("null")]
    [InlineData("\"x\"")]
    [InlineData("{\"x\":\"1\",\"y\":2,\"z\":3}")]
    public void NonObjectOrNonNumberThrowsJsonException(string json)
    {
        Assert.Throws<JsonException>(() => ReadJson(json));
    }

    [Fact]
    public void ReadLeavesReaderOnEndObject()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("[{\"x\":1,\"y\":2,\"z\":3},{\"x\":4,\"y\":5,\"z\":6}]");
        Utf8JsonReader reader = new Utf8JsonReader(bytes);
        reader.Read();
        reader.Read();
        Vector3 first = Converter.Read(ref reader, typeof(Vector3), Options);
        Assert.Equal(JsonTokenType.EndObject, reader.TokenType);
        reader.Read();
        Vector3 second = Converter.Read(ref reader, typeof(Vector3), Options);

        Assert.Equal(new Vector3(1f, 2f, 3f), first);
        Assert.Equal(new Vector3(4f, 5f, 6f), second);
    }

    [Fact]
    public void ReadAllocatesNothingPerVector()
    {
        const int Count = 1000;
        StringBuilder builder = new StringBuilder("[");
        for (int i = 0; i < Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append("{\"x\":").Append(i).Append(".5,\"y\":-").Append(i).Append(",\"z\":3.25}");
        }

        builder.Append(']');
        byte[] bytes = Encoding.UTF8.GetBytes(builder.ToString());

        long bytesPerIteration = AllocationAssert.AtMost(0, () =>
        {
            Utf8JsonReader reader = new Utf8JsonReader(bytes);
            reader.Read();
            for (int i = 0; i < Count; i++)
            {
                reader.Read();
                Converter.Read(ref reader, typeof(Vector3), Options);
            }
        });

        Assert.Equal(0, bytesPerIteration);
    }
}
