using Dream.Multiplayer.Diagnostics;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Persistence;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Server;

// The /dev staging line (DevCommandText, DevCommands). A "/dev" line is a ChatChannel.Command line that never
// reaches HandleAdminCommand: it is gated here and nowhere else.
//
// The gate is two things at once, read at the moment the line runs, under the tick lock: the server allows
// development commands (DevCommandsAllowed: the integrated server, or DREAM_DEV_COMMANDS) AND the connection
// is loopback. A direct public connection never passes the second half, whatever the flag or their role says; a tunnel or
// proxy on the same machine does (every remote player then arrives from loopback), hence the start warning.
// The sender is the session the connection claimed at join; a client names no target.
public sealed partial class DedicatedServerHost
{
    internal const string DevCommandsOffText = DevCommandText.ErrorPrefix + "Dev commands are off on this server.";

    internal const string DevTooManyText = DevCommandText.ErrorPrefix + "Too many subcommands (at most 8); nothing was run.";

    internal const string DevUnavailableText = DevCommandText.ErrorPrefix + "Dev commands are not available.";

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
            Reply(DevUnavailableText);
            return;
        }

        if (subcommands.Count == 0)
        {
            Reply(DevCommandText.ReplyPrefix + DevCommands.Help);
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
                DevCommandKind.Help => (true, DevCommands.Help),
                DevCommandKind.Spawn => runtime.DevSpawn(session, command.Key, command.Count, command.Distance),
                DevCommandKind.Teleport => runtime.DevTeleport(session, command.Key, command.Key2, command.X, command.Z),
                DevCommandKind.Give => runtime.DevGive(session, command.Key, command.Rarity, command.Count),
                DevCommandKind.Clear => runtime.DevClear(session, command.Distance),
                DevCommandKind.Heal => runtime.DevHeal(session),
                DevCommandKind.SetHealth => runtime.DevSetHealth(session, command.Count),
                DevCommandKind.SetMana => runtime.DevSetMana(session, command.Count),
                DevCommandKind.Boost => DevBoost(runtime, session, sender.Name, command.Count),
                DevCommandKind.TimeOfDay => DevTimeOfDay(runtime, command.X, replies),
                _ => (false, DevCommands.Help)
            };
            NetworkDiagnosticsLog.Write(
                "server.dev",
                $"player={SanitizeForLog(sender.Name)} verb={command.Kind} ok={ok}");
            Reply((ok ? DevCommandText.ReplyPrefix : DevCommandText.ErrorPrefix) + message);
        }
    }

    // tod = jump the world clock forward to a time of day. The clock is not a player's, so no seated or alive
    // player is needed; it runs under the tick lock like every dev command. Every connected client is
    // re-anchored (WorldTicks - CurrentServerTick moved), even when the clock already read that time, and the
    // anchors are queued before the sender's "[dev] " reply.
    private (bool Ok, string Message) DevTimeOfDay(WorldSessionRuntime runtime, double hours, List<PendingConnectionReply> replies)
    {
        long forward = WorldTimeOfDay.ForwardTicks(runtime.WorldClock.Settings, runtime.WorldClock.WorldTicks, hours);
        WorldClockAnchor anchor = runtime.JumpWorldClockForward(forward);
        foreach (KeyValuePair<string, string> claim in _connectionClaims)
        {
            replies.Add(new PendingConnectionReply(claim.Key, new ServerWorldClockMessage(anchor)));
        }

        return (true, $"World time set to {WorldTimeOfDay.Format(hours)} (day {runtime.WorldClock.DayNumber})");
    }

    // boost = the seat's stats (runtime) plus the admin role (account). The role lives on the ACCOUNT, not
    // the seat, so it cannot be set by the runtime, and it is written through the persistence queue rather
    // than inline: a synchronous SQLite write on the tick thread is exactly what that queue exists to avoid.
    //
    // The role therefore lands a moment after the reply, which is fine - the host re-reads the role and
    // sanctions of connected accounts on a timer, so it reaches this very session without a reconnect. The
    // reply says "queued" rather than "granted" because claiming otherwise would be a guess about a write
    // that has not run yet.
    private (bool Ok, string Message) DevBoost(
        WorldSessionRuntime runtime,
        PlayerSessionId session,
        string senderName,
        int attribute)
    {
        (bool ok, string message) = runtime.DevBoost(session, attribute, DevCommands.DefaultBoostLevel);
        if (!ok)
        {
            return (ok, message);
        }

        IAccountRepository? accounts = runtime.AccountRepository;
        PersistenceWriteQueue? writes = runtime.PersistenceWrites;
        string name = senderName;
        if (accounts == null || writes == null)
        {
            return (true, message + "; admin not granted (no account store on this server)");
        }

        return writes.Enqueue(() => accounts.TrySetRole(name, 1))
            ? (true, message + "; admin queued")
            : (true, message + "; admin not granted (the write queue refused)");
    }

    // Dev commands are on for this server AND the connection is a loopback one. Never the role.
    private bool DevCommandsPermitted(ServerRoutingContext context)
        => context.Authority?.DevCommandsAllowed == true
            && NewAccountLimiter.IsExempt(JoinThrottleTable.ResolveSource(context.ConnectionId));
}
