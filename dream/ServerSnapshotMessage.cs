public sealed record ServerSnapshotMessage(ServerSnapshotEnvelope Snapshot) : IServerToClientMessage
{
    public string MessageKind => "server.snapshot";
}
