public sealed class PlayerCommandHandler : IServerMessageHandler
{
    public string MessageKind => "player.command";

    public void Handle(ServerRoutingContext context, IClientToServerMessage message)
    {
        PlayerCommandMessage commandMessage = (PlayerCommandMessage)message;
        context.LastCommandMessage = commandMessage;
        context.CommandInbox?.Enqueue(commandMessage.CommandFrame);
    }
}
