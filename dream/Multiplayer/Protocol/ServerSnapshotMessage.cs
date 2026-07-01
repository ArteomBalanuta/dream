using Dream.Simulation.World;

namespace Dream.Multiplayer.Protocol;

public sealed record ServerSnapshotMessage(ServerSnapshotEnvelope Snapshot) : IServerToClientMessage
{
    public string MessageKind => "server.snapshot";
}
