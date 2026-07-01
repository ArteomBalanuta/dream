using Dream.Simulation.World;

namespace Dream.Multiplayer.Protocol;

public sealed class JoinRequestHandler : IServerMessageHandler
{
    public string MessageKind => "client.join-request";

    public void Handle(ServerRoutingContext context, IClientToServerMessage message)
    {
        ClientJoinRequestMessage joinRequest = (ClientJoinRequestMessage)message;
        if (context.Authority == null)
        {
            context.OutboundMessages.Add(new JoinRejectedMessage(
                "Remote session joins are not implemented yet in this phase."));
            return;
        }

        if (context.SessionClaims != null
            && !context.SessionClaims.TryClaimSession(
                context.ConnectionId,
                joinRequest.RequestedSessionId,
                out string? rejectionReason))
        {
            context.OutboundMessages.Add(new JoinRejectedMessage(
                rejectionReason ?? "Requested session is already claimed by another connection."));
            return;
        }

        try
        {
            JoinAcceptedMessage accepted = context.Authority.JoinSession(
                joinRequest.RequestedSessionId,
                joinRequest.DisplayName);
            context.LastJoinRequest = joinRequest;
            context.OutboundMessages.Add(accepted);
        }
        catch (InvalidOperationException ex)
        {
            context.SessionClaims?.ReleaseConnection(context.ConnectionId);
            context.OutboundMessages.Add(new JoinRejectedMessage(ex.Message));
        }
    }
}
