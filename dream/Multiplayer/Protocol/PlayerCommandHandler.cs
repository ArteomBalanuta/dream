namespace Dream.Multiplayer.Protocol;

public sealed class PlayerCommandHandler : IServerMessageHandler
{
    public string MessageKind => "player.command";

    public void Handle(ServerRoutingContext context, IClientToServerMessage message)
    {
        PlayerCommandMessage commandMessage = (PlayerCommandMessage)message;
        context.LastCommandMessage = commandMessage;
        if (context.LastJoinRequest == null)
        {
            return;
        }

        if (!string.Equals(
            context.LastJoinRequest.RequestedSessionId,
            commandMessage.CommandFrame.SessionId.Value,
            StringComparison.Ordinal))
        {
            return;
        }

        context.CommandInbox?.Enqueue(commandMessage.CommandFrame);
    }
}
