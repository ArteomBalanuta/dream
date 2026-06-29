public sealed class JoinRequestHandler : IServerMessageHandler
{
    public string MessageKind => "client.join-request";

    public void Handle(ServerRoutingContext context, IClientToServerMessage message)
    {
        ClientJoinRequestMessage joinRequest = (ClientJoinRequestMessage)message;
        context.LastJoinRequest = joinRequest;
        if (context.Authority == null)
        {
            context.OutboundMessages.Add(new JoinRejectedMessage(
                "Remote session joins are not implemented yet in this phase."));
            return;
        }

        try
        {
            context.OutboundMessages.Add(context.Authority.JoinSession(
                joinRequest.RequestedSessionId,
                joinRequest.DisplayName));
        }
        catch (InvalidOperationException ex)
        {
            context.OutboundMessages.Add(new JoinRejectedMessage(ex.Message));
        }
    }
}
