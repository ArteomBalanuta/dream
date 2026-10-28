using System.Buffers;
using System.Diagnostics;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;
using Dream.Multiplayer.Protocol;
using Dream.Simulation.World;
using Dream.Tests.Multiplayer.Protocol;

namespace Dream.Tests.Integration;

// WIRE-11's snapshot converters (message, envelope, mutations with fracture pieces, damage
// events, plus the pooled scratch arrays of their readers) are static on the codec's options and
// shared by every writer thread. On the server each snapshot is encoded once per tick through
// SharedEncodedPayload, whose encode runs on whichever writer thread gets there first, into that
// thread's pooled scratch. Here 16 threads race on fresh shared payloads (all start together, so
// the once-gate is contended), also encode and decode the same snapshots privately, and every
// result must equal the reflection reference (ProtocolWireOptions.V2) byte for byte.
public class ServerSnapshotConverterConcurrencyIntegrationTests(ITestOutputHelper output)
{
    private const int Threads = 16;
    private const int Snapshots = 40;
    private const int Rounds = 6;
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);

    [Fact]
    public void SixteenThreads_EncodeThroughSharedEncodedPayload_AreByteIdenticalToTheReference()
    {
        Random random = new(16_2026);
        TransportJsonCodec sharedCodec = new();
        ServerSnapshotMessage[] snapshots = new ServerSnapshotMessage[Snapshots];
        byte[][] expected = new byte[Snapshots][];
        for (int i = 0; i < Snapshots; i++)
        {
            ServerSnapshotEnvelope snapshot = ServerSnapshotSamples.RandomSnapshot(
                random,
                roundTrippable: true,
                actorCount: i switch { 0 => 0, 1 => 24, _ => random.Next(0, 200) });
            if (i % 4 == 0)
            {
                // Long mutation and damage lists push the readers' pooled scratch past its first size.
                snapshot = snapshot with
                {
                    WorldMutations = ServerSnapshotSamples.RandomMutations(random, random.Next(40, 120)),
                    DamageEvents = ServerSnapshotSamples.RandomDamageEvents(random, random.Next(20, 60))
                };
            }

            snapshots[i] = new ServerSnapshotMessage(snapshot);
            expected[i] = JsonSerializer.SerializeToUtf8Bytes(snapshots[i], ProtocolWireOptions.V2);
        }

        string?[] failures = new string?[Threads];
        int sharedReads = 0;
        int privateEncodes = 0;
        int decodes = 0;
        Stopwatch elapsed = Stopwatch.StartNew();
        for (int round = 0; round < Rounds; round++)
        {
            // A fresh set of shared payloads per round, so every round races on the first encode.
            SharedEncodedPayload[] shared = new SharedEncodedPayload[Snapshots];
            for (int i = 0; i < Snapshots; i++)
            {
                shared[i] = new SharedEncodedPayload(sharedCodec, snapshots[i]);
            }

            using Barrier start = new(Threads);
            Thread[] threads = new Thread[Threads];
            for (int t = 0; t < Threads; t++)
            {
                int index = t;
                int seed = round * Threads + t;
                threads[t] = new Thread(() =>
                {
                    try
                    {
                        TransportJsonCodec codec = index % 2 == 0 ? sharedCodec : new TransportJsonCodec();
                        Random order = new(seed);
                        ArrayBufferWriter<byte> scratch = new();
                        start.SignalAndWait();
                        foreach (int i in Enumerable.Range(0, Snapshots).OrderBy(_ => order.Next()))
                        {
                            Expect(shared[i].Bytes.SequenceEqual(expected[i]), $"snapshot {i}: shared payload differs from the reference");
                            Interlocked.Increment(ref sharedReads);

                            scratch.Clear();
                            codec.EncodePayload(snapshots[i], scratch);
                            Expect(scratch.WrittenSpan.SequenceEqual(expected[i]), $"snapshot {i}: private encode differs from the reference");
                            Interlocked.Increment(ref privateEncodes);

                            if ((i + index) % 3 == 0)
                            {
                                TransportEnvelope envelope = new(TransportEnvelope.CurrentProtocolVersion, snapshots[i].MessageKind, (ulong)i);
                                ServerSnapshotMessage decoded = Assert.IsType<ServerSnapshotMessage>(codec.Decode(codec.Encode(envelope, snapshots[i])).Message);
                                Expect(
                                    JsonSerializer.SerializeToUtf8Bytes(decoded, ProtocolWireOptions.V2).AsSpan().SequenceEqual(expected[i]),
                                    $"snapshot {i}: decoded snapshot re-encodes differently through the reference");
                                Interlocked.Increment(ref decodes);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        failures[index] = ex.ToString();
                    }
                })
                {
                    IsBackground = true,
                    Name = $"wire11-worker-{t}"
                };
                threads[t].Start();
            }

            foreach (Thread thread in threads)
            {
                Assert.True(thread.Join(Bound), $"A worker did not finish within {Bound.TotalSeconds} s.");
            }

            Assert.All(failures, failure => Assert.Null(failure));
            Assert.All(shared, payload => Assert.True(payload.IsEncoded));
        }

        elapsed.Stop();
        output.WriteLine(
            $"{Threads} threads x {Rounds} rounds: {sharedReads} shared reads, {privateEncodes} private encodes, " +
            $"{decodes} decodes in {elapsed.ElapsedMilliseconds} ms; largest payload {expected.Max(p => p.Length)} B");
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
