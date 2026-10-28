using System.Buffers;
using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;
using Dream.Multiplayer.Protocol;
using Dream.Simulation.Combat;
using Dream.Simulation.Players;
using Dream.Simulation.World;

namespace Dream.Tests.Integration;

// WIRE-9's once-gate under load: 64 concurrent readers on the thread pool walk 10,000 fresh
// SharedEncodedPayload instances in the same order, so every instance is raced by every reader
// that reaches it while it is unencoded (the winner encodes, the rest spin and then wait on the
// monitor). Every instance must be encoded exactly once, every reader must see the same array, and
// in fault rounds every reader must see the same exception instance. The whole run is bounded at
// 30 s, so a lost pulse (a deadlock in WaitUntilDone) fails instead of hanging.
public class SharedEncodedPayloadLoadIntegrationTests(ITestOutputHelper output)
{
    private const int Readers = 64;
    private const int Rounds = 10_000;
    // A deadlock bound, not a performance budget: the run normally takes a few seconds, and it
    // shares the machine with the rest of the parallel test run.
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    [Fact]
    public void SixtyFourThreadPoolReaders_TenThousandFreshPayloads_OneEncodeEach_SameArray_NoDeadlock()
    {
        RunLoad(faultEvery: 0);
    }

    [Fact]
    public void SixtyFourThreadPoolReaders_TenThousandFreshPayloads_MixedFaultRounds_EveryReaderGetsTheSameException()
    {
        RunLoad(faultEvery: 3);
    }

    private void RunLoad(int faultEvery)
    {
        CountingCodec[] codecs = new CountingCodec[Rounds];
        SharedEncodedPayload[] payloads = new SharedEncodedPayload[Rounds];
        for (int round = 0; round < Rounds; round++)
        {
            bool fault = faultEvery > 0 && round % faultEvery == 1;
            // A few rounds encode slowly, so readers pile up on the monitor wait, not only on the spin.
            codecs[round] = new CountingCodec(round, fault, slow: round % 500 == 0);
            payloads[round] = new SharedEncodedPayload(codecs[round], CreateMessage((ulong)round));
        }

        // Per round, what each reader observed: the array it was handed, or the exception it caught.
        byte[]?[,] arrays = new byte[Rounds, Readers][];
        Exception?[,] faults = new Exception[Rounds, Readers];
        int started = 0;
        Stopwatch elapsed = Stopwatch.StartNew();
        Task[] readers = new Task[Readers];
        for (int reader = 0; reader < Readers; reader++)
        {
            int index = reader;
            readers[index] = Task.Run(() =>
            {
                Interlocked.Increment(ref started);
                for (int round = 0; round < Rounds; round++)
                {
                    try
                    {
                        ReadOnlySpan<byte> bytes = payloads[round].Bytes;
                        arrays[round, index] = CachedArrayOf(payloads[round], bytes);
                    }
                    catch (Exception ex)
                    {
                        faults[round, index] = ex;
                    }
                }
            });
        }

        bool finished = Task.WaitAll(readers, Bound);
        elapsed.Stop();
        Assert.True(finished, $"The readers did not finish within {Bound.TotalSeconds} s (started {Volatile.Read(ref started)} of {Readers}): deadlock or starvation.");

        int faultRounds = 0;
        for (int round = 0; round < Rounds; round++)
        {
            CountingCodec codec = codecs[round];
            Assert.True(codec.Calls == 1, $"round {round}: encoded {codec.Calls} times.");
            if (codec.Fault)
            {
                faultRounds++;
                Exception first = Assert.IsType<InvalidOperationException>(faults[round, 0]);
                Assert.Contains($"round {round}", first.Message);
                Assert.False(payloads[round].IsEncoded);
                for (int reader = 0; reader < Readers; reader++)
                {
                    Assert.True(arrays[round, reader] is null, $"round {round} reader {reader}: got bytes from a failed encode.");
                    Assert.True(ReferenceEquals(first, faults[round, reader]), $"round {round} reader {reader}: a different exception instance ({faults[round, reader]?.GetType().Name}).");
                }
            }
            else
            {
                byte[] first = Assert.IsType<byte[]>(arrays[round, 0]);
                Assert.True(first.AsSpan().SequenceEqual(CountingCodec.PayloadFor(round)), $"round {round}: wrong payload bytes.");
                Assert.True(payloads[round].IsEncoded);
                for (int reader = 0; reader < Readers; reader++)
                {
                    Assert.True(faults[round, reader] is null, $"round {round} reader {reader}: unexpected {faults[round, reader]}.");
                    Assert.True(ReferenceEquals(first, arrays[round, reader]), $"round {round} reader {reader}: a different array.");
                }
            }
        }

        output.WriteLine($"{Readers} readers x {Rounds} payloads ({faultRounds} fault rounds) in {elapsed.ElapsedMilliseconds} ms");
        Assert.Equal(faultEvery > 0 ? (Rounds + faultEvery - 2) / faultEvery : 0, faultRounds);
    }

    // The span the payload hands out views its published array; recover that array through a second
    // read, which must return the very same memory.
    private static byte[] CachedArrayOf(SharedEncodedPayload payload, ReadOnlySpan<byte> bytes)
    {
        byte[] copy = bytes.ToArray();
        ReadOnlySpan<byte> again = payload.Bytes;
        Assert.True(again.Overlaps(bytes, out int offset) && offset == 0 && again.Length == bytes.Length, "A second read returned different memory.");
        return ArrayIdentity.Get(payload, copy);
    }

    private static ServerSnapshotMessage CreateMessage(ulong tick)
    {
        return new ServerSnapshotMessage(new ServerSnapshotEnvelope(
            tick,
            new Dictionary<string, ulong>(StringComparer.Ordinal),
            Array.Empty<ActorSnapshot>(),
            PlayerInteractionMode.Building,
            null,
            Array.Empty<WorldMutationDelta>(),
            Array.Empty<DamageEventDelta>()));
    }

    // The published array, read once per (payload, reader) through the same private field the WIRE-9
    // unit tests use; the span alone cannot name its array.
    private static class ArrayIdentity
    {
        private static readonly System.Reflection.FieldInfo BytesField = typeof(SharedEncodedPayload).GetField(
            "_bytes",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        public static byte[] Get(SharedEncodedPayload payload, byte[] expectedContent)
        {
            byte[] array = (byte[])BytesField.GetValue(payload)!;
            Assert.True(array.AsSpan().SequenceEqual(expectedContent));
            return array;
        }
    }

    private sealed class CountingCodec(int round, bool fault, bool slow) : IProtocolCodec
    {
        private int _calls;

        public bool Fault { get; } = fault;

        public int Calls => Volatile.Read(ref _calls);

        public static byte[] PayloadFor(int round) => System.Text.Encoding.UTF8.GetBytes($"{{\"round\":{round}}}");

        public void EncodePayload<TMessage>(TMessage message, IBufferWriter<byte> destination)
            where TMessage : IProtocolMessage
        {
            Interlocked.Increment(ref _calls);
            if (slow)
            {
                Thread.Sleep(2);
            }
            else
            {
                Thread.SpinWait(50);
            }

            if (Fault)
            {
                throw new InvalidOperationException($"Simulated encode failure in round {round}.");
            }

            destination.Write(PayloadFor(round));
        }

        public byte[] Encode<TMessage>(TransportEnvelope envelope, TMessage message)
            where TMessage : IProtocolMessage => throw new NotSupportedException();

        public void Encode<TMessage>(TransportEnvelope envelope, TMessage message, IBufferWriter<byte> destination)
            where TMessage : IProtocolMessage => throw new NotSupportedException();

        public void WriteEnvelope(TransportEnvelope envelope, ReadOnlySpan<byte> payloadJson, IBufferWriter<byte> destination)
            => throw new NotSupportedException();

        public DecodedProtocolMessage Decode(ReadOnlySpan<byte> payload) => throw new NotSupportedException();
    }
}
