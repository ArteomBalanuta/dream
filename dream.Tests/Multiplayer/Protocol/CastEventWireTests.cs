using System.Text;
using System.Text.Json;
using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Simulation.Content;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using static Dream.Tests.Multiplayer.Protocol.ServerSnapshotSamples;

namespace Dream.Tests.Multiplayer.Protocol;

// Protocol v22: the trailing optional "castEvents" of a server snapshot. The reflection
// serializer (ProtocolWireOptions.V3) stays the byte-identity reference for the hand-written
// converter; absent and empty both mean "no casts", and an empty list is never written.
public class CastEventWireTests
{
    private static readonly JsonSerializerOptions Reference = ProtocolWireOptions.V3;

    private static readonly CastEventDelta Cleave = new(7, SkillKind.MeleeArc, new Vector3(1.5f, 2f, -3.25f), new Vector3(0f, 0f, -1f), 2.5f);
    private static readonly CastEventDelta Bolt = new(9, SkillKind.RangedBolt, new Vector3(-10f, 0.5f, 4f), new Vector3(0.6f, 0f, 0.8f), 24f);
    private static readonly CastEventDelta Mend = new(11, SkillKind.SelfHeal, new Vector3(0f, 1f, 0f), Vector3.Zero, 0f);

    private static ServerSnapshotEnvelope Base()
    {
        return new ServerSnapshotEnvelope(
            42,
            new Dictionary<string, ulong>(),
            [],
            PlayerInteractionMode.Combat,
            null,
            [],
            [],
            null,
            5);
    }

    private static string Encode(ServerSnapshotEnvelope snapshot)
    {
        System.Buffers.ArrayBufferWriter<byte> payload = new();
        new TransportJsonCodec().EncodePayload(new ServerSnapshotMessage(snapshot), payload);
        return Encoding.UTF8.GetString(payload.WrittenSpan);
    }

    private static ServerSnapshotMessage Decode(string payload)
    {
        string json = "{\"protocolVersion\":" + TransportEnvelope.CurrentProtocolVersion
            + ",\"messageKind\":\"server.snapshot\",\"sequence\":1,\"payload\":" + payload + "}";
        return Assert.IsType<ServerSnapshotMessage>(new TransportJsonCodec().Decode(Encoding.UTF8.GetBytes(json)).Message);
    }

    private static void AssertByteIdentical(ServerSnapshotEnvelope snapshot)
    {
        string reference = Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(new ServerSnapshotMessage(snapshot), Reference));
        Assert.Equal(reference, Encode(snapshot));
    }

    [Fact]
    public void ProtocolVersion_IsTwentyTwo()
    {
        Assert.Equal(22, TransportEnvelope.CurrentProtocolVersion);
    }

    [Fact]
    public void NullEmptyOneAndMany_AreByteIdenticalToReference()
    {
        ServerSnapshotEnvelope snapshot = Base();
        AssertByteIdentical(snapshot with { CastEvents = null });
        AssertByteIdentical(snapshot with { CastEvents = Array.Empty<CastEventDelta>() });
        AssertByteIdentical(snapshot with { CastEvents = new[] { Cleave } });
        AssertByteIdentical(snapshot with { CastEvents = new[] { Cleave, Bolt, Mend, Cleave } });
    }

    [Fact]
    public void RandomCastLists_AreByteIdenticalToReference()
    {
        Random random = new(22_2026);
        for (int i = 0; i < 200; i++)
        {
            AssertByteIdentical(RandomSnapshot(random) with { CastEvents = RandomCastEvents(random, random.Next(0, 8)) });
        }
    }

    [Fact]
    public void EdgeValueCasts_AreByteIdenticalToReference()
    {
        List<CastEventDelta> edge = [];
        foreach (float value in EdgeFloats)
        {
            edge.Add(new CastEventDelta(int.MinValue, (SkillKind)(-1), new Vector3(value, -value, value), new Vector3(-value, value, 0f), value));
            edge.Add(new CastEventDelta(int.MaxValue, (SkillKind)99, new Vector3(value), Vector3.Zero, -value));
        }

        AssertByteIdentical(Base() with { CastEvents = edge });
    }

    [Fact]
    public void CastFreeSnapshot_ContainsNoCastEventsProperty()
    {
        Assert.DoesNotContain("castEvents", Encode(Base() with { CastEvents = null }), StringComparison.Ordinal);
        // The empty-is-absent rule is enforced where the list is produced (export, coalesce, drop),
        // which all yield null; the converter itself writes what it is given, like the reference.
        Assert.Contains("\"castEvents\":[]", Encode(Base() with { CastEvents = Array.Empty<CastEventDelta>() }), StringComparison.Ordinal);
    }

    [Fact]
    public void CastEvents_AreWrittenLast_AfterWorldChecksumSequence()
    {
        string json = Encode(Base() with { CastEvents = new[] { Cleave } });
        Assert.True(json.IndexOf("\"castEvents\"", StringComparison.Ordinal) > json.IndexOf("\"worldChecksumSequence\"", StringComparison.Ordinal));
        Assert.EndsWith("]}}", json, StringComparison.Ordinal);
    }

    [Fact]
    public void CastEvents_RoundTripWithValuesIntact()
    {
        CastEventDelta[] casts = [Cleave, Bolt, Mend];
        ServerSnapshotMessage decoded = Decode(Encode(Base() with { CastEvents = casts }));

        Assert.NotNull(decoded.Snapshot.CastEvents);
        Assert.Equal(casts, decoded.Snapshot.CastEvents!.ToArray());
    }

    [Fact]
    public void EdgeValueCasts_RoundTripBitForBit()
    {
        CastEventDelta[] casts = new CastEventDelta[EdgeFloats.Length];
        for (int i = 0; i < casts.Length; i++)
        {
            float value = EdgeFloats[i];
            casts[i] = new CastEventDelta(int.MaxValue - i, (SkillKind)(i % 5 - 1), new Vector3(value, -value, 1f), new Vector3(0f, value, -value), value);
        }

        IReadOnlyList<CastEventDelta> decoded = Decode(Encode(Base() with { CastEvents = casts })).Snapshot.CastEvents!;
        Assert.Equal(casts.Length, decoded.Count);
        for (int i = 0; i < casts.Length; i++)
        {
            Assert.Equal(casts[i], decoded[i]);
        }
    }

    [Fact]
    public void AbsentCastEvents_DecodeAsNull_WithoutError()
    {
        ServerSnapshotMessage decoded = Decode(Encode(Base()));
        Assert.Null(decoded.Snapshot.CastEvents);
    }

    [Fact]
    public void EmptyCastEventsList_RoundTripsAsAnEmptyList()
    {
        // An empty list is never written by the server, but a peer that sends one must decode
        // exactly as the reference does (an empty array), and mean "no casts".
        ServerSnapshotMessage decoded = Decode(Encode(Base() with { CastEvents = Array.Empty<CastEventDelta>() }));
        Assert.True(decoded.Snapshot.CastEvents is null || decoded.Snapshot.CastEvents.Count == 0);

        const string payload = "{\"snapshot\":{\"serverTick\":1,\"interactionMode\":0,\"worldChecksumSequence\":0,\"castEvents\":[]}}";
        ServerSnapshotMessage expected = JsonSerializer.Deserialize<ServerSnapshotMessage>(payload, Reference)!;
        ServerSnapshotMessage actual = Decode(payload);
        Assert.NotNull(actual.Snapshot.CastEvents);
        Assert.Empty(actual.Snapshot.CastEvents!);
        Assert.Equal(expected.Snapshot.CastEvents is null, actual.Snapshot.CastEvents is null);
    }

    [Fact]
    public void NullCastEventsToken_DecodesAsNull_LikeTheReference()
    {
        const string payload = "{\"snapshot\":{\"serverTick\":1,\"interactionMode\":0,\"castEvents\":null}}";
        ServerSnapshotMessage expected = JsonSerializer.Deserialize<ServerSnapshotMessage>(payload, Reference)!;
        Assert.Null(expected.Snapshot.CastEvents);
        Assert.Null(Decode(payload).Snapshot.CastEvents);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"snapshot\":{}}")]
    [InlineData("{\"snapshot\":{\"serverTick\":3,\"castEvents\":null,\"actors\":null,\"worldMutations\":null,\"damageEvents\":null}}")]
    public void DegenerateSnapshots_StillDecode_WithNullCasts(string payload)
    {
        ServerSnapshotMessage expected = JsonSerializer.Deserialize<ServerSnapshotMessage>(payload, Reference)!;
        ServerSnapshotMessage actual = Decode(payload);
        Assert.Equal(expected.Snapshot.CastEvents is null, actual.Snapshot.CastEvents is null);
        Assert.Null(actual.Snapshot.CastEvents);
    }

    [Fact]
    public void ReorderedExtraAndDuplicatedProperties_DecodeLikeTheReference()
    {
        // castEvents first, a duplicate castEvents (the later one wins), unknown properties at the
        // envelope and inside a cast, and cast fields in a different order.
        const string payload =
            "{\"snapshot\":{\"castEvents\":[{\"actorId\":1,\"kind\":0,\"origin\":{\"x\":0,\"y\":0,\"z\":0},\"direction\":{\"x\":0,\"y\":0,\"z\":1},\"range\":1}]," +
            "\"serverTick\":8,\"unknown\":{\"a\":[1,2]}," +
            "\"castEvents\":[{\"extra\":[true],\"range\":24.5,\"direction\":{\"x\":1,\"y\":0,\"z\":0},\"origin\":{\"x\":3,\"y\":4,\"z\":5},\"kind\":1,\"actorId\":77}," +
            "{\"actorId\":78,\"kind\":2}],\"interactionMode\":1}}";

        ServerSnapshotMessage expected = JsonSerializer.Deserialize<ServerSnapshotMessage>(payload, Reference)!;
        ServerSnapshotMessage actual = Decode(payload);

        Assert.Equal(2, actual.Snapshot.CastEvents!.Count);
        Assert.Equal(
            new CastEventDelta(77, SkillKind.RangedBolt, new Vector3(3f, 4f, 5f), new Vector3(1f, 0f, 0f), 24.5f),
            actual.Snapshot.CastEvents[0]);
        Assert.Equal(new CastEventDelta(78, SkillKind.SelfHeal, Vector3.Zero, Vector3.Zero, 0f), actual.Snapshot.CastEvents[1]);
        Assert.Equal(
            Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(expected, Reference)),
            Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(actual, Reference)));
    }

    [Fact]
    public void TryCoalesce_ConcatenatesCasts_AndKeepsEmptyAsNull()
    {
        ServerSnapshotEnvelope older = Base() with { CastEvents = new[] { Cleave } };
        ServerSnapshotEnvelope newer = Base() with { CastEvents = new[] { Bolt, Mend } };

        Assert.True(ServerSnapshotEnvelope.TryCoalesce(older, newer, out ServerSnapshotEnvelope merged));
        Assert.Equal(new[] { Cleave, Bolt, Mend }, merged.CastEvents!.ToArray());

        Assert.True(ServerSnapshotEnvelope.TryCoalesce(Base(), newer, out merged));
        Assert.Equal(new[] { Bolt, Mend }, merged.CastEvents!.ToArray());

        Assert.True(ServerSnapshotEnvelope.TryCoalesce(older, Base() with { CastEvents = Array.Empty<CastEventDelta>() }, out merged));
        Assert.Equal(new[] { Cleave }, merged.CastEvents!.ToArray());

        Assert.True(ServerSnapshotEnvelope.TryCoalesce(Base(), Base() with { CastEvents = Array.Empty<CastEventDelta>() }, out merged));
        Assert.Null(merged.CastEvents);

        Assert.True(ServerSnapshotEnvelope.TryCoalesce(Base(), Base(), out merged));
        Assert.Null(merged.CastEvents);
    }
}
