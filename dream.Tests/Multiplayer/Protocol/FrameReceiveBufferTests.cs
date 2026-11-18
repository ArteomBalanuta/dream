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

    // A wall-clock budget is not a complexity claim in either direction. The old
    // "ElapsedMilliseconds < 2000" could not see the asymptotics at all - a genuinely quadratic
    // drain finishes well inside 2 s at this N, so it stayed green - while it DID fire on a
    // correct linear drain whenever the parallel phase saturated the machine, which is exactly
    // when the suite runs.
    //
    // The claim is about growth, so the test measures growth: drain N frames and 4N frames and
    // compare. Linear work gives about 4x; the ToArray-per-frame drain this test was written
    // against copied on the order of n^2 bytes, which gives about 16x. The budget is a ratio
    // measured on the same host in the same test, best of several runs for each size, so it is
    // scale-free and survives a loaded host instead of being decided by it.
    //
    // (The assertion this type really wants is a copy or compaction counter on
    // FrameReceiveBuffer - Assert.InRange(buffer.Compactions, 0, k) - which needs a counter in the
    // product and so is not done here.)
    [Fact]
    public void LargeBacklog_IsReadWithoutQuadraticCopying()
    {
        const int Frames = 12_500;
        double small = BestDrainMicroseconds(Frames);
        double large = BestDrainMicroseconds(Frames * 4);
        double ratio = large / Math.Max(small, 1d);

        Assert.True(
            ratio < 8d,
            $"draining {Frames} frames took {small:0} us and {Frames * 4} took {large:0} us: {ratio:0.00}x for 4x the work, " +
            "which is not linear (quadratic copying would be about 16x)");
    }

    // The fastest of five drains of `frames` queued frames, in microseconds. The fastest run is the
    // one least disturbed by a neighbour on the machine: scheduling noise only ever adds time.
    private static double BestDrainMicroseconds(int frames)
    {
        LengthPrefixedMessageFramer framer = new();
        byte[] frame = framer.Frame(new byte[64]);
        double best = double.MaxValue;
        for (int run = 0; run < 5; run++)
        {
            FrameReceiveBuffer buffer = new();
            for (int index = 0; index < frames; index++)
            {
                buffer.Append(frame);
            }

            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            int count = 0;
            while (buffer.TryReadFrame(framer, out _))
            {
                count++;
            }

            double microseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMicroseconds;
            Assert.Equal(frames, count);
            best = Math.Min(best, microseconds);
        }

        return best;
    }
}
