using Dream.Multiplayer.Diagnostics;
using Dream.Simulation.Runtime;

namespace Dream.Multiplayer.Protocol;

public sealed class WorldResyncRequestHandler : IServerMessageHandler
{
    public string MessageKind => "client.world-resync";

    public void Handle(ServerRoutingContext context, IClientToServerMessage message)
    {
        ClientWorldResyncRequestMessage request = (ClientWorldResyncRequestMessage)message;
        if (context.LastJoinRequest == null || context.Authority is not IWorldResyncSource source)
        {
            return;
        }

        NetworkDiagnosticsLog.Write(
            "server.world.resync",
            $"connection={context.ConnectionId} clientChecksum={request.ClientChecksum}");
        context.OutboundMessages.Add(new ServerSnapshotMessage(source.ExportWorldResync()));
    }
}
