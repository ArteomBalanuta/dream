namespace Dream.Multiplayer.Protocol;

public sealed class ServerHelloMessageHandler : IClientMessageHandler
{
    public string MessageKind => "server.hello";

    public void Handle(ClientRoutingContext context, IServerToClientMessage message)
    {
        context.LastServerHello = (ServerHelloMessage)message;
    }
}
