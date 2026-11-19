using Dream.Multiplayer.Diagnostics;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Runtime;

namespace Dream.Server;

// The /dev staging line (DevCommandText, DevCommands). A "/dev" line is a ChatChannel.Command line that never
// reaches HandleAdminCommand: it is gated here and nowhere else.
//
// The gate is two things at once, read at the moment the line runs, under the tick lock: the server allows
// development commands (DevCommandsAllowed: the integrated server, or DREAM_DEV_COMMANDS) AND the connection
// is loopback. A player on a public host never passes the second half, whatever the flag or their role says.
// The sender is the session the connection claimed at join; a client names no target.
public sealed partial class DedicatedServerHost
{
    internal const string DevCommandsOffText = DevCommandText.ErrorPrefix + "Dev commands are off on this server.";

    private const string DevTooManyText = DevCommandText.ErrorPrefix + "Too many subcommands (at most 8); nothing was run.";

    // One line to the sender per subcommand; a failure replies and the next one still runs.
    private void HandleDevCommand(
        ServerRoutingContext context,
        ChatParticipant sender,
        ClientChatMessage line,
        List<PendingConnectionReply> replies)
    {
        ulong tick = Runtime?.CurrentServerTick ?? 0UL;
        void Reply(string text) =>
            replies.Add(new PendingConnectionReply(sender.ConnectionId, new ServerChatMessage(ChatChannel.System, "", text, tick)));

        // The gate first, so a server with dev commands off reveals nothing about the grammar.
        if (!DevCommandsPermitted(context))
        {
            NetworkDiagnosticsLog.Write("server.dev.denied", $"connection={context.ConnectionId} player={SanitizeForLog(sender.Name)}");
            Reply(DevCommandsOffText);
            return;
        }

        IReadOnlyList<string>? subcommands = DevCommandText.SplitSubcommands(line.Text);
        if (subcommands == null
            || Runtime is not { } runtime
            || !_connectionClaims.TryGetValue(context.ConnectionId, out string? sessionId))
        {
            Reply(DevCommandText.ErrorPrefix + "Dev commands are not available.");
            return;
        }

        if (subcommands.Count == 0)
        {
            Reply(DevCommandText.ReplyPrefix + DevCommands.Usage);
            return;
        }

        if (subcommands.Count > DevCommandText.MaxSubcommands)
        {
            Reply(DevTooManyText);
            return;
        }

        PlayerSessionId session = new(sessionId);
        foreach (string subcommand in subcommands)
        {
            if (!DevCommands.TryParse(subcommand, out DevCommand command, out string error))
            {
                Reply(DevCommandText.ErrorPrefix + error);
                continue;
            }

            // Re-checked per subcommand, not once per line: nothing between them can change the answer under
            // the lock, but a line that is cheap to re-judge is the rule for anything with an effect.
            if (!DevCommandsPermitted(context))
            {
                Reply(DevCommandsOffText);
                return;
            }

            (bool ok, string message) = command.Kind switch
            {
                DevCommandKind.Help => (true, DevCommands.Usage),
                DevCommandKind.Spawn => runtime.DevSpawn(session, command.Key, command.Count, command.Distance),
                DevCommandKind.Teleport => runtime.DevTeleport(session, command.Key, command.Key2, command.X, command.Z),
                DevCommandKind.Give => runtime.DevGive(session, command.Key, command.Rarity, command.Count),
                DevCommandKind.Clear => runtime.DevClear(session, command.Distance),
                DevCommandKind.Heal => runtime.DevHeal(session),
                DevCommandKind.SetHealth => runtime.DevSetHealth(session, command.Count),
                DevCommandKind.SetMana => runtime.DevSetMana(session, command.Count),
                _ => (false, DevCommands.Usage)
            };
            NetworkDiagnosticsLog.Write(
                "server.dev",
                $"player={SanitizeForLog(sender.Name)} verb={command.Kind} ok={ok}");
            Reply((ok ? DevCommandText.ReplyPrefix : DevCommandText.ErrorPrefix) + message);
        }
    }

    // Dev commands are on for this server AND the connection is a loopback one. Never the role.
    private bool DevCommandsPermitted(ServerRoutingContext context)
        => context.Authority?.DevCommandsAllowed == true
            && NewAccountLimiter.IsExempt(JoinThrottleTable.ResolveSource(context.ConnectionId));
}
