using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Tests.Utilities;

namespace Dream.Tests.Multiplayer.Protocol;

// TryDeframe copied every inbound payload into a new array (each client command on the server,
// each ~8.5 KB snapshot on the client). The span API returns frame bounds and a view instead.
public class FrameReceiveBufferSpanTests
{
    [Fact]
    public void FramesSplitAcrossChunks_AreReassembledInOrder()
    {
        LengthPrefixedMessageFramer framer = new();
        byte[] stream = [.. framer.Frame([1, 2, 3]), .. framer.Frame([4]), .. framer.Frame(new byte[5000])];
        FrameReceiveBuffer buffer = new(initialCapacity: 64);
        List<byte[]> received = [];

        for (int offset = 0; offset < stream.Length; offset += 7)
        {
            buffer.Append(stream.AsSpan(offset, Math.Min(7, stream.Length - offset)));
            while (buffer.TryReadPayload(framer, out ReadOnlySpan<byte> payload))
            {
                received.Add(payload.ToArray());
            }
        }

        Assert.Equal(3, received.Count);
        Assert.Equal([1, 2, 3], received[0]);
        Assert.Equal([4], received[1]);
        Assert.Equal(5000, received[2].Length);
        Assert.Equal(0, buffer.BufferedBytes);
    }

    [Fact]
    public void MultipleFramesInOneAppend_AreReadInOrder()
    {
        LengthPrefixedMessageFramer framer = new();
        byte[] stream = [.. framer.Frame([7, 8]), .. framer.Frame([9]), .. framer.Frame([10, 11, 12])];
        FrameReceiveBuffer buffer = new();
        buffer.Append(stream);

        Assert.True(buffer.TryReadPayload(framer, out ReadOnlySpan<byte> first));
        Assert.Equal([7, 8], first.ToArray());
        Assert.True(buffer.TryReadPayload(framer, out ReadOnlySpan<byte> second));
        Assert.Equal([9], second.ToArray());
        Assert.True(buffer.TryReadPayload(framer, out ReadOnlySpan<byte> third));
        Assert.Equal([10, 11, 12], third.ToArray());
        Assert.False(buffer.TryReadPayload(framer, out _));
        Assert.Equal(0, buffer.BufferedBytes);
    }

    [Fact]
    public void PartialFrame_IsNotConsumedUntilComplete()
    {
        LengthPrefixedMessageFramer framer = new();
        byte[] framed = framer.Frame([1, 2, 3, 4, 5]);
        FrameReceiveBuffer buffer = new();

        buffer.Append(framed.AsSpan(0, 2));
        Assert.False(buffer.TryReadPayload(framer, out _));
        Assert.Equal(2, buffer.BufferedBytes);

        buffer.Append(framed.AsSpan(2, 4));
        Assert.False(buffer.TryReadPayload(framer, out _));
        Assert.Equal(6, buffer.BufferedBytes);

        buffer.Append(framed.AsSpan(6));
        Assert.True(buffer.TryReadPayload(framer, out ReadOnlySpan<byte> payload));
        Assert.Equal([1, 2, 3, 4, 5], payload.ToArray());
        Assert.Equal(0, buffer.BufferedBytes);
    }

    [Fact]
    public void EmptyFrame_IsConsumedWithEmptyPayload()
    {
        LengthPrefixedMessageFramer framer = new();
        byte[] stream = [.. framer.Frame([]), .. framer.Frame([42])];
        FrameReceiveBuffer buffer = new();
        buffer.Append(stream);

        Assert.True(buffer.TryReadPayload(framer, out ReadOnlySpan<byte> empty));
        Assert.True(empty.IsEmpty);
        Assert.True(buffer.TryReadPayload(framer, out ReadOnlySpan<byte> next));
        Assert.Equal([42], next.ToArray());
    }

    [Fact]
    public void ConsumedSpace_IsReusedInsteadOfGrowingForever()
    {
        LengthPrefixedMessageFramer framer = new();
        FrameReceiveBuffer buffer = new(initialCapacity: 256);
        byte[] frame = framer.Frame(new byte[100]);

        for (int index = 0; index < 1000; index++)
        {
            buffer.Append(frame);
            Assert.True(buffer.TryReadPayload(framer, out _));
        }

        Assert.Equal(256, buffer.Capacity);
    }

    [Fact]
    public void TryReadFrameBounds_ReportsOffsetsWithoutCopying()
    {
        LengthPrefixedMessageFramer framer = new();
        byte[] framed = framer.Frame([10, 20, 30, 40]);

        Assert.True(framer.TryReadFrameBounds(framed, out int offset, out int length, out int consumed));
        Assert.Equal(4, offset);
        Assert.Equal(4, length);
        Assert.Equal(framed.Length, consumed);
        Assert.False(framer.TryReadFrameBounds(framed.AsSpan(0, 3), out _, out _, out _));
        Assert.False(framer.TryReadFrameBounds(framed.AsSpan(0, 7), out _, out _, out int partialConsumed));
        Assert.Equal(0, partialConsumed);
    }

    [Fact]
    public void TryReadFrameBounds_RejectsNegativeLength()
    {
        LengthPrefixedMessageFramer framer = new();
        byte[] framed = BitConverter.GetBytes(-1);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            framer.TryReadFrameBounds(framed, out _, out _, out _));
        Assert.Contains("Negative", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TryReadFrameBounds_RejectsFramesLargerThanConfiguredMaximum()
    {
        LengthPrefixedMessageFramer framer = new(maxPayloadBytes: 8);
        byte[] framed = BitConverter.GetBytes(12);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            framer.TryReadFrameBounds(framed, out _, out _, out _));
        Assert.Contains("maximum", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryReadFrameBounds_RejectsOverflowingLength()
    {
        LengthPrefixedMessageFramer framer = new(maxPayloadBytes: int.MaxValue);
        byte[] framed = BitConverter.GetBytes(int.MaxValue);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            framer.TryReadFrameBounds(framed, out _, out _, out _));
        Assert.Contains("overflow", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryReadPayload_PropagatesValidationErrors()
    {
        LengthPrefixedMessageFramer framer = new(maxPayloadBytes: 8);
        FrameReceiveBuffer buffer = new();
        buffer.Append(BitConverter.GetBytes(12));

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            buffer.TryReadPayload(framer, out _));
        Assert.Contains("maximum", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryDeframe_StillCopiesPayloadForCompatibility()
    {
        LengthPrefixedMessageFramer framer = new();
        byte[] framed = framer.Frame([5, 6]);

        Assert.True(framer.TryDeframe(framed, out byte[]? payload, out int consumed));
        Assert.Equal([5, 6], payload);
        Assert.Equal(framed.Length, consumed);
        Assert.False(framer.TryDeframe(framed.AsSpan(0, 5), out byte[]? partial, out int partialConsumed));
        Assert.Null(partial);
        Assert.Equal(0, partialConsumed);
    }

    [Fact]
    public void ReadingSmallFrames_ThroughSpanApi_AllocatesNothing()
    {
        // Before (byte[] TryReadFrame): 5600 B per 100 frames of 32 B, i.e. 56 B per frame.
        const int FrameCount = 100;
        LengthPrefixedMessageFramer framer = new();
        byte[] stream = BuildStream(framer, FrameCount, payloadLength: 32);
        FrameReceiveBuffer buffer = new(initialCapacity: stream.Length * 2);
        int framesRead = 0;
        int appends = 0;
        long checksum = 0;

        AllocationAssert.AtMost(0, () =>
        {
            appends++;
            buffer.Append(stream);
            while (buffer.TryReadPayload(framer, out ReadOnlySpan<byte> payload))
            {
                framesRead++;
                checksum += payload[0];
            }
        });

        // Every append is read back completely, whatever number of calls the helper makes.
        Assert.True(appends > 0);
        Assert.Equal(FrameCount * appends, framesRead);
        Assert.True(checksum > 0);
    }

    private static byte[] BuildStream(LengthPrefixedMessageFramer framer, int frameCount, int payloadLength)
    {
        List<byte> stream = [];
        for (int index = 0; index < frameCount; index++)
        {
            byte[] payload = new byte[payloadLength];
            payload.AsSpan().Fill((byte)index);
            stream.AddRange(framer.Frame(payload));
        }

        return [.. stream];
    }
}
