namespace Dream.Multiplayer.Protocol;

public interface IClientMessageHandler
{
    string MessageKind { get; }

    void Handle(ClientRoutingContext context, IServerToClientMessage message);
}
