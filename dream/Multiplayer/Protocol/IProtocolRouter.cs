namespace Dream.Multiplayer.Protocol;

public interface IProtocolRouter
{
    void RouteServerInbound(ServerRoutingContext context, IClientToServerMessage message);

    void RouteClientInbound(ClientRoutingContext context, IServerToClientMessage message);
}
