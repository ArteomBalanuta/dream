namespace Dream.Multiplayer.Protocol;

public sealed class ServerSnapshotMessageHandler : IClientMessageHandler
{
    public string MessageKind => "server.snapshot";

    public void Handle(ClientRoutingContext context, IServerToClientMessage message)
    {
        context.LastSnapshot = ((ServerSnapshotMessage)message).Snapshot;
    }
}
