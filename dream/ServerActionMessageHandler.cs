public sealed class ServerActionMessageHandler : IClientMessageHandler
{
    public string MessageKind => "server.action";

    public void Handle(ClientRoutingContext context, IServerToClientMessage message)
    {
        context.LastActionResult = ((ServerActionMessage)message).Result;
    }
}
