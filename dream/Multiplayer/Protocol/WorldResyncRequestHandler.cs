using Dream.Multiplayer.Diagnostics;
using Dream.Simulation.Runtime;

namespace Dream.Multiplayer.Protocol;

// A resync exports the whole world, so it is not done on the receive thread. The handler only marks
// the connection as wanting one, at most once per ServerRoutingContext.WorldResyncCooldown, and
// DedicatedServerHost serves it at the end of its next tick as a pending connection reply.
public sealed class WorldResyncRequestHandler : IServerMessageHandler
{
    private readonly TimeProvider _timeProvider;

    public WorldResyncRequestHandler()
        : this(TimeProvider.System)
    {
    }

    public WorldResyncRequestHandler(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

    public string MessageKind => "client.world-resync";

    public void Handle(ServerRoutingContext context, IClientToServerMessage message)
    {
        ClientWorldResyncRequestMessage request = (ClientWorldResyncRequestMessage)message;
        if (context.LastJoinRequest == null || context.Authority is not IWorldResyncSource)
        {
            return;
        }

        bool accepted = context.TryRequestWorldResync(_timeProvider.GetTimestamp(), _timeProvider.TimestampFrequency);
        NetworkDiagnosticsLog.Write(
            "server.world.resync",
            $"connection={context.ConnectionId} clientChecksum={request.ClientChecksum} accepted={accepted}");
    }
}
