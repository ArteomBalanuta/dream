public sealed class PlayerActionHandler : IServerMessageHandler
{
    public string MessageKind => "player.action";

    public void Handle(ServerRoutingContext context, IClientToServerMessage message)
    {
        PlayerActionMessage actionMessage = (PlayerActionMessage)message;
        context.LastActionMessage = actionMessage;

        if (context.LastJoinRequest == null)
        {
            context.OutboundMessages.Add(new ServerActionMessage(CreateRejectedResult(
                actionMessage,
                serverTick: 0,
                "Join required before player actions can be processed.")));
            return;
        }

        if (!string.Equals(
                context.LastJoinRequest.RequestedSessionId,
                actionMessage.SessionId.Value,
                StringComparison.Ordinal))
        {
            context.OutboundMessages.Add(new ServerActionMessage(CreateRejectedResult(
                actionMessage,
                serverTick: 0,
                "Player action session does not match the joined session.")));
            return;
        }

        if (context.Authority == null)
        {
            context.OutboundMessages.Add(new ServerActionMessage(CreateRejectedResult(
                actionMessage,
                serverTick: 0,
                "Authoritative runtime is not attached to the server host yet.")));
            return;
        }

        context.OutboundMessages.Add(new ServerActionMessage(
            context.Authority.ProcessAction(actionMessage)));
    }

    private static AuthoritativeActionResult CreateRejectedResult(
        PlayerActionMessage message,
        ulong serverTick,
        string reason)
    {
        return new AuthoritativeActionResult(
            message.SessionId.Value,
            message.ActionSequence,
            serverTick,
            Accepted: false,
            Payload: null,
            Rejection: new RejectedUseDelta(reason));
    }
}
