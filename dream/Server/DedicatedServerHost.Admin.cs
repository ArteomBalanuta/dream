using Dream.Multiplayer.Protocol;

namespace Dream.Server;

// Server commands and sanctions (M8 workstream C). Contract stubs: no command runs and nobody is muted
// until workstream C fills this file in.
public sealed partial class DedicatedServerHost
{
    internal const string MutedText = "You are muted.";

    internal const string CommandsUnavailableText = "Server commands are not available.";

    // One /command line a client sent as ChatChannel.Command. The server alone decides: the role comes
    // from the connection's join, never from the client. Replies go to the sender.
    private void HandleAdminCommand(
        ServerRoutingContext context,
        ChatParticipant sender,
        ClientChatMessage line,
        List<PendingConnectionReply> replies)
    {
        _ = context;
        _ = line;
        replies.Add(new PendingConnectionReply(
            sender.ConnectionId,
            new ServerChatMessage(ChatChannel.System, "", CommandsUnavailableText, Runtime?.CurrentServerTick ?? 0UL)));
    }

    // Whether the account behind the connection is muted now.
    private bool IsMuted(ServerRoutingContext context)
    {
        _ = context;
        return false;
    }
}
