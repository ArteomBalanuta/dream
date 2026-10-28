using System.Text.Json.Serialization;
using Dream.Simulation.World;

namespace Dream.Multiplayer.Protocol;

// WorldTransferId (protocol v3): set on a resync carrier sent without its world, which follows as
// the server.world-chunk messages of this transfer (WorldChunkMessage); the client session
// delivers the snapshot with the assembled world and this left null.
public sealed record ServerSnapshotMessage(ServerSnapshotEnvelope Snapshot, int? WorldTransferId = null) : IServerToClientMessage
{
    public string MessageKind => "server.snapshot";

    // SCALE-2. The payload is the broadcast body shared by every connection: it leaves out
    // lastAcceptedCommandSequences, and each connection's own accepted command sequence travels
    // in its transport envelope header instead (TransportEnvelope.Ack), so a broadcast to P
    // players no longer writes P sequences to each of them. Set on the shared encode
    // (SharedEncodedPayload) and on a decoded snapshot whose envelope carried the ack, so a
    // re-encode reproduces the payload. Never on the wire as itself.
    [JsonIgnore]
    public bool SharedBody { get; init; }
}
