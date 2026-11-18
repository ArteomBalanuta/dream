using System.Text;
using Xunit;
using Dream.Client.Networking;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Transport;
using Dream.Simulation.Players;
using Dream.Simulation.World;

namespace Dream.Tests.BugHunt;

// BH-NET2-1. A server.snapshot payload that omits one of its collection properties decodes to a
// null collection: the hand-written decoder keeps that parity with the reference serializer on
// purpose (ServerSnapshotConverterTests), so a payload of "{}" still decodes. The client used to
// carry the null all the way to the frame thread, where it threw while the snapshot was applied.
// It now ends the session as a protocol error on the receive loop instead.
public class Cli2MalformedSnapshotTests
{
    [Theory]
    // Every collection absent, the shape ServerSnapshotConverterTests pins as decodable.
    [InlineData("""{"serverTick":9}""", "LastAcceptedCommandSequences")]
    [InlineData("""{"serverTick":9,"lastAcceptedCommandSequences":null}""", "LastAcceptedCommandSequences")]
    // One collection at a time, the rest well formed.
    [InlineData(
        """{"serverTick":9,"lastAcceptedCommandSequences":{},"actors":[],"damageEvents":[]}""",
        "WorldMutations")]
    [InlineData(
        """{"serverTick":9,"lastAcceptedCommandSequences":{},"actors":[],"worldMutations":[]}""",
        "DamageEvents")]
    // Actors as neither a list nor a frame: nothing downstream has an actor list to read.
    [InlineData(
        """{"serverTick":9,"lastAcceptedCommandSequences":{},"worldMutations":[],"damageEvents":[]}""",
        "Actors")]
    public async Task AMalformedSnapshotEndsTheSessionAsAProtocolError(string snapshotJson, string expectedField)
    {
        RawPayloadConnection connection = new();
        RemoteClientSession session = new(
            connection,
            new TransportJsonCodec(),
            new LengthPrefixedMessageFramer(),
            new RemoteClientOptions("127.0.0.1", 5057, "session-a", "player-a", "player-a"));
        await using (session)
        {
            connection.QueueSnapshotPayload(snapshotJson);

            // Before the fix this was a NullReferenceException raised while the snapshot was
            // applied, not a protocol error the session can report.
            InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
                async () => await session.ProcessInboundOnceAsync());
            Assert.Contains(expectedField, error.Message, StringComparison.Ordinal);
            Assert.False(session.TryDequeueInbound(out _));
        }
    }

    [Fact]
    public async Task AWellFormedSnapshotStillApplies()
    {
        RawPayloadConnection connection = new();
        RemoteClientSession session = new(
            connection,
            new TransportJsonCodec(),
            new LengthPrefixedMessageFramer(),
            new RemoteClientOptions("127.0.0.1", 5057, "session-a", "player-a", "player-a"));
        await using (session)
        {
            connection.QueueSnapshotPayload(
                """{"serverTick":9,"lastAcceptedCommandSequences":{},"actors":[],"worldMutations":[],"damageEvents":[]}""");

            Assert.True(await session.ProcessInboundOnceAsync());
            Assert.True(session.TryDequeueInbound(out IServerToClientMessage? message));
            ServerSnapshotMessage snapshot = Assert.IsType<ServerSnapshotMessage>(message);
            Assert.Equal(9UL, snapshot.Snapshot.ServerTick);
        }
    }

    // The delta-coded shape: actors absent because they came as a frame. The guard must not touch
    // it, or every snapshot after the first keyframe would end the session.
    [Fact]
    public async Task ASnapshotWhoseActorsCameAsAFrameStillApplies()
    {
        RawPayloadConnection connection = new();
        RemoteClientSession session = new(
            connection,
            new TransportJsonCodec(),
            new LengthPrefixedMessageFramer(),
            new RemoteClientOptions("127.0.0.1", 5057, "session-a", "player-a", "player-a"));
        await using (session)
        {
            connection.QueueSnapshotPayload(
                """
                {"serverTick":9,"lastAcceptedCommandSequences":{},"worldMutations":[],"damageEvents":[],
                 "actorFrame":{"seq":3,"base":2,"actors":[7]}}
                """);

            Assert.True(await session.ProcessInboundOnceAsync());
            Assert.True(session.TryDequeueInbound(out IServerToClientMessage? message));
            ServerSnapshotMessage snapshot = Assert.IsType<ServerSnapshotMessage>(message);
            // Actor 7 is unknown to the cache and its chain is broken, so it is withheld as stale:
            // the actor list is present and empty, not null.
            Assert.NotNull(snapshot.Snapshot.Actors);
            Assert.Empty(snapshot.Snapshot.Actors);
        }
    }

    // Shows what the guard prevents: the same snapshot, applied as the frame thread would.
    [Fact]
    public void ANullActorListThrowsWhenApplied()
    {
        ServerSnapshotEnvelope malformed = new(
            9,
            new Dictionary<string, ulong>(),
            null!,
            PlayerInteractionMode.Combat,
            null,
            Array.Empty<WorldMutationDelta>(),
            Array.Empty<DamageEventDelta>());

        Assert.Throws<NullReferenceException>(
            () => new Dream.Client.Presentation.ClientScenePresenter().ApplySnapshot(malformed));
    }

    private sealed class RawPayloadConnection : ITransportConnection
    {
        private readonly Queue<byte[]> _inbound = new();
        private readonly LengthPrefixedMessageFramer _framer = new();

        public string ConnectionId => "cli2-malformed-snapshot";

        public ValueTask DisposeAsync()
        {
            _inbound.Clear();
            return ValueTask.CompletedTask;
        }

        public ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_inbound.Count == 0)
            {
                return ValueTask.FromResult(0);
            }

            byte[] payload = _inbound.Dequeue();
            payload.CopyTo(buffer);
            return ValueTask.FromResult(payload.Length);
        }

        public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            return ValueTask.CompletedTask;
        }

        // The envelope a server writes, with the snapshot payload exactly as given.
        public void QueueSnapshotPayload(string snapshotJson)
        {
            string json = "{\"protocolVersion\":" + TransportEnvelope.CurrentProtocolVersion
                + ",\"messageKind\":\"server.snapshot\",\"sequence\":1,\"payload\":{\"snapshot\":"
                + snapshotJson + "}}";
            _inbound.Enqueue(_framer.Frame(Encoding.UTF8.GetBytes(json)));
        }
    }
}
