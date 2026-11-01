namespace Dream.Multiplayer.Protocol;

public sealed class InventoryPushMessageHandler : IClientMessageHandler
{
    public string MessageKind => InventoryPushMessage.Kind;

    public void Handle(ClientRoutingContext context, IServerToClientMessage message)
    {
        context.LastInventoryPush = (InventoryPushMessage)message;
    }
}
