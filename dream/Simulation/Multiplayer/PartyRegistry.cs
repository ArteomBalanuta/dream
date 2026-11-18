using Dream.Simulation.Characters;

namespace Dream.Simulation.Multiplayer;

public enum PartyOutcome : byte
{
    Ok,
    InvalidName,
    SelfInvite,
    NotLeader,
    TargetInParty,
    PartyFull,
    AlreadyInvited,
    NoInvite,
    InviteExpired,
    AlreadyInParty,
    NotInParty,
    Cooldown,
}

// Who is in which party and who has a pending invite (M5). Pure memory keyed by character key through
// CharacterNames.Comparer; no sessions, no clock but the tick the caller passes. A party has 2 to
// PartyRules.MaxMembers members in join order and its leader is the first, so the lead passes to the next
// member in join order when the leader leaves. A party that falls to one member dissolves. Every change
// records the keys it touched (DrainChanged), so the runtime sends ServerPartyState only to those.
public sealed class PartyRegistry
{
    private sealed class Party(string first, string second)
    {
        public List<string> Members { get; } = [first, second];
    }

    private readonly record struct PendingInvite(string From, ulong ExpiresTick);

    private readonly Dictionary<string, Party> _partyOf = new(CharacterNames.Comparer);
    private readonly Dictionary<string, PendingInvite> _invites = new(CharacterNames.Comparer);
    private readonly HashSet<string> _changed = new(CharacterNames.Comparer);
    private readonly List<string> _scratch = [];

    private readonly Dictionary<string, ulong> _lastInviteTick = new(CharacterNames.Comparer);

    // Ticks an inviter must wait between invites; 0 is no cooldown (the runtime sets PartyRules.InviteCooldownTicks).
    public ulong InviteCooldownTicks { get; set; }

    public int PartyCount { get; private set; }

    public int InviteCount => _invites.Count;

    public bool HasChanges => _changed.Count > 0;

    // Moves the keys changed since the last call into into.
    public void DrainChanged(List<string> into)
    {
        foreach (string key in _changed)
        {
            into.Add(key);
        }

        _changed.Clear();
    }

    public bool IsInParty(string key) => _partyOf.ContainsKey(key);

    public bool AreInSameParty(string a, string b)
        => _partyOf.TryGetValue(a, out Party? party) && _partyOf.TryGetValue(b, out Party? other) && ReferenceEquals(party, other);

    // The members in join order (leader first); false when the key is in none. The list is the registry's
    // own: do not keep it across a change.
    public bool TryGetMembers(string key, out IReadOnlyList<string> members)
    {
        if (_partyOf.TryGetValue(key, out Party? party))
        {
            members = party.Members;
            return true;
        }

        members = [];
        return false;
    }

    public bool IsLeader(string key)
        => _partyOf.TryGetValue(key, out Party? party) && CharacterNames.Comparer.Equals(party.Members[0], key);

    public string? PendingInviteFrom(string invitee)
        => _invites.TryGetValue(invitee, out PendingInvite invite) ? invite.From : null;

    public static bool IsValidName(string? name)
        => !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= PartyRules.MaxTargetNameChars;

    // from invites to. A member who is not the leader cannot invite. The invitee must be in no party and
    // have no other pending invite (the same inviter may refresh its own).
    public PartyOutcome Invite(string from, string to, ulong tick)
        => Invite(from, to, tick, out _);

    // refreshed: the same inviter already had a pending invite to this invitee, so its time was renewed and
    // nothing changed for the invitee (no state is sent). Cooldown: with InviteCooldownTicks set, an inviter
    // may make one invite (or refresh) per that many ticks.
    public PartyOutcome Invite(string from, string to, ulong tick, out bool refreshed)
    {
        refreshed = false;
        Expire(tick);
        if (string.IsNullOrWhiteSpace(from) || !IsValidName(to))
        {
            return PartyOutcome.InvalidName;
        }

        if (CharacterNames.Comparer.Equals(from, to))
        {
            return PartyOutcome.SelfInvite;
        }

        if (_partyOf.TryGetValue(from, out Party? own))
        {
            if (!CharacterNames.Comparer.Equals(own.Members[0], from))
            {
                return PartyOutcome.NotLeader;
            }

            if (own.Members.Count >= PartyRules.MaxMembers)
            {
                return PartyOutcome.PartyFull;
            }
        }

        if (_partyOf.ContainsKey(to))
        {
            return PartyOutcome.TargetInParty;
        }

        if (_invites.TryGetValue(to, out PendingInvite pending) && !CharacterNames.Comparer.Equals(pending.From, from))
        {
            return PartyOutcome.AlreadyInvited;
        }

        if (InviteCooldownTicks > 0
            && _lastInviteTick.TryGetValue(from, out ulong last)
            && tick - last < InviteCooldownTicks)
        {
            return PartyOutcome.Cooldown;
        }

        _lastInviteTick[from] = tick;
        refreshed = _invites.ContainsKey(to);
        _invites[to] = new PendingInvite(from, tick + (ulong)PartyRules.InviteTimeoutTicks);
        if (!refreshed)
        {
            _changed.Add(to);
        }

        return PartyOutcome.Ok;
    }

    // The invitee answers its pending invite. On accept the inviter's party takes it in, or a new party of
    // the two is made when the inviter has none. The invite is consumed either way. An inviter who has
    // since joined a party it does not lead cannot admit anyone to it: NotLeader.
    public PartyOutcome Respond(string invitee, bool accept, ulong tick, out string? inviter)
    {
        inviter = null;
        if (!_invites.TryGetValue(invitee, out PendingInvite invite))
        {
            return PartyOutcome.NoInvite;
        }

        _invites.Remove(invitee);
        _changed.Add(invitee);
        inviter = invite.From;
        if (!accept)
        {
            return PartyOutcome.Ok;
        }

        if (tick > invite.ExpiresTick)
        {
            return PartyOutcome.InviteExpired;
        }

        if (_partyOf.ContainsKey(invitee))
        {
            return PartyOutcome.AlreadyInParty;
        }

        if (_partyOf.TryGetValue(invite.From, out Party? party))
        {
            // Leadership is re-checked at the moment of effect, not only when the invite was sent: the
            // inviter may have joined another party as a plain member in between, and an invite is not a
            // bearer token for whatever party its sender happens to sit in when it is redeemed.
            if (!CharacterNames.Comparer.Equals(party.Members[0], invite.From))
            {
                return PartyOutcome.NotLeader;
            }

            if (party.Members.Count >= PartyRules.MaxMembers)
            {
                return PartyOutcome.PartyFull;
            }

            party.Members.Add(invitee);
            _partyOf[invitee] = party;
            MarkAll(party);
            return PartyOutcome.Ok;
        }

        Party created = new(invite.From, invitee);
        _partyOf[invite.From] = created;
        _partyOf[invitee] = created;
        PartyCount++;
        MarkAll(created);
        return PartyOutcome.Ok;
    }

    // Takes the key out of its party, if any; a party of one dissolves. The leaver and the rest are marked.
    public PartyOutcome Leave(string key)
    {
        if (!_partyOf.Remove(key, out Party? party))
        {
            return PartyOutcome.NotInParty;
        }

        party.Members.RemoveAll(member => CharacterNames.Comparer.Equals(member, key));
        _changed.Add(key);
        if (party.Members.Count < 2)
        {
            foreach (string last in party.Members)
            {
                _partyOf.Remove(last);
                _changed.Add(last);
            }

            party.Members.Clear();
            PartyCount--;
            return PartyOutcome.Ok;
        }

        MarkAll(party);
        return PartyOutcome.Ok;
    }

    // A character went away: leaves its party and drops the invites to and from it.
    public void Remove(string key)
    {
        Leave(key);
        _lastInviteTick.Remove(key);
        if (_invites.Remove(key))
        {
            _changed.Add(key);
        }

        _scratch.Clear();
        foreach (KeyValuePair<string, PendingInvite> entry in _invites)
        {
            if (CharacterNames.Comparer.Equals(entry.Value.From, key))
            {
                _scratch.Add(entry.Key);
            }
        }

        foreach (string invitee in _scratch)
        {
            _invites.Remove(invitee);
            _changed.Add(invitee);
        }

        _scratch.Clear();
    }

    // Drops the invites past their time. Cheap when none is pending.
    public void Expire(ulong tick)
    {
        if (_invites.Count == 0)
        {
            return;
        }

        _scratch.Clear();
        foreach (KeyValuePair<string, PendingInvite> entry in _invites)
        {
            if (tick > entry.Value.ExpiresTick)
            {
                _scratch.Add(entry.Key);
            }
        }

        foreach (string invitee in _scratch)
        {
            _invites.Remove(invitee);
            _changed.Add(invitee);
        }

        _scratch.Clear();
    }

    private void MarkAll(Party party)
    {
        foreach (string member in party.Members)
        {
            _changed.Add(member);
        }
    }
}
