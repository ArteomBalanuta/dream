using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;
using Xunit.Abstractions;
using Dream.Multiplayer.Diagnostics;
using Dream.Multiplayer.Protocol;
using Dream.Simulation.Combat;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using Dream.Tests.Utilities;

namespace Dream.Tests.Server;

public class SharedEncodedPayloadTests
{
    private const int Readers = 16;

    private readonly ITestOutputHelper _output;

    public SharedEncodedPayloadTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void ConcurrentReaders_ShareOneEncodeAndTheSameArray()
    {
        StubCodec codec = new() { EncodeDelayMilliseconds = 50 };
        SharedEncodedPayload payload = new(codec, CreateMessage());
        byte[][] arrays = new byte[Readers][];

        RunReaders(index =>
        {
            ReadOnlySpan<byte> bytes = payload.Bytes;
            Assert.True(bytes.SequenceEqual(StubCodec.Payload));
            arrays[index] = CachedArray(payload);
            // The span must view that cached array, not a copy.
            Assert.True(Unsafe.AreSame(in MemoryMarshal.GetReference(bytes), in MemoryMarshal.GetArrayDataReference(arrays[index])));
        });

        Assert.Equal(1, codec.EncodePayloadCalls);
        Assert.True(payload.IsEncoded);
        Assert.All(arrays, array => Assert.Same(arrays[0], array));
    }

    [Fact]
    public void FailedEncode_GivesEveryReaderTheSameException_LoggedOnce_WithoutRetry()
    {
        StubCodec codec = new() { Fail = true, EncodeDelayMilliseconds = 20 };
        SharedEncodedPayload payload = new(codec, CreateMessage());
        List<string> lines = [];
        Exception[] caught = new Exception[Readers];

        // Diagnostics capture is per thread, so the first read, which encodes and logs, happens
        // here; the concurrent readers afterwards must neither encode nor log again.
        using (NetworkDiagnosticsLog.CaptureOnCurrentThread((category, message) => lines.Add($"{category} {message}")))
        {
            caught[0] = Assert.Throws<InvalidOperationException>(() => payload.Bytes.ToArray());
        }

        RunReaders(index =>
        {
            if (index != 0)
            {
                caught[index] = Assert.Throws<InvalidOperationException>(() => payload.Bytes.ToArray());
            }
        });

        Assert.Equal(1, codec.EncodePayloadCalls);
        Assert.False(payload.IsEncoded);
        Assert.Single(lines);
        Assert.Contains("server.snapshot.encode.fault", lines[0]);
        Assert.All(caught, ex => Assert.Same(caught[0], ex));
        Assert.Equal("Simulated payload encode failure.", caught[0].Message);
        Assert.Contains(nameof(StubCodec.EncodePayload), caught[0].StackTrace);
    }

    [Fact]
    public void FailedEncode_WithConcurrentFirstReaders_EncodesOnce_AndAllSeeTheSameException()
    {
        StubCodec codec = new() { Fail = true, EncodeDelayMilliseconds = 50 };
        SharedEncodedPayload payload = new(codec, CreateMessage());
        Exception[] caught = new Exception[Readers];

        RunReaders(index => caught[index] = Assert.Throws<InvalidOperationException>(() => payload.Bytes.ToArray()));

        Assert.Equal(1, codec.EncodePayloadCalls);
        Assert.All(caught, ex => Assert.Same(caught[0], ex));
    }

    [Fact]
    public void CreateAndReadOnce_GateOverheadIsSmall()
    {
        StubCodec codec = new();
        ServerSnapshotMessage message = CreateMessage();
        long bytes = AllocationAssert.MeasureBytesPerIteration(() =>
        {
            SharedEncodedPayload payload = new(codec, message);
            _ = payload.Bytes.Length;
        });
        long resultArray = AllocationAssert.MeasureBytesPerIteration(() => _ = StubCodec.Payload.AsSpan().ToArray());
        long overhead = bytes - resultArray;

        _output.WriteLine($"create + read once: {bytes} B, of which {resultArray} B is the result array; gate overhead {overhead} B");
        // The payload object itself is 56 B (header, 4 references, state); the Lazy and its factory
        // delegate used to add about 140 B on top.
        Assert.True(overhead <= 64, $"Gate overhead {overhead} B exceeds 64 B.");
    }

    private static byte[] CachedArray(SharedEncodedPayload payload)
    {
        System.Reflection.FieldInfo field = typeof(SharedEncodedPayload).GetField(
            "_bytes", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        return (byte[])field.GetValue(payload)!;
    }

    private static void RunReaders(Action<int> read)
    {
        using Barrier barrier = new(Readers);
        Task[] tasks = new Task[Readers];
        for (int index = 0; index < Readers; index++)
        {
            int readerIndex = index;
            tasks[index] = Task.Factory.StartNew(
                () =>
                {
                    barrier.SignalAndWait();
                    read(readerIndex);
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        Assert.True(Task.WaitAll(tasks, TimeSpan.FromSeconds(30)));
    }

    private static ServerSnapshotMessage CreateMessage()
    {
        return new ServerSnapshotMessage(new ServerSnapshotEnvelope(
            1,
            new Dictionary<string, ulong>(StringComparer.Ordinal),
            Array.Empty<ActorSnapshot>(),
            PlayerInteractionMode.Building,
            null,
            Array.Empty<WorldMutationDelta>(),
            Array.Empty<DamageEventDelta>()));
    }

    private sealed class StubCodec : IProtocolCodec
    {
        public static readonly byte[] Payload = "{\"stub\":true}"u8.ToArray();

        private int _encodePayloadCalls;

        public bool Fail { get; init; }

        public int EncodeDelayMilliseconds { get; init; }

        public int EncodePayloadCalls => Volatile.Read(ref _encodePayloadCalls);

        public void EncodePayload<TMessage>(TMessage message, IBufferWriter<byte> destination)
            where TMessage : IProtocolMessage
        {
            Interlocked.Increment(ref _encodePayloadCalls);
            if (EncodeDelayMilliseconds > 0)
            {
                Thread.Sleep(EncodeDelayMilliseconds);
            }

            if (Fail)
            {
                throw new InvalidOperationException("Simulated payload encode failure.");
            }

            destination.Write(Payload);
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
