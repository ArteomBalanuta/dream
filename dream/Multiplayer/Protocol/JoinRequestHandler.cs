using Dream.Simulation.Characters;
using Dream.Simulation.World;

namespace Dream.Multiplayer.Protocol;

public sealed class JoinRequestHandler : IServerMessageHandler
{
    public string MessageKind => "client.join-request";

    // Order (G-D1): ownership is decided first, from memory, before the claim: the claim can take over
    // another connection's seat, so a check after it would let a stranger kick the owner.
    public void Handle(ServerRoutingContext context, IClientToServerMessage message)
    {
        ClientJoinRequestMessage joinRequest = (ClientJoinRequestMessage)message;
        if (context.Authority == null)
        {
            context.OutboundMessages.Add(new JoinRejectedMessage(
                "Remote session joins are not implemented yet in this phase."));
            return;
        }

        CharacterAccessDecision access;
        try
        {
            access = context.Authority.AuthorizeJoin(
                joinRequest.RequestedSessionId,
                joinRequest.DisplayName,
                joinRequest.ResolvedAccount);
        }
        catch (InvalidOperationException ex)
        {
            context.OutboundMessages.Add(new JoinRejectedMessage(ex.Message));
            return;
        }

        if (access.IsRejected)
        {
            context.OutboundMessages.Add(new JoinRejectedMessage(
                access.RejectionReason ?? JoinRejectionReasons.NameInvalid));
            if (string.Equals(access.RejectionReason, JoinRejectionReasons.CharacterOwned, StringComparison.Ordinal))
            {
                // Starts the join throttle for this name from this source, as a live-holder rejection does.
                context.JoinConflictPlayerName = access.DisplayName;
            }

            return;
        }

        JoinHolderIdentity? holder = access.Holder;
        if (holder is { } requested
            && context.JoinedIdentity is { } joined
            && !joined.Equals(requested))
        {
            context.OutboundMessages.Add(new JoinRejectedMessage(JoinRejectionReasons.AlreadyJoined));
            return;
        }

        if (access.IsNewAccount && context.SessionClaims?.CheckNewAccountLimit(context.ConnectionId) is { } limitReason)
        {
            context.OutboundMessages.Add(new JoinRejectedMessage(limitReason));
            return;
        }

        if (context.SessionClaims != null
            && !context.SessionClaims.TryClaimSession(
                context.ConnectionId,
                joinRequest.RequestedSessionId,
                joinRequest.DisplayName,
                holder,
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
                joinRequest.DisplayName,
                access);
            context.LastJoinRequest = joinRequest;
            context.JoinedIdentity = holder;
            if (access.IsNewAccount)
            {
                context.SessionClaims?.RecordNewAccount(context.ConnectionId);
            }

            context.OutboundMessages.Add(accepted);
        }
        catch (InvalidOperationException ex)
        {
            context.SessionClaims?.ReleaseConnection(context.ConnectionId);
            context.OutboundMessages.Add(new JoinRejectedMessage(ex.Message));
        }
    }
}
