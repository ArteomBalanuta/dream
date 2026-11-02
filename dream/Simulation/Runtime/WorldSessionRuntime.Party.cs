using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Characters;
using Dream.Simulation.Multiplayer;
using OpenTK.Mathematics;

namespace Dream.Simulation.Runtime;

// Parties (M5 workstream A). The membership lives in PartyRegistry (pure memory, keyed by character key);
// this partial maps it to sessions, answers the three party actions and sends ServerPartyState through the
// generic session queue, on change and every PartyRules.RefreshIntervalTicks ticks while parties exist.
// Nothing here is persisted: a party does not outlive its members' sessions, and a detach removes the member.
public sealed partial class WorldSessionRuntime
{
    private readonly PartyRegistry _parties = new() { InviteCooldownTicks = PartyRules.InviteCooldownTicks };
    private readonly List<string> _partyChangedScratch = [];
    private readonly List<PartyMemberSnapshot> _partyMemberScratch = [];

    internal PartyRegistry Parties => _parties;

    // The spawned player a name (display name or character key, ASCII case folded) belongs to.
    private bool TryFindSpawnedPlayerByName(string? name, out ConnectedPlayer player)
    {
        player = null!;
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        string wanted = CharacterNames.Normalize(name);
        ConnectedPlayer? byKey = null;
        foreach (ConnectedPlayer candidate in _sessions.Players)
        {
            if (!candidate.IsSpawned)
            {
                continue;
            }

            if (CharacterNames.Comparer.Equals(candidate.DisplayName, wanted))
            {
                player = candidate;
                return true;
            }

            if (byKey == null && CharacterNames.Comparer.Equals(candidate.CharacterKey.Value, wanted))
            {
                byKey = candidate;
            }
        }

        if (byKey == null)
        {
            return false;
        }

        player = byKey;
        return true;
    }

    // The spawned player a character key belongs to.
    private bool TryFindSpawnedPlayerByKey(string key, out ConnectedPlayer player)
    {
        foreach (ConnectedPlayer candidate in _sessions.Players)
        {
            if (candidate.IsSpawned && CharacterNames.Comparer.Equals(candidate.CharacterKey.Value, key))
            {
                player = candidate;
                return true;
            }
        }

        player = null!;
        return false;
    }

    private string DisplayNameOfKey(string key)
        => TryFindSpawnedPlayerByKey(key, out ConnectedPlayer player) ? player.DisplayName : key;

    // One System chat line to one session, through the session queue.
    private void SendSystemLine(PlayerSessionId session, string text)
        => QueueSessionMessage(session, new ServerChatMessage(ChatChannel.System, "", text, CurrentServerTick));

    private AuthoritativeActionResult CreateAcceptedActionResult(PlayerActionMessage message)
        => new(message.SessionId.Value, message.ActionSequence, CurrentServerTick, Accepted: true, Payload: null);

    private AuthoritativeActionResult ProcessPartyInviteAction(
        PlayerActionMessage message,
        ConnectedPlayer player,
        PartyInviteIntent intent)
    {
        if (!PartyRegistry.IsValidName(intent.TargetName))
        {
            return CreateRejectedActionResult(message, "Party invite rejected because the name is not valid.");
        }

        if (!TryFindSpawnedPlayerByName(intent.TargetName, out ConnectedPlayer target))
        {
            SendSystemLine(player.SessionId, "That player is not online.");
            return CreateRejectedActionResult(message, "Party invite rejected because that player is not online.");
        }

        // A muted account cannot reach other players through an invite either.
        if (IsSessionMuted?.Invoke(player.SessionId) == true)
        {
            SendSystemLine(player.SessionId, "You are muted.");
            return CreateRejectedActionResult(message, "Party invite rejected because the sender is muted.");
        }

        PartyOutcome outcome = _parties.Invite(player.CharacterKey.Value, target.CharacterKey.Value, CurrentServerTick, out bool refreshed);
        if (outcome != PartyOutcome.Ok)
        {
            string text = outcome switch
            {
                PartyOutcome.Cooldown => "Wait a moment before inviting again.",
                PartyOutcome.SelfInvite => "You cannot invite yourself.",
                PartyOutcome.NotLeader => "Only the party leader can invite.",
                PartyOutcome.TargetInParty => $"{target.DisplayName} is already in a party.",
                PartyOutcome.PartyFull => "Your party is full.",
                PartyOutcome.AlreadyInvited => $"{target.DisplayName} already has an invite pending.",
                _ => "The invite was not valid.",
            };
            SendSystemLine(player.SessionId, text);
            return CreateRejectedActionResult(message, $"Party invite rejected: {text}");
        }

        if (refreshed)
        {
            // The invite is already on the target's screen: its time is renewed, and nothing is sent to it.
            SendSystemLine(player.SessionId, $"{target.DisplayName} already has your invite.");
            return CreateAcceptedActionResult(message);
        }

        SendSystemLine(player.SessionId, $"You invited {target.DisplayName} to your party.");
        SendSystemLine(target.SessionId, $"{player.DisplayName} invited you to a party. Type /accept or /decline.");
        return CreateAcceptedActionResult(message);
    }

    private AuthoritativeActionResult ProcessPartyRespondAction(
        PlayerActionMessage message,
        ConnectedPlayer player,
        PartyRespondIntent intent)
    {
        PartyOutcome outcome = _parties.Respond(player.CharacterKey.Value, intent.Accept, CurrentServerTick, out string? inviterKey);
        if (outcome == PartyOutcome.NoInvite)
        {
            SendSystemLine(player.SessionId, "You have no party invite.");
            return CreateRejectedActionResult(message, "Party response rejected because there is no invite.");
        }

        if (outcome != PartyOutcome.Ok)
        {
            string text = outcome switch
            {
                PartyOutcome.InviteExpired => "The invite has expired.",
                PartyOutcome.PartyFull => "That party is full.",
                PartyOutcome.AlreadyInParty => "You are already in a party.",
                _ => "The invite was not valid.",
            };
            SendSystemLine(player.SessionId, text);
            return CreateRejectedActionResult(message, $"Party response rejected: {text}");
        }

        string inviterName = inviterKey == null ? "" : DisplayNameOfKey(inviterKey);
        if (intent.Accept)
        {
            SendSystemLine(player.SessionId, $"You joined {inviterName}'s party.");
            if (inviterKey != null && TryFindSpawnedPlayerByKey(inviterKey, out ConnectedPlayer inviter))
            {
                SendSystemLine(inviter.SessionId, $"{player.DisplayName} joined the party.");
            }
        }
        else
        {
            SendSystemLine(player.SessionId, "Invite declined.");
            if (inviterKey != null && TryFindSpawnedPlayerByKey(inviterKey, out ConnectedPlayer inviter))
            {
                SendSystemLine(inviter.SessionId, $"{player.DisplayName} declined the invite.");
            }
        }

        return CreateAcceptedActionResult(message);
    }

    private AuthoritativeActionResult ProcessPartyLeaveAction(
        PlayerActionMessage message,
        ConnectedPlayer player,
        PartyLeaveIntent intent)
    {
        _ = intent;
        string key = player.CharacterKey.Value;
        bool inParty = _parties.TryGetMembers(key, out IReadOnlyList<string> members);
        List<string> others = [];
        if (inParty)
        {
            foreach (string member in members)
            {
                if (!CharacterNames.Comparer.Equals(member, key))
                {
                    others.Add(member);
                }
            }
        }

        if (_parties.Leave(key) != PartyOutcome.Ok)
        {
            SendSystemLine(player.SessionId, "You are not in a party.");
            return CreateRejectedActionResult(message, "Party leave rejected because you are not in a party.");
        }

        SendSystemLine(player.SessionId, "You left the party.");
        foreach (string other in others)
        {
            if (TryFindSpawnedPlayerByKey(other, out ConnectedPlayer mate))
            {
                SendSystemLine(mate.SessionId, $"{player.DisplayName} left the party.");
            }
        }

        return CreateAcceptedActionResult(message);
    }

    // On change, and every 32 ticks while parties exist, queues ServerPartyState for the members. A steady
    // tick with no party and no invite reads two counts.
    private void StepPartyRefresh(ulong tick)
    {
        if (_parties.PartyCount == 0 && _parties.InviteCount == 0 && !_parties.HasChanges)
        {
            return;
        }

        _parties.Expire(tick);
        bool cadence = _parties.PartyCount > 0 && tick % PartyRules.RefreshIntervalTicks == 0;
        if (cadence)
        {
            foreach (ConnectedPlayer player in _sessions.Players)
            {
                if (player.IsSpawned && _parties.IsInParty(player.CharacterKey.Value))
                {
                    QueueSessionMessage(player.SessionId, BuildPartyState(player.CharacterKey.Value));
                }
            }
        }

        if (!_parties.HasChanges)
        {
            return;
        }

        _partyChangedScratch.Clear();
        _parties.DrainChanged(_partyChangedScratch);
        foreach (string key in _partyChangedScratch)
        {
            // Members already served by the cadence this tick are not sent twice.
            if (cadence && _parties.IsInParty(key))
            {
                continue;
            }

            if (TryFindSpawnedPlayerByKey(key, out ConnectedPlayer player))
            {
                QueueSessionMessage(player.SessionId, BuildPartyState(key));
            }
        }

        _partyChangedScratch.Clear();
    }

    private ServerPartyState BuildPartyState(string key)
    {
        _partyMemberScratch.Clear();
        if (_parties.TryGetMembers(key, out IReadOnlyList<string> members))
        {
            for (int i = 0; i < members.Count && _partyMemberScratch.Count < ServerPartyState.MaxMembers; i++)
            {
                if (TryFindSpawnedPlayerByKey(members[i], out ConnectedPlayer member)
                    && TryResolveAuthoritativePlayerTarget(member.SessionId, out _, out IAuthoritativePlayerTarget? target)
                    && target != null)
                {
                    _partyMemberScratch.Add(new PartyMemberSnapshot(
                        member.DisplayName,
                        target.Combatant.Level,
                        target.Combatant.Health.CurrentHealth,
                        target.Combatant.Health.MaxHealth,
                        IsLeader: i == 0));
                }
            }
        }

        string? pending = _parties.PendingInviteFrom(key);
        return new ServerPartyState(
            _partyMemberScratch.ToArray(),
            pending == null ? null : DisplayNameOfKey(pending));
    }

    private void LeavePartyOnDetach(PlayerSessionId session, string characterKey)
    {
        _ = session;
        _parties.Remove(characterKey);
    }

    // The two character keys (ordinal) are in one party.
    internal bool AreInSameParty(string characterKeyA, string characterKeyB)
        => _parties.PartyCount > 0 && _parties.AreInSameParty(characterKeyA, characterKeyB);

    // Appends the other online members of the session's party that are spawned, alive and within the
    // sharing range (40 m) of the position. False when there are none.
    internal bool TryGetNearbyPartyMembers(PlayerSessionId member, Vector3 at, List<PlayerSessionId> into)
    {
        if (_parties.PartyCount == 0
            || !_sessions.TryGet(member, out ConnectedPlayer? self)
            || self == null
            || !_parties.TryGetMembers(self.CharacterKey.Value, out IReadOnlyList<string> members))
        {
            return false;
        }

        float rangeSquared = PartyRules.ShareRangeMeters * PartyRules.ShareRangeMeters;
        int before = into.Count;
        for (int i = 0; i < members.Count; i++)
        {
            if (CharacterNames.Comparer.Equals(members[i], self.CharacterKey.Value)
                || !TryFindSpawnedPlayerByKey(members[i], out ConnectedPlayer mate)
                || !TryResolveAuthoritativePlayerTarget(mate.SessionId, out _, out IAuthoritativePlayerTarget? target)
                || target == null
                || !target.IsAlive
                || (target.Position - at).LengthSquared > rangeSquared)
            {
                continue;
            }

            into.Add(mate.SessionId);
        }

        return into.Count > before;
    }

    // Appends the sessions of the party the session belongs to, itself included, in join order. False
    // when the session is in none.
    internal bool CopyPartyMemberSessions(PlayerSessionId session, List<PlayerSessionId> into)
    {
        if (_parties.PartyCount == 0
            || !_sessions.TryGet(session, out ConnectedPlayer? self)
            || self == null
            || !_parties.TryGetMembers(self.CharacterKey.Value, out IReadOnlyList<string> members))
        {
            return false;
        }

        int before = into.Count;
        for (int i = 0; i < members.Count; i++)
        {
            if (TryFindSpawnedPlayerByKey(members[i], out ConnectedPlayer mate))
            {
                into.Add(mate.SessionId);
            }
        }

        return into.Count > before;
    }
}
