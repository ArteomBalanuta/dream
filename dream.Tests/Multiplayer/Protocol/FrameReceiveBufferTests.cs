using Xunit;
using Dream.Multiplayer.Protocol;

namespace Dream.Tests.Multiplayer.Protocol;

// Both receive paths used List<byte> + ToArray() per frame, copying the whole backlog for every
// frame read (quadratic for large join snapshots).
public class FrameReceiveBufferTests
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
            while (buffer.TryReadFrame(framer, out byte[]? payload))
            {
                received.Add(payload!);
            }
        }

        Assert.Equal(3, received.Count);
        Assert.Equal([1, 2, 3], received[0]);
        Assert.Equal([4], received[1]);
        Assert.Equal(5000, received[2].Length);
        Assert.Equal(0, buffer.BufferedBytes);
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
            Assert.True(buffer.TryReadFrame(framer, out _));
        }

        Assert.Equal(256, buffer.Capacity);
    }

    [Fact]
    public void LargeBacklog_IsReadWithoutQuadraticCopying()
    {
        LengthPrefixedMessageFramer framer = new();
        FrameReceiveBuffer buffer = new();
        byte[] frame = framer.Frame(new byte[64]);
        for (int index = 0; index < 50_000; index++)
        {
            buffer.Append(frame);
        }

        System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
        int count = 0;
        while (buffer.TryReadFrame(framer, out _))
        {
            count++;
        }

        stopwatch.Stop();
        Assert.Equal(50_000, count);
        // ~3.4 MB backlog: the old ToArray-per-frame approach copied on the order of 85 GB here.
        Assert.True(stopwatch.ElapsedMilliseconds < 2000, $"Reading took {stopwatch.ElapsedMilliseconds} ms.");
    }
}
