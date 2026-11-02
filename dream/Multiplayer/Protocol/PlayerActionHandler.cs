using Dream.Multiplayer.Sessions;
using Dream.Simulation.Players;

namespace Dream.Multiplayer.Protocol;

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

        if (context.ActionQueue == null)
        {
            context.OutboundMessages.Add(new ServerActionMessage(CreateRejectedResult(
                actionMessage,
                serverTick: 0,
                "Authoritative action queue is not attached to the server host yet.")));
            return;
        }

        if (!IsClientSendable(actionMessage.Payload))
        {
            context.OutboundMessages.Add(new ServerActionMessage(CreateRejectedResult(
                actionMessage,
                serverTick: 0,
                $"{DescribePayload(actionMessage.Payload)} cannot be sent by clients; build and combat use come from player.command primary use.")));
            return;
        }

        if (actionMessage.Payload is PlayerStatsUpdateIntent && !context.Authority.DevCommandsAllowed)
        {
            context.OutboundMessages.Add(new ServerActionMessage(CreateRejectedResult(
                actionMessage,
                serverTick: 0,
                "Stat changes are disabled on this server.")));
            return;
        }

        if (!context.ActionQueue.TryEnqueue(actionMessage, out _))
        {
            context.OutboundMessages.Add(new ServerActionMessage(CreateRejectedResult(
                actionMessage,
                serverTick: 0,
                "Too many pending player actions for this session; wait for the next tick to drain them.")));
        }
    }

    // Build and combat use intents are created only by the server, from command-frame primary use,
    // which applies the use cadence and targeting. Over the wire a client may send only these. The
    // build material selection places nothing by itself; it only picks what later uses place.
    // PlayerStatsUpdateIntent is the development stat sliders (decision U4): any client can set its own
    // base stats within the validated range, and they persist and travel between worlds with the
    // character. It goes away when levels give stat points.
    // PlayerStatSpendIntent is the real thing: points the level earned, checked by the runtime and
    // allowed on every server, so it is not gated on DevCommandsAllowed.
    // The M3 intents (ground pickup, NPC interact, vendor buy and sell) name what they act on; the
    // server rechecks existence, claim, range and budget for each.
    private static bool IsClientSendable(IPlayerActionPayload payload)
    {
        return payload is InventoryEquipIntent or InventoryUnequipIntent or InventoryMoveIntent or InventoryDestroyIntent
            or PlayerStatsUpdateIntent or PlayerStatSpendIntent or BuildMaterialSelectIntent
            or GroundPickupIntent or NpcInteractIntent or VendorBuyIntent or VendorSellIntent
            or SkillUseIntent or ItemUseIntent or QuickbarBindIntent;
    }

    private static string DescribePayload(IPlayerActionPayload? payload)
    {
        return payload?.GetType().Name ?? "A null action payload";
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
