using System.Collections.Concurrent;
using Dream.Multiplayer.Diagnostics;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Characters;
using Dream.Simulation.Persistence;
using Dream.Simulation.Runtime;

namespace Dream.Server;

// Server commands, sanctions, the player cap and the message of the day (M8 workstream C).
//
// Who may run a command is decided here and nowhere else: a ChatChannel.Command line is honoured only when
// the connection joined with the admin role (ServerRoutingContext.Standing, read from the account at join).
// The client's own claims are never looked at. Sanctions live on the account (global across worlds) and are
// written through the persistence queue; the result reaches the admin as a connection reply.
public sealed partial class DedicatedServerHost
{
    internal const string MutedText = "You are muted.";

    internal const string CommandsUnavailableText = "Server commands are not available.";

    internal const string PermissionDeniedText = "You do not have permission.";

    internal const string AccountDataUnavailableText = "Account data is not available on this server.";

    internal const string KickedText = "You were kicked from the server.";

    // A sanction set this recently is kept in memory, because a join whose persisted-state load started
    // before the write reaches the database would otherwise be judged on the old standing.
    private static readonly TimeSpan SanctionMemoryWindow = TimeSpan.FromMinutes(2);

    private const int MaxSanctionMemory = 256;

    private readonly ConcurrentDictionary<string, SanctionMemory> _sanctionMemory = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _closeReasons = new(StringComparer.Ordinal);

    // The most players joined at once; 0 is no cap. The dedicated bootstrap sets it from DREAM_MAX_PLAYERS.
    public int MaxPlayers { get; set; }

    // One System chat line sent after each accepted join (already cleaned); null for none.
    public string? Motd { get; set; }

    // A sanction as the host last set it: Set says whether it was set or cleared (a cleared one still
    // overrides a stale prefetch that read the old value).
    private readonly record struct SanctionMemory(
        bool BanSet,
        DateTime? BanUntilUtc,
        string? BanReason,
        bool MuteSet,
        DateTime? MuteUntilUtc,
        DateTime StampUtc);

    // One /command line a client sent as ChatChannel.Command. Replies go to the sender.
    private void HandleAdminCommand(
        ServerRoutingContext context,
        ChatParticipant sender,
        ClientChatMessage line,
        List<PendingConnectionReply> replies)
    {
        ulong tick = Runtime?.CurrentServerTick ?? 0UL;
        void Reply(string text) =>
            replies.Add(new PendingConnectionReply(sender.ConnectionId, new ServerChatMessage(ChatChannel.System, "", text, tick)));

        // Role first: a player learns nothing about the commands, not even whether a line parses.
        if (!context.IsAdmin)
        {
            NetworkDiagnosticsLog.Write("server.admin.denied", $"connection={context.ConnectionId} player={sender.Name}");
            Reply(PermissionDeniedText);
            return;
        }

        if (Runtime is not { } runtime)
        {
            Reply(CommandsUnavailableText);
            return;
        }

        if (!AdminCommands.TryParse(line.Text, out AdminCommand command, out string error))
        {
            Reply(error);
            return;
        }

        switch (command.Kind)
        {
            case AdminCommandKind.Announce:
                AnnounceToEveryone(sender, command.Text, tick, replies);
                return;
            case AdminCommandKind.Kick:
            case AdminCommandKind.Ban:
            case AdminCommandKind.Unban:
            case AdminCommandKind.Mute:
            case AdminCommandKind.Unmute:
                break;
            default:
                Reply(CommandsUnavailableText);
                return;
        }

        string targetName = CharacterNames.Normalize(command.Target);
        bool online = TryFindOnlinePlayer(runtime, targetName, out string targetConnection, out ServerRoutingContext? targetContext);
        if (online && string.Equals(targetConnection, sender.ConnectionId, StringComparison.Ordinal))
        {
            Reply("You cannot do that to yourself.");
            return;
        }

        if (online && targetContext!.IsAdmin && command.Kind is AdminCommandKind.Kick or AdminCommandKind.Ban or AdminCommandKind.Mute)
        {
            Reply("That player is an administrator.");
            return;
        }

        string shownName = SanitizeForLog(targetName);
        switch (command.Kind)
        {
            case AdminCommandKind.Kick:
                if (!online)
                {
                    Reply(PlayerNotOnlineText);
                    return;
                }

                LogAdmin(sender, "kick", shownName, command.Minutes, command.Text);
                CloseConnectionWithReason(
                    targetConnection,
                    string.IsNullOrEmpty(command.Text) ? KickedText : $"{KickedText} Reason: {command.Text}");
                Reply($"Kicked {targetName}.");
                return;
            case AdminCommandKind.Ban:
            {
                DateTime until = command.Minutes is { } minutes
                    ? DateTime.UtcNow.AddMinutes(minutes)
                    : PermanentUntilUtc;
                string? reason = string.IsNullOrEmpty(command.Text) ? null : command.Text;
                AccountStanding banned = new(0, until, reason, null);
                LogAdmin(sender, "ban", shownName, command.Minutes, command.Text);
                RememberSanction(runtime, targetName, targetContext, ban: (until, reason), mute: null, clearBan: false, clearMute: false);
                QueueSanction(
                    runtime,
                    sender.ConnectionId,
                    accounts => accounts.TrySetBan(targetName, until, reason),
                    $"Banned {targetName}.",
                    NotFoundText(targetName));
                if (online)
                {
                    CloseConnectionWithReason(targetConnection, BanMessage.Format(banned));
                }

                return;
            }

            case AdminCommandKind.Unban:
                LogAdmin(sender, "unban", shownName, null, string.Empty);
                RememberSanction(runtime, targetName, targetContext, ban: null, mute: null, clearBan: true, clearMute: false);
                QueueSanction(
                    runtime,
                    sender.ConnectionId,
                    accounts => accounts.TrySetBan(targetName, null, null),
                    $"Unbanned {targetName}.",
                    NotFoundText(targetName));
                return;
            case AdminCommandKind.Mute:
            {
                DateTime until = command.Minutes is { } minutes
                    ? DateTime.UtcNow.AddMinutes(minutes)
                    : PermanentUntilUtc;
                LogAdmin(sender, "mute", shownName, command.Minutes, string.Empty);
                RememberSanction(runtime, targetName, targetContext, ban: null, mute: until, clearBan: false, clearMute: false);
                QueueSanction(
                    runtime,
                    sender.ConnectionId,
                    accounts => accounts.TrySetMute(targetName, until),
                    $"Muted {targetName}.",
                    NotFoundText(targetName));
                return;
            }

            default:
                LogAdmin(sender, "unmute", shownName, null, string.Empty);
                RememberSanction(runtime, targetName, targetContext, ban: null, mute: null, clearBan: false, clearMute: true);
                QueueSanction(
                    runtime,
                    sender.ConnectionId,
                    accounts => accounts.TrySetMute(targetName, null),
                    $"Unmuted {targetName}.",
                    NotFoundText(targetName));
                return;
        }
    }

    internal const string PlayerNotOnlineText = "That player is not online.";

    // Far-future end of a ban or mute with no minutes.
    internal static readonly DateTime PermanentUntilUtc = new(9999, 12, 31, 0, 0, 0, DateTimeKind.Utc);

    private static string NotFoundText(string name) => $"No account owns a character named '{name}'.";

    // Whether the account behind the connection is muted now.
    private bool IsMuted(ServerRoutingContext context)
    {
        DateTime? until = context.Standing.MutedUntilUtc;
        return until is { } end && end > DateTime.UtcNow;
    }

    private void AnnounceToEveryone(ChatParticipant sender, string text, ulong tick, List<PendingConnectionReply> replies)
    {
        LogAdmin(sender, "announce", string.Empty, null, text);
        ServerChatMessage announcement = new(ChatChannel.System, "", $"[Announcement] {text}", tick);
        foreach (KeyValuePair<string, string> claim in _connectionClaims)
        {
            replies.Add(new PendingConnectionReply(claim.Key, announcement));
        }
    }

    // Runs the account write on the persistence queue and tells the admin how it went. Without a queue or
    // accounts nothing can be stored, and the admin is told so.
    private void QueueSanction(
        WorldSessionRuntime runtime,
        string adminConnection,
        Func<IAccountRepository, bool> write,
        string doneText,
        string notFoundText)
    {
        IAccountRepository? accounts = runtime.AccountRepository;
        PersistenceWriteQueue? writes = runtime.PersistenceWrites;
        if (accounts == null || writes == null)
        {
            EnqueueConnectionReply(adminConnection, new ServerChatMessage(ChatChannel.System, "", AccountDataUnavailableText, runtime.CurrentServerTick));
            return;
        }

        ulong tick = runtime.CurrentServerTick;
        bool queued = writes.Enqueue(() =>
        {
            bool changed;
            try
            {
                changed = write(accounts);
            }
            catch (Exception ex)
            {
                NetworkDiagnosticsLog.Write("server.admin.failed", $"{ex.GetType().Name}: {ex.Message}");
                EnqueueConnectionReply(adminConnection, new ServerChatMessage(ChatChannel.System, "", "The change could not be saved.", tick));
                return;
            }

            EnqueueConnectionReply(adminConnection, new ServerChatMessage(ChatChannel.System, "", changed ? doneText : notFoundText, tick));
        });
        if (!queued)
        {
            EnqueueConnectionReply(adminConnection, new ServerChatMessage(ChatChannel.System, "", AccountDataUnavailableText, tick));
        }
    }

    // The online player with this character name (display name, ASCII case folded). Under the tick lock.
    private bool TryFindOnlinePlayer(
        WorldSessionRuntime runtime,
        string name,
        out string connectionId,
        out ServerRoutingContext? context)
    {
        foreach (KeyValuePair<string, string> claim in _connectionClaims)
        {
            if (runtime.Sessions.TryGet(new PlayerSessionId(claim.Value), out ConnectedPlayer? player)
                && player != null
                && player.IsSpawned
                && CharacterNames.Comparer.Equals(player.DisplayName, name)
                && _connections.TryGetValue(claim.Key, out ServerRoutingContext? found))
            {
                connectionId = claim.Key;
                context = found;
                return true;
            }
        }

        connectionId = string.Empty;
        context = null;
        return false;
    }

    // Applies a sanction to what the host holds in memory: the live connection's standing (so it takes
    // effect at once), and the recent-sanction memory by account (so a join already in flight is judged on
    // it). The account is found through the live connection or a pending join of that character.
    private void RememberSanction(
        WorldSessionRuntime runtime,
        string targetName,
        ServerRoutingContext? online,
        (DateTime Until, string? Reason)? ban,
        DateTime? mute,
        bool clearBan,
        bool clearMute)
    {
        _ = runtime;
        AccountKey? account = online?.JoinedIdentity?.Account;
        if (account == null)
        {
            foreach (PendingJoin pending in _pendingJoins)
            {
                if (pending.Request.ResolvedAccount is { } pendingAccount
                    && (CharacterNames.Comparer.Equals(pending.PlayerName, targetName)
                        || CharacterNames.Comparer.Equals(pending.Request.DisplayName.Trim(), targetName)))
                {
                    account = pendingAccount;
                    break;
                }
            }
        }

        if (online != null)
        {
            AccountStanding standing = online.Standing;
            if (ban is { } setBan)
            {
                standing = standing with { BannedUntilUtc = setBan.Until, BanReason = setBan.Reason };
            }

            if (clearBan)
            {
                standing = standing with { BannedUntilUtc = null, BanReason = null };
            }

            if (mute is { } until)
            {
                standing = standing with { MutedUntilUtc = until };
            }

            if (clearMute)
            {
                standing = standing with { MutedUntilUtc = null };
            }

            online.Standing = standing;
        }

        if (account is not { } key)
        {
            return;
        }

        if (_sanctionMemory.Count >= MaxSanctionMemory)
        {
            DateTime now = DateTime.UtcNow;
            foreach (KeyValuePair<string, SanctionMemory> entry in _sanctionMemory)
            {
                if (now - entry.Value.StampUtc > SanctionMemoryWindow)
                {
                    _sanctionMemory.TryRemove(entry.Key, out _);
                }
            }
        }

        _sanctionMemory.AddOrUpdate(
            key.HashHex,
            _ => new SanctionMemory(
                ban != null || clearBan, ban?.Until, ban?.Reason, mute != null || clearMute, mute, DateTime.UtcNow),
            (_, old) => old with
            {
                BanSet = ban != null || clearBan || old.BanSet,
                BanUntilUtc = ban != null ? ban.Value.Until : clearBan ? null : old.BanUntilUtc,
                BanReason = ban != null ? ban.Value.Reason : clearBan ? null : old.BanReason,
                MuteSet = mute != null || clearMute || old.MuteSet,
                MuteUntilUtc = mute ?? (clearMute ? null : old.MuteUntilUtc),
                StampUtc = DateTime.UtcNow,
            });
    }

    // The standing a join is judged on: what the prefetch read, overridden by a sanction this host set
    // since (see SanctionMemoryWindow).
    private AccountStanding OverlaySanctions(AccountKey? account, AccountStanding read)
    {
        if (account is not { } key
            || !_sanctionMemory.TryGetValue(key.HashHex, out SanctionMemory memory)
            || DateTime.UtcNow - memory.StampUtc > SanctionMemoryWindow)
        {
            return read;
        }

        if (memory.BanSet)
        {
            read = read with { BannedUntilUtc = memory.BanUntilUtc, BanReason = memory.BanReason };
        }

        if (memory.MuteSet)
        {
            read = read with { MutedUntilUtc = memory.MuteUntilUtc };
        }

        return read;
    }

    // The ban text when the account was banned here since its join started loading; null otherwise.
    private string? RecentBanText(AccountKey? account)
    {
        AccountStanding overlaid = OverlaySanctions(account, default);
        return BanMessage.IsActive(overlaid, DateTime.UtcNow) ? BanMessage.Format(overlaid) : null;
    }

    // Closes the connection like a takeover does (the host releases it now, the service closes its
    // transport), telling the player why first. Under the tick lock.
    private void CloseConnectionWithReason(string connectionId, string reason)
    {
        _closeReasons[connectionId] = reason;
        _takenOverConnections[connectionId] = 0;
        ReleaseConnection(connectionId);
        _takenOverConnectionQueue.Enqueue(connectionId);
    }

    // The reason to send a closed connection (a kick or ban), else the takeover text. Used once.
    internal string TakeCloseReason(string connectionId) =>
        _closeReasons.TryRemove(connectionId, out string? reason) ? reason : OpenedElsewhereRejectionReason;

    // What an accepted join adds on the host side: the standing the account has now, the presence lease and
    // the message of the day, which follows the join reply.
    private ServerChatMessage? CompleteAcceptedJoin(ServerRoutingContext context)
    {
        if (context.JoinedIdentity is { } identity)
        {
            context.Standing = OverlaySanctions(identity.Account, context.Standing);
            Runtime?.ClaimPresenceAfterJoin(identity.Character.Value);
        }

        return string.IsNullOrEmpty(Motd)
            ? null
            : new ServerChatMessage(ChatChannel.System, "", Motd, Runtime?.CurrentServerTick ?? 0UL);
    }

    private static bool HasJoinAccepted(IReadOnlyList<IServerToClientMessage> messages)
    {
        for (int i = 0; i < messages.Count; i++)
        {
            if (messages[i] is JoinAcceptedMessage)
            {
                return true;
            }
        }

        return false;
    }

    // Joined players plus joins waiting for the next tick, other than this connection's own.
    private int PlayerSlotsInUse(string connectionId)
    {
        int slots = _connectionClaims.Count;
        for (int i = 0; i < _pendingJoins.Count; i++)
        {
            if (!string.Equals(_pendingJoins[i].ConnectionId, connectionId, StringComparison.Ordinal))
            {
                slots++;
            }
        }

        return slots;
    }

    private bool IsServerFullFor(string connectionId) =>
        MaxPlayers > 0
        && !_connectionClaims.ContainsKey(connectionId)
        && PlayerSlotsInUse(connectionId) >= MaxPlayers;

    private static string SanitizeForLog(string value) =>
        ChatMessageHandler.Sanitize(value).Replace('\n', ' ');

    private static void LogAdmin(ChatParticipant admin, string action, string target, int? minutes, string text)
    {
        NetworkDiagnosticsLog.Write(
            "server.admin",
            $"admin={SanitizeForLog(admin.Name)} action={action} target={target} minutes={(minutes?.ToString() ?? "-")} text={SanitizeForLog(text)}");
    }
}
