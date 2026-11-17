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
        AccountKey Key,
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
                // M8: a muted account is not heard, the admin role included. Announce is the one command
                // that speaks to everyone, so the mute is checked here as it is on the chat path; the other
                // verbs only reply to the sender, which a mute does not cover.
                if (IsMuted(context))
                {
                    Reply(MutedText);
                    return;
                }

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

        // A kick closes one connection, so it is judged on the connection. A ban or mute is judged on the
        // account that owns the character (see QueueSanction), which the repository resolves.
        if (online && targetContext!.IsAdmin && command.Kind == AdminCommandKind.Kick)
        {
            Reply("That player is an administrator.");
            return;
        }

        string shownName = SanitizeForLog(targetName);
        AccountKey? senderAccount = context.JoinedIdentity?.Account;
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
                LogAdmin(sender, "ban", shownName, command.Minutes, command.Text);
                QueueSanction(
                    runtime,
                    sender.ConnectionId,
                    senderAccount,
                    targetName,
                    new SanctionChange((until, reason), null, false, false, GuardsAdmin: true),
                    accounts => accounts.TrySetBan(targetName, until, reason),
                    $"Banned {targetName}.");
                return;
            }

            case AdminCommandKind.Unban:
                LogAdmin(sender, "unban", shownName, null, string.Empty);
                QueueSanction(
                    runtime,
                    sender.ConnectionId,
                    senderAccount,
                    targetName,
                    new SanctionChange(null, null, true, false, GuardsAdmin: false),
                    accounts => accounts.TrySetBan(targetName, null, null),
                    $"Unbanned {targetName}.");
                return;
            case AdminCommandKind.Mute:
            {
                DateTime until = command.Minutes is { } minutes
                    ? DateTime.UtcNow.AddMinutes(minutes)
                    : PermanentUntilUtc;
                LogAdmin(sender, "mute", shownName, command.Minutes, string.Empty);
                QueueSanction(
                    runtime,
                    sender.ConnectionId,
                    senderAccount,
                    targetName,
                    new SanctionChange(null, until, false, false, GuardsAdmin: true),
                    accounts => accounts.TrySetMute(targetName, until),
                    $"Muted {targetName}.");
                return;
            }

            default:
                LogAdmin(sender, "unmute", shownName, null, string.Empty);
                QueueSanction(
                    runtime,
                    sender.ConnectionId,
                    senderAccount,
                    targetName,
                    new SanctionChange(null, null, false, true, GuardsAdmin: false),
                    accounts => accounts.TrySetMute(targetName, null),
                    $"Unmuted {targetName}.");
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

    // Whether the account behind the session is muted now (the runtime asks on the tick, for party invites).
    private bool IsSessionMuted(PlayerSessionId session) =>
        _sessionClaims.TryGetValue(session.Value, out string? connectionId)
        && _connections.TryGetValue(connectionId, out ServerRoutingContext? context)
        && IsMuted(context);

    private void AnnounceToEveryone(ChatParticipant sender, string text, ulong tick, List<PendingConnectionReply> replies)
    {
        LogAdmin(sender, "announce", string.Empty, null, text);
        ServerChatMessage announcement = new(ChatChannel.System, "", $"[Announcement] {text}", tick);
        foreach (KeyValuePair<string, string> claim in _connectionClaims)
        {
            replies.Add(new PendingConnectionReply(claim.Key, announcement));
        }
    }

    // What a sanction command changes on the account.
    private readonly record struct SanctionChange(
        (DateTime Until, string? Reason)? Ban,
        DateTime? Mute,
        bool ClearBan,
        bool ClearMute,
        bool GuardsAdmin);

    // The result of a queued sanction, written by the queue's worker before the reply barrier reads it.
    private sealed class SanctionOutcome
    {
        public volatile string? Text;
        public AccountKey? Applied;
    }

    // Sanctions are per account, so the account is resolved first: the repository says who owns the
    // character name, and everything after (the self and administrator guards, the write, the memory for
    // joins in flight, the live effect on every connection of the account) is about that account. The
    // work runs on the persistence queue, never on the tick. A write failure propagates to the queue
    // (it retries a locked database); the admin's reply is sent on a read barrier queued behind the
    // write, so it is never sent before the change committed.
    private void QueueSanction(
        WorldSessionRuntime runtime,
        string adminConnection,
        AccountKey? senderAccount,
        string targetName,
        SanctionChange change,
        Func<IAccountRepository, bool> write,
        string doneText)
    {
        IAccountRepository? accounts = runtime.AccountRepository;
        PersistenceWriteQueue? writes = runtime.PersistenceWrites;
        ulong tick = runtime.CurrentServerTick;
        if (accounts == null || writes == null)
        {
            EnqueueConnectionReply(adminConnection, new ServerChatMessage(ChatChannel.System, "", AccountDataUnavailableText, tick));
            return;
        }

        string notFoundText = NotFoundText(targetName);
        SanctionOutcome outcome = new();
        bool queued = writes.Enqueue(() =>
        {
            outcome.Text = null;
            outcome.Applied = null;
            if (accounts.Lookup(targetName, null).Existing is not { Owner: { } owner })
            {
                outcome.Text = notFoundText;
                return;
            }

            if (senderAccount is { } self && self.Equals(owner))
            {
                outcome.Text = "You cannot do that to yourself.";
                return;
            }

            if (change.GuardsAdmin && accounts.Lookup(targetName, owner).Standing.Role == 1)
            {
                outcome.Text = "That player is an administrator.";
                return;
            }

            if (!write(accounts))
            {
                outcome.Text = notFoundText;
                return;
            }

            RememberSanction(owner, change.Ban, change.Mute, change.ClearBan, change.ClearMute);
            outcome.Applied = owner;
            outcome.Text = doneText;
        });
        bool barrier = queued && writes.EnqueueRead(() =>
        {
            if (outcome.Applied is { } account)
            {
                _pendingAccountUpdates.Enqueue(new AccountUpdate(account, null));
            }

            EnqueueConnectionReply(
                adminConnection,
                new ServerChatMessage(ChatChannel.System, "", outcome.Text ?? "The change could not be saved.", tick));
        });
        if (!barrier)
        {
            EnqueueConnectionReply(adminConnection, new ServerChatMessage(ChatChannel.System, "", AccountDataUnavailableText, tick));
        }
    }

    // A change to what the host knows of an account, made off the tick and applied to its connections
    // on the next tick. Read null: re-apply the recent sanctions on top of the connection's standing;
    // otherwise the standing was just read from the database (role included).
    private readonly record struct AccountUpdate(AccountKey Account, AccountStanding? Read);

    private readonly ConcurrentQueue<AccountUpdate> _pendingAccountUpdates = new();
    private int _standingRefreshInFlight;

    // How often (in ticks) the role and sanctions of the connected accounts are read again, so another
    // world's ban, or a revoked role, reaches a player who stays connected. It rides the presence renew's
    // cadence by default; 0 turns it off.
    internal ulong StandingRefreshIntervalTicks { get; set; } = WorldSessionRuntime.PresenceRenewIntervalTicks;

    // Runs first in a tick, under the tick lock: applies the queued account changes to the live
    // connections and, on the refresh cadence, queues one read of the connected accounts' standing.
    private void StepAccountStandings(WorldSessionRuntime? runtime, ulong tick)
    {
        while (_pendingAccountUpdates.TryDequeue(out AccountUpdate update))
        {
            ApplyAccountUpdate(update);
        }

        if (runtime == null || StandingRefreshIntervalTicks == 0 || tick % StandingRefreshIntervalTicks != 0)
        {
            return;
        }

        QueueStandingRefresh(runtime);
    }

    // The cadence work lives in its own method: the lambda below captures locals, and the compiler allocates
    // a closure object when the scope of a captured local is entered, which would cost every tick 40 bytes.
    private void QueueStandingRefresh(WorldSessionRuntime runtime)
    {
        if (runtime.AccountRepository is not { } accounts
            || runtime.PersistenceWrites is not { } writes
            || Interlocked.CompareExchange(ref _standingRefreshInFlight, 1, 0) != 0)
        {
            return;
        }

        Dictionary<string, AccountKey> connected = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, ServerRoutingContext> connection in _connections)
        {
            if (connection.Value.JoinedIdentity is { } identity)
            {
                connected[identity.Account.HashHex] = identity.Account;
            }
        }

        // The accounts this host sanctioned recently are read too, even when nobody of theirs is
        // connected: the read drops their recent-sanction memory (ApplyAccountUpdate), which is what the
        // join refusal consults, so a change another process wrote to the row is honoured on the next
        // refresh instead of being overridden for the whole SanctionMemoryWindow.
        foreach (KeyValuePair<string, SanctionMemory> entry in _sanctionMemory)
        {
            connected[entry.Key] = entry.Value.Key;
        }

        if (connected.Count == 0)
        {
            Volatile.Write(ref _standingRefreshInFlight, 0);
            return;
        }

        AccountKey[] keys = [.. connected.Values];
        bool queued = writes.EnqueueRead(() =>
        {
            try
            {
                foreach (AccountKey key in keys)
                {
                    if (accounts.ReadStanding(key) is { } read)
                    {
                        _pendingAccountUpdates.Enqueue(new AccountUpdate(key, read));
                    }
                }
            }
            catch (Exception ex)
            {
                NetworkDiagnosticsLog.Write("server.admin.refresh-failed", $"{ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                Volatile.Write(ref _standingRefreshInFlight, 0);
            }
        });
        if (!queued)
        {
            Volatile.Write(ref _standingRefreshInFlight, 0);
        }
    }

    // Sets the standing of every connection of the account, and closes the ones now banned. Under the tick lock.
    private void ApplyAccountUpdate(in AccountUpdate update)
    {
        DateTime now = DateTime.UtcNow;
        if (update.Read != null)
        {
            // The row was just read, and the read was queued behind every write this host made, so it is
            // newer than the recent-sanction memory: the memory's job (covering a read that started before
            // the write) is done. Dropping it here is what lets a change another process wrote -- an
            // `--accounts unban` while the window is open -- decide, instead of being overridden for two
            // minutes by what this host last set.
            _sanctionMemory.TryRemove(update.Account.HashHex, out _);
        }

        foreach (KeyValuePair<string, ServerRoutingContext> connection in _connections)
        {
            ServerRoutingContext context = connection.Value;
            if (context.JoinedIdentity is not { } identity || !identity.Account.Equals(update.Account))
            {
                continue;
            }

            AccountStanding next = OverlaySanctions(update.Account, update.Read ?? context.Standing);
            context.Standing = next;
            if (BanMessage.IsActive(next, now)
                && _connectionClaims.ContainsKey(connection.Key)
                && !_takenOverConnections.ContainsKey(connection.Key))
            {
                NetworkDiagnosticsLog.Write("server.admin.ban-applied", $"connection={connection.Key}");
                CloseConnectionWithReason(connection.Key, BanMessage.Format(next));
            }
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

    // Keeps a sanction in the recent-sanction memory by account, so a join already in flight is judged on
    // it and the live connections of the account can be brought up to date from it (ApplyAccountUpdate).
    // Thread-safe: it runs on the persistence worker, even when nobody is online.
    private void RememberSanction(
        AccountKey key,
        (DateTime Until, string? Reason)? ban,
        DateTime? mute,
        bool clearBan,
        bool clearMute)
    {
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
                key, ban != null || clearBan, ban?.Until, ban?.Reason, mute != null || clearMute, mute, DateTime.UtcNow),
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
            // A pending join whose connection already holds a claim (a repeat join from a seated
            // player) is that same seat, already counted in _connectionClaims.
            if (!string.Equals(_pendingJoins[i].ConnectionId, connectionId, StringComparison.Ordinal)
                && !_connectionClaims.ContainsKey(_pendingJoins[i].ConnectionId))
            {
                slots++;
            }
        }

        return slots;
    }

    private bool IsServerFullFor(string connectionId, ClientJoinRequestMessage join) =>
        MaxPlayers > 0
        && !_connectionClaims.ContainsKey(connectionId)
        && PlayerSlotsInUse(connectionId) >= MaxPlayers
        && !HoldsSeat(connectionId, join);

    // Whether the joining account or character already holds a seat (or a leftover claim of the requested
    // session): its new connection replaces the old one, so a full server must not lock it out of its own
    // seat. Under the tick lock.
    private bool HoldsSeat(string connectionId, ClientJoinRequestMessage join)
    {
        AccountKey? account = join.ResolvedAccount;
        // A client-supplied field: a join with no displayName decodes as null, and a host without
        // accounts validates nothing before the cap is checked.
        string name = join.DisplayName?.Trim() ?? string.Empty;
        foreach (KeyValuePair<string, string> claim in _connectionClaims)
        {
            if (string.Equals(claim.Key, connectionId, StringComparison.Ordinal))
            {
                continue;
            }

            if (!_connections.TryGetValue(claim.Key, out ServerRoutingContext? other))
            {
                // A claim without a context is a leftover; the requested session id is its.
                if (string.Equals(claim.Value, join.RequestedSessionId, StringComparison.Ordinal))
                {
                    return true;
                }

                continue;
            }

            // A seat is the joiner's own only when the stable identity matches: the account when the
            // joiner has one, and the character name only when it has none. Character names ride in
            // every snapshot, so a name alone is attacker-chosen and must not open the cap.
            if (other.JoinedIdentity is { } identity
                && ((account is { } joining && identity.Account.Equals(joining))
                    || (account is null && CharacterNames.Comparer.Equals(identity.Character.Value, name))))
            {
                return true;
            }
        }

        if (account is { } pendingAccount)
        {
            for (int i = 0; i < _pendingJoins.Count; i++)
            {
                if (!string.Equals(_pendingJoins[i].ConnectionId, connectionId, StringComparison.Ordinal)
                    && _pendingJoins[i].Request.ResolvedAccount is { } other
                    && other.Equals(pendingAccount))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string SanitizeForLog(string value) =>
        ChatMessageHandler.Sanitize(value).Replace('\n', ' ');

    private static void LogAdmin(ChatParticipant admin, string action, string target, int? minutes, string text)
    {
        NetworkDiagnosticsLog.Write(
            "server.admin",
            $"admin={SanitizeForLog(admin.Name)} action={action} target={target} minutes={(minutes?.ToString() ?? "-")} text={SanitizeForLog(text)}");
    }
}
