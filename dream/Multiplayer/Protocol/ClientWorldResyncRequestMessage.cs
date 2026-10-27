namespace Dream.Multiplayer.Protocol;

// Sent by a client whose world checksum no longer matches the server's. The server answers with
// a full world snapshot for that connection only.
public sealed record ClientWorldResyncRequestMessage(ulong ClientChecksum) : IClientToServerMessage
{
    public string MessageKind => "client.world-resync";
}
