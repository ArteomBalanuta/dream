namespace Dream.Multiplayer.Protocol;

public sealed class JoinAcceptedMessageHandler : IClientMessageHandler
{
    public string MessageKind => "server.join-accepted";

    public void Handle(ClientRoutingContext context, IServerToClientMessage message)
    {
        context.LastJoinAccepted = (JoinAcceptedMessage)message;
    }
}
