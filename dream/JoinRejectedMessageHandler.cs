public sealed class JoinRejectedMessageHandler : IClientMessageHandler
{
    public string MessageKind => "server.join-rejected";

    public void Handle(ClientRoutingContext context, IServerToClientMessage message)
    {
        context.LastJoinRejected = (JoinRejectedMessage)message;
    }
}
