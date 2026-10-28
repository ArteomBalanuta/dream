using Dream.Simulation.World;

namespace Dream.Multiplayer.Protocol;

// WorldTransferId (protocol v3): set on a resync carrier sent without its world, which follows as
// the server.world-chunk messages of this transfer (WorldChunkMessage); the client session
// delivers the snapshot with the assembled world and this left null.
public sealed record ServerSnapshotMessage(ServerSnapshotEnvelope Snapshot, int? WorldTransferId = null) : IServerToClientMessage
{
    public string MessageKind => "server.snapshot";
}
