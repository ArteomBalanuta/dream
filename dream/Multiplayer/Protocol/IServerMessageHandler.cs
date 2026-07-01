namespace Dream.Multiplayer.Protocol;

public interface IServerMessageHandler
{
    string MessageKind { get; }

    void Handle(ServerRoutingContext context, IClientToServerMessage message);
}
