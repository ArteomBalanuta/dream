using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Client.Replication;
using Dream.Client.World;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Multiplayer.Transport;
using Dream.Server;
using Dream.Simulation.Combat;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Multiplayer.Protocol;
using static Dream.Tests.Integration.ProtocolV2SharedSnapshotLoopbackIntegrationTests;

namespace Dream.Tests.Integration;

// ProtocolV2SharedSnapshotLoopbackIntegrationTests' old-client case: the server rejects a v1, v2,
// v3 or v4 client (protocol v3 changed the world wire format, SCALE-1; v4 the snapshot actors and
// the envelope header, SCALE-2; v5 added the world clock to join-accepted, T1; v6 the held light, T2f) with a logged reason
// and no unobserved task fault while the other clients keep playing. Its own class so it runs in
// parallel with the other loopback runs.
public class ProtocolV2VersionRejectionLoopbackIntegrationTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    public async Task OldClient_IsRejectedWithALoggedReason_WithoutAnUnobservedTaskFault_AndTheOtherClientsKeepPlaying(int legacyVersion)
    {
        Assert.Equal(16, TransportEnvelope.CurrentProtocolVersion);
        string ExpectedReason = $"Client uses protocol v{legacyVersion}; this server uses v16.";
        List<Exception> unobserved = [];
        EventHandler<UnobservedTaskExceptionEventArgs> onUnobserved = (_, args) =>
        {
            // The handler is process-wide; keep only faults caused by this test's v1 frame.
            if (args.Exception.Flatten().InnerExceptions.Any(inner => inner.ToString().Contains($"protocol version {legacyVersion}", StringComparison.OrdinalIgnoreCase)))
            {
                lock (unobserved)
                {
                    unobserved.Add(args.Exception);
                }
            }
        };
        TaskScheduler.UnobservedTaskException += onUnobserved;
        try
        {
            await using ServerUnderTest server = await ServerUnderTest.StartAsync();
            List<ServerConnectionError> connectionErrors = [];
            server.Service.ConnectionError += error =>
            {
                lock (connectionErrors)
                {
                    connectionErrors.Add(error);
                }
            };
            await using RawClient a = await server.ConnectAndJoinAsync(PrimarySession);
            await using RawClient b = await server.ConnectAndJoinAsync(EarlySecondary);
            for (int i = 0; i < 20; i++)
            {
                await a.SendCommandAsync(walking: true);
                await b.SendCommandAsync(walking: false);
                await server.TickAsync();
                Assert.True(await server.FlushAllAsync());
            }

            // A protocol v1, v2, v3 or v4 client: that version's serializer options (v1: explicit
            // nulls and messageKind in the payload; v3 and v4 hellos are shaped like a v5 one) and
            // its protocolVersion in the envelope, framed exactly like a v5 frame.
            ClientHelloMessage hello = new($"legacy-v{legacyVersion}-client");
            byte[] v1Hello = JsonSerializer.SerializeToUtf8Bytes(
                new { protocolVersion = legacyVersion, messageKind = hello.MessageKind, sequence = 1UL, payload = hello },
                legacyVersion switch
                {
                    1 => ProtocolWireOptions.V1,
                    2 => ProtocolWireOptions.V2,
                    _ => ProtocolWireOptions.V3,
                });
            InvalidOperationException versionError = Assert.Throws<InvalidOperationException>(() => new TransportJsonCodec().Decode(v1Hello));
            Assert.Equal($"Unsupported protocol version {legacyVersion}. Supported version: 16.", versionError.Message);

            using TcpClient legacy = new();
            await legacy.ConnectAsync(IPAddress.Loopback, server.Port);
            ServerConnection legacyConnection = await server.AcceptAsync();
            Assert.Equal(3, server.Service.ConnectionCount);
            NetworkStream legacyStream = legacy.GetStream();
            await legacyStream.WriteAsync(new LengthPrefixedMessageFramer().Frame(v1Hello));

            // The server reads the frame, fails to decode it, answers with one v2 rejection that
            // names both versions, and closes only that connection.
            using MemoryStream received = new();
            byte[] buffer = new byte[4096];
            using (CancellationTokenSource readTimeout = new(ProtocolV2SharedSnapshotLoopbackIntegrationTests.Timeout))
            {
                while (true)
                {
                    int read;
                    try
                    {
                        read = await legacyStream.ReadAsync(buffer, readTimeout.Token);
                    }
                    catch (IOException)
                    {
                        // A reset is also a close.
                        break;
                    }

                    if (read == 0)
                    {
                        break;
                    }

                    received.Write(buffer, 0, read);
                }
            }

            byte[] receivedBytes = received.ToArray();
            Assert.True(
                new LengthPrefixedMessageFramer().TryDeframe(receivedBytes, out byte[]? rejectionPayload, out int consumed),
                $"expected one complete rejection frame, got {receivedBytes.Length} bytes");
            Assert.Equal(receivedBytes.Length, consumed);
            DecodedProtocolMessage rejection = new TransportJsonCodec().Decode(rejectionPayload!);
            Assert.Equal(TransportEnvelope.CurrentProtocolVersion, rejection.Envelope.ProtocolVersion);
            Assert.Equal(1UL, rejection.Envelope.Sequence);
            Assert.Equal(ExpectedReason, Assert.IsType<JoinRejectedMessage>(rejection.Message).Reason);

            await WaitUntilAsync(() => server.Service.ConnectionCount == 2, $"the v{legacyVersion} connection was never dropped");
            Assert.False(server.Host.IsConnectionJoined(legacyConnection.ConnectionId));

            // The server logged the reason once, for that connection only (its id is the remote
            // endpoint), as a protocol mismatch rather than a generic error.
            lock (connectionErrors)
            {
                ServerConnectionError logged = Assert.Single(connectionErrors);
                Assert.Equal(legacyConnection.ConnectionId, logged.ConnectionId);
                Assert.EndsWith($":{((IPEndPoint)legacy.Client.LocalEndPoint!).Port}", logged.ConnectionId);
                Assert.Equal("protocol-mismatch", logged.Kind);
                Assert.Equal(ExpectedReason, logged.Reason);
                ProtocolVersionMismatchException mismatch = Assert.IsType<ProtocolVersionMismatchException>(logged.Exception);
                Assert.Equal(legacyVersion, mismatch.PeerVersion);
                Assert.Equal(versionError.Message, mismatch.InnerException!.Message);
            }

            // The connection loop task must not end faulted: force the finalizers that would
            // report an unobserved task exception and check that none came from this connection.
            for (int i = 0; i < 5; i++)
            {
                await Task.Delay(20);
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            lock (unobserved)
            {
                Assert.Empty(unobserved);
            }

            // The server keeps ticking and the v2 clients keep receiving every snapshot.
            int framesBefore = a.Frames.Count;
            for (int i = 0; i < 30; i++)
            {
                await a.SendCommandAsync(walking: true);
                await b.SendCommandAsync(walking: true);
                await server.TickAsync();
                Assert.True(await server.FlushAllAsync());
            }

            ulong lastTick = server.Host.Runtime!.CurrentServerTick;
            await a.WaitForSnapshotTickAsync(lastTick);
            await b.WaitForSnapshotTickAsync(lastTick);
            Assert.True(a.Frames.Count >= framesBefore + 30);
            foreach (RawClient client in new[] { a, b })
            {
                Assert.Null(client.ReadFault);
                AssertEnvelopeSequencesAreGapFree(client);
                Assert.True(client.IsConnected);
            }

            lock (connectionErrors)
            {
                Assert.Single(connectionErrors);
            }

            // A v2 client can still join afterwards.
            await using RawClient c = await server.ConnectAndJoinAsync(LateSecondary);
            await server.TickAsync();
            Assert.True(await server.FlushAllAsync());
            await c.WaitForSnapshotTickAsync(server.Host.Runtime!.CurrentServerTick);
            Assert.Null(c.ReadFault);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= onUnobserved;
        }
    }
}
